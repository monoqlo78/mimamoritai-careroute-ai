using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>One local calendar day's usage figures for a single device.</summary>
public sealed record DeviceDailyUsage(DateOnly Date, int OnCount, int OffCount, int EventCount);

/// <summary>A single raw event for the device timeline, already resolved to a display-ready shape.</summary>
public sealed record DeviceEventItem(
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string State,
    double? PowerWatts,
    double? NumericValue,
    string? Unit,
    EventSource Source);

/// <summary>
/// Per-device usage figures used by the sensor/device detail page: current-day and
/// period totals, a daily breakdown for the trend chart, and the most recent raw events
/// for the timeline. Every figure here is a direct aggregation of <see cref="DeviceEvent"/>
/// rows - nothing is estimated or invented.
/// </summary>
public sealed record DeviceUsageSummary(
    Guid DeviceId,
    DateTimeOffset? LastEventAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    int TodayUsageCount,
    int PeriodDays,
    int PeriodUsageCount,
    double AveragePerDay,
    IReadOnlyList<DeviceDailyUsage> DailyBreakdown,
    IReadOnlyList<DeviceEventItem> RecentEvents);

/// <summary>
/// Aggregates raw <see cref="DeviceEvent"/> rows into the per-device figures the sensor
/// detail page shows (today/period usage, a daily trend breakdown, and a recent event
/// timeline). Mirrors <see cref="ActivityService"/>'s pattern of a pure, unit-testable
/// static aggregator plus a thin instance method that does the actual querying.
/// </summary>
public sealed class DeviceInsightService(IAppDbContext db, TimeProvider clock)
{
    public const int DefaultPeriodDays = 14;
    public const int DefaultRecentEventTake = 30;

    /// <summary>
    /// Builds the usage summary for one device, scoped to its household. Returns null
    /// when the device does not exist or does not belong to the given household - callers
    /// must already have authorized the household itself (e.g. via HouseholdAccessService).
    /// </summary>
    public async Task<DeviceUsageSummary?> GetUsageSummaryAsync(
        Guid householdId,
        Guid deviceId,
        int periodDays = DefaultPeriodDays,
        int recentEventTake = DefaultRecentEventTake,
        CancellationToken ct = default)
    {
        var deviceExists = await db.Devices.AnyAsync(d => d.Id == deviceId && d.HouseholdId == householdId, ct);
        if (!deviceExists)
        {
            return null;
        }

        var today = HouseholdTime.LocalDate(clock.GetUtcNow());
        var firstDate = today.AddDays(-(periodDays - 1));
        var periodStartUtc = HouseholdTime.StartOfLocalDayUtc(firstDate);

        var periodEvents = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.DeviceId == deviceId && e.OccurredAtUtc >= periodStartUtc)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        // All-time history for this device, newest first, so "last event" / "last used"
        // are correct even when the most recent activity falls outside the trend period.
        // State comparisons are done client-side (like ActivityService/DashboardService)
        // because EF Core cannot reliably translate a case-insensitive string compare.
        var allEvents = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.DeviceId == deviceId)
            .OrderByDescending(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        var lastEvent = allEvents.FirstOrDefault();
        var lastUsedAtUtc = allEvents
            .FirstOrDefault(e => e.State.Equals("on", StringComparison.OrdinalIgnoreCase))?
            .OccurredAtUtc;

        return Summarize(deviceId, firstDate, periodDays, today, periodEvents, lastEvent, lastUsedAtUtc, recentEventTake);
    }

    /// <summary>Pure aggregation: given the raw events for the period, computes every summary figure.</summary>
    public static DeviceUsageSummary Summarize(
        Guid deviceId,
        DateOnly firstDate,
        int periodDays,
        DateOnly today,
        IReadOnlyList<DeviceEvent> periodEvents,
        DeviceEvent? lastEvent,
        DateTimeOffset? lastUsedAtUtc,
        int recentEventTake = DefaultRecentEventTake)
    {
        var byDate = periodEvents
            .GroupBy(e => HouseholdTime.LocalDate(e.OccurredAtUtc))
            .ToDictionary(g => g.Key, g => g.ToList());

        var dailyBreakdown = new List<DeviceDailyUsage>();
        for (var i = 0; i < periodDays; i++)
        {
            var date = firstDate.AddDays(i);
            var dayEvents = byDate.TryGetValue(date, out var list) ? list : [];
            var onCount = dayEvents.Count(e => e.State.Equals("on", StringComparison.OrdinalIgnoreCase));
            var offCount = dayEvents.Count(e => e.State.Equals("off", StringComparison.OrdinalIgnoreCase));
            dailyBreakdown.Add(new DeviceDailyUsage(date, onCount, offCount, dayEvents.Count));
        }

        var todayUsage = dailyBreakdown.FirstOrDefault(d => d.Date == today)?.OnCount ?? 0;
        var periodUsageCount = dailyBreakdown.Sum(d => d.OnCount);
        var averagePerDay = periodDays > 0 ? Math.Round((double)periodUsageCount / periodDays, 1) : 0;

        var recentEvents = periodEvents
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(recentEventTake)
            .Select(e => new DeviceEventItem(e.OccurredAtUtc, e.EventType, e.State, e.PowerWatts, e.NumericValue, e.Unit, e.Source))
            .ToList();

        return new DeviceUsageSummary(
            deviceId,
            lastEvent?.OccurredAtUtc,
            lastUsedAtUtc,
            todayUsage,
            periodDays,
            periodUsageCount,
            averagePerDay,
            dailyBreakdown,
            recentEvents);
    }
}
