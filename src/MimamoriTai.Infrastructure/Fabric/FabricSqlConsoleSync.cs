using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Reads the cross-household operator rollup from the app's own database and MERGEs it
/// into the Fabric SQL database behind the Rayfin console.
///
/// This replaces <c>fabric-app/scripts/sync-to-fabric.ps1</c> as the ingestion path.
/// The script had two problems this class does not:
///   1. it only ran when a human ran it, so the console froze at whatever the last
///      manual run captured; and
///   2. it could not reach Fabric SQL from a developer machine at all, because Fabric
///      SQL uses the Azure SQL Redirect connection policy (connect on 1433, then get
///      redirected to a node port in 11000-11999) and that range is blocked on normal
///      networks. Running inside Azure is what makes the redirect reachable.
///
/// The aggregation deliberately mirrors <c>AdminConsoleService.LoadAsync</c> and
/// <c>scripts/extract-snapshot.sql</c> so the operator console and the Fabric console
/// cannot disagree about the same window. It reads only counts and machine-generated
/// text: no prompt/completion bodies, no encrypted SwitchBot credentials, and not the
/// family-facing <c>WatchAlert.Message</c>, which can name the resident.
/// </summary>
public sealed class FabricSqlConsoleSync(
    IAppDbContext db,
    TokenCredential credential,
    IOptions<FabricConsoleSyncOptions> options,
    TimeProvider clock,
    ILogger<FabricSqlConsoleSync> logger) : IFabricConsoleSync
{
    private static readonly string[] SqlScopes = ["https://database.windows.net/.default"];

    /// <summary>
    /// Longest gap that still counts as continuous draw when integrating plug samples.
    /// Matches <c>PowerUsageService</c> so the console and the family app cannot disagree
    /// about how much electricity the same hour used.
    /// </summary>
    private const int MaxSampleSpanMinutes = 10;

    private readonly FabricConsoleSyncOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<FabricConsoleSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();

        if (!IsConfigured)
        {
            return FabricConsoleSyncResult.Failed("Fabric console sync is not configured.", 0);
        }

        try
        {
            var snapshot = await BuildSnapshotAsync(ct);

            var token = await credential.GetTokenAsync(new TokenRequestContext(SqlScopes), ct);

            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = $"{_options.ServerFqdn.Split(',')[0]},1433",
                InitialCatalog = _options.Database,
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = false,
                ConnectTimeout = _options.ConnectTimeoutSeconds,
                CommandTimeout = _options.CommandTimeoutSeconds,
            }.ConnectionString;

            await using var connection = new SqlConnection(connectionString) { AccessToken = token.Token };
            await connection.OpenAsync(ct);

            var households = await WriteHouseholdsAsync(connection, snapshot, ct);
            var alerts = await WriteAlertsAsync(connection, snapshot, ct);
            var activity = await WriteActivityAsync(connection, snapshot, ct);
            var aiCalls = await WriteAiRouterCallsAsync(connection, snapshot, ct);

            await LogAiRouterTotalsAsync(connection, snapshot, ct);

            var elapsed = ElapsedMs(started);
            logger.LogInformation(
                "Fabric console sync wrote {Households} household(s), {Alerts} alert(s), {Activity} activity bucket(s) and {AiCalls} AI router group(s) in {Elapsed}ms.",
                households, alerts, activity, aiCalls, elapsed);

            return new FabricConsoleSyncResult(true, households, alerts, activity, aiCalls, elapsed, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never surface the raw exception text to callers: it embeds the Fabric
            // server FQDN and, on auth failures, token diagnostics.
            logger.LogWarning(ex, "Fabric console sync failed; the next cycle will retry.");
            return FabricConsoleSyncResult.Failed($"{ex.GetType().Name}: {ex.Message}", ElapsedMs(started));
        }
    }

    private static long ElapsedMs(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // ---------------------------------------------------------------- read side

    internal sealed record HouseholdRow(
        Guid HouseholdId,
        string Name,
        DataSourceMode DataSourceMode,
        int MemberCount,
        int ResidentCount,
        int DeviceCount,
        DateTimeOffset? LastEventUtc,
        SwitchBotConnectionStatus? SwitchBotStatus,
        string? SwitchBotError,
        int ActiveLineRecipients,
        int AlertsInWindow,
        int FailedAlertsInWindow,
        RiskLevel? LatestRiskLevel,
        // Defaulted so the many call sites that only care about the operational
        // counters do not have to state a power figure they have no view of.
        double PowerTodayWh = 0,
        double? PowerBaselineWh = null,
        PowerUsageTrend PowerTrend = PowerUsageTrend.Unknown);

    internal sealed record AlertRow(
        Guid AlertId,
        Guid HouseholdId,
        string HouseholdName,
        RiskLevel RiskLevel,
        int Score,
        string Reason,
        bool Success,
        string? Error,
        DateTimeOffset SentAtUtc);

    internal sealed record ActivityRow(
        Guid HouseholdId,
        string HouseholdName,
        string DeviceName,
        string DeviceType,
        DateTime BucketStart,
        int EventCount,
        int OnCount,
        string Source,
        /// <summary>
        /// Watt-hours drawn in the hour. Null when the device is not metered, which
        /// the console must render as a gap rather than a measured zero.
        /// </summary>
        double? EnergyWh = null);

    internal sealed record AiCallRow(
        string Purpose,
        string Router,
        string ResolvedModel,
        int CallCount,
        int SuccessCount,
        long AvgDurationMs,
        DateTimeOffset LastCalledAt);

    internal sealed record Snapshot(
        DateTimeOffset CapturedAt,
        IReadOnlyList<HouseholdRow> Households,
        IReadOnlyList<AlertRow> Alerts,
        IReadOnlyList<ActivityRow> Activity,
        IReadOnlyList<AiCallRow> AiCalls);

    internal async Task<Snapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddDays(-Math.Max(1, _options.WindowDays));
        var activitySince = now.AddDays(-Math.Max(1, _options.ActivityWindowDays));

        var households = await db.Households
            .OrderBy(h => h.DataSourceMode)
            .ThenBy(h => h.CreatedAtUtc)
            .Select(h => new { h.Id, h.Name, h.DataSourceMode })
            .ToListAsync(ct);

        var householdNames = households.ToDictionary(h => h.Id, h => h.Name);

        var memberCounts = await CountByHouseholdAsync(db.HouseholdMembers.Select(m => m.HouseholdId), ct);
        var residentCounts = await CountByHouseholdAsync(
            db.People.Where(p => p.Role == PersonRole.Resident).Select(p => p.HouseholdId), ct);
        var deviceCounts = await CountByHouseholdAsync(db.Devices.Select(d => d.HouseholdId), ct);
        var recipientCounts = await CountByHouseholdAsync(
            db.LineRecipients.Where(r => r.IsActive).Select(r => r.HouseholdId), ct);

        var lastDeviceEvents = await db.DeviceEvents
            .GroupBy(e => e.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Last = g.Max(e => e.OccurredAtUtc) })
            .ToDictionaryAsync(x => x.HouseholdId, x => (DateTimeOffset?)x.Last, ct);

        var lastPlugReadings = await db.PlugMiniReadings
            .GroupBy(r => r.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Last = g.Max(r => r.OccurredAtUtc) })
            .ToDictionaryAsync(x => x.HouseholdId, x => (DateTimeOffset?)x.Last, ct);

        // Only the status/error fields; the Encrypted* columns are never selected.
        var switchBot = await db.SwitchBotConnections
            .Select(c => new { c.HouseholdId, c.Status, c.LastErrorMessage })
            .ToListAsync(ct);
        var switchBotByHousehold = switchBot
            .GroupBy(c => c.HouseholdId)
            .ToDictionary(g => g.Key, g => g.First());

        var alertCounts = await db.WatchAlerts
            .Where(a => a.SentAtUtc >= since)
            .GroupBy(a => a.HouseholdId)
            .Select(g => new
            {
                HouseholdId = g.Key,
                Total = g.Count(),
                Failed = g.Count(a => !a.Success),
            })
            .ToDictionaryAsync(x => x.HouseholdId, x => x, ct);

        var latestRisk = await db.RiskAssessments
            .GroupBy(r => r.HouseholdId)
            .Select(g => new
            {
                HouseholdId = g.Key,
                LatestAt = g.Max(r => r.CreatedAtUtc),
            })
            .ToListAsync(ct);

        var latestRiskLevels = new Dictionary<Guid, RiskLevel>();
        foreach (var entry in latestRisk)
        {
            var level = await db.RiskAssessments
                .Where(r => r.HouseholdId == entry.HouseholdId && r.CreatedAtUtc == entry.LatestAt)
                .Select(r => (RiskLevel?)r.RiskLevel)
                .FirstOrDefaultAsync(ct);

            if (level is not null)
            {
                latestRiskLevels[entry.HouseholdId] = level.Value;
            }
        }

        // Electricity is the one signal here that comes from measurement rather than
        // counting, so it is computed per household with the same service the family
        // app uses -- an operator and a family must never see different numbers.
        var power = new Dictionary<Guid, PowerUsageSummary>();
        foreach (var h in households)
        {
            power[h.Id] = await new PowerUsageService(db, clock).GetAsync(h.Id, ct: ct);
        }

        var householdRows = households
            .Select(h => new HouseholdRow(
                h.Id,
                h.Name,
                h.DataSourceMode,
                memberCounts.GetValueOrDefault(h.Id),
                residentCounts.GetValueOrDefault(h.Id),
                deviceCounts.GetValueOrDefault(h.Id),
                Max(lastDeviceEvents.GetValueOrDefault(h.Id), lastPlugReadings.GetValueOrDefault(h.Id)),
                switchBotByHousehold.TryGetValue(h.Id, out var sb) ? sb.Status : null,
                switchBotByHousehold.TryGetValue(h.Id, out var sbe) ? sbe.LastErrorMessage : null,
                recipientCounts.GetValueOrDefault(h.Id),
                alertCounts.TryGetValue(h.Id, out var ac) ? ac.Total : 0,
                alertCounts.TryGetValue(h.Id, out var af) ? af.Failed : 0,
                latestRiskLevels.TryGetValue(h.Id, out var risk) ? risk : null,
                power[h.Id].TodayWh,
                power[h.Id].Headline.Baseline,
                power[h.Id].Headline.Trend))
            .ToList();

        // WatchAlert.Message is intentionally not selected: it is family-facing prose
        // that can name the resident. Only the machine-generated Reason is mirrored.
        var alertRows = (await db.WatchAlerts
            .Where(a => a.SentAtUtc >= since)
            .OrderByDescending(a => a.SentAtUtc)
            .Take(50)
            .Select(a => new
            {
                a.Id,
                a.HouseholdId,
                a.RiskLevel,
                a.Score,
                a.Reason,
                a.Success,
                a.Error,
                a.SentAtUtc,
            })
            .ToListAsync(ct))
            .Select(a => new AlertRow(
                a.Id,
                a.HouseholdId,
                householdNames.GetValueOrDefault(a.HouseholdId, "(削除済み)"),
                a.RiskLevel,
                a.Score,
                a.Reason,
                a.Success,
                a.Error,
                a.SentAtUtc))
            .ToList();

        var deviceNames = await db.Devices
            .Select(d => new { d.Id, d.Name, d.DeviceType })
            .ToDictionaryAsync(d => d.Id, d => d, ct);

        // Rolled up in memory rather than with a GROUP BY on the hour parts, because
        // DateTimeOffset component access does not translate on every provider the app
        // runs against, and a query that only fails on one of them is worse than one
        // that is a little less efficient everywhere.
        //
        // The cost is bounded on purpose: only five scalar columns are read, the window
        // is capped by ActivityWindowDays, and MaxActivityEvents caps the row count, so a
        // household polled for months cannot make this allocate without limit. The newest
        // events are kept, since a truncated tail is what an operator is least likely to
        // be looking at.
        var activityRaw = await db.DeviceEvents
            .Where(e => e.OccurredAtUtc >= activitySince)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(Math.Max(1000, _options.MaxActivityEvents))
            .Select(e => new
            {
                e.HouseholdId,
                e.DeviceId,
                e.OccurredAtUtc,
                e.State,
                e.Source,
            })
            .ToListAsync(ct);

        var activityRows = MergeHourlyEnergy(
            activityRaw
                .GroupBy(e => new
                {
                    e.HouseholdId,
                    e.DeviceId,
                    BucketStart = new DateTime(
                        e.OccurredAtUtc.UtcDateTime.Year,
                        e.OccurredAtUtc.UtcDateTime.Month,
                        e.OccurredAtUtc.UtcDateTime.Day,
                        e.OccurredAtUtc.UtcDateTime.Hour,
                        0, 0, DateTimeKind.Utc),
                })
                .Select(g => (
                    g.Key.HouseholdId,
                    g.Key.DeviceId,
                    Row: new ActivityRow(
                        g.Key.HouseholdId,
                        householdNames.GetValueOrDefault(g.Key.HouseholdId, "(削除済み)"),
                        deviceNames.TryGetValue(g.Key.DeviceId, out var d) ? d.Name : "(unknown)",
                        deviceNames.TryGetValue(g.Key.DeviceId, out var dt) ? dt.DeviceType.ToString() : string.Empty,
                        g.Key.BucketStart,
                        g.Count(),
                        // "the resident was up and about" -- the states the console charts.
                        g.Count(e => e.State == "on" || e.State == "active"),
                        g.Max(e => e.Source).ToString())))
                .ToList(),
            await HourlyEnergyAsync(activitySince, ct),
            householdNames,
            deviceNames.ToDictionary(kv => kv.Key, kv => (kv.Value.Name, kv.Value.DeviceType.ToString())));

        // No time window on purpose: callCount is an all-time total, so the console's
        // "OrcaRouter calls" number only ever moves forward.
        var aiCalls = (await db.AiRequestLogs
            .GroupBy(l => new { l.Purpose, l.Router, l.ResolvedModel })
            .Select(g => new
            {
                g.Key.Purpose,
                g.Key.Router,
                g.Key.ResolvedModel,
                CallCount = g.Count(),
                SuccessCount = g.Count(l => l.Success),
                AvgDurationMs = g.Average(l => (double)l.DurationMs),
                LastCalledAt = g.Max(l => l.CreatedAtUtc),
            })
            .ToListAsync(ct))
            .OrderByDescending(g => g.CallCount)
            .Select(g => new AiCallRow(
                g.Purpose,
                g.Router,
                g.ResolvedModel,
                g.CallCount,
                g.SuccessCount,
                (long)Math.Round(g.AvgDurationMs, MidpointRounding.AwayFromZero),
                g.LastCalledAt))
            .ToList();

        return new Snapshot(now, householdRows, alertRows, activityRows, aiCalls);
    }

    /// <summary>
    /// Integrates the metered plugs' real power into watt-hours per (household, device, hour).
    ///
    /// The plug reports instantaneous watts, so energy is the area under a zero-order hold
    /// between samples. A gap longer than <see cref="MaxSampleSpanMinutes"/> is treated as
    /// missing data rather than a long steady draw: the poller stopping is not evidence the
    /// kettle stayed on, and inventing that area is how an outage turns into a fake spike.
    /// Spans that straddle an hour boundary are split so each hour is charged only its share.
    /// </summary>
    private async Task<Dictionary<(Guid Household, Guid Device, DateTime Hour), double>> HourlyEnergyAsync(
        DateTimeOffset since,
        CancellationToken ct)
    {
        var readings = await db.PlugMiniReadings
            .Where(r => r.OccurredAtUtc >= since && r.DailyEnergyWh != null)
            .OrderBy(r => r.OccurredAtUtc)
            .Select(r => new { r.HouseholdId, r.DeviceId, r.OccurredAtUtc, Watts = r.DailyEnergyWh!.Value })
            .ToListAsync(ct);

        var energy = new Dictionary<(Guid, Guid, DateTime), double>();
        var maxSpan = TimeSpan.FromMinutes(MaxSampleSpanMinutes);

        foreach (var device in readings.GroupBy(r => (r.HouseholdId, r.DeviceId)))
        {
            var samples = device.OrderBy(r => r.OccurredAtUtc).ToList();
            for (var i = 0; i < samples.Count - 1; i++)
            {
                var watts = samples[i].Watts;
                if (watts <= 0)
                {
                    continue;
                }

                var start = samples[i].OccurredAtUtc.UtcDateTime;
                var end = samples[i + 1].OccurredAtUtc.UtcDateTime;
                if (end - start > maxSpan)
                {
                    end = start + maxSpan;
                }

                while (start < end)
                {
                    var hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
                    var sliceEnd = hour.AddHours(1) < end ? hour.AddHours(1) : end;
                    var key = (device.Key.HouseholdId, device.Key.DeviceId, hour);
                    energy[key] = energy.GetValueOrDefault(key) + (watts * (sliceEnd - start).TotalHours);
                    start = sliceEnd;
                }
            }
        }

        return energy;
    }

    /// <summary>
    /// Joins the event-derived buckets with the metered hours.
    ///
    /// An always-on appliance emits no events, so hours that only have a power reading
    /// are emitted as their own zero-event rows. Without that the electricity chart would
    /// silently skip exactly the quiet stretches an operator most wants to see.
    /// </summary>
    private static List<ActivityRow> MergeHourlyEnergy(
        List<(Guid HouseholdId, Guid DeviceId, ActivityRow Row)> buckets,
        Dictionary<(Guid Household, Guid Device, DateTime Hour), double> energy,
        Dictionary<Guid, string> householdNames,
        Dictionary<Guid, (string Name, string Type)> devices)
    {
        var rows = new List<ActivityRow>(buckets.Count + energy.Count);
        var claimed = new HashSet<(Guid, Guid, DateTime)>();

        foreach (var (householdId, deviceId, row) in buckets)
        {
            var key = (householdId, deviceId, row.BucketStart);
            claimed.Add(key);
            rows.Add(energy.TryGetValue(key, out var wh) ? row with { EnergyWh = Math.Round(wh, 3) } : row);
        }

        foreach (var (key, wh) in energy)
        {
            if (claimed.Contains(key))
            {
                continue;
            }

            var device = devices.TryGetValue(key.Device, out var d) ? d : ("(unknown)", string.Empty);
            rows.Add(new ActivityRow(
                key.Household,
                householdNames.GetValueOrDefault(key.Household, "(削除済み)"),
                device.Item1,
                device.Item2,
                key.Hour,
                0,
                0,
                nameof(EventSource.SwitchBotPoll),
                Math.Round(wh, 3)));
        }

        return rows.OrderBy(a => a.BucketStart).ToList();
    }

    private static async Task<Dictionary<Guid, int>> CountByHouseholdAsync(
        IQueryable<Guid> householdIds,
        CancellationToken ct) =>
        await householdIds
            .GroupBy(id => id)
            .Select(g => new { HouseholdId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HouseholdId, x => x.Count, ct);

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    // --------------------------------------------------------------- write side

    /// <summary>
    /// Stable surrogate key for rows whose source has no natural key.
    ///
    /// MD5 here is a hashing function, not a security control: it makes re-running the
    /// sync update the same row instead of inserting a duplicate. The scheme matches
    /// scripts/sync-to-fabric.ps1 byte for byte so rows written by either path collide
    /// deliberately rather than accumulating.
    /// </summary>
    internal static Guid DeterministicId(string key) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// The Rayfin-generated columns are text, and the console renders an empty string
    /// as "unknown", so nulls are normalised here rather than in each query.
    /// </summary>
    private static string Text(string? value) => value ?? string.Empty;

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<int> ExecuteAsync(
        SqlConnection connection,
        string sql,
        Action<SqlParameterCollection> bind,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        bind(command.Parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HasColumnsAsync(
        SqlConnection connection,
        string table,
        string[] columns,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        var names = string.Join(", ", columns.Select((_, i) => $"@c{i}"));
        command.CommandText = $"""
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @table
              AND COLUMN_NAME IN ({names});
            """;
        command.CommandTimeout = 60;
        command.Parameters.AddWithValue("@table", table);
        for (var i = 0; i < columns.Length; i++)
        {
            command.Parameters.AddWithValue($"@c{i}", columns[i]);
        }

        var found = await command.ExecuteScalarAsync(ct);
        return found is int count && count == columns.Length;
    }

    private static Task<bool> HasPowerColumnsAsync(SqlConnection connection, CancellationToken ct) =>
        HasColumnsAsync(
            connection,
            "HouseholdSnapshots",
            ["powerTodayWh", "powerBaselineWh", "powerTrend"],
            ct);

    private async Task<int> WriteHouseholdsAsync(SqlConnection connection, Snapshot snapshot, CancellationToken ct)
    {
        // The power columns were added to the Rayfin model after the table already
        // existed in Fabric. Probing once per run means a workspace that has not
        // picked up the new model yet still gets every other operational figure
        // instead of losing the whole household sync to an invalid-column error.
        var hasPower = await HasPowerColumnsAsync(connection, ct);
        if (!hasPower)
        {
            // Worth saying out loud: the console will silently look like the power
            // work was never done, and the fix is a Rayfin model apply, not a code change.
            logger.LogWarning(
                "dbo.HouseholdSnapshots has no power columns; skipping electricity use. Run `npm run rayfin:db` in fabric-app.");
        }

        var sql = hasPower
            ? """
            MERGE dbo.HouseholdSnapshots AS t
            USING (SELECT @id AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET
                householdId = @householdId, name = @name, dataSourceMode = @dataSourceMode,
                memberCount = @memberCount, residentCount = @residentCount, deviceCount = @deviceCount,
                lastEventUtc = @lastEventUtc, switchBotStatus = @switchBotStatus, switchBotError = @switchBotError,
                activeLineRecipients = @activeLineRecipients, alertsInWindow = @alertsInWindow,
                failedAlertsInWindow = @failedAlertsInWindow, latestRiskLevel = @latestRiskLevel,
                needsAttention = @needsAttention, capturedAt = @capturedAt,
                powerTodayWh = @powerTodayWh, powerBaselineWh = @powerBaselineWh,
                powerTrend = @powerTrend
            WHEN NOT MATCHED THEN INSERT
                (id, householdId, name, dataSourceMode, memberCount, residentCount, deviceCount,
                 lastEventUtc, switchBotStatus, switchBotError, activeLineRecipients, alertsInWindow,
                 failedAlertsInWindow, latestRiskLevel, needsAttention, capturedAt,
                 powerTodayWh, powerBaselineWh, powerTrend)
            VALUES
                (@id, @householdId, @name, @dataSourceMode, @memberCount, @residentCount, @deviceCount,
                 @lastEventUtc, @switchBotStatus, @switchBotError, @activeLineRecipients, @alertsInWindow,
                 @failedAlertsInWindow, @latestRiskLevel, @needsAttention, @capturedAt,
                 @powerTodayWh, @powerBaselineWh, @powerTrend);
            """
            : """
            MERGE dbo.HouseholdSnapshots AS t
            USING (SELECT @id AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET
                householdId = @householdId, name = @name, dataSourceMode = @dataSourceMode,
                memberCount = @memberCount, residentCount = @residentCount, deviceCount = @deviceCount,
                lastEventUtc = @lastEventUtc, switchBotStatus = @switchBotStatus, switchBotError = @switchBotError,
                activeLineRecipients = @activeLineRecipients, alertsInWindow = @alertsInWindow,
                failedAlertsInWindow = @failedAlertsInWindow, latestRiskLevel = @latestRiskLevel,
                needsAttention = @needsAttention, capturedAt = @capturedAt
            WHEN NOT MATCHED THEN INSERT
                (id, householdId, name, dataSourceMode, memberCount, residentCount, deviceCount,
                 lastEventUtc, switchBotStatus, switchBotError, activeLineRecipients, alertsInWindow,
                 failedAlertsInWindow, latestRiskLevel, needsAttention, capturedAt)
            VALUES
                (@id, @householdId, @name, @dataSourceMode, @memberCount, @residentCount, @deviceCount,
                 @lastEventUtc, @switchBotStatus, @switchBotError, @activeLineRecipients, @alertsInWindow,
                 @failedAlertsInWindow, @latestRiskLevel, @needsAttention, @capturedAt);
            """;

        var written = 0;

        foreach (var h in snapshot.Households)
        {
            await ExecuteAsync(connection, sql, p =>
            {
                p.AddWithValue("@id", DeterministicId($"household-snapshot:{h.HouseholdId}"));
                p.AddWithValue("@householdId", h.HouseholdId.ToString());
                p.AddWithValue("@name", Text(h.Name));
                p.AddWithValue("@dataSourceMode", h.DataSourceMode.ToString());
                p.AddWithValue("@memberCount", Num(h.MemberCount));
                p.AddWithValue("@residentCount", Num(h.ResidentCount));
                p.AddWithValue("@deviceCount", Num(h.DeviceCount));
                p.AddWithValue("@lastEventUtc", h.LastEventUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty);
                p.AddWithValue("@switchBotStatus", h.SwitchBotStatus?.ToString() ?? string.Empty);
                p.AddWithValue("@switchBotError", Text(h.SwitchBotError));
                p.AddWithValue("@activeLineRecipients", Num(h.ActiveLineRecipients));
                p.AddWithValue("@alertsInWindow", Num(h.AlertsInWindow));
                p.AddWithValue("@failedAlertsInWindow", Num(h.FailedAlertsInWindow));
                p.AddWithValue("@latestRiskLevel", h.LatestRiskLevel?.ToString() ?? string.Empty);
                p.AddWithValue("@needsAttention", NeedsAttention(h));
                p.AddWithValue("@capturedAt", snapshot.CapturedAt.UtcDateTime);

                // Sent as text like every other measure on this table, so the console
                // renders it without a schema migration on the Fabric side.
                p.AddWithValue("@powerTodayWh", h.PowerTodayWh.ToString("0.##", CultureInfo.InvariantCulture));
                p.AddWithValue("@powerBaselineWh",
                    h.PowerBaselineWh?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty);
                p.AddWithValue("@powerTrend", h.PowerTrend.ToString());
            }, ct);

            written++;
        }

        return written;
    }

    /// <summary>
    /// Mirrors <c>AdminConsoleService.NeedsAttention</c>: a household is flagged when an
    /// operator would actually have to do something.
    /// </summary>
    internal static bool NeedsAttention(HouseholdRow row) =>
        row.FailedAlertsInWindow > 0
        || row.SwitchBotStatus == SwitchBotConnectionStatus.Error
        || (row.DataSourceMode == DataSourceMode.Production && row.ActiveLineRecipients == 0);

    private async Task<int> WriteAlertsAsync(SqlConnection connection, Snapshot snapshot, CancellationToken ct)
    {
        const string Sql = """
            MERGE dbo.AlertRecords AS t
            USING (SELECT @id AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET
                householdId = @householdId, householdName = @householdName, riskLevel = @riskLevel,
                score = @score, reason = @reason, success = @success, error = @error, sentAt = @sentAt
            WHEN NOT MATCHED THEN INSERT
                (id, householdId, householdName, riskLevel, score, reason, success, error, sentAt)
            VALUES
                (@id, @householdId, @householdName, @riskLevel, @score, @reason, @success, @error, @sentAt);
            """;

        var written = 0;

        foreach (var a in snapshot.Alerts)
        {
            await ExecuteAsync(connection, Sql, p =>
            {
                p.AddWithValue("@id", a.AlertId);
                p.AddWithValue("@householdId", a.HouseholdId.ToString());
                p.AddWithValue("@householdName", Text(a.HouseholdName));
                p.AddWithValue("@riskLevel", a.RiskLevel.ToString());
                p.AddWithValue("@score", Num(a.Score));
                p.AddWithValue("@reason", Text(a.Reason));
                p.AddWithValue("@success", a.Success);
                p.AddWithValue("@error", Text(a.Error));
                p.AddWithValue("@sentAt", a.SentAtUtc.UtcDateTime);
            }, ct);

            written++;
        }

        return written;
    }

    private async Task<int> WriteActivityAsync(SqlConnection connection, Snapshot snapshot, CancellationToken ct)
    {
        const string Sql = """
            MERGE dbo.ActivityBuckets AS t
            USING (SELECT @id AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET
                householdId = @householdId, householdName = @householdName, deviceName = @deviceName,
                deviceType = @deviceType, bucketStart = @bucketStart, eventCount = @eventCount,
                onCount = @onCount, source = @source
            WHEN NOT MATCHED THEN INSERT
                (id, householdId, householdName, deviceName, deviceType, bucketStart, eventCount, onCount, source)
            VALUES
                (@id, @householdId, @householdName, @deviceName, @deviceType, @bucketStart, @eventCount, @onCount, @source);
            """;

        const string SqlWithEnergy = """
            MERGE dbo.ActivityBuckets AS t
            USING (SELECT @id AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET
                householdId = @householdId, householdName = @householdName, deviceName = @deviceName,
                deviceType = @deviceType, bucketStart = @bucketStart, eventCount = @eventCount,
                onCount = @onCount, source = @source, energyWh = @energyWh
            WHEN NOT MATCHED THEN INSERT
                (id, householdId, householdName, deviceName, deviceType, bucketStart, eventCount, onCount, source, energyWh)
            VALUES
                (@id, @householdId, @householdName, @deviceName, @deviceType, @bucketStart, @eventCount, @onCount, @source, @energyWh);
            """;

        // Same reasoning as the household power columns: a workspace still on the older
        // Rayfin model keeps receiving activity instead of failing the whole write.
        var hasEnergy = await HasColumnsAsync(connection, "ActivityBuckets", ["energyWh"], ct);
        if (!hasEnergy)
        {
            logger.LogWarning(
                "Fabric console table dbo.ActivityBuckets has no energyWh column, so hourly electricity will not appear in the console. Run 'npm run rayfin:db' in fabric-app to apply the current model.");
        }

        var written = 0;

        foreach (var b in snapshot.Activity)
        {
            var key = $"activity-bucket:{b.HouseholdId}|{b.DeviceName}|{b.BucketStart:o}";

            await ExecuteAsync(connection, hasEnergy ? SqlWithEnergy : Sql, p =>
            {
                p.AddWithValue("@id", DeterministicId(key));
                p.AddWithValue("@householdId", b.HouseholdId.ToString());
                p.AddWithValue("@householdName", Text(b.HouseholdName));
                p.AddWithValue("@deviceName", Text(b.DeviceName));
                p.AddWithValue("@deviceType", Text(b.DeviceType));
                p.AddWithValue("@bucketStart", b.BucketStart);
                p.AddWithValue("@eventCount", Num(b.EventCount));
                p.AddWithValue("@onCount", Num(b.OnCount));
                p.AddWithValue("@source", Text(b.Source));

                if (hasEnergy)
                {
                    // Empty, not "0": an unmetered hour is unknown, and the console
                    // draws a gap for it instead of a floor.
                    p.AddWithValue(
                        "@energyWh",
                        b.EnergyWh is { } wh ? wh.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty);
                }
            }, ct);

            written++;
        }

        return written;
    }

    private async Task<int> WriteAiRouterCallsAsync(SqlConnection connection, Snapshot snapshot, CancellationToken ct)
    {
        const string Sql = """
            MERGE dbo.AiRouterCalls AS t
            USING (SELECT @id AS id) AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET
                purpose = @purpose, router = @router, resolvedModel = @resolvedModel,
                callCount = @callCount, successCount = @successCount,
                avgDurationMs = @avgDurationMs, lastCalledAt = @lastCalledAt
            WHEN NOT MATCHED THEN INSERT
                (id, purpose, router, resolvedModel, callCount, successCount, avgDurationMs, lastCalledAt)
            VALUES
                (@id, @purpose, @router, @resolvedModel, @callCount, @successCount, @avgDurationMs, @lastCalledAt);
            """;

        var written = 0;

        foreach (var c in snapshot.AiCalls)
        {
            var key = $"ai-router-call:{c.Purpose}|{c.Router}|{c.ResolvedModel}";

            await ExecuteAsync(connection, Sql, p =>
            {
                p.AddWithValue("@id", DeterministicId(key));
                p.AddWithValue("@purpose", Text(c.Purpose));
                p.AddWithValue("@router", Text(c.Router));
                p.AddWithValue("@resolvedModel", Text(c.ResolvedModel));
                p.AddWithValue("@callCount", Num(c.CallCount));
                p.AddWithValue("@successCount", Num(c.SuccessCount));
                p.AddWithValue("@avgDurationMs", Num(c.AvgDurationMs));
                p.AddWithValue("@lastCalledAt", c.LastCalledAt.UtcDateTime);
            }, ct);

            written++;
        }

        return written;
    }

    // The console shows one number above the model chart: the sum of callCount
    // over every row whose router is not the offline stub. When that number looks
    // wrong there are three places it can go wrong -- the source table, this
    // rollup, or the read the console does -- and from the outside they are
    // indistinguishable. Log both ends of the write so the log alone says which.
    //
    // Reading it back rather than trusting the MERGE also catches the one failure
    // that would otherwise be silent: rows landing in the table but the console
    // still drawing its bundled snapshot, which happens to total a similar number.
    private async Task LogAiRouterTotalsAsync(SqlConnection connection, Snapshot snapshot, CancellationToken ct)
    {
        const string MockRouter = "MockAiRouter";

        var sourceRows = snapshot.AiCalls.Count;
        var sourceTotal = snapshot.AiCalls.Sum(c => (long)c.CallCount);
        var sourceViaRouter = snapshot.AiCalls
            .Where(c => !string.Equals(c.Router, MockRouter, StringComparison.Ordinal))
            .Sum(c => (long)c.CallCount);

        // callCount is NVARCHAR in the Rayfin-generated table, so cast defensively:
        // a single unparseable row must not take the whole diagnostic down.
        const string Sql = """
            SELECT
                COUNT(*),
                SUM(TRY_CAST(callCount AS BIGINT)),
                SUM(CASE WHEN router <> @mock THEN TRY_CAST(callCount AS BIGINT) ELSE 0 END)
            FROM dbo.AiRouterCalls;
            """;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Sql;
            command.CommandTimeout = _options.CommandTimeoutSeconds;
            command.Parameters.AddWithValue("@mock", MockRouter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return;

            var fabricRows = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var fabricTotal = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
            var fabricViaRouter = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);

            logger.LogInformation(
                "Fabric AI router rollup: source {SourceRows} row(s) / {SourceTotal} call(s) / {SourceViaRouter} via OrcaRouter; " +
                "Fabric now holds {FabricRows} row(s) / {FabricTotal} call(s) / {FabricViaRouter} via OrcaRouter.",
                sourceRows, sourceTotal, sourceViaRouter, fabricRows, fabricTotal, fabricViaRouter);

            if (fabricViaRouter != sourceViaRouter)
            {
                // Rows the rollup no longer produces are never deleted, so Fabric
                // holding more than the source means stale grains are still counted.
                logger.LogWarning(
                    "Fabric AI router total {FabricViaRouter} does not match the source total {SourceViaRouter}; " +
                    "the console will show the Fabric figure.",
                    fabricViaRouter, sourceViaRouter);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diagnostics must never fail the sync: the write above already succeeded.
            logger.LogWarning(ex, "Could not read the AI router totals back from Fabric.");
        }
    }
}
