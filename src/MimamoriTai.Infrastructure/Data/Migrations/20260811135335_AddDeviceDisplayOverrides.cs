using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceDisplayOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayNameOverride",
                schema: "mimamori",
                table: "Devices",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomOverride",
                schema: "mimamori",
                table: "Devices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // 分析ビューも利用者が直した呼び名・部屋を返すようにする（Fabric 側の回答が古い名前にならないように）。
            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW mimamori.vw_CurrentDeviceStatus AS
                SELECT
                    d.Id AS DeviceId,
                    d.HouseholdId AS HouseholdId,
                    ap.AnalyticsProfileId AS AnalyticsProfileId,
                    ap.AnalyticsProfileName AS AnalyticsProfileName,
                    ap.DataScope AS DataScope,
                    COALESCE(NULLIF(d.DisplayNameOverride, N''), d.Name) AS DeviceName,
                    d.Name AS ProviderDeviceName,
                    d.Alias AS Alias,
                    COALESCE(NULLIF(d.RoomOverride, N''), d.Room) AS Room,
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
                CREATE OR ALTER VIEW mimamori.vw_RecentDeviceActivity AS
                SELECT
                    e.Id AS EventId,
                    e.HouseholdId AS HouseholdId,
                    ap.AnalyticsProfileId AS AnalyticsProfileId,
                    ap.AnalyticsProfileName AS AnalyticsProfileName,
                    ap.DataScope AS DataScope,
                    e.DeviceId AS DeviceId,
                    COALESCE(NULLIF(d.DisplayNameOverride, N''), d.Name) AS DeviceName,
                    d.Name AS ProviderDeviceName,
                    COALESCE(NULLIF(d.RoomOverride, N''), d.Room) AS Room,
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
                    COALESCE(NULLIF(d.DisplayNameOverride, N''), d.Name) AS DeviceName,
                    d.Name AS ProviderDeviceName,
                    COALESCE(NULLIF(d.RoomOverride, N''), d.Room) AS Room,
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

            migrationBuilder.DropColumn(
                name: "DisplayNameOverride",
                schema: "mimamori",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RoomOverride",
                schema: "mimamori",
                table: "Devices");
        }
    }
}
