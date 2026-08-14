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
        var usage = events.Where(IsUse).ToList();

        if (usage.Count == 0)
        {
            return new DailyActivity(date, null, null, 0, 0, 0);
        }

        // The day starts at the first real use. Anything logged before that -- a plug
        // reporting its standby draw, or the socket being de-energised -- is the house
        // sitting still, and calling it the moment someone got up is the kind of
        // confident falsehood this app exists to avoid. The closing time may still come
        // from a later "off", because that is genuinely when the use ended.
        var first = usage.Min(e => e.OccurredAtUtc);
        var last = events.Where(e => e.OccurredAtUtc >= first).Max(e => e.OccurredAtUtc);
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

    /// <summary>
    /// Whether an event is evidence that somebody used an appliance.
    ///
    /// Being switched on is not enough on its own. A Plug Mini left permanently
    /// energised -- which is how anyone actually lives with one -- reports a small
    /// standby draw forever, so a bare "on" says only that the socket has electricity
    /// in it. The poller already knows this and applies
    /// <see cref="SwitchBotPollingCycleService.InUseWattsThreshold"/> when it decides
    /// what to record; reading the events back has to apply the same rule, or the two
    /// halves of the app disagree about what a use is.
    ///
    /// That disagreement is not hypothetical. Events written before the poller learned
    /// to prefer real watts over volts-times-amps carry the apparent-power figure, and
    /// one production morning a socket sitting idle at 0.3W was logged as 32.7W and
    /// reported to the family as "活動を始めた8時35分". Holding both sides to the same
    /// threshold retires those rows without having to trust when they were written.
    ///
    /// Events with no measurement attached still count. A button press, a motion
    /// sensor and a contact sensor all arrive without watts, and there is no evidence
    /// there to dismiss them with.
    /// </summary>
    private static bool IsUse(DeviceEvent e) =>
        e.State.Equals("on", StringComparison.OrdinalIgnoreCase)
        && e.PowerWatts is null or >= SwitchBotPollingCycleService.InUseWattsThreshold;
}
