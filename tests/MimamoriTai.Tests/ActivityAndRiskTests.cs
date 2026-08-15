using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

public class ActivityServiceTests
{
    private static DeviceEvent Event(DateOnly date, int hour, int minute, string state, double? watts = null)
    {
        var localMidnightUtc = HouseholdTime.StartOfLocalDayUtc(date);
        return new DeviceEvent
        {
            EventType = "PowerState",
            State = state,
            Source = EventSource.Seed,
            PowerWatts = watts,
            OccurredAtUtc = localMidnightUtc.AddHours(hour).AddMinutes(minute)
        };
    }

    private static PlugMiniReading Reading(DateOnly date, int hour, int minute, double watts)
    {
        var localMidnightUtc = HouseholdTime.StartOfLocalDayUtc(date);
        return new PlugMiniReading
        {
            ApproxWatts = watts,
            OccurredAtUtc = localMidnightUtc.AddHours(hour).AddMinutes(minute)
        };
    }

    [Fact]
    public void A_Steady_Appliance_Is_Visible_Even_Though_It_Raises_No_Events()
    {
        // The regression this exists to stop: a television left on at a constant draw is
        // never a "significant change", so the poller writes no watt-bearing event for it
        // after the first one. Reading the day back from events alone therefore showed an
        // almost empty chart for a house that was demonstrably occupied -- the電力 was
        // being recorded all along, in the measurement table, and simply never read.
        var today = new DateOnly(2026, 8, 15);
        var readings = new List<PlugMiniReading>();
        for (var minute = 0; minute <= 60; minute += 5)
        {
            readings.Add(Reading(today, 9, minute, 99));
        }

        var profile = ActivityService.BuildHourlyProfile(today, [], readings);

        // 09時 through 10時 at 99W is one hour of it, charged to the hour it happened in.
        Assert.Equal(99, profile.Today.Sum(), 1);
        Assert.Equal(11, profile.StartHour);
        Assert.Equal(99, profile.Today[22], 1);
        Assert.Equal(99, profile.TodayByDevice.Single().TotalWh, 1);
    }

    [Fact]
    public void The_Day_Opens_And_Closes_On_The_Measured_Draw()
    {
        // Same story for the two times the family is actually shown. With only events to
        // go on these came back null and the bars sat at 0:00; the measurements know
        // exactly when the draw rose and when it went quiet.
        var today = new DateOnly(2026, 8, 15);

        var activity = ActivityService.Summarize(today, [],
        [
            Reading(today, 7, 0, 0.2),
            Reading(today, 7, 5, 99),
            Reading(today, 7, 10, 99),
            Reading(today, 21, 0, 99),
            Reading(today, 21, 5, 0.2)
        ]);

        Assert.Equal(new TimeOnly(7, 5), activity.FirstPowerMoveTime);
        Assert.Equal(new TimeOnly(21, 5), activity.SettledTime);
        Assert.True(activity.EnergyWh > 0);
    }

    [Fact]
    public void Standby_Draw_Is_Not_The_Start_Of_The_Day()
    {
        // A plug left permanently in the socket reports a fraction of a watt around the
        // clock. Older rows recorded that as volts-times-amps -- 104V x 314mA became
        // 32.7W -- and the family was told the resident got up at 08:35 on a morning
        // nobody had touched anything. The measurement travels with the event, so the
        // summary can decline it on the same terms the poller would have.
        var date = new DateOnly(2026, 8, 14);
        var summary = ActivityService.Summarize(date,
        [
            Event(date, 8, 35, "on", 0.3),
            Event(date, 10, 18, "off", 0.8)
        ]);

        Assert.Equal(0, summary.DeviceUsageCount);
        Assert.Null(summary.FirstActivityTime);
        Assert.Null(summary.LastActivityTime);
        Assert.Equal(0, summary.ActiveMinutes);
    }

    [Fact]
    public void A_Plug_Left_In_The_Wall_Still_Reports_The_Day_From_The_Draw()
    {
        // How a Plug Mini is actually lived with: it goes in the socket once and stays
        // there. The socket never switches again, so every subsequent use of the kettle
        // or the heater reaches us only as the draw moving. Counting on-transitions
        // alone scored this day zero and the family was shown "まだ本日の活動記録が
        // ありません" beside a morning of real activity.
        var date = new DateOnly(2026, 8, 14);
        var summary = ActivityService.Summarize(date,
        [
            PowerSwing(date, 7, 10, "increased", 830),
            PowerSwing(date, 7, 25, "decreased", 33),
            PowerSwing(date, 18, 40, "increased", 1100),
            PowerSwing(date, 20, 5, "decreased", 33)
        ]);

        Assert.Equal(2, summary.DeviceUsageCount);
        Assert.Equal(new TimeOnly(7, 10), summary.FirstActivityTime);
        Assert.Equal(new TimeOnly(20, 5), summary.LastActivityTime);
    }

    [Fact]
    public void A_Flat_Draw_All_Day_Is_Still_No_Activity()
    {
        // The counterpart, and the reason the family is told anything at all: a socket
        // that is energised but whose draw never moves means nobody used the appliance.
        // This must stay distinguishable from the case above, or the alert is worthless.
        var date = new DateOnly(2026, 8, 14);
        var summary = ActivityService.Summarize(date,
        [
            Event(date, 0, 5, "on", 0.4),
            Event(date, 12, 0, "on", 0.4)
        ]);

        Assert.Equal(0, summary.DeviceUsageCount);
        Assert.Null(summary.FirstActivityTime);
    }

    [Fact]
    public void Energy_Is_The_Draw_Held_Until_The_Next_Reading()
    {
        // 60W held for 10 minutes is 10Wh. The last reading of the day has nothing after
        // it to bound it, so it contributes nothing rather than being extrapolated.
        var date = new DateOnly(2026, 8, 14);
        var deviceId = Guid.NewGuid();

        var a = Event(date, 9, 0, "on", 60.0);
        var b = Event(date, 9, 10, "on", 0.0);
        var c = Event(date, 9, 20, "off", 0.0);
        a.DeviceId = b.DeviceId = c.DeviceId = deviceId;

        Assert.Equal(10.0, ActivityService.EnergyWh([a, b, c]), 2);
    }

    [Fact]
    public void Energy_Does_Not_Extrapolate_Across_A_Long_Silence()
    {
        // A plug that drops off the network at 09:00 and reappears at 18:00 must not be
        // credited with nine hours of its last known load; that would invent a day of
        // activity out of a network outage.
        var date = new DateOnly(2026, 8, 14);
        var deviceId = Guid.NewGuid();

        var a = Event(date, 9, 0, "on", 60.0);
        var b = Event(date, 18, 0, "on", 60.0);
        a.DeviceId = b.DeviceId = deviceId;

        var expected = 60.0 * ActivityService.MaxIntegrationGap.TotalHours;
        Assert.Equal(expected, ActivityService.EnergyWh([a, b]), 2);
    }

    [Fact]
    public void Flat_Standby_Draw_Still_Registers_As_Energy_But_Not_As_Activity()
    {
        // The two figures answer different questions and must not be conflated: the plug
        // really did consume electricity, and nobody really did use the appliance.
        var date = new DateOnly(2026, 8, 14);
        var deviceId = Guid.NewGuid();

        var a = Event(date, 0, 0, "on", 0.4);
        var b = Event(date, 0, 15, "on", 0.4);
        a.DeviceId = b.DeviceId = deviceId;

        var summary = ActivityService.Summarize(date, [a, b]);

        Assert.Equal(0, summary.DeviceUsageCount);
        Assert.True(summary.EnergyWh > 0);
    }

    private static DeviceEvent PowerSwing(DateOnly date, int hour, int minute, string state, double watts)
    {
        var e = Event(date, hour, minute, state, watts);
        e.EventType = "PowerChange";
        e.Unit = "W";
        return e;
    }

    [Fact]
    public void Real_Use_After_Standby_Starts_The_Day_When_It_Started()
    {
        var date = new DateOnly(2026, 8, 14);
        var summary = ActivityService.Summarize(date,
        [
            Event(date, 6, 0, "on", 0.4),
            Event(date, 9, 15, "on", 42.0),
            Event(date, 9, 50, "off", 0.0)
        ]);

        Assert.Equal(1, summary.DeviceUsageCount);
        Assert.Equal(new TimeOnly(9, 15), summary.FirstActivityTime);
        Assert.Equal(new TimeOnly(9, 50), summary.LastActivityTime);
        Assert.Equal(35, summary.ActiveMinutes);
    }

    [Fact]
    public void Events_Without_A_Measurement_Still_Count()
    {
        // Buttons, motion and contact sensors report no watts at all. There is nothing
        // to disbelieve, so they are taken at their word.
        var date = new DateOnly(2026, 8, 14);
        var summary = ActivityService.Summarize(date, [Event(date, 7, 0, "on")]);

        Assert.Equal(1, summary.DeviceUsageCount);
        Assert.Equal(new TimeOnly(7, 0), summary.FirstActivityTime);
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

    [Fact]
    public void Hourly_Energy_Splits_A_Reading_Across_The_Hours_It_Spans()
    {
        // 06:50 -> 07:20 at 60W is 10 minutes of the six o'clock hour and 20 of the seven,
        // not half an hour dumped on whichever hour happened to be first.
        var date = new DateOnly(2026, 8, 14);
        var hours = ActivityService.HourlyEnergyWh(
        [
            Event(date, 6, 50, "on", 60),
            Event(date, 7, 20, "on", 60)
        ]);

        Assert.Equal(10, hours[6], 2);
        Assert.Equal(20, hours[7], 2);
        Assert.Equal(0, hours[8], 2);
    }

    [Fact]
    public void Hourly_Energy_Adds_Up_To_The_Daily_Total()
    {
        var date = new DateOnly(2026, 8, 14);
        DeviceEvent[] events =
        [
            Event(date, 6, 50, "on", 60),
            Event(date, 7, 10, "on", 120),
            Event(date, 7, 30, "off", 0)
        ];

        Assert.Equal(ActivityService.EnergyWh(events),
            Math.Round(ActivityService.HourlyEnergyWh(events).Sum(), 2), 2);
    }

    [Fact]
    public void Hourly_Energy_Is_Split_Per_Device()
    {
        var date = new DateOnly(2026, 8, 14);
        var kitchen = Guid.NewGuid();
        var bedroom = Guid.NewGuid();

        var a1 = Event(date, 7, 0, "on", 60); a1.DeviceId = kitchen;
        var a2 = Event(date, 7, 30, "off", 0); a2.DeviceId = kitchen;
        var b1 = Event(date, 20, 0, "on", 30); b1.DeviceId = bedroom;
        var b2 = Event(date, 20, 30, "off", 0); b2.DeviceId = bedroom;

        var byDevice = ActivityService.HourlyEnergyByDevice([a1, a2, b1, b2]);

        Assert.Equal(2, byDevice.Count);
        Assert.Equal(kitchen, byDevice[0].DeviceId);
        Assert.Equal(30, byDevice[0].TotalWh, 2);
        Assert.Equal(15, byDevice[1].TotalWh, 2);
        Assert.Equal(30, byDevice[0].Hours[7], 2);
    }

    [Fact]
    public void The_Usual_Profile_Excludes_Today_And_Silent_Days()
    {
        // Averaging today into its own comparison line would make the line chase the bars
        // and never disagree; averaging in a day the poller missed would drag the whole
        // rhythm toward zero and make every real day look unusually busy.
        var today = new DateOnly(2026, 8, 14);
        var yesterday = today.AddDays(-1);
        var silent = today.AddDays(-2);

        var profile = ActivityService.BuildHourlyProfile(today,
        [
            Event(silent, 7, 0, "off", 0),
            Event(silent, 7, 30, "off", 0),
            Event(yesterday, 7, 0, "on", 60),
            Event(yesterday, 7, 30, "off", 0),
            Event(today, 7, 0, "on", 600),
            Event(today, 7, 30, "off", 0)
        ]);

        Assert.Equal(1, profile.UsualDayCount);

        // The window ends at the newest reading (07:30 rounded up to 08:00), so today's
        // seven o'clock hour is the rightmost bar rather than index 7.
        Assert.Equal(8, profile.StartHour);
        Assert.Equal(30, profile.Usual[23], 2);
        Assert.Equal(300, profile.Today[23], 2);
    }

    [Fact]
    public void The_Newest_Reading_Sits_At_The_Right_Hand_Edge()
    {
        // A fixed 0時-24時 axis spends most of the day drawing hours that have not happened
        // yet and squeezes the part the family cares about into a sliver on the left.
        var today = new DateOnly(2026, 8, 14);

        var profile = ActivityService.BuildHourlyProfile(today,
        [
            Event(today.AddDays(-1), 20, 0, "on", 60),
            Event(today.AddDays(-1), 20, 30, "off", 0),
            Event(today, 9, 0, "on", 120),
            Event(today, 9, 30, "off", 0)
        ]);

        // Newest reading 09:30 -> window 10:00 yesterday .. 10:00 today.
        Assert.Equal(10, profile.StartHour);
        Assert.Equal(60, profile.Today[23], 2);       // today 09:00, the rightmost bar
        Assert.Equal(30, profile.Today[10], 2);       // yesterday 20:00, ten bars in
        Assert.Equal(0, profile.Today[0], 2);
    }

    [Fact]
    public void The_Day_Starts_And_Settles_With_The_Power_Not_The_Socket()
    {
        // The plug is never unplugged, so "when did the socket switch" answers nothing.
        // What moves is the draw behind it: the kettle at 06:40, the last of it at 21:10.
        var date = new DateOnly(2026, 8, 14);
        var summary = ActivityService.Summarize(date,
        [
            Event(date, 3, 0, "on", 0.4),
            Event(date, 6, 40, "on", 900),
            Event(date, 7, 0, "on", 0.4),
            Event(date, 21, 10, "on", 300),
            Event(date, 22, 0, "on", 0.4)
        ]);

        Assert.Equal(new TimeOnly(6, 40), summary.FirstPowerMoveTime);
        Assert.Equal(new TimeOnly(22, 0), summary.SettledTime);
    }

    [Fact]
    public void Standby_Jitter_Is_Not_A_Movement()
    {
        var date = new DateOnly(2026, 8, 14);
        var summary = ActivityService.Summarize(date,
        [
            Event(date, 3, 0, "on", 0.3),
            Event(date, 9, 0, "on", 0.5),
            Event(date, 15, 0, "on", 0.4)
        ]);

        Assert.Null(summary.FirstPowerMoveTime);
        Assert.Null(summary.SettledTime);
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
    public void An_Early_Riser_Is_Chased_Long_Before_The_Ten_Oclock_Backstop()
    {
        var today = new DateOnly(2026, 8, 8);
        var upAtSix = Enumerable.Range(1, 7).Select(i => Day(today.AddDays(-i), 10, hour: 6)).ToList();
        var noActivity = new DailyActivity(today, null, null, 0, 0, 0);

        var result = RiskAssessmentService.Evaluate(noActivity, upAtSix, new TimeOnly(8, 30));

        Assert.Equal(RiskLevel.High, result.Level);
        Assert.Contains("08:00", result.Reason);
    }

    [Fact]
    public void A_Late_Riser_Is_Not_Chased_For_Keeping_Their_Own_Hours()
    {
        var today = new DateOnly(2026, 8, 8);
        var upAtNine = Enumerable.Range(1, 7).Select(i => Day(today.AddDays(-i), 10, hour: 9)).ToList();
        var noActivity = new DailyActivity(today, null, null, 0, 0, 0);

        // Their habit plus grace lands after the backstop, so the backstop still rules.
        Assert.Equal(RiskLevel.Low, RiskAssessmentService.Evaluate(noActivity, upAtNine, new TimeOnly(9, 30)).Level);
        Assert.Equal(RiskLevel.High, RiskAssessmentService.Evaluate(noActivity, upAtNine, new TimeOnly(10, 0)).Level);
    }

    [Fact]
    public void A_Habit_Before_Dawn_Never_Pulls_The_Alarm_Into_The_Night()
    {
        var today = new DateOnly(2026, 8, 8);
        var upAtThree = Enumerable.Range(1, 7).Select(i => Day(today.AddDays(-i), 10, hour: 3)).ToList();
        var noActivity = new DailyActivity(today, null, null, 0, 0, 0);

        var result = RiskAssessmentService.Evaluate(noActivity, upAtThree, new TimeOnly(6, 0));

        Assert.Equal(RiskLevel.Low, result.Level);
    }

    [Fact]
    public void Too_Little_History_Falls_Back_To_The_Fixed_Hour()
    {
        var today = new DateOnly(2026, 8, 8);
        var twoDays = new[] { Day(today.AddDays(-1), 10, hour: 6), Day(today.AddDays(-2), 10, hour: 6) };
        var noActivity = new DailyActivity(today, null, null, 0, 0, 0);

        Assert.Equal(RiskLevel.Low, RiskAssessmentService.Evaluate(noActivity, twoDays, new TimeOnly(9, 0)).Level);
    }

    [Fact]
    public void A_Still_House_At_Night_Is_Someone_Asleep_Not_An_Emergency()
    {
        var today = new DateOnly(2026, 8, 8);
        var flat = new[] { new FlatPowerDevice("テレビ", TimeSpan.FromHours(3), 3) };

        var result = RiskAssessmentService.Evaluate(
            Day(today, 6), Baseline(today), new TimeOnly(2, 0), null, flat);

        Assert.Equal(RiskLevel.Low, result.Level);
    }

    [Fact]
    public void A_Still_House_At_Dawn_Is_Not_Reported_Because_The_Window_Was_Asleep()
    {
        var today = new DateOnly(2026, 8, 8);
        var flat = new[] { new FlatPowerDevice("テレビ", TimeSpan.FromHours(3), 3) };

        var result = RiskAssessmentService.Evaluate(
            Day(today, 6), Baseline(today), new TimeOnly(6, 30), null, flat);

        Assert.Equal(RiskLevel.Low, result.Level);
    }

    [Fact]
    public void A_Still_House_Through_The_Waking_Day_Is_Reported()
    {
        var today = new DateOnly(2026, 8, 8);
        var flat = new[] { new FlatPowerDevice("テレビ", TimeSpan.FromHours(3), 3) };

        var result = RiskAssessmentService.Evaluate(
            Day(today, 6), Baseline(today), new TimeOnly(14, 0), null, flat);

        Assert.Equal(RiskLevel.Medium, result.Level);
        Assert.Contains("変わっていません", result.Reason);
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

/// <summary>
/// The database side of the flat-power rule: which devices are watched, and whether a
/// window of readings really was still. Covers the switch from opt-in to watched-by-
/// default, which is the difference between the family hearing about a silent afternoon
/// and hearing nothing at all.
/// </summary>
public class FlatPowerDetectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 5, 0, 0, TimeSpan.Zero);

    private static PlugMiniReading Reading(TestDb db, Guid deviceId, TimeSpan ago, double watts) => new()
    {
        HouseholdId = db.HouseholdId,
        DeviceId = deviceId,
        ApproxWatts = watts,
        OccurredAtUtc = Now - ago
    };

    /// <summary>A device whose family asked to hear when its draw stops moving.</summary>
    private static Device Watched()
    {
        var light = TestDb.Light();
        light.FlatPowerAlertHours = RiskAssessmentService.DefaultFlatPowerAlertHours;
        return light;
    }

    private static RiskAssessmentService Service(TestDb db) =>
        new(db.Context, new FakeTimeProvider(Now));

    [Fact]
    public async Task A_Device_Nobody_Configured_Is_Left_Alone()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();
        Assert.Null(device.FlatPowerAlertHours);

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, device.Id, TimeSpan.FromHours(3), 40),
            Reading(db, device.Id, TimeSpan.FromHours(1), 40));
        await db.Context.SaveChangesAsync();

        // Watching every device by default was measured against a week of real readings
        // and rejected: an always-on lamp is flat nearly every afternoon.
        Assert.Empty(await Service(db).LoadFlatPowerAsync(db.HouseholdId));
    }

    [Fact]
    public async Task A_Device_The_Family_Asked_About_Is_Reported_When_Still()
    {
        var light = TestDb.Light();
        light.FlatPowerAlertHours = RiskAssessmentService.DefaultFlatPowerAlertHours;
        using var db = await new TestDb().SeedAsync(light);
        var device = db.Context.Devices.Single();

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, device.Id, TimeSpan.FromHours(3), 40),
            Reading(db, device.Id, TimeSpan.FromHours(1), 40));
        await db.Context.SaveChangesAsync();

        var flat = await Service(db).LoadFlatPowerAsync(db.HouseholdId);

        Assert.Equal(RiskAssessmentService.DefaultFlatPowerAlertHours, Assert.Single(flat).ThresholdHours);
    }

    [Fact]
    public async Task A_Device_Set_To_Zero_Hours_Is_Left_Alone()
    {
        var light = TestDb.Light();
        light.FlatPowerAlertHours = 0;
        using var db = await new TestDb().SeedAsync(light);
        var device = db.Context.Devices.Single();

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, device.Id, TimeSpan.FromHours(3), 40),
            Reading(db, device.Id, TimeSpan.FromHours(1), 40));
        await db.Context.SaveChangesAsync();

        Assert.Empty(await Service(db).LoadFlatPowerAsync(db.HouseholdId));
    }

    [Fact]
    public async Task A_Draw_That_Moved_Is_Not_Flat()
    {
        using var db = await new TestDb().SeedAsync(Watched());
        var device = db.Context.Devices.Single();

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, device.Id, TimeSpan.FromHours(3), 0.2),
            Reading(db, device.Id, TimeSpan.FromHours(1), 60));
        await db.Context.SaveChangesAsync();

        Assert.Empty(await Service(db).LoadFlatPowerAsync(db.HouseholdId));
    }

    [Fact]
    public async Task A_Plug_That_Only_Just_Came_Online_Is_Not_Called_Still()
    {
        using var db = await new TestDb().SeedAsync(Watched());
        var device = db.Context.Devices.Single();

        // Both readings are recent: we simply were not watching for the rest of it.
        db.Context.PlugMiniReadings.AddRange(
            Reading(db, device.Id, TimeSpan.FromMinutes(20), 40),
            Reading(db, device.Id, TimeSpan.FromMinutes(5), 40));
        await db.Context.SaveChangesAsync();

        Assert.Empty(await Service(db).LoadFlatPowerAsync(db.HouseholdId));
    }

    [Fact]
    public async Task A_Disabled_Device_Is_Not_Watched()
    {
        var light = Watched();
        light.IsEnabled = false;
        using var db = await new TestDb().SeedAsync(light);
        var device = db.Context.Devices.Single();

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, device.Id, TimeSpan.FromHours(3), 40),
            Reading(db, device.Id, TimeSpan.FromHours(1), 40));
        await db.Context.SaveChangesAsync();

        Assert.Empty(await Service(db).LoadFlatPowerAsync(db.HouseholdId));
    }
}

