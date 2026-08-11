using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsProfileViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_AnalyticsProfiles AS
                SELECT
                    h.Id AS HouseholdId,
                    COALESCE(lineProfile.Id, h.Id) AS AnalyticsProfileId,
                    CASE
                        WHEN h.DataSourceMode = N'Sample' THEN N'デモデータ'
                        WHEN lineProfile.Id IS NULL THEN CONCAT(h.Name, N'（LINE未連携）')
                        ELSE CONCAT(COALESCE(NULLIF(lineProfile.DisplayName, N''), h.Name), N'（LINE）')
                    END AS AnalyticsProfileName,
                    CASE WHEN h.DataSourceMode = N'Sample' THEN N'Demo' ELSE N'LineAccount' END AS DataScope,
                    h.DataSourceMode AS DataSourceMode,
                    CAST(CASE WHEN lineProfile.Id IS NULL THEN 0 ELSE 1 END AS bit) AS HasActiveLineAccount
                FROM mimamori.Households h
                OUTER APPLY (
                    SELECT TOP 1 r.Id, r.DisplayName
                    FROM mimamori.LineRecipients r
                    WHERE r.HouseholdId = h.Id AND r.IsActive = 1
                    ORDER BY r.LastSeenAt DESC, r.CreatedAt DESC
                ) lineProfile;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_CurrentDeviceStatus AS
                SELECT
                    d.Id AS DeviceId,
                    d.HouseholdId AS HouseholdId,
                    ap.AnalyticsProfileId AS AnalyticsProfileId,
                    ap.AnalyticsProfileName AS AnalyticsProfileName,
                    ap.DataScope AS DataScope,
                    d.Name AS DeviceName,
                    d.Alias AS Alias,
                    d.Room AS Room,
                    d.DeviceType AS DeviceType,
                    d.RemoteControlAllowed AS RemoteControlAllowed,
                    d.SafetyClass AS SafetyClass,
                    latest.State AS CurrentState,
                    latest.OccurredAtUtc AS LastEventAtUtc
                FROM mimamori.Devices d
                INNER JOIN mimamori.vw_AnalyticsProfiles ap ON ap.HouseholdId = d.HouseholdId
                OUTER APPLY (
                    SELECT TOP 1 e.State, e.OccurredAtUtc
                    FROM mimamori.DeviceEvents e
                    WHERE e.DeviceId = d.Id
                    ORDER BY e.OccurredAtUtc DESC
                ) latest
                WHERE d.IsEnabled = 1 AND d.IsActive = 1;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_DailyActivity AS
                SELECT
                    s.HouseholdId AS HouseholdId,
                    ap.AnalyticsProfileId AS AnalyticsProfileId,
                    ap.AnalyticsProfileName AS AnalyticsProfileName,
                    ap.DataScope AS DataScope,
                    s.PersonId AS PersonId,
                    p.DisplayName AS PersonName,
                    s.Date AS ActivityDate,
                    s.FirstActivityTime AS FirstActivityTime,
                    s.LastActivityTime AS LastActivityTime,
                    s.DeviceUsageCount AS DeviceUsageCount,
                    s.ActiveMinutes AS ActiveMinutes,
                    s.NightActivityCount AS NightActivityCount,
                    s.RiskScore AS RiskScore,
                    s.RiskLevel AS RiskLevel
                FROM mimamori.DailyActivitySummaries s
                INNER JOIN mimamori.vw_AnalyticsProfiles ap ON ap.HouseholdId = s.HouseholdId
                LEFT JOIN mimamori.People p ON p.Id = s.PersonId;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_RecentDeviceActivity AS
                SELECT
                    e.Id AS EventId,
                    e.HouseholdId AS HouseholdId,
                    ap.AnalyticsProfileId AS AnalyticsProfileId,
                    ap.AnalyticsProfileName AS AnalyticsProfileName,
                    ap.DataScope AS DataScope,
                    e.DeviceId AS DeviceId,
                    d.Name AS DeviceName,
                    d.Room AS Room,
                    d.DeviceType AS DeviceType,
                    e.EventType AS EventType,
                    e.State AS State,
                    e.PowerWatts AS PowerWatts,
                    e.Source AS EventSource,
                    e.OccurredAtUtc AS OccurredAtUtc
                FROM mimamori.DeviceEvents e
                INNER JOIN mimamori.Devices d ON d.Id = e.DeviceId
                INNER JOIN mimamori.vw_AnalyticsProfiles ap ON ap.HouseholdId = e.HouseholdId
                WHERE e.OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME());
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_PlugMiniReadings AS
                SELECT
                    r.Id AS ReadingId,
                    r.HouseholdId AS HouseholdId,
                    ap.AnalyticsProfileId AS AnalyticsProfileId,
                    ap.AnalyticsProfileName AS AnalyticsProfileName,
                    ap.DataScope AS DataScope,
                    r.DeviceId AS DeviceId,
                    d.Name AS DeviceName,
                    d.Room AS Room,
                    r.VoltageV AS VoltageV,
                    r.CurrentMa AS CurrentMa,
                    r.DailyEnergyWh AS DailyEnergyWh,
                    r.UsageMinutesToday AS UsageMinutesToday,
                    r.ApproxWatts AS ApproxWatts,
                    r.OccurredAtUtc AS OccurredAtUtc
                FROM mimamori.PlugMiniReadings r
                INNER JOIN mimamori.Devices d ON d.Id = r.DeviceId
                INNER JOIN mimamori.vw_AnalyticsProfiles ap ON ap.HouseholdId = r.HouseholdId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS mimamori.vw_PlugMiniReadings;");

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_CurrentDeviceStatus AS
                SELECT
                    d.Id AS DeviceId,
                    d.HouseholdId AS HouseholdId,
                    d.Name AS DeviceName,
                    d.Alias AS Alias,
                    d.Room AS Room,
                    d.DeviceType AS DeviceType,
                    d.RemoteControlAllowed AS RemoteControlAllowed,
                    d.SafetyClass AS SafetyClass,
                    latest.State AS CurrentState,
                    latest.OccurredAtUtc AS LastEventAtUtc
                FROM mimamori.Devices d
                OUTER APPLY (
                    SELECT TOP 1 e.State, e.OccurredAtUtc
                    FROM mimamori.DeviceEvents e
                    WHERE e.DeviceId = d.Id
                    ORDER BY e.OccurredAtUtc DESC
                ) latest
                WHERE d.IsEnabled = 1;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_DailyActivity AS
                SELECT
                    s.HouseholdId AS HouseholdId,
                    s.PersonId AS PersonId,
                    p.DisplayName AS PersonName,
                    s.Date AS ActivityDate,
                    s.FirstActivityTime AS FirstActivityTime,
                    s.LastActivityTime AS LastActivityTime,
                    s.DeviceUsageCount AS DeviceUsageCount,
                    s.ActiveMinutes AS ActiveMinutes,
                    s.NightActivityCount AS NightActivityCount,
                    s.RiskScore AS RiskScore,
                    s.RiskLevel AS RiskLevel
                FROM mimamori.DailyActivitySummaries s
                LEFT JOIN mimamori.People p ON p.Id = s.PersonId;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_RecentDeviceActivity AS
                SELECT
                    e.Id AS EventId,
                    e.HouseholdId AS HouseholdId,
                    e.DeviceId AS DeviceId,
                    d.Name AS DeviceName,
                    d.Room AS Room,
                    d.DeviceType AS DeviceType,
                    e.EventType AS EventType,
                    e.State AS State,
                    e.PowerWatts AS PowerWatts,
                    e.Source AS EventSource,
                    e.OccurredAtUtc AS OccurredAtUtc
                FROM mimamori.DeviceEvents e
                INNER JOIN mimamori.Devices d ON d.Id = e.DeviceId
                WHERE e.OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME());
                """);

            migrationBuilder.Sql("DROP VIEW IF EXISTS mimamori.vw_AnalyticsProfiles;");
        }
    }
}
