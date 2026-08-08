using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

public class ActivityServiceTests
{
    private static DeviceEvent Event(DateOnly date, int hour, int minute, string state)
    {
        var localMidnightUtc = HouseholdTime.StartOfLocalDayUtc(date);
        return new DeviceEvent
        {
            EventType = "PowerState",
            State = state,
            Source = EventSource.Seed,
            OccurredAtUtc = localMidnightUtc.AddHours(hour).AddMinutes(minute)
        };
    }

    [Fact]
    public void No_Events_Yields_Empty_Summary()
    {
        var date = new DateOnly(2026, 8, 8);
        var summary = ActivityService.Summarize(date, []);

        Assert.Equal(0, summary.DeviceUsageCount);
        Assert.Null(summary.FirstActivityTime);
        Assert.Null(summary.LastActivityTime);
        Assert.Equal(0, summary.NightActivityCount);
    }

    [Fact]
    public void Counts_Only_On_Events_As_Usage()
    {
        var date = new DateOnly(2026, 8, 8);
        var summary = ActivityService.Summarize(date,
        [
            Event(date, 7, 0, "on"),
            Event(date, 7, 30, "off"),
            Event(date, 18, 0, "on")
        ]);

        Assert.Equal(2, summary.DeviceUsageCount);
        Assert.Equal(new TimeOnly(7, 0), summary.FirstActivityTime);
        Assert.Equal(new TimeOnly(18, 0), summary.LastActivityTime);
    }

    [Fact]
    public void Night_Window_Is_Midnight_To_Five()
    {
        var date = new DateOnly(2026, 8, 8);
        var summary = ActivityService.Summarize(date,
        [
            Event(date, 2, 10, "on"),
            Event(date, 4, 59, "on"),
            Event(date, 5, 0, "on"),
            Event(date, 23, 0, "on")
        ]);

        Assert.Equal(2, summary.NightActivityCount);
        Assert.Equal(4, summary.DeviceUsageCount);
    }
}

public class RiskAssessmentServiceTests
{
    private static DailyActivity Day(DateOnly date, int usage, int hour = 7, int night = 0) =>
        new(date, new TimeOnly(hour, 0), new TimeOnly(20, 0), usage, 600, night);

    private static IReadOnlyList<DailyActivity> Baseline(DateOnly today, int usagePerDay = 10) =>
        Enumerable.Range(1, 7).Select(i => Day(today.AddDays(-i), usagePerDay)).ToList();

    [Fact]
    public void Normal_Day_Is_Low_Risk()
    {
        var today = new DateOnly(2026, 8, 8);
        var result = RiskAssessmentService.Evaluate(Day(today, 10), Baseline(today), new TimeOnly(20, 0));

        Assert.Equal(RiskLevel.Low, result.Level);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void No_Activity_After_The_Threshold_Hour_Is_High_Risk()
    {
        var today = new DateOnly(2026, 8, 8);
        var noActivity = new DailyActivity(today, null, null, 0, 0, 0);

        var result = RiskAssessmentService.Evaluate(noActivity, Baseline(today), new TimeOnly(11, 0));

        Assert.Equal(RiskLevel.High, result.Level);
        Assert.Contains("家電の利用がありません", result.Reason);
    }

    [Fact]
    public void No_Activity_Early_In_The_Morning_Is_Not_Alarming()
    {
        var today = new DateOnly(2026, 8, 8);
        var noActivity = new DailyActivity(today, null, null, 0, 0, 0);

        var result = RiskAssessmentService.Evaluate(noActivity, Baseline(today), new TimeOnly(6, 0));

        Assert.Equal(RiskLevel.Low, result.Level);
    }

    [Fact]
    public void Repeated_Night_Activity_Raises_Risk()
    {
        var today = new DateOnly(2026, 8, 8);
        var result = RiskAssessmentService.Evaluate(Day(today, 8, night: 3), Baseline(today), new TimeOnly(9, 0));

        Assert.True(result.Score >= 30);
        Assert.Contains("深夜帯", result.Reason);
    }

    [Fact]
    public void Late_Start_Raises_Risk()
    {
        var today = new DateOnly(2026, 8, 8);
        var result = RiskAssessmentService.Evaluate(Day(today, 6, hour: 11), Baseline(today), new TimeOnly(12, 0));

        Assert.Equal(RiskLevel.Medium, result.Level);
        Assert.Contains("活動開始", result.Reason);
    }

    [Fact]
    public void Much_Lower_Activity_Than_Usual_Raises_Risk()
    {
        var today = new DateOnly(2026, 8, 8);
        var result = RiskAssessmentService.Evaluate(Day(today, 2), Baseline(today, usagePerDay: 10), new TimeOnly(21, 0));

        Assert.Contains("活動量が少なめ", result.Reason);
    }

    [Fact]
    public void Score_Is_Capped_At_100()
    {
        var today = new DateOnly(2026, 8, 8);
        var awful = new DailyActivity(today, null, null, 0, 0, 9);

        var result = RiskAssessmentService.Evaluate(awful, Baseline(today), new TimeOnly(23, 0));

        Assert.True(result.Score <= 100);
    }
}

public class HouseholdTimeTests
{
    [Fact]
    public void Local_Day_Starts_At_1500_Utc_The_Previous_Day()
    {
        var start = HouseholdTime.StartOfLocalDayUtc(new DateOnly(2026, 8, 8));
        var utc = start.ToUniversalTime();

        Assert.Equal(new DateTime(2026, 8, 7, 15, 0, 0, DateTimeKind.Utc), utc.UtcDateTime);
    }

    [Fact]
    public void Utc_Is_Converted_To_Jst()
    {
        var utc = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new TimeOnly(9, 0), HouseholdTime.LocalTime(utc));
        Assert.Equal(new DateOnly(2026, 8, 8), HouseholdTime.LocalDate(utc));
    }

    [Fact]
    public void Late_Utc_Evening_Is_Already_The_Next_Jst_Day()
    {
        var utc = new DateTimeOffset(2026, 8, 8, 16, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 8, 9), HouseholdTime.LocalDate(utc));
    }
}
