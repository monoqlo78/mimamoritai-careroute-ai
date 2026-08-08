namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// The signed-in user's identity, decoupled from any specific auth mechanism.
/// Today the only registration is a fixed dev/demo user
/// (<c>DevCurrentUserAccessor</c> in Infrastructure) so the app works with zero
/// configuration and zero login. A later task replaces the DI registration with a
/// claims-based implementation (Entra External ID / LINE OIDC) -- this interface is
/// the only coupling point, and nothing outside of it should change.
/// </summary>
public sealed record CurrentUser(
    Guid AppUserId,
    string DisplayName,
    string IdentityProvider,
    string ExternalSubject,
    bool IsAuthenticated);

public interface ICurrentUserAccessor
{
    /// <summary>The signed-in user, or null when the request is anonymous.</summary>
    CurrentUser? Current { get; }
}
