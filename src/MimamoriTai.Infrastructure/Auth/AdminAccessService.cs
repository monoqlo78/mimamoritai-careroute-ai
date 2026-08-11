using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Auth;

/// <summary>
/// The single gate for the cross-household operator console. Everything that reads
/// across household boundaries -- and therefore deliberately bypasses
/// <c>HouseholdAccessService</c> -- must call <see cref="IsAdmin"/> first.
/// </summary>
public sealed class AdminAccessService(
    ICurrentUserAccessor currentUserAccessor,
    IOptions<AdminOptions> adminOptions,
    IOptions<AuthOptions> authOptions)
{
    private readonly AdminOptions _admin = adminOptions.Value;
    private readonly AuthOptions _auth = authOptions.Value;

    /// <summary>True when a real IdP is wired up, so identities are actually verified.</summary>
    public bool AuthIsConfigured => _auth.IsConfigured;

    /// <summary>
    /// True when the console is only reachable because sign-in is not configured
    /// (see <see cref="AdminOptions.AllowDemoUserWhenAuthDisabled"/>). The UI surfaces
    /// this so a demo build is never mistaken for an access-controlled deployment.
    /// </summary>
    public bool IsDemoModeGrant => !_auth.IsConfigured && _admin.AllowDemoUserWhenAuthDisabled;

    public bool IsAdmin => IsAdminUser(currentUserAccessor.Current);

    public bool IsAdminUser(CurrentUser? user)
    {
        if (user is not null && MatchesConfiguredSubject(user))
        {
            return true;
        }

        // No configured IdP means no identity can be proven, so the allow-list can
        // never match. Fall back to the demo grant only in that case.
        return IsDemoModeGrant && user is not null;
    }

    private bool MatchesConfiguredSubject(CurrentUser user)
    {
        if (_admin.Subjects.Count == 0)
        {
            return false;
        }

        var key = $"{user.IdentityProvider}:{user.ExternalSubject}";
        foreach (var subject in _admin.Subjects)
        {
            if (!string.IsNullOrWhiteSpace(subject)
                && string.Equals(subject.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
