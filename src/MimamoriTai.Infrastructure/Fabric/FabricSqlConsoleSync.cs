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
        RiskLevel? LatestRiskLevel);

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
        string Source);

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

        var lastEvents = await db.DeviceEvents
            .GroupBy(e => e.HouseholdId)
            .Select(g => new { HouseholdId = g.Key, Last = g.Max(e => e.OccurredAtUtc) })
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

        var householdRows = households
            .Select(h => new HouseholdRow(
                h.Id,
                h.Name,
                h.DataSourceMode,
                memberCounts.GetValueOrDefault(h.Id),
                residentCounts.GetValueOrDefault(h.Id),
                deviceCounts.GetValueOrDefault(h.Id),
                lastEvents.GetValueOrDefault(h.Id),
                switchBotByHousehold.TryGetValue(h.Id, out var sb) ? sb.Status : null,
                switchBotByHousehold.TryGetValue(h.Id, out var sbe) ? sbe.LastErrorMessage : null,
                recipientCounts.GetValueOrDefault(h.Id),
                alertCounts.TryGetValue(h.Id, out var ac) ? ac.Total : 0,
                alertCounts.TryGetValue(h.Id, out var af) ? af.Failed : 0,
                latestRiskLevels.TryGetValue(h.Id, out var risk) ? risk : null))
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

        var activityRows = activityRaw
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
            .Select(g => new ActivityRow(
                g.Key.HouseholdId,
                householdNames.GetValueOrDefault(g.Key.HouseholdId, "(削除済み)"),
                deviceNames.TryGetValue(g.Key.DeviceId, out var d) ? d.Name : "(unknown)",
                deviceNames.TryGetValue(g.Key.DeviceId, out var dt) ? dt.DeviceType.ToString() : string.Empty,
                g.Key.BucketStart,
                g.Count(),
                // "the resident was up and about" -- the states the console charts.
                g.Count(e => e.State == "on" || e.State == "active"),
                g.Max(e => e.Source).ToString()))
            .OrderBy(a => a.BucketStart)
            .ToList();

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

    private static async Task<Dictionary<Guid, int>> CountByHouseholdAsync(
        IQueryable<Guid> householdIds,
        CancellationToken ct) =>
        await householdIds
            .GroupBy(id => id)
            .Select(g => new { HouseholdId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HouseholdId, x => x.Count, ct);

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

    private async Task<int> WriteHouseholdsAsync(SqlConnection connection, Snapshot snapshot, CancellationToken ct)
    {
        const string Sql = """
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
            await ExecuteAsync(connection, Sql, p =>
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

        var written = 0;

        foreach (var b in snapshot.Activity)
        {
            var key = $"activity-bucket:{b.HouseholdId}|{b.DeviceName}|{b.BucketStart:o}";

            await ExecuteAsync(connection, Sql, p =>
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
}
