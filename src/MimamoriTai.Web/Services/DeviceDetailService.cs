using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Services;

public sealed record DeviceDetailTimelineItem(
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string State,
    double? PowerWatts,
    double? NumericValue,
    string? Unit,
    string Source);

public sealed record DeviceDailyUsageItem(DateOnly Date, int OnCount, int OffCount);

public sealed record DeviceDetailModel(
    Guid HouseholdId,
    string HouseholdName,
    DataSourceMode DataSourceMode,
    Guid DeviceId,
    string Name,
    string Alias,
    string Room,
    /// <summary>The provider's own label, shown only as a hint on the rename form.</summary>
    string ProviderName,
    /// <summary>The provider's own room value, shown only as a hint on the rename form.</summary>
    string ProviderRoom,
    string DeviceType,
    string Provider,
    bool IsEnabled,
    bool IsActive,
    bool RemoteControlAllowed,
    string SafetyClass,
    bool IsOn,
    /// <summary>
    /// False when neither the hub nor the recorded history can say whether the appliance is
    /// on. Rendering that case as "停止中" told the family the light was off when in truth
    /// nothing had been read back at all.
    /// </summary>
    bool IsStateKnown,
    /// <summary>True when <see cref="IsOn"/> came from a live read; false when it was recovered from the event log.</summary>
    bool IsStateLive,
    double? PowerWatts,
    DateTimeOffset? StatusObservedAtUtc,
    DateTimeOffset? LastEventAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    int TodayUsageCount,
    int PeriodDays,
    int PeriodUsageCount,
    double AveragePerDay,
    IReadOnlyList<DeviceDailyUsageItem> DailyBreakdown,
    IReadOnlyList<DeviceDetailTimelineItem> Timeline,
    IntegrationStatus Integrations);

/// <summary>
/// Read model builder for the device/sensor detail page. Follows the same
/// authorize-then-set-data-source-context-then-load shape as <see cref="DashboardService"/>,
/// scoped down to a single device.
/// </summary>
public sealed class DeviceDetailService(
    AppDbContext db,
    IDeviceProvider provider,
    IDataSourceContext dataSourceContext,
    HouseholdAccessService householdAccess,
    DeviceInsightService deviceInsight,
    IntegrationStatus integrations,
    TimeProvider clock)
{
    /// <summary>
    /// Loads the detail model for one device. Returns null when the device does not
    /// exist or the current user is not authorized for its household - callers must
    /// treat both the same way (e.g. redirect to "not found") to avoid leaking which
    /// case occurred for a household the caller cannot access.
    /// </summary>
    public async Task<DeviceDetailModel?> LoadAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null)
        {
            return null;
        }

        if (!await householdAccess.CanAccessAsync(device.HouseholdId, ct))
        {
            return null;
        }

        var household = await db.Households.FirstOrDefaultAsync(h => h.Id == device.HouseholdId, ct);
        if (household is null)
        {
            return null;
        }

        // Same rule as DashboardService.LoadAsync: the ambient data-source context must
        // be set explicitly so the IDeviceProvider decorator resolves the correct concrete
        // provider - including this household's own SwitchBot credentials, which the
        // factory-based lookup this replaced never saw.
        dataSourceContext.Mode = household.DataSourceMode;
        dataSourceContext.HouseholdId = household.Id;

        var status = await provider.GetStatusAsync(device.ExternalDeviceId, ct);
        var summary = await deviceInsight.GetUsageSummaryAsync(household.Id, device.Id, ct: ct);

        // Infrared remotes have no status endpoint at all, and a hub that is offline or
        // rate-limited returns nothing either. In both cases the last recorded event is the
        // best answer available - notably the one this app wrote itself when the family
        // pressed "つける", which is exactly the moment the old code claimed "停止中".
        // For the first few seconds that event also beats a live read, because SwitchBot
        // still reports the previous state right after a change - see DevicePowerState.
        var lastEvent = await db.DeviceEvents
            .Where(e => e.DeviceId == device.Id)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Select(e => new { e.State, e.OccurredAtUtc })
            .FirstOrDefaultAsync(ct);

        var power = DevicePowerState.Resolve(
            status?.IsOn, lastEvent?.State, lastEvent?.OccurredAtUtc, clock.GetUtcNow());

        var dailyBreakdown = summary?.DailyBreakdown
            .Select(d => new DeviceDailyUsageItem(d.Date, d.OnCount, d.OffCount))
            .ToList()
            ?? [];

        var timeline = summary?.RecentEvents
            .Select(e => new DeviceDetailTimelineItem(e.OccurredAtUtc, e.EventType, e.State, e.PowerWatts, e.NumericValue, e.Unit, e.Source.ToString()))
            .ToList()
            ?? [];

        return new DeviceDetailModel(
            household.Id,
            household.Name,
            household.DataSourceMode,
            device.Id,
            device.DisplayName,
            device.Alias,
            device.DisplayRoom,
            device.Name,
            device.Room,
            device.DeviceType.ToString(),
            device.Provider.ToString(),
            device.IsEnabled,
            device.IsActive,
            device.RemoteControlAllowed,
            device.SafetyClass.ToString(),
            power.IsOn,
            power.IsKnown,
            status is not null,
            status?.PowerWatts,
            status?.ObservedAtUtc ?? lastEvent?.OccurredAtUtc,
            summary?.LastEventAtUtc,
            summary?.LastUsedAtUtc,
            summary?.TodayUsageCount ?? 0,
            summary?.PeriodDays ?? DeviceInsightService.DefaultPeriodDays,
            summary?.PeriodUsageCount ?? 0,
            summary?.AveragePerDay ?? 0,
            dailyBreakdown,
            timeline,
            integrations);
    }
}
