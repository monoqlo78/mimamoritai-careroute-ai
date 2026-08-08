using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Read-only SQL views consumed by the Microsoft Fabric Data Agent.
    /// Keeping them as views (rather than raw tables) gives the agent a stable,
    /// business-meaningful schema and hides implementation details.
    /// SQL Server only; the SQLite demo fallback uses EnsureCreated and skips migrations.
    /// </summary>
    public partial class FabricViews : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_CurrentDeviceStatus AS
                SELECT
                    d.Id                  AS DeviceId,
                    d.HouseholdId         AS HouseholdId,
                    d.Name                AS DeviceName,
                    d.Alias               AS Alias,
                    d.Room                AS Room,
                    d.DeviceType          AS DeviceType,
                    d.RemoteControlAllowed AS RemoteControlAllowed,
                    d.SafetyClass         AS SafetyClass,
                    latest.State          AS CurrentState,
                    latest.OccurredAtUtc  AS LastEventAtUtc
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
                    s.HouseholdId         AS HouseholdId,
                    s.PersonId            AS PersonId,
                    p.DisplayName         AS PersonName,
                    s.Date                AS ActivityDate,
                    s.FirstActivityTime   AS FirstActivityTime,
                    s.LastActivityTime    AS LastActivityTime,
                    s.DeviceUsageCount    AS DeviceUsageCount,
                    s.ActiveMinutes       AS ActiveMinutes,
                    s.NightActivityCount  AS NightActivityCount,
                    s.RiskScore           AS RiskScore,
                    s.RiskLevel           AS RiskLevel
                FROM mimamori.DailyActivitySummaries s
                LEFT JOIN mimamori.People p ON p.Id = s.PersonId;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_RecentDeviceActivity AS
                SELECT
                    e.Id                  AS EventId,
                    e.HouseholdId         AS HouseholdId,
                    e.DeviceId            AS DeviceId,
                    d.Name                AS DeviceName,
                    d.Room                AS Room,
                    d.DeviceType          AS DeviceType,
                    e.EventType           AS EventType,
                    e.State               AS State,
                    e.PowerWatts          AS PowerWatts,
                    e.Source              AS EventSource,
                    e.OccurredAtUtc       AS OccurredAtUtc
                FROM mimamori.DeviceEvents e
                INNER JOIN mimamori.Devices d ON d.Id = e.DeviceId
                WHERE e.OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME());
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS mimamori.vw_RecentDeviceActivity;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS mimamori.vw_DailyActivity;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS mimamori.vw_CurrentDeviceStatus;");
        }
    }
}
