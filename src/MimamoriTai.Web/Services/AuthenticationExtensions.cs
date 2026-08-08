using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Infrastructure.Auth;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Wires cookie + OpenID Connect authentication, but only when <see cref="AuthOptions.IsConfigured"/>
/// is true. When it is not, this method does nothing, so the app keeps running
/// anonymously with zero configuration exactly as before.
/// </summary>
public static class AuthenticationExtensions
{
    public static IServiceCollection AddMimamoriTaiAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var authOptions = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        services.AddHttpContextAccessor();

        if (!authOptions.IsConfigured)
        {
            // No auth configured: keep DevCurrentUserAccessor (registered in
            // AddMimamoriTaiInfrastructure) and skip the auth pipeline entirely. Still
            // register the bare authentication/authorization services (no schemes) so
            // Program.cs can unconditionally call UseAuthentication/UseAuthorization
            // without failing to resolve IAuthenticationSchemeProvider.
            services.AddAuthentication();
            services.AddAuthorization();
            return services;
        }

        // Replace the zero-config demo user with the claims-based accessor.
        services.AddScoped<ICurrentUserAccessor, ClaimsCurrentUserAccessor>();

        var tenantId = ExtractTenantIdSegment(authOptions.Authority);

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                options.Authority = authOptions.Authority;
                options.ClientId = authOptions.ClientId;
                options.ClientSecret = authOptions.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.CallbackPath = authOptions.CallbackPath;
                options.SignedOutCallbackPath = authOptions.SignedOutCallbackPath;
                options.MapInboundClaims = false;

                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                if (!authOptions.IsLineAuthority)
                {
                    // LINE Login does not support offline_access; requesting it there
                    // fails, so only request it for providers that advertise it (Entra
                    // External ID). This keeps one code path working for both.
                    options.Scope.Add("offline_access");
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "name",
                    ValidateIssuer = true,
                    ValidIssuers = BuildValidIssuers(authOptions.Authority, tenantId)
                };

                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        if (principal is null)
                        {
                            return;
                        }

                        var accessor = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserAccessorFactory>();
                        var appUserId = await accessor.ResolveAndProvisionAsync(principal, authOptions, context.HttpContext.RequestAborted);

                        var identity = (ClaimsIdentity?)principal.Identity;
                        identity?.AddClaim(new Claim(ClaimsCurrentUserAccessor.AppUserIdClaimType, appUserId.ToString()));
                    }
                };
            });

        services.AddAuthorization();
        services.AddScoped<ICurrentUserAccessorFactory, CurrentUserAccessorFactory>();

        return services;
    }

    /// <summary>
    /// Configures forwarded-headers handling so redirect_uri is computed as https
    /// behind a reverse proxy (Azure App Service). Safe to call unconditionally.
    /// </summary>
    public static IApplicationBuilder UseMimamoriTaiForwardedHeaders(this IApplicationBuilder app)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        return app.UseForwardedHeaders(options);
    }

    /// <summary>
    /// Microsoft Entra External ID's discovery document issuer host differs from the
    /// authority host (tenant-id subdomain vs the configured custom subdomain), so both
    /// forms must be accepted or validation fails with IDX10205.
    /// </summary>
    private static string[] BuildValidIssuers(string authority, string? tenantId)
    {
        var trimmed = authority.TrimEnd('/');
        var issuers = new List<string> { trimmed + "/", trimmed };

        if (tenantId is not null && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var tenantIssuer = $"{uri.Scheme}://{tenantId}.ciamlogin.com/{tenantId}/v2.0";
            issuers.Add(tenantIssuer);
            issuers.Add(tenantIssuer + "/");
        }

        return [.. issuers.Distinct()];
    }

    /// <summary>Extracts the tenant id path segment from an Entra External ID authority URL.</summary>
    private static string? ExtractTenantIdSegment(string authority)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : null;
    }
}

/// <summary>Resolves/creates the AppUser row for a freshly validated OIDC token, once per sign-in.</summary>
public interface ICurrentUserAccessorFactory
{
    Task<Guid> ResolveAndProvisionAsync(ClaimsPrincipal principal, AuthOptions options, CancellationToken ct);
}

internal sealed class CurrentUserAccessorFactory(
    HouseholdAccessService householdAccess,
    IAppDbContext db) : ICurrentUserAccessorFactory
{
    public async Task<Guid> ResolveAndProvisionAsync(ClaimsPrincipal principal, AuthOptions options, CancellationToken ct)
    {
        var externalSubject =
            principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "";

        var displayName =
            principal.FindFirst("name")?.Value
            ?? principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst("email")?.Value
            ?? "ご家族";

        var idpClaim = principal.FindFirst("idp")?.Value;
        var identityProvider = (idpClaim is not null && idpClaim.Contains("line", StringComparison.OrdinalIgnoreCase))
                || options.IsLineAuthority
            ? "line"
            : options.ProviderName;

        var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;

        var current = new CurrentUser(
            AppUserId: Guid.NewGuid(),
            DisplayName: displayName,
            IdentityProvider: identityProvider,
            ExternalSubject: externalSubject,
            IsAuthenticated: true);

        var appUser = await householdAccess.EnsureUserAsync(current, ct);

        if (!string.IsNullOrWhiteSpace(email))
        {
            appUser.Email = email;
        }

        if (identityProvider == "line")
        {
            appUser.LineUserId = externalSubject;
        }

        await db.SaveChangesAsync(ct);

        return appUser.Id;
    }
}
