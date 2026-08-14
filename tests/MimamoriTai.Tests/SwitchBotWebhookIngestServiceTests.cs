using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// The push counterpart to polling. These tests exist because the poller cannot tell a
/// steady house from a silent one -- SwitchBot's cloud replays the last status it
/// received -- and because getting the units wrong here would repeat the apparent-power
/// mistake that once reported a dark lamp as drawing 14.5W.
/// </summary>
public sealed class SwitchBotWebhookIngestServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

    private const string Mac = "8CFD49F79C92";

    private static Device Plug(string mac = Mac) => new()
    {
        ExternalDeviceId = mac,
        Name = "リビングの電気",
        Alias = "living-plug",
        DeviceType = DeviceType.Plug,
        Room = "リビング",
        Provider = DeviceProviderKind.SwitchBot,
        SafetyClass = SafetyClass.Safe
    };

    private static SwitchBotWebhookIngestService Service(TestDb db) =>
        new(db.Context, new FakeTimeProvider(NowUtc));

    private static string Payload(string context) =>
        """{"eventType":"changeReport","eventVersion":"1","context":{""" + context + "}}";

    private static string Measured(string mac = Mac, long? sampledAt = null) => Payload(
        $$"""
        "deviceType":"WoPlugJP","deviceMac":"{{mac}}","powerState":"ON",
        "voltage":103.9,"electricCurrent":140,"weight":12.2,"electricityOfDay":120,
        "timeOfSample":{{sampledAt ?? NowUtc.ToUnixTimeMilliseconds()}}
        """);

    /// <summary>
    /// The whole point of the callback: a measurement lands the moment the plug reports
    /// it, rather than being discovered by the next poll up to five minutes later.
    /// </summary>
    [Fact]
    public async Task Stores_The_Measurements_A_Plug_Reports()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        var result = await Service(db).IngestAsync(Measured());

        Assert.True(result.Recognised);
        var reading = await db.Context.PlugMiniReadings.SingleAsync();
        Assert.Equal(103.9, reading.VoltageV);
        Assert.Equal(140, reading.CurrentMa);
        Assert.Equal(120, reading.UsageMinutesToday);
    }

    /// <summary>
    /// "weight" is the plug's own real power, exactly as in the status API. Deriving
    /// watts from volts times amps instead yields 14.5 against a real 12.2 here, and on
    /// a reactive load is wrong by two orders of magnitude.
    /// </summary>
    [Fact]
    public async Task Takes_Watts_From_The_Plug_Not_From_Volts_Times_Amps()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        await Service(db).IngestAsync(Measured());

        var reading = await db.Context.PlugMiniReadings.SingleAsync();
        Assert.Equal(12.2, reading.DailyEnergyWh);

        // And the apparent-power field it must never be confused with.
        Assert.Equal(14.5, Math.Round(reading.ApproxWatts!.Value, 1));
    }

    /// <summary>
    /// A state-only callback is the documented shape for several devices. Writing a row
    /// of nulls for it would manufacture an observation nobody made, and that row would
    /// then be charted as a dip to zero.
    /// </summary>
    [Fact]
    public async Task Records_The_State_But_No_Reading_When_No_Measurements_Arrive()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        var result = await Service(db).IngestAsync(Payload(
            $$"""
            "deviceType":"WoPlugJP","deviceMac":"{{Mac}}","powerState":"ON",
            "timeOfSample":{{NowUtc.ToUnixTimeMilliseconds()}}
            """));

        Assert.NotNull(result.StateChange);
        Assert.Null(result.Reading);
        Assert.Empty(await db.Context.PlugMiniReadings.ToListAsync());
    }

    /// <summary>
    /// The timestamp that matters is when the device reported, not when the callback
    /// arrived: arrival time describes the network, not the house.
    /// </summary>
    [Fact]
    public async Task Timestamps_The_Reading_When_The_Device_Sampled_It()
    {
        using var db = await new TestDb().SeedAsync(Plug());
        var sampledAt = NowUtc.AddMinutes(-2);

        await Service(db).IngestAsync(Measured(sampledAt: sampledAt.ToUnixTimeMilliseconds()));

        var reading = await db.Context.PlugMiniReadings.SingleAsync();
        Assert.Equal(sampledAt, reading.OccurredAtUtc);
        Assert.Equal(NowUtc, reading.ReceivedAtUtc);
    }

    /// <summary>
    /// SwitchBot has shipped timeOfSample in both seconds and milliseconds. Reading
    /// milliseconds as seconds lands the sample tens of thousands of years out and
    /// would stretch every chart axis to nothing.
    /// </summary>
    [Fact]
    public async Task Reads_A_Seconds_Timestamp_As_Seconds()
    {
        using var db = await new TestDb().SeedAsync(Plug());
        var sampledAt = NowUtc.AddMinutes(-2);

        await Service(db).IngestAsync(Measured(sampledAt: sampledAt.ToUnixTimeSeconds()));

        var reading = await db.Context.PlugMiniReadings.SingleAsync();
        Assert.Equal(sampledAt, reading.OccurredAtUtc);
    }

    /// <summary>
    /// A timestamp far from our own clock cannot be placed on a timeline at all, so fall
    /// back to arrival rather than drawing the sample in the future.
    /// </summary>
    [Fact]
    public async Task Falls_Back_To_Arrival_When_The_Device_Clock_Is_Nonsense()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        await Service(db).IngestAsync(Measured(sampledAt: 4_102_444_800_000));

        var reading = await db.Context.PlugMiniReadings.SingleAsync();
        Assert.Equal(NowUtc, reading.OccurredAtUtc);
    }

    /// <summary>
    /// The webhook is configured once for the whole SwitchBot account, so most callbacks
    /// are about devices nobody here registered. Ignoring them must stay silent and
    /// cheap, not look like a failure.
    /// </summary>
    [Fact]
    public async Task Ignores_A_Device_Nobody_Here_Registered()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        var result = await Service(db).IngestAsync(Measured("AABBCCDDEEFF"));

        Assert.False(result.Recognised);
        Assert.Empty(await db.Context.PlugMiniReadings.ToListAsync());
    }

    /// <summary>
    /// SwitchBot writes the MAC with separators in some payloads and without in others,
    /// while devices are registered by the bare hex the status API returns. A colon must
    /// not silently drop a household's telemetry.
    /// </summary>
    [Fact]
    public async Task Matches_A_Mac_Written_With_Separators()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        var result = await Service(db).IngestAsync(Measured("8C:FD:49:F7:9C:92"));

        Assert.True(result.Recognised);
        Assert.Single(await db.Context.PlugMiniReadings.ToListAsync());
    }

    /// <summary>
    /// Polling and the callback both run, so the same instant can arrive twice. The
    /// second must not become a second observation, or a chart would show a step that
    /// never happened.
    /// </summary>
    [Fact]
    public async Task Does_Not_Store_The_Same_Sample_Twice()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        await Service(db).IngestAsync(Measured());
        var second = await Service(db).IngestAsync(Measured());

        Assert.True(second.Recognised);
        Assert.Null(second.Reading);
        Assert.Single(await db.Context.PlugMiniReadings.ToListAsync());
    }

    /// <summary>
    /// A callback that only confirms the state a poll already recorded adds nothing, and
    /// duplicating it would show a family a light switching on twice.
    /// </summary>
    [Fact]
    public async Task Does_Not_Repeat_A_State_The_Poller_Already_Recorded()
    {
        var plug = Plug();
        using var db = await new TestDb().SeedAsync(plug);
        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = plug.Id,
            EventType = "PowerState",
            State = "on",
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = NowUtc.AddMinutes(-10),
            ReceivedAtUtc = NowUtc.AddMinutes(-10)
        });
        await db.Context.SaveChangesAsync();

        var result = await Service(db).IngestAsync(Measured());

        Assert.Null(result.StateChange);
        Assert.Single(await db.Context.DeviceEvents.ToListAsync());
    }

    /// <summary>
    /// Recorded as a webhook, not a poll, so the two ingestion paths stay separable when
    /// working out why a reading is or is not there.
    /// </summary>
    [Fact]
    public async Task Marks_The_Event_As_Having_Come_From_The_Webhook()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        await Service(db).IngestAsync(Measured());

        var recorded = await db.Context.DeviceEvents.SingleAsync();
        Assert.Equal(EventSource.SwitchBotWebhook, recorded.Source);
        Assert.Equal("on", recorded.State);
    }

    /// <summary>
    /// SwitchBot has quoted numerics as strings in past payloads. Dropping a measurement
    /// over its JSON type would look exactly like a plug going quiet.
    /// </summary>
    [Fact]
    public async Task Accepts_Numbers_Sent_As_Strings()
    {
        using var db = await new TestDb().SeedAsync(Plug());

        await Service(db).IngestAsync(Payload(
            $$"""
            "deviceMac":"{{Mac}}","voltage":"103.9","electricCurrent":"140","weight":"12.2",
            "timeOfSample":{{NowUtc.ToUnixTimeMilliseconds()}}
            """));

        var reading = await db.Context.PlugMiniReadings.SingleAsync();
        Assert.Equal(103.9, reading.VoltageV);
        Assert.Equal(12.2, reading.DailyEnergyWh);
    }

    /// <summary>
    /// The endpoint is public. Malformed bodies must be a shrug, because throwing would
    /// return a non-2xx and SwitchBot disables a URL that keeps failing -- losing the
    /// subscription entirely over one bad request.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"eventType":"changeReport"}""")]
    [InlineData("""{"eventType":"changeReport","context":{}}""")]
    [InlineData("""{"eventType":"changeReport","context":"nope"}""")]
    public async Task Shrugs_Off_A_Body_It_Cannot_Read(string body)
    {
        using var db = await new TestDb().SeedAsync(Plug());

        var result = await Service(db).IngestAsync(body);

        Assert.False(result.Recognised);
        Assert.Empty(await db.Context.PlugMiniReadings.ToListAsync());
    }
}
