namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Target Fabric SQL database for the operator-console sync.
///
/// There is no secret here on purpose: the App Service managed identity is already a
/// Fabric workspace Admin, so the sync authenticates with a managed-identity token and
/// the configuration only needs to say <em>where</em> to write.
/// </summary>
public sealed class FabricConsoleSyncOptions
{
    public const string SectionName = "FabricConsoleSync";

    public bool Enabled { get; set; }

    /// <summary>e.g. <c>xxxx.database.fabric.microsoft.com</c>, from the Fabric item's serverFqdn.</summary>
    public string ServerFqdn { get; set; } = string.Empty;

    /// <summary>e.g. <c>mimamoritai-admin-{itemId}</c>, from the Fabric item's databaseName.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// How often the background sync runs. The console is an operations view, not a
    /// live feed, so minutes rather than seconds; every cycle is a full idempotent
    /// MERGE, so a missed cycle self-heals rather than leaving a gap.
    /// </summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Matches AdminConsoleService.DefaultWindowDays, so both views agree.</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>
    /// Longer than <see cref="WindowDays"/> so the console keeps a usable time series
    /// even when alerting is quiet, matching scripts/extract-snapshot.sql.
    /// </summary>
    public int ActivityWindowDays { get; set; } = 30;

    /// <summary>
    /// Ceiling on the DeviceEvent rows read per sync for the hourly activity rollup.
    /// The rollup happens in memory (DateTimeOffset part extraction does not translate
    /// on every provider), so this is what stops a long-polled household from turning a
    /// routine sync into a large allocation. The newest events are kept.
    /// </summary>
    public int MaxActivityEvents { get; set; } = 200_000;

    /// <summary>Fabric SQL on a small capacity can be slow to wake, so allow generous timeouts.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 60;

    public int CommandTimeoutSeconds { get; set; } = 120;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ServerFqdn)
        && !string.IsNullOrWhiteSpace(Database);
}
