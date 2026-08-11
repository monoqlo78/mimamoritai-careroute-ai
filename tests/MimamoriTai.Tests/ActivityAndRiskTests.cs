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

    private static DailyActivity Normal(DateOnly today) => Day(today, 6);

    [Fact]
    public void A_light_on_briefly_is_not_flagged()
    {
        var today = new DateOnly(2026, 8, 8);
        var leftOn = new[] { new LeftOnDevice("リビング照明", DeviceType.Light, TimeSpan.FromHours(2)) };

        var result = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(20, 0), leftOn);

        Assert.DoesNotContain("つけっぱなし", result.Reason);
    }

    [Fact]
    public void A_light_on_all_day_is_flagged()
    {
        var today = new DateOnly(2026, 8, 8);
        var leftOn = new[] { new LeftOnDevice("リビング照明", DeviceType.Light, TimeSpan.FromHours(13)) };

        var result = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(20, 0), leftOn);

        Assert.Contains("つけっぱなし", result.Reason);
        Assert.Contains("リビング照明", result.Reason);
    }

    [Fact]
    public void At_night_a_light_is_flagged_much_sooner()
    {
        var today = new DateOnly(2026, 8, 8);
        var leftOn = new[] { new LeftOnDevice("リビング照明", DeviceType.Light, TimeSpan.FromHours(5)) };

        var atNight = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(3, 0), leftOn);
        var inTheDay = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(15, 0), leftOn);

        Assert.Contains("つけっぱなし", atNight.Reason);
        Assert.DoesNotContain("つけっぱなし", inTheDay.Reason);
    }

    [Fact]
    public void A_heater_left_on_is_treated_as_urgent()
    {
        var today = new DateOnly(2026, 8, 8);
        var leftOn = new[] { new LeftOnDevice("電気ストーブ", DeviceType.Heater, TimeSpan.FromHours(3)) };

        var result = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(14, 0), leftOn);

        Assert.Equal(RiskLevel.High, result.Level);
        Assert.Contains("火災", result.Reason);
    }

    [Fact]
    public void Several_lights_on_do_not_stack_into_a_false_emergency()
    {
        var today = new DateOnly(2026, 8, 8);
        var many = Enumerable.Range(0, 6)
            .Select(i => new LeftOnDevice($"照明{i}", DeviceType.Light, TimeSpan.FromHours(13)))
            .ToArray();

        var one = RiskAssessmentService.Evaluate(
            Normal(today), Baseline(today), new TimeOnly(20, 0),
            [new LeftOnDevice("照明0", DeviceType.Light, TimeSpan.FromHours(13))]);

        var all = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(20, 0), many);

        Assert.Equal(one.Score, all.Score);
        Assert.NotEqual(RiskLevel.High, all.Level);
    }

    [Fact]
    public void The_heater_is_reported_ahead_of_a_light()
    {
        var today = new DateOnly(2026, 8, 8);
        var leftOn = new[]
        {
            new LeftOnDevice("リビング照明", DeviceType.Light, TimeSpan.FromHours(20)),
            new LeftOnDevice("電気ストーブ", DeviceType.Heater, TimeSpan.FromHours(3))
        };

        var result = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(14, 0), leftOn);

        Assert.Contains("電気ストーブ", result.Reason);
        Assert.DoesNotContain("リビング照明", result.Reason);
    }

    [Fact]
    public void Nothing_left_on_keeps_the_previous_behaviour()
    {
        var today = new DateOnly(2026, 8, 8);

        var without = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(20, 0));
        var withEmpty = RiskAssessmentService.Evaluate(Normal(today), Baseline(today), new TimeOnly(20, 0), []);

        Assert.Equal(without.Score, withEmpty.Score);
        Assert.Equal(without.Reason, withEmpty.Reason);
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
