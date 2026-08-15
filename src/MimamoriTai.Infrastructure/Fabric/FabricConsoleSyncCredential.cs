using Azure.Core;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// The identity the operator-console sync signs in to Fabric SQL with.
///
/// This exists so the sync cannot silently borrow somebody else's identity. The app
/// registers its <see cref="TokenCredential"/> with <c>TryAddSingleton</c>, and when the
/// Fabric Data Agent is configured it wins that race with a service-principal credential
/// -- the Data Agent needs one because its query API does not accept managed identities.
/// The console sync then inherited that service principal and Fabric SQL refused it:
/// "Validation of user's permissions failed. Verify the user has the Read item
/// permission." A service principal can only reach a SQL database in Fabric when the
/// tenant-wide "Service principals can use Fabric APIs" setting is on, and that switch is
/// not ours to flip on a shared tenant.
///
/// The App Service managed identity is already a Fabric workspace Admin, which is exactly
/// the Read item permission the database asks for, so wrapping the credential in its own
/// type keeps the sync on that identity no matter what else is registered.
/// </summary>
public sealed class FabricConsoleSyncCredential(TokenCredential credential)
{
    public TokenCredential Credential { get; } = credential;
}
