using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// The local answer is the only source of facts a summarising model is given, so
/// anything missing here is something the model is free to invent. These tests pin the
/// two things it previously had to guess: how much power is being drawn, and how many
/// appliances actually exist.
/// </summary>
public sealed class LocalDataQuestionServiceTests
{
    private static Device Plug(string name) => new()
    {
        ExternalDeviceId = "ext-" + name,
        Name = name,
        Alias = name,
        DeviceType = DeviceType.Plug,
        Room = "リビング",
        Provider = DeviceProviderKind.SwitchBot,
        IsActive = true
    };

    private static LocalDataQuestionService Service(TestDb db, DateTimeOffset now) =>
        new(db.Context, new FakeTimeProvider(now));

    private static async Task AddReadingAsync(TestDb db, Device device, DateTimeOffset at, double watts)
    {
        db.Context.PlugMiniReadings.Add(new PlugMiniReading
        {
            HouseholdId = db.HouseholdId,
            DeviceId = device.Id,
            VoltageV = 104.1,
            CurrentMa = watts / 104.1 * 1000,
            ApproxWatts = watts,
            DailyEnergyWh = watts, // SwitchBot's `weight`: instantaneous real watts
            UsageMinutesToday = 90,
            OccurredAtUtc = at,
            ReceivedAtUtc = at
        });

        await db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Power_Question_Reports_The_Measured_Values_Not_A_Missing_Record()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        await AddReadingAsync(db, device, now.AddMinutes(-5), 32.7);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.True(answer.Success);
        Assert.Contains("32.7", answer.Answer);
        Assert.Contains("104.1", answer.Answer);
        Assert.Contains("使用電力量", answer.Answer);
        Assert.DoesNotContain("記録がありません", answer.Answer);
    }

    /// <summary>
    /// The figure a family cares about is not the watt-hours but whether today looks
    /// like their usual day, so the answer has to say which it is.
    /// </summary>
    [Fact]
    public async Task Power_Question_Says_How_Today_Compares_With_The_Usual_Day()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 8, 3, 0, 0, TimeSpan.Zero);

        // Four previous mornings at 100W over the same hour, then this morning at a tenth.
        foreach (var back in new[] { 1, 2, 3, 4 })
        {
            for (var i = 0; i < 12; i++)
            {
                await AddReadingAsync(db, device, now.AddDays(-back).AddMinutes(-60 + (i * 5)), 100);
            }
        }

        for (var i = 0; i < 12; i++)
        {
            await AddReadingAsync(db, device, now.AddMinutes(-60 + (i * 5)), 10);
        }

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.Contains("少ない", answer.Answer);
    }

    /// <summary>
    /// A single day of history is not enough to say what "usual" is, but it is more
    /// than enough to answer with. Telling a worried family "there isn't enough data"
    /// while holding yesterday's figure is the wrong trade every time.
    /// </summary>
    [Fact]
    public async Task Power_Question_Still_Compares_When_There_Is_Only_One_Past_Day()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 8, 3, 0, 0, TimeSpan.Zero);

        // Yesterday only: too thin for a baseline, so the fallback wording applies.
        for (var i = 0; i < 12; i++)
        {
            await AddReadingAsync(db, device, now.AddDays(-1).AddMinutes(-60 + (i * 5)), 100);
        }

        await AddReadingAsync(db, device, now.AddDays(-1).AddMinutes(0), 0);

        for (var i = 0; i < 12; i++)
        {
            await AddReadingAsync(db, device, now.AddMinutes(-60 + (i * 5)), 10);
        }

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.DoesNotContain("実績がまだ足りません", answer.Answer);
        Assert.Contains("昨日は約", answer.Answer);
        Assert.Contains("平均は約", answer.Answer);
        Assert.Contains("少なめ", answer.Answer);
    }

    [Fact]
    public async Task Power_Question_Says_So_Plainly_When_Nothing_Has_Been_Measured()
    {
        using var db = await new TestDb().SeedAsync(Plug("リビングの電気"));
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.Contains("記録されていません", answer.Answer);
    }

    /// <summary>
    /// A family that has just been told "いつもとほぼ同じです" and asks "具体的に数値も教えて"
    /// is asking about the power, but says none of the words the routing looks for. It
    /// came back with the activity summary and not one figure. Any list of keywords will
    /// keep missing phrasings like this, so the readings ride along with the overview
    /// itself rather than waiting to be asked for by name.
    /// </summary>
    [Theory]
    [InlineData("具体的に数値も教えて")]
    [InlineData("もう少し詳しく")]
    [InlineData("今日の様子は？")]
    public async Task Overview_Always_Carries_The_Measured_Figures(string question)
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        await AddRawReadingAsync(db, device, now.AddMinutes(-5), 12.2, 103.7, 131, 120);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, question);

        Assert.Contains("消費電力は12.2W", answer.Answer);
        Assert.Contains("103.7V", answer.Answer);
        Assert.Contains("1台", answer.Answer);
    }

    /// <summary>
    /// The regression behind "家電は2台使っているようです" for a one-appliance household:
    /// with no inventory in the facts, the model reused the usage count as a device count.
    /// </summary>
    [Fact]
    public async Task Every_Overview_States_The_Registered_Appliance_Count()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "今日の様子は？");

        Assert.Contains("1台", answer.Answer);
        Assert.Contains("リビングの電気", answer.Answer);
    }

    [Fact]
    public async Task Appliance_Count_Follows_The_Household_Not_The_Usage_Count()
    {
        using var db = await new TestDb().SeedAsync(Plug("リビングの電気"), Plug("寝室の空気清浄機"));
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "今日の様子は？");

        Assert.Contains("2台", answer.Answer);
    }

    /// <summary>A household's power answer must never expose another household's meter.</summary>
    [Fact]
    public async Task Power_Question_Ignores_Readings_From_Another_Household()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        var stranger = new Household { Name = "よその家" };
        db.Context.Households.Add(stranger);
        await db.Context.SaveChangesAsync();

        db.Context.PlugMiniReadings.Add(new PlugMiniReading
        {
            HouseholdId = stranger.Id,
            DeviceId = device.Id,
            ApproxWatts = 999,
            OccurredAtUtc = now,
            ReceivedAtUtc = now
        });
        await db.Context.SaveChangesAsync();

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.DoesNotContain("999", answer.Answer);
    }

    /// <summary>
    /// Swings in draw are the only trace a kettle or a rice cooker leaves behind a
    /// permanently energised plug. If they never reach the local answer, the model has
    /// no way to know they happened and will say the day was quiet.
    /// </summary>
    [Fact]
    public async Task Overview_Reports_Changes_In_Draw_Not_Just_On_Off()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = device.Id,
            EventType = "PowerChange",
            State = "increased",
            PowerWatts = 833,
            NumericValue = 800,
            Unit = "W",
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = now.AddHours(-1),
            ReceivedAtUtc = now.AddHours(-1)
        });
        await db.Context.SaveChangesAsync();

        var overview = await Service(db, now).AnswerAsync(db.HouseholdId, "今日の様子");
        Assert.Contains("消費電力の変化", overview.Answer);
        Assert.Contains("833", overview.Answer);

        var power = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");
        Assert.Contains("833", power.Answer);
    }

    [Fact]
    public async Task Overview_Says_So_Plainly_When_The_Draw_Never_Moved()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        var overview = await Service(db, now).AnswerAsync(db.HouseholdId, "今日の様子");

        Assert.Contains("大きな変化は記録されていません", overview.Answer);
    }

    /// <summary>
    /// Adds a reading whose real power and voltage/current disagree, which is the normal
    /// case on a live plug: the two only coincide on a purely resistive load.
    /// </summary>
    private static async Task AddRawReadingAsync(
        TestDb db, Device device, DateTimeOffset at,
        double realWatts, double voltage, double milliamps, int usageMinutes)
    {
        db.Context.PlugMiniReadings.Add(new PlugMiniReading
        {
            HouseholdId = db.HouseholdId,
            DeviceId = device.Id,
            VoltageV = voltage,
            CurrentMa = milliamps,
            ApproxWatts = voltage * milliamps / 1000.0,
            DailyEnergyWh = realWatts,
            UsageMinutesToday = usageMinutes,
            OccurredAtUtc = at,
            ReceivedAtUtc = at
        });

        await db.Context.SaveChangesAsync();
    }

    /// <summary>
    /// The reported draw must be the plug's own measurement, not voltage times current.
    /// Taken from a real reading: 103.4V and 140mA computes to 14.5VA while the plug
    /// reported 0W, and quoting the former told a family a lamp was on when it was off.
    /// </summary>
    [Fact]
    public async Task Power_Question_Quotes_Real_Power_Not_Volts_Times_Amps()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        await AddRawReadingAsync(db, device, now.AddMinutes(-5), 0, 103.4, 140, 120);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.Contains("消費電力は0W", answer.Answer);
        Assert.DoesNotContain("14.5", answer.Answer);
    }

    /// <summary>
    /// A plug that stopped reporting looks exactly like a plug reporting an unchanging
    /// house, because SwitchBot's cloud keeps serving the last status it received. The
    /// answer has to name the moment those values were first seen and warn, or a family
    /// reads a ten-hour-old number as the state of the room right now.
    /// </summary>
    [Fact]
    public async Task Power_Question_Warns_When_The_Plug_Has_Stopped_Reporting()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        // Nine JST-morning hours of polls all returning the identical cached status.
        for (var i = 0; i < 108; i++)
        {
            await AddRawReadingAsync(
                db, device, now.AddHours(-9).AddMinutes(i * 5), 0, 103.4, 140, 120);
        }

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.Contains("一度も変わっていません", answer.Answer);
        Assert.Contains("今の様子としては読まないでください", answer.Answer);

        // The time quoted is when the values were first seen (12:00 JST), not the last
        // poll nine hours later, which is the misreading the warning exists to prevent.
        Assert.Contains("12:00時点", answer.Answer);
        Assert.DoesNotContain("21:00時点", answer.Answer);
    }

    /// <summary>
    /// The counterpart: a plug that is genuinely reporting must not be described as
    /// stale, or the warning becomes noise a family learns to ignore.
    /// </summary>
    [Fact]
    public async Task Power_Question_Stays_Quiet_While_The_Plug_Is_Still_Reporting()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        // Mains voltage drifting from poll to poll, which is what a live plug looks like.
        await AddRawReadingAsync(db, device, now.AddMinutes(-15), 12.0, 103.9, 130, 118);
        await AddRawReadingAsync(db, device, now.AddMinutes(-10), 12.4, 104.2, 132, 119);
        await AddRawReadingAsync(db, device, now.AddMinutes(-5), 12.2, 103.7, 131, 120);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.DoesNotContain("一度も変わっていません", answer.Answer);
        Assert.Contains("消費電力は12.2W", answer.Answer);
    }

    /// <summary>
    /// The usage counter is only meaningful attached to the moment it was reported, so
    /// it must never be presented as a bare "today" figure.
    /// </summary>
    [Fact]
    public async Task Power_Question_Ties_The_Usage_Counter_To_The_Moment_It_Was_Read()
    {
        var device = Plug("リビングの電気");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        await AddRawReadingAsync(db, device, now.AddMinutes(-5), 12.2, 103.7, 131, 120);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "電力使用量は？");

        Assert.Contains("その時点の通電時間は120分", answer.Answer);
        Assert.DoesNotContain("今日の通電時間", answer.Answer);
    }

    /// <summary>
    /// The overview and comparison branches read their day-by-day history through
    /// <c>ActivityService</c>, which windows on the wall clock rather than on the
    /// injected one. Anchoring these tests to the real local day is therefore what makes
    /// them exercise the code path at all: a fixed calendar date falls outside the
    /// fourteen-day window and every question collapses to "記録がありません".
    /// </summary>
    private static DateTimeOffset MiddayToday() =>
        HouseholdTime.StartOfLocalDayUtc(HouseholdTime.LocalDate(DateTimeOffset.UtcNow)).AddHours(15);

    private static async Task AddPowerStateAsync(TestDb db, Device device, DateTimeOffset at, string state)
    {
        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = device.Id,
            EventType = "PowerState",
            State = state,
            Source = EventSource.Seed,
            OccurredAtUtc = at
        });

        await db.Context.SaveChangesAsync();
    }

    /// <summary>
    /// The overview must not lead with a usage count. That figure counts the state
    /// changes we happened to poll, so it says more about SwitchBot's reporting than
    /// about anybody's day -- and because it used to be the only concrete number in the
    /// facts, the summarising model reached for it and produced "家電も2台を6回使われて
    /// います", a sentence about nothing.
    /// </summary>
    [Fact]
    public async Task Overview_Leads_With_The_Shape_Of_The_Day_Not_A_Usage_Count()
    {
        var device = Plug("テレビ");
        using var db = await new TestDb().SeedAsync(device);
        var now = MiddayToday();
        await AddPowerStateAsync(db, device, now.AddHours(-5), "on");
        await AddReadingAsync(db, device, now.AddMinutes(-5), 99.0);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "今日のお母さんどう？");

        Assert.True(answer.Success);
        Assert.DoesNotContain("回利用", answer.Answer);
        Assert.DoesNotContain("回使", answer.Answer);
    }

    /// <summary>
    /// What replaces the count: which appliance is actually drawing power right now.
    /// A family wants to hear that the television is on, not that a counter moved.
    /// </summary>
    [Fact]
    public async Task Overview_Says_Which_Appliances_Are_Switched_On()
    {
        var television = Plug("テレビ");
        var heater = Plug("ヒーター");
        using var db = await new TestDb().SeedAsync(television, heater);
        var now = MiddayToday();
        await AddPowerStateAsync(db, television, now.AddHours(-5), "on");
        await AddPowerStateAsync(db, heater, now.AddHours(-4), "off");

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "今日のお母さんどう？");

        Assert.Contains("いま電源が入っているのはテレビ", answer.Answer);
        Assert.Contains("電源が切れているのはヒーター", answer.Answer);
    }

    /// <summary>
    /// Dropping the count from the overview must not lose the ability to answer for it.
    /// Asked directly, the number is the honest response.
    /// </summary>
    [Fact]
    public async Task Usage_Count_Is_Still_Answered_When_It_Is_What_Was_Asked()
    {
        var device = Plug("テレビ");
        using var db = await new TestDb().SeedAsync(device);
        var now = MiddayToday();
        await AddPowerStateAsync(db, device, now.AddHours(-5), "on");

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "今日は家電を何回使った？");

        Assert.Contains("回利用しています", answer.Answer);
    }

    /// <summary>
    /// "昨日と比べてどう？" is a question about the person, and a difference in poll
    /// counts is not an answer to it. The comparison is made on electricity and on when
    /// the day started instead.
    /// </summary>
    [Fact]
    public async Task Comparison_With_Yesterday_Uses_Energy_And_Rhythm_Not_Counts()
    {
        var device = Plug("テレビ");
        using var db = await new TestDb().SeedAsync(device);
        var now = MiddayToday();
        await AddPowerStateAsync(db, device, now.AddDays(-1).AddHours(-4), "on");
        await AddPowerStateAsync(db, device, now.AddHours(-4), "on");
        await AddReadingAsync(db, device, now.AddDays(-1).AddHours(-3), 80.0);
        await AddReadingAsync(db, device, now.AddMinutes(-5), 99.0);

        var answer = await Service(db, now).AnswerAsync(db.HouseholdId, "昨日と比べてどう？");

        Assert.Contains("使用電力量", answer.Answer);
        Assert.DoesNotContain("回利用", answer.Answer);
    }
}

