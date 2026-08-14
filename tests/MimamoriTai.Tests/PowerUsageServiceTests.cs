using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

public class PowerUsageServiceTests
{
    // Mid-afternoon, so "today" is unambiguous in the household's timezone.
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 13, 5, 0, 0, TimeSpan.Zero);

    private static DateOnly Today => HouseholdTime.LocalDate(NowUtc);

    private static PowerUsageService Service(TestDb db) =>
        new(db.Context, new FakeTimeProvider(NowUtc));

    /// <summary>
    /// One telemetry sample. DailyEnergyWh carries SwitchBot's `weight`, which despite
    /// the property name is instantaneous real power in watts, so these are watts.
    /// </summary>
    private static PlugMiniReading Reading(
        TestDb db, Guid deviceId, DateOnly date, double localHour, double? watts) => new()
        {
            HouseholdId = db.HouseholdId,
            DeviceId = deviceId,
            OccurredAtUtc = HouseholdTime.StartOfLocalDayUtc(date).AddHours(localHour),
            DailyEnergyWh = watts
        };

    /// <summary>
    /// A run of samples five minutes apart, i.e. what the poll actually writes, closed
    /// off by a zero-watt sample so the run covers exactly the stated hours. Lets a
    /// test state the energy it expects directly: watts times hours.
    /// </summary>
    private static IEnumerable<PlugMiniReading> Steady(
        TestDb db, Guid deviceId, DateOnly date, double fromHour, double hours, double watts)
    {
        var samples = (int)Math.Round(hours * 12);
        for (var i = 0; i < samples; i++)
        {
            yield return Reading(db, deviceId, date, fromHour + (i * 5 / 60.0), watts);
        }

        yield return Reading(db, deviceId, date, fromHour + hours, 0);
    }

    [Fact]
    public async Task No_Readings_Reports_No_Data()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.False(summary.HasData);
        Assert.Null(summary.YesterdayWh);
        Assert.Equal(0, summary.Last7DaysWh);
        Assert.Empty(summary.Daily);
    }

    /// <summary>
    /// The core of this service. SwitchBot's field is instantaneous watts, so a day's
    /// energy is the draw integrated over time -- never the sum of the samples (which
    /// would scale with how often we happened to poll) and never the highest sample
    /// (which would report a two-hour bake and a two-minute one as the same day).
    /// </summary>
    [Fact]
    public async Task Daily_Energy_Is_Power_Integrated_Over_Time()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        // 100W held for two hours yesterday = 200Wh, whatever the sample count.
        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today.AddDays(-1), 6, 2, 100));
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(200, summary.YesterdayWh!.Value, 1);
    }

    /// <summary>
    /// Doubling the polling rate must not double the reported consumption -- the bug a
    /// naive sum of a supposedly-cumulative field would produce.
    /// </summary>
    [Fact]
    public async Task Sampling_Rate_Does_Not_Change_The_Answer()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        var day = Today.AddDays(-1);
        for (var i = 0; i < 24; i++) // every 2.5 minutes for an hour
        {
            db.Context.PlugMiniReadings.Add(Reading(db, light.Id, day, 6 + (i * 2.5 / 60.0), 60));
        }

        db.Context.PlugMiniReadings.Add(Reading(db, light.Id, day, 7, 0));
        await db.Context.SaveChangesAsync();

        // 60W for one hour, sampled twice as often as usual, is still 60Wh.
        Assert.Equal(60, (await Service(db).GetAsync(db.HouseholdId)).YesterdayWh!.Value, 1);
    }

    /// <summary>
    /// An outage must not be billed as if the appliance ran through it, so a lone
    /// sample only ever stands for <see cref="PowerUsageService.MaxSampleSpan"/>.
    /// </summary>
    [Fact]
    public async Task A_Gap_In_Telemetry_Does_Not_Invent_Consumption()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        var day = Today.AddDays(-1);
        db.Context.PlugMiniReadings.AddRange(
            Reading(db, light.Id, day, 6, 600),    // then nothing for eight hours
            Reading(db, light.Id, day, 14, 600));
        await db.Context.SaveChangesAsync();

        // Two samples, each standing for ten minutes: 600W * (1/6 h) * 2 = 200Wh.
        Assert.Equal(200, (await Service(db).GetAsync(db.HouseholdId)).YesterdayWh!.Value, 1);
    }

    [Fact]
    public async Task Devices_Are_Added_Together_For_The_Household()
    {
        var light = TestDb.Light();
        var heater = TestDb.Heater();
        using var db = await new TestDb().SeedAsync(light, heater);

        var day = Today.AddDays(-1);
        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, day, 6, 1, 40));
        db.Context.PlugMiniReadings.AddRange(Steady(db, heater.Id, day, 6, 1, 100));
        await db.Context.SaveChangesAsync();

        var service = Service(db);

        Assert.Equal(140, (await service.GetAsync(db.HouseholdId)).YesterdayWh!.Value, 1);
        Assert.Equal(40, (await service.GetAsync(db.HouseholdId, light.Id)).YesterdayWh!.Value, 1);
    }

    [Fact]
    public async Task Yesterday_Is_Separate_From_Today()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today.AddDays(-1), 20, 1, 100));
        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today, 6, 1, 30));
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(100, summary.YesterdayWh!.Value, 1);
        Assert.Equal(30, summary.TodayWh, 1);
        Assert.Equal(130, summary.Last7DaysWh, 1);
    }

    /// <summary>A day with no reading is a gap, not a zero-consumption day.</summary>
    [Fact]
    public async Task Missing_Yesterday_Is_Null_Rather_Than_Zero()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today, 6, 1, 30));
        await db.Context.SaveChangesAsync();

        Assert.Null((await Service(db).GetAsync(db.HouseholdId)).YesterdayWh);
    }

    /// <summary>
    /// Today is still being lived, so the newest sample cannot be credited a whole
    /// cycle it has not lasted through yet.
    /// </summary>
    [Fact]
    public async Task Todays_Latest_Sample_Is_Only_Counted_Up_To_Now()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        // A sample one minute before "now", at 60W: one minute of it is 1Wh.
        db.Context.PlugMiniReadings.Add(new PlugMiniReading
        {
            HouseholdId = db.HouseholdId,
            DeviceId = light.Id,
            OccurredAtUtc = NowUtc.AddMinutes(-1),
            DailyEnergyWh = 60
        });
        await db.Context.SaveChangesAsync();

        Assert.Equal(1, (await Service(db).GetAsync(db.HouseholdId)).TodayWh, 1);
    }

    [Fact]
    public async Task Weekly_Window_Excludes_Days_Older_Than_Seven()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today.AddDays(-6), 12, 1, 60));
        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today.AddDays(-7), 12, 1, 500));
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(60, summary.Last7DaysWh, 1);
        Assert.Equal(560, summary.Last30DaysWh, 1);
    }

    [Fact]
    public async Task Readings_Older_Than_The_Window_Are_Ignored()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today, 1, 1, 25));
        db.Context.PlugMiniReadings.AddRange(
            Steady(db, light.Id, Today.AddDays(-PowerUsageService.WindowDays), 12, 1, 9999));
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(25, summary.Last30DaysWh, 1);
        Assert.Equal(PowerUsageService.WindowDays, summary.Daily.Count);
    }

    [Fact]
    public async Task Series_Is_Continuous_And_Ends_Today()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today.AddDays(-3), 12, 1, 40));
        await db.Context.SaveChangesAsync();

        var daily = (await Service(db).GetAsync(db.HouseholdId)).Daily;

        Assert.Equal(PowerUsageService.WindowDays, daily.Count);
        Assert.Equal(Today, daily[^1].Date);
        Assert.Equal(Today.AddDays(-(PowerUsageService.WindowDays - 1)), daily[0].Date);
        Assert.Equal(0, daily[^1].EnergyWh);
    }

    [Fact]
    public async Task Readings_Without_A_Power_Figure_Are_Skipped()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.Add(Reading(db, light.Id, Today, 12, null));
        await db.Context.SaveChangesAsync();

        Assert.False((await Service(db).GetAsync(db.HouseholdId)).HasData);
    }

    // --- comparing a day with this home's own habit -------------------------------

    /// <summary>
    /// Seeds the same one-hour morning run of <paramref name="watts"/> on each of the
    /// given days, so a baseline can be established cheaply.
    /// </summary>
    private static void SeedHabit(TestDb db, Guid deviceId, double watts, params int[] daysAgo)
    {
        foreach (var back in daysAgo)
        {
            db.Context.PlugMiniReadings.AddRange(
                Steady(db, deviceId, Today.AddDays(-back), 6, 1, watts));
        }
    }

    [Fact]
    public async Task Trend_Is_Unknown_Until_There_Is_Enough_History()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        SeedHabit(db, light.Id, 100, 1, 2); // only two comparable days
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(PowerUsageTrend.Unknown, summary.Yesterday!.Trend);
    }

    [Fact]
    public async Task A_Day_In_Line_With_Habit_Reads_As_Typical()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        SeedHabit(db, light.Id, 100, 1, 2, 3, 4, 5);
        await db.Context.SaveChangesAsync();

        var yesterday = (await Service(db).GetAsync(db.HouseholdId)).Yesterday!;

        Assert.Equal(PowerUsageTrend.Typical, yesterday.Trend);
        Assert.Equal(100, yesterday.Baseline!.Value, 1);
    }

    /// <summary>
    /// The direction that matters most: a quiet day is how "nobody got up" shows up in
    /// electricity, and it must be called out rather than averaged away.
    /// </summary>
    [Fact]
    public async Task A_Much_Quieter_Day_Reads_As_Lower()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        SeedHabit(db, light.Id, 100, 2, 3, 4, 5);
        SeedHabit(db, light.Id, 10, 1); // yesterday: a tenth of the usual
        await db.Context.SaveChangesAsync();

        var yesterday = (await Service(db).GetAsync(db.HouseholdId)).Yesterday!;

        Assert.Equal(PowerUsageTrend.Lower, yesterday.Trend);
        Assert.Equal(0.1, yesterday.Ratio!.Value, 2);
    }

    [Fact]
    public async Task A_Much_Busier_Day_Reads_As_Higher()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        SeedHabit(db, light.Id, 100, 2, 3, 4, 5);
        SeedHabit(db, light.Id, 300, 1);
        await db.Context.SaveChangesAsync();

        Assert.Equal(
            PowerUsageTrend.Higher,
            (await Service(db).GetAsync(db.HouseholdId)).Yesterday!.Trend);
    }

    /// <summary>
    /// One unusual day must not become the new normal, which is why the baseline is a
    /// median rather than a mean.
    /// </summary>
    [Fact]
    public async Task One_Freak_Day_Does_Not_Redefine_Normal()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        SeedHabit(db, light.Id, 100, 1, 2, 3, 5);
        SeedHabit(db, light.Id, 5000, 4); // a one-off
        await db.Context.SaveChangesAsync();

        var yesterday = (await Service(db).GetAsync(db.HouseholdId)).Yesterday!;

        Assert.Equal(100, yesterday.Baseline!.Value, 1);
        Assert.Equal(PowerUsageTrend.Typical, yesterday.Trend);
    }

    /// <summary>
    /// Today is only partly lived, so it is compared with how much previous days had
    /// used by this hour -- otherwise every morning would look like a collapse.
    /// </summary>
    [Fact]
    public async Task Today_Is_Compared_With_The_Same_Hours_Of_Previous_Days()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        // Previous days: an hour in the morning, then a big evening load today has not
        // reached yet. Only the morning part may count towards the baseline.
        foreach (var back in new[] { 1, 2, 3, 4 })
        {
            db.Context.PlugMiniReadings.AddRange(
                Steady(db, light.Id, Today.AddDays(-back), 6, 1, 100));
            db.Context.PlugMiniReadings.AddRange(
                Steady(db, light.Id, Today.AddDays(-back), 20, 1, 5000));
        }

        db.Context.PlugMiniReadings.AddRange(Steady(db, light.Id, Today, 6, 1, 100));
        await db.Context.SaveChangesAsync();

        var today = (await Service(db).GetAsync(db.HouseholdId)).Today!;

        Assert.Equal(100, today.Baseline!.Value, 1);
        Assert.Equal(PowerUsageTrend.Typical, today.Trend);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0d, "0 Wh")]
    [InlineData(123.45, "123.5 Wh")]
    [InlineData(1000d, "1 kWh")]
    [InlineData(2345d, "2.35 kWh")]
    public void Format_Switches_To_KWh_Once_The_Number_Gets_Long(double? wh, string expected) =>
        Assert.Equal(expected, PowerUsageService.Format(wh));
}
