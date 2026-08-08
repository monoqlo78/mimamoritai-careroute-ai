using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Auth;

/// <summary>
/// Zero-configuration fallback: always returns a single, well-known demo user so the
/// app runs end to end with no login and no secrets. A later task registers a
/// claims-based <see cref="ICurrentUserAccessor"/> (Entra External ID / LINE OIDC) in
/// its place -- this is the only DI registration that needs to change.
/// </summary>
public sealed class DevCurrentUserAccessor : ICurrentUserAccessor
{
    /// <summary>Deterministic id so the demo user's data is stable across restarts.</summary>
    public static readonly Guid DemoUserId = new("11111111-1111-1111-1111-111111111111");

    public CurrentUser? Current { get; } = new CurrentUser(
        AppUserId: DemoUserId,
        DisplayName: "デモユーザー",
        IdentityProvider: "dev",
        ExternalSubject: "demo",
        IsAuthenticated: false);
}
