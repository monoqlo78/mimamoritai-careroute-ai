using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Auth;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Claims-based <see cref="ICurrentUserAccessor"/>, registered instead of
/// <c>DevCurrentUserAccessor</c> only when <see cref="AuthOptions.IsConfigured"/> is
/// true. The database <see cref="MimamoriTai.Core.Domain.AppUser.Id"/> is resolved
/// once per sign-in (in the OIDC <c>OnTokenValidated</c> handler) and carried on the
/// principal as the <c>mimamori:uid</c> claim, since this accessor is a synchronous
/// property and cannot itself await a database upsert.
/// </summary>
public sealed class ClaimsCurrentUserAccessor(
    IHttpContextAccessor httpContextAccessor,
    IOptions<AuthOptions> authOptions) : ICurrentUserAccessor
{
    /// <summary>Custom claim type carrying the resolved AppUser.Id, added in OnTokenValidated.</summary>
    public const string AppUserIdClaimType = "mimamori:uid";

    public CurrentUser? Current
    {
        get
        {
            var options = authOptions.Value;
            var httpContext = httpContextAccessor.HttpContext;
            var principal = httpContext?.User;

            if (!options.Enabled || principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var uidClaim = principal.FindFirst(AppUserIdClaimType)?.Value;
            if (!Guid.TryParse(uidClaim, out var appUserId))
            {
                // Principal is authenticated but the uid claim hasn't been minted yet
                // (e.g. mid sign-in); treat the request as anonymous rather than fault.
                return null;
            }

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
            var identityProvider = idpClaim is not null && idpClaim.Contains("line", StringComparison.OrdinalIgnoreCase)
                ? "line"
                : options.ProviderName;

            return new CurrentUser(
                AppUserId: appUserId,
                DisplayName: displayName,
                IdentityProvider: identityProvider,
                ExternalSubject: externalSubject,
                IsAuthenticated: true);
        }
    }
}
