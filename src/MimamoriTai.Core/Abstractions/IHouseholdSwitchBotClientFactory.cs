namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// Resolves an <see cref="ISwitchBotClient"/> scoped to exactly one household's
/// decrypted SwitchBot credentials. Implementations must never cache decrypted
/// Token/Secret beyond a single call: a fresh client is built (and the plaintext
/// discarded once the call returns) every time this is invoked, so no in-memory
/// cache can leak one household's secret into another household's request.
///
/// Precedence (see docs/SECURITY.md for the full write-up):
///   1. A per-household <c>SwitchBotConnection</c> row, when present.
///   2. The legacy global bootstrap <c>SwitchBotOptions</c>, only when that
///      household has no connection row AND <c>SwitchBotOptions.AllowGlobalFallback</c>
///      is explicitly enabled (local/dev bring-up only).
///   3. Otherwise, a client that reports <c>IsConfigured = false</c>.
/// </summary>
public interface IHouseholdSwitchBotClientFactory
{
    /// <summary>
    /// Never throws for "not configured": check the returned client's
    /// <see cref="ISwitchBotClient.IsConfigured"/> instead. Only throws for genuine
    /// infrastructure failures unrelated to whether SwitchBot is set up.
    /// </summary>
    Task<ISwitchBotClient> GetClientAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>
    /// Builds a client bound to a raw, not-yet-saved Token/Secret pair, used only to
    /// validate credentials (a real <c>GET /v1.1/devices</c> call) before they are
    /// encrypted and persisted from the Settings UI. Never persists anything itself.
    /// </summary>
    ISwitchBotClient CreateAdHocClient(string token, string secret);

    /// <summary>
    /// Convenience wrapper over <see cref="GetClientAsync"/> that returns a full
    /// <see cref="IDeviceProvider"/> (bound to that household's client) for callers
    /// that need the mapped device/status contract rather than the raw JSON client
    /// -- e.g. the Settings page's "Sync devices now" action and the polling
    /// background service.
    /// </summary>
    Task<IDeviceProvider> GetDeviceProviderAsync(Guid householdId, CancellationToken ct = default);
}
