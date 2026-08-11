namespace MimamoriTai.Infrastructure.Auth;

/// <summary>
/// Who may open the cross-household operator console at <c>/admin</c>.
///
/// The app's own permission model (<c>HouseholdMemberRole</c>) is deliberately
/// household-scoped -- Owner/Member/Viewer all mean "inside this one household" -- so
/// there is no place in it to express "may read every household". Rather than widen
/// that enum (which would let a household Owner silently become a tenant-wide admin),
/// system administrators are named out-of-band in configuration, exactly like the
/// integration secrets: from User Secrets, environment variables, or App Service
/// settings, never from appsettings.json.
///
/// Each entry of <see cref="Subjects"/> is <c>"&lt;identityProvider&gt;:&lt;externalSubject&gt;"</c>,
/// matching <c>CurrentUser.IdentityProvider</c> / <c>CurrentUser.ExternalSubject</c>
/// (e.g. <c>"line:U4af4980629..."</c>, <c>"entra-external:00000000-..."</c>).
/// Comparison is ordinal and case-insensitive.
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// Identities allowed into the admin console, as <c>"provider:subject"</c>.
    /// </summary>
    public IList<string> Subjects { get; set; } = [];

    /// <summary>
    /// When no real sign-in is configured (<see cref="AuthOptions.IsConfigured"/> is
    /// false) the app runs on <c>DevCurrentUserAccessor</c>'s single demo identity, and
    /// there is no way for anyone to prove they are an administrator. In that
    /// zero-configuration demo mode the console is opened to that demo user so the
    /// feature is reachable in the hackathon build. Set this to false to keep
    /// <c>/admin</c> closed even then.
    ///
    /// This has no effect once authentication is configured: with a real IdP the only
    /// way in is to be listed in <see cref="Subjects"/>.
    /// </summary>
    public bool AllowDemoUserWhenAuthDisabled { get; set; } = true;
}
