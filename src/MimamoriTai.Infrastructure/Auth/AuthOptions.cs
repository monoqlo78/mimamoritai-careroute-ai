namespace MimamoriTai.Infrastructure.Auth;

/// <summary>
/// OpenID Connect sign-in settings. Authority/ClientId/ClientSecret must come from
/// User Secrets, environment variables, or Azure App Service settings -- they are
/// never written to appsettings.json. When <see cref="IsConfigured"/> is false the
/// app registers no auth pipeline at all and keeps using <c>DevCurrentUserAccessor</c>,
/// so it still runs end to end with zero configuration.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public bool Enabled { get; set; }

    /// <summary>e.g. https://contsoexternal.ciamlogin.com/&lt;tenantId&gt;/v2.0, or https://access.line.me for LINE Login.</summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string CallbackPath { get; set; } = "/signin-oidc";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>Reported as CurrentUser.IdentityProvider, e.g. "entra-external" or "line".</summary>
    public string ProviderName { get; set; } = "entra-external";

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>True when Authority points at LINE Login's own OIDC issuer, not Entra External ID.</summary>
    public bool IsLineAuthority =>
        Authority.Contains("access.line.me", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the provider publishes an <c>end_session_endpoint</c> in its discovery
    /// document. LINE Login does not (verified against
    /// https://access.line.me/.well-known/openid-configuration), so asking the OpenID
    /// Connect handler to sign out there throws
    /// <c>InvalidOperationException: Cannot redirect to the end session endpoint</c>.
    /// </summary>
    public bool SupportsRemoteSignOut => !IsLineAuthority;

    /// <summary>
    /// Effective identity provider label for a signed-in principal. LINE reaches the app
    /// either directly (<see cref="IsLineAuthority"/>) or federated through Entra External
    /// ID, in which case Entra stamps an <c>idp</c> claim containing "line".
    /// </summary>
    public string ResolveIdentityProvider(string? idpClaim) =>
        IsLineAuthority || (idpClaim is not null && idpClaim.Contains("line", StringComparison.OrdinalIgnoreCase))
            ? "line"
            : ProviderName;
}
