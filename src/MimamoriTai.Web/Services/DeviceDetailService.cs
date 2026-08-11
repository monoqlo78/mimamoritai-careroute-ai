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
    string DeviceType,
    string Provider,
    bool IsEnabled,
    bool IsActive,
    bool RemoteControlAllowed,
    string SafetyClass,
    bool IsOn,
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
    IDeviceProviderFactory deviceProviderFactory,
    IDataSourceContext dataSourceContext,
    HouseholdAccessService householdAccess,
    DeviceInsightService deviceInsight,
    IntegrationStatus integrations)
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
        // be set explicitly so IDeviceProvider resolves the correct concrete provider.
        dataSourceContext.Mode = household.DataSourceMode;
        dataSourceContext.HouseholdId = household.Id;
        var provider = deviceProviderFactory.Get(household.DataSourceMode);

        var status = await provider.GetStatusAsync(device.ExternalDeviceId, ct);
        var summary = await deviceInsight.GetUsageSummaryAsync(household.Id, device.Id, ct: ct);

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
            device.Name,
            device.Alias,
            device.Room,
            device.DeviceType.ToString(),
            device.Provider.ToString(),
            device.IsEnabled,
            device.IsActive,
            device.RemoteControlAllowed,
            device.SafetyClass.ToString(),
            status?.IsOn ?? false,
            status?.PowerWatts,
            status?.ObservedAtUtc,
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
