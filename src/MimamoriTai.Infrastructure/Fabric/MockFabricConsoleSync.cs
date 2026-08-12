using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Used when no Fabric SQL target is configured, so the app (and every test) runs end
/// to end with zero Fabric setup. Mirrors the Mock* publishers alongside it: reports
/// itself as unconfigured so callers no-op rather than logging a failure every cycle.
/// </summary>
public sealed class MockFabricConsoleSync : IFabricConsoleSync
{
    public bool IsConfigured => false;

    public Task<FabricConsoleSyncResult> SyncAsync(CancellationToken ct = default) =>
        Task.FromResult(FabricConsoleSyncResult.Failed(
            "Fabric console sync is not configured (FabricConsoleSync:Enabled is false).", 0));
}
