using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record DailyActivity(
    DateOnly Date,
    TimeOnly? FirstActivityTime,
    TimeOnly? LastActivityTime,
    int DeviceUsageCount,
    int ActiveMinutes,
    int NightActivityCount);

/// <summary>Aggregates raw device events into the daily life-rhythm figures the UI and Q&amp;A use.</summary>
public sealed class ActivityService(IAppDbContext db)
{
    public const int NightStartHour = 0;
    public const int NightEndHour = 5;

    public async Task<DailyActivity> GetDailyAsync(Guid householdId, DateOnly localDate, CancellationToken ct = default)
    {
        var from = HouseholdTime.StartOfLocalDayUtc(localDate);
        var to = HouseholdTime.StartOfLocalDayUtc(localDate.AddDays(1));

        var events = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= from && e.OccurredAtUtc < to)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        return Summarize(localDate, events);
    }

    public async Task<IReadOnlyList<DailyActivity>> GetRecentAsync(Guid householdId, int days, CancellationToken ct = default)
    {
        var today = HouseholdTime.LocalDate(DateTimeOffset.UtcNow);
        var firstDate = today.AddDays(-(days - 1));
        var from = HouseholdTime.StartOfLocalDayUtc(firstDate);

        var events = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= from)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

        var byDate = events.GroupBy(e => HouseholdTime.LocalDate(e.OccurredAtUtc))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<DailyActivity>();
        for (var i = 0; i < days; i++)
        {
            var date = firstDate.AddDays(i);
            result.Add(Summarize(date, byDate.TryGetValue(date, out var list) ? list : []));
        }

        return result;
    }

    public static DailyActivity Summarize(DateOnly date, IReadOnlyList<DeviceEvent> events)
    {
        // "usage" means the device was actually switched on by/for the resident.
        var usage = events
            .Where(e => e.State.Equals("on", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (events.Count == 0)
        {
            return new DailyActivity(date, null, null, 0, 0, 0);
        }

        var first = events.Min(e => e.OccurredAtUtc);
        var last = events.Max(e => e.OccurredAtUtc);
        var night = usage.Count(e =>
        {
            var hour = HouseholdTime.LocalTime(e.OccurredAtUtc).Hour;
            return hour >= NightStartHour && hour < NightEndHour;
        });

        var activeMinutes = (int)Math.Round((last - first).TotalMinutes);

        return new DailyActivity(
            date,
            HouseholdTime.LocalTime(first),
            HouseholdTime.LocalTime(last),
            usage.Count,
            Math.Max(activeMinutes, 0),
            night);
    }
}
