using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers DeviceInsightService.Summarize directly with hand-built DeviceEvent lists,
/// mirroring ActivityServiceTests: the pure aggregation logic is tested without a
/// database so every figure (daily breakdown, today/period usage, average, recent
/// events) can be pinned down precisely.
/// </summary>
public class DeviceInsightServiceTests
{
    private static readonly Guid DeviceId = Guid.NewGuid();

    private static DeviceEvent Event(DateOnly date, int hour, int minute, string state, EventSource source = EventSource.Seed) =>
        new()
        {
            DeviceId = DeviceId,
            EventType = "PowerState",
            State = state,
            Source = source,
            OccurredAtUtc = HouseholdTime.StartOfLocalDayUtc(date).AddHours(hour).AddMinutes(minute)
        };

    [Fact]
    public void No_Events_Yields_Empty_Summary_With_Full_Day_Range()
    {
        var today = new DateOnly(2026, 8, 8);
        var firstDate = today.AddDays(-13);

        var summary = DeviceInsightService.Summarize(
            DeviceId, firstDate, periodDays: 14, today, periodEvents: [], lastEvent: null, lastUsedAtUtc: null);

        Assert.Equal(14, summary.DailyBreakdown.Count);
        Assert.All(summary.DailyBreakdown, d => Assert.Equal(0, d.OnCount + d.OffCount));
        Assert.Equal(0, summary.TodayUsageCount);
        Assert.Equal(0, summary.PeriodUsageCount);
        Assert.Equal(0, summary.AveragePerDay);
        Assert.Null(summary.LastEventAtUtc);
        Assert.Null(summary.LastUsedAtUtc);
        Assert.Empty(summary.RecentEvents);
    }

    [Fact]
    public void Daily_Breakdown_Counts_On_And_Off_Separately_Per_Local_Day()
    {
        var today = new DateOnly(2026, 8, 8);
        var yesterday = today.AddDays(-1);
        var firstDate = today.AddDays(-1);

        var events = new List<DeviceEvent>
        {
            Event(yesterday, 8, 0, "on"),
            Event(yesterday, 9, 0, "off"),
            Event(today, 7, 0, "on"),
            Event(today, 7, 30, "off"),
            Event(today, 20, 0, "on")
        };

        var summary = DeviceInsightService.Summarize(
            DeviceId, firstDate, periodDays: 2, today, events, lastEvent: events[^1], lastUsedAtUtc: events[^1].OccurredAtUtc);

        var yesterdayRow = Assert.Single(summary.DailyBreakdown, d => d.Date == yesterday);
        var todayRow = Assert.Single(summary.DailyBreakdown, d => d.Date == today);

        Assert.Equal(1, yesterdayRow.OnCount);
        Assert.Equal(1, yesterdayRow.OffCount);
        Assert.Equal(2, todayRow.OnCount);
        Assert.Equal(1, todayRow.OffCount);
        Assert.Equal(2, summary.TodayUsageCount);
    }

    [Fact]
    public void Period_Usage_And_Average_Are_Computed_From_On_Events_Only()
    {
        var today = new DateOnly(2026, 8, 8);
        var firstDate = today.AddDays(-3);

        var events = new List<DeviceEvent>
        {
            Event(firstDate, 8, 0, "on"),
            Event(firstDate.AddDays(1), 8, 0, "on"),
            Event(firstDate.AddDays(1), 8, 5, "off"),
            Event(today, 8, 0, "on")
        };

        var summary = DeviceInsightService.Summarize(
            DeviceId, firstDate, periodDays: 4, today, events, lastEvent: events[^1], lastUsedAtUtc: events[^1].OccurredAtUtc);

        Assert.Equal(3, summary.PeriodUsageCount);
        Assert.Equal(0.8, summary.AveragePerDay); // 3 / 4 rounded to 1 decimal
    }

    [Fact]
    public void Recent_Events_Are_Newest_First_And_Truncated_To_The_Requested_Count()
    {
        var today = new DateOnly(2026, 8, 8);
        var firstDate = today;

        var events = Enumerable.Range(0, 5)
            .Select(i => Event(today, 8 + i, 0, i % 2 == 0 ? "on" : "off"))
            .ToList();

        var summary = DeviceInsightService.Summarize(
            DeviceId, firstDate, periodDays: 1, today, events, lastEvent: events[^1], lastUsedAtUtc: null, recentEventTake: 3);

        Assert.Equal(3, summary.RecentEvents.Count);
        Assert.Equal(events[4].OccurredAtUtc, summary.RecentEvents[0].OccurredAtUtc);
        Assert.Equal(events[3].OccurredAtUtc, summary.RecentEvents[1].OccurredAtUtc);
        Assert.Equal(events[2].OccurredAtUtc, summary.RecentEvents[2].OccurredAtUtc);
    }

    [Fact]
    public void Last_Event_And_Last_Used_Come_From_The_Caller_Supplied_All_Time_Lookup()
    {
        // GetUsageSummaryAsync computes lastEvent/lastUsedAtUtc from ALL-time history
        // (not just the trend period) - Summarize just threads those values through.
        var today = new DateOnly(2026, 8, 8);
        var longAgo = today.AddDays(-90);
        var lastEvent = Event(longAgo, 3, 0, "off");

        var summary = DeviceInsightService.Summarize(
            DeviceId, today.AddDays(-13), periodDays: 14, today,
            periodEvents: [], lastEvent: lastEvent, lastUsedAtUtc: null);

        Assert.Equal(lastEvent.OccurredAtUtc, summary.LastEventAtUtc);
        Assert.Null(summary.LastUsedAtUtc);
    }

    [Fact]
    public async Task GetUsageSummaryAsync_Returns_Null_When_Device_Does_Not_Belong_To_Household()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var otherHouseholdId = Guid.NewGuid();
        var service = new DeviceInsightService(db.Context, TimeProvider.System);
        var device = db.Context.Devices.Single();

        var summary = await service.GetUsageSummaryAsync(otherHouseholdId, device.Id);

        Assert.Null(summary);
    }

    [Fact]
    public async Task GetUsageSummaryAsync_Aggregates_Real_Events_From_The_Database()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero)); // JST 8/8 12:00

        var today = HouseholdTime.LocalDate(clock.GetUtcNow());
        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = device.Id,
            EventType = "PowerState",
            State = "on",
            Source = EventSource.Seed,
            OccurredAtUtc = HouseholdTime.StartOfLocalDayUtc(today).AddHours(7)
        });
        await db.Context.SaveChangesAsync();

        var service = new DeviceInsightService(db.Context, clock);
        var summary = await service.GetUsageSummaryAsync(db.HouseholdId, device.Id);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TodayUsageCount);
        Assert.Equal(1, summary.PeriodUsageCount);
        Assert.NotNull(summary.LastUsedAtUtc);
        Assert.Single(summary.RecentEvents);
    }
}
