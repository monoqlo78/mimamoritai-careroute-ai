using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Auth;

namespace MimamoriTai.Web.Endpoints;

/// <summary>
/// Sign-in / sign-out endpoints. These always exist, even when Auth:Enabled is false,
/// so linking to them never throws -- they just report that auth is not configured.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/login", (string? returnUrl, IOptions<AuthOptions> options) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.Content("ログイン機能は現在設定されていません（デモモードで動作中です）。", "text/plain; charset=utf-8");
            }

            var redirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = redirectUri },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }).WithName("AuthLogin");

        app.MapGet("/auth/logout", (IOptions<AuthOptions> options) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.Content("ログイン機能は現在設定されていません（デモモードで動作中です）。", "text/plain; charset=utf-8");
            }

            // LINE Login publishes no end_session_endpoint, so including the OpenID
            // Connect scheme there fails with "Cannot redirect to the end session
            // endpoint". Clearing the local cookie is the correct sign-out for it.
            string[] schemes = options.Value.SupportsRemoteSignOut
                ? [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
                : [CookieAuthenticationDefaults.AuthenticationScheme];

            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, schemes);
        }).WithName("AuthLogout");

        app.MapGet("/auth/me", (IOptions<AuthOptions> options, ICurrentUserAccessor currentUserAccessor) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.Ok(new { authenticated = false, displayName = (string?)null, provider = "dev", appUserId = (Guid?)null });
            }

            var user = currentUserAccessor.Current;
            return Results.Ok(new
            {
                authenticated = user is not null,
                displayName = user?.DisplayName,
                provider = user?.IdentityProvider,
                appUserId = user?.AppUserId
            });
        }).WithName("AuthMe");

        return app;
    }
}
