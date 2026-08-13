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

    /// <summary>Adds one reading at <paramref name="hour"/> on a household-local day.</summary>
    private static PlugMiniReading Reading(TestDb db, Guid deviceId, DateOnly date, int hour, double? energyWh) => new()
    {
        HouseholdId = db.HouseholdId,
        DeviceId = deviceId,
        OccurredAtUtc = HouseholdTime.StartOfLocalDayUtc(date).AddHours(hour),
        DailyEnergyWh = energyWh
    };

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
    /// The core of this service: SwitchBot reports a running daily total, so a day's
    /// consumption is the highest reading of that day, never the sum of its readings.
    /// </summary>
    [Fact]
    public async Task Daily_Total_Is_The_High_Water_Mark_Not_The_Sum()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, light.Id, Today, 6, 10),
            Reading(db, light.Id, Today, 12, 55),
            Reading(db, light.Id, Today, 18, 120));
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(120, summary.Last7DaysWh);
        Assert.Equal(120, Assert.Single(summary.Daily, d => d.Date == Today).EnergyWh);
    }

    [Fact]
    public async Task Devices_Are_Added_Together_For_The_Household()
    {
        var light = TestDb.Light();
        var heater = TestDb.Heater();
        using var db = await new TestDb().SeedAsync(light, heater);

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, light.Id, Today, 12, 100),
            Reading(db, heater.Id, Today, 12, 250));
        await db.Context.SaveChangesAsync();

        var service = Service(db);

        Assert.Equal(350, (await service.GetAsync(db.HouseholdId)).Last7DaysWh);
        Assert.Equal(100, (await service.GetAsync(db.HouseholdId, light.Id)).Last7DaysWh);
    }

    [Fact]
    public async Task Yesterday_Is_Separate_From_Today()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, light.Id, Today.AddDays(-1), 20, 400),
            Reading(db, light.Id, Today, 12, 30));
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(400, summary.YesterdayWh);
        Assert.Equal(430, summary.Last7DaysWh);
    }

    /// <summary>A day with no reading is a gap, not a zero-consumption day.</summary>
    [Fact]
    public async Task Missing_Yesterday_Is_Null_Rather_Than_Zero()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.Add(Reading(db, light.Id, Today, 12, 30));
        await db.Context.SaveChangesAsync();

        Assert.Null((await Service(db).GetAsync(db.HouseholdId)).YesterdayWh);
    }

    [Fact]
    public async Task Weekly_Window_Excludes_Days_Older_Than_Seven()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, light.Id, Today.AddDays(-6), 12, 60),   // in the week
            Reading(db, light.Id, Today.AddDays(-7), 12, 500));  // out of it, in the month
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(60, summary.Last7DaysWh);
        Assert.Equal(560, summary.Last30DaysWh);
    }

    [Fact]
    public async Task Readings_Older_Than_The_Window_Are_Ignored()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.AddRange(
            Reading(db, light.Id, Today, 12, 25),
            Reading(db, light.Id, Today.AddDays(-PowerUsageService.WindowDays), 12, 9999));
        await db.Context.SaveChangesAsync();

        var summary = await Service(db).GetAsync(db.HouseholdId);

        Assert.Equal(25, summary.Last30DaysWh);
        Assert.Equal(PowerUsageService.WindowDays, summary.Daily.Count);
    }

    [Fact]
    public async Task Series_Is_Continuous_And_Ends_Today()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.Add(Reading(db, light.Id, Today.AddDays(-3), 12, 40));
        await db.Context.SaveChangesAsync();

        var daily = (await Service(db).GetAsync(db.HouseholdId)).Daily;

        Assert.Equal(PowerUsageService.WindowDays, daily.Count);
        Assert.Equal(Today, daily[^1].Date);
        Assert.Equal(Today.AddDays(-(PowerUsageService.WindowDays - 1)), daily[0].Date);
        Assert.Equal(0, daily[^1].EnergyWh);
    }

    [Fact]
    public async Task Readings_Without_An_Energy_Figure_Are_Skipped()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        db.Context.PlugMiniReadings.Add(Reading(db, light.Id, Today, 12, null));
        await db.Context.SaveChangesAsync();

        Assert.False((await Service(db).GetAsync(db.HouseholdId)).HasData);
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
