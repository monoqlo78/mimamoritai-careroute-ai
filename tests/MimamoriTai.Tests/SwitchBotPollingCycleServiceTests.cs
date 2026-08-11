using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

/// <summary>
/// Controllable device provider that also implements IDeviceStatusSnapshotProvider
/// (mirroring the real SwitchBotDeviceProvider), so tests can exercise both the
/// state-change DeviceEvent path and the every-cycle PlugMiniReading path from one
/// fake, driven by a single combined snapshot call -- the same shape
/// SwitchBotPollingCycleService uses in production. The legacy GetStatusAsync/
/// GetPlugMiniReadingAsync methods and their *Calls trackers are kept only so older
/// direct-call tests/back-compat scenarios still compile; PollHouseholdAsync itself
/// never calls them when a snapshot provider is available (see SnapshotCalls).
/// </summary>
public sealed class FakePollingDeviceProvider : IDeviceProvider, ISwitchBotPlugMiniReader, IDeviceStatusSnapshotProvider
{
    public DeviceProviderKind Kind => DeviceProviderKind.SwitchBot;
    public bool IsConfigured { get; init; } = true;

    public Dictionary<string, ProviderDeviceStatus?> Statuses { get; } = [];
    public Dictionary<string, PlugMiniPowerReading?> PlugMiniReadings { get; } = [];

    public List<string> StatusCalls { get; } = [];
    public List<string> PlugMiniCalls { get; } = [];

    /// <summary>Every deviceId passed to GetStatusSnapshotAsync, in call order -- the
    /// primary assertion point for "at most one status call per device per cycle".</summary>
    public List<string> SnapshotCalls { get; } = [];

    public Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderDevice>>([]);

    public Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default)
    {
        StatusCalls.Add(externalDeviceId);
        return Task.FromResult(Statuses.GetValueOrDefault(externalDeviceId));
    }

    public Task<PlugMiniPowerReading?> GetPlugMiniReadingAsync(string externalDeviceId, CancellationToken ct = default)
    {
        PlugMiniCalls.Add(externalDeviceId);
        return Task.FromResult(PlugMiniReadings.GetValueOrDefault(externalDeviceId));
    }

    public Task<DeviceStatusSnapshot> GetStatusSnapshotAsync(string externalDeviceId, CancellationToken ct = default)
    {
        SnapshotCalls.Add(externalDeviceId);
        return Task.FromResult(new DeviceStatusSnapshot(
            Statuses.GetValueOrDefault(externalDeviceId),
            PlugMiniReadings.GetValueOrDefault(externalDeviceId)));
    }

    public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());

    public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());

    public Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());
}

/// <summary>A provider that has no Plug Mini telemetry at all (e.g. Bot/Light), to confirm the polling
/// service degrades gracefully when ISwitchBotPlugMiniReader is not implemented.</summary>
public sealed class FakeNonPlugMiniDeviceProvider : IDeviceProvider
{
    public DeviceProviderKind Kind => DeviceProviderKind.SwitchBot;
    public bool IsConfigured => true;

    public Dictionary<string, ProviderDeviceStatus?> Statuses { get; } = [];

    public Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderDevice>>([]);

    public Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(Statuses.GetValueOrDefault(externalDeviceId));

    public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());

    public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());

    public Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());
}

public class SwitchBotPollingCycleServiceTests
{
    private static Device SwitchBotDevice(string externalId, DeviceType type = DeviceType.Plug, bool isActive = true) => new()
    {
        ExternalDeviceId = externalId,
        Name = "テストプラグ",
        Alias = "test-plug-" + externalId,
        DeviceType = type,
        Room = "リビング",
        Provider = DeviceProviderKind.SwitchBot,
        RemoteControlAllowed = true,
        SafetyClass = SafetyClass.Safe,
        IsActive = isActive
    };

    [Fact]
    public async Task PollHouseholdAsync_Returns_Empty_When_Household_Has_No_SwitchBot_Devices()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light()); // Mock provider device, not SwitchBot
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var provider = new FakePollingDeviceProvider();

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal(0, result.DeviceCount);
        Assert.Empty(result.CreatedEvents);
        Assert.Empty(result.CreatedReadings);
    }

    [Fact]
    public async Task PollHouseholdAsync_Inserts_A_DeviceEvent_On_First_Observed_State()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(now));

        var provider = new FakePollingDeviceProvider();
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 5.0, now);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal(1, result.DeviceCount);
        var created = Assert.Single(result.CreatedEvents);
        Assert.Equal("on", created.Event.State);
        Assert.Equal(EventSource.SwitchBotPoll, created.Event.Source);

        var stored = await db.Context.DeviceEvents.SingleAsync();
        Assert.Equal("on", stored.State);
    }

    [Fact]
    public async Task PollHouseholdAsync_Does_Not_Duplicate_DeviceEvent_When_State_Is_Unchanged()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new SwitchBotPollingCycleService(db.Context, clock);

        var provider = new FakePollingDeviceProvider();
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 5.0, clock.GetUtcNow());

        await service.PollHouseholdAsync(db.HouseholdId, provider);
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Empty(second.CreatedEvents);
        Assert.Single(db.Context.DeviceEvents);
    }

    [Fact]
    public async Task PollHouseholdAsync_Inserts_A_New_DeviceEvent_When_State_Changes()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new SwitchBotPollingCycleService(db.Context, clock);
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 5.0, clock.GetUtcNow());
        await service.PollHouseholdAsync(db.HouseholdId, provider);

        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "off", 0.0, clock.GetUtcNow());
        var second = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Single(second.CreatedEvents);
        Assert.Equal(2, await db.Context.DeviceEvents.CountAsync());
    }

    [Fact]
    public async Task PollHouseholdAsync_Inserts_A_PlugMiniReading_Every_Cycle_Regardless_Of_StateChange()
    {
        var device = SwitchBotDevice("plug-1");
        using var db = await new TestDb().SeedAsync(device);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new SwitchBotPollingCycleService(db.Context, clock);
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 12.3, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] = new PlugMiniPowerReading(
            device.ExternalDeviceId, VoltageV: 100.5, CurrentMa: 0.5, DailyEnergyWh: 12.3, UsageMinutesToday: 30, clock.GetUtcNow());

        // Same unchanged status/reading on every cycle -- state doesn't change, but a
        // reading row must still be inserted each time.
        var first = await service.PollHouseholdAsync(db.HouseholdId, provider);
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await service.PollHouseholdAsync(db.HouseholdId, provider);
        clock.Advance(TimeSpan.FromMinutes(5));
        var third = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Single(first.CreatedReadings);
        Assert.Single(second.CreatedReadings);
        Assert.Single(third.CreatedReadings);
        Assert.Equal(3, await db.Context.PlugMiniReadings.CountAsync());

        // Only the very first cycle should have produced a DeviceEvent (unchanged state after).
        Assert.Single(first.CreatedEvents);
        Assert.Empty(second.CreatedEvents);
        Assert.Empty(third.CreatedEvents);
    }

    [Fact]
    public async Task PollHouseholdAsync_Maps_PlugMiniReading_Fields_Including_ApproxWatts()
    {
        var device = SwitchBotDevice("plug-1");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(now));
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 12.3, now);
        provider.PlugMiniReadings[device.ExternalDeviceId] = new PlugMiniPowerReading(
            device.ExternalDeviceId, VoltageV: 100.0, CurrentMa: 500.0, DailyEnergyWh: 12.3, UsageMinutesToday: 45, now);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        var reading = Assert.Single(result.CreatedReadings).Reading;
        Assert.Equal(100.0, reading.VoltageV);
        Assert.Equal(500.0, reading.CurrentMa);
        Assert.Equal(12.3, reading.DailyEnergyWh);
        Assert.Equal(45, reading.UsageMinutesToday);
        Assert.Equal(50.0, reading.ApproxWatts); // 100.0 * 500.0 / 1000
        Assert.Equal(db.HouseholdId, reading.HouseholdId);
        Assert.Equal(device.Id, reading.DeviceId);
        Assert.Null(reading.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PollHouseholdAsync_Skips_PlugMiniReading_When_Provider_Does_Not_Implement_The_Reader()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var provider = new FakeNonPlugMiniDeviceProvider();
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 5.0, DateTimeOffset.UtcNow);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Empty(result.CreatedReadings);
        Assert.Empty(await db.Context.PlugMiniReadings.ToListAsync());
    }

    [Fact]
    public async Task PollHouseholdAsync_Skips_Inactive_Devices()
    {
        var device = SwitchBotDevice("dev-1", isActive: false);
        using var db = await new TestDb().SeedAsync(device);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var provider = new FakePollingDeviceProvider();
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 5.0, DateTimeOffset.UtcNow);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal(0, result.DeviceCount);
        Assert.Empty(provider.StatusCalls);
        Assert.Empty(provider.SnapshotCalls);
    }

    [Fact]
    public async Task PollHouseholdAsync_Calls_GetStatusSnapshotAsync_Exactly_Once_Per_Device_And_Never_The_Legacy_Methods()
    {
        // Guards against the regression this test suite was added to catch: polling
        // must never call both GetStatusAsync (state) and GetPlugMiniReadingAsync
        // (telemetry) separately for the same device in the same cycle -- it must
        // use the combined snapshot call exactly once per device instead.
        var device = SwitchBotDevice("plug-1");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(now));
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 12.3, now);
        provider.PlugMiniReadings[device.ExternalDeviceId] = new PlugMiniPowerReading(
            device.ExternalDeviceId, VoltageV: 100.0, CurrentMa: 500.0, DailyEnergyWh: 12.3, UsageMinutesToday: 30, now);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal(["plug-1"], provider.SnapshotCalls);
        Assert.Empty(provider.StatusCalls); // legacy state-only method never called
        Assert.Empty(provider.PlugMiniCalls); // legacy Plug-Mini-only method never called

        Assert.Single(result.CreatedEvents);
        Assert.Single(result.CreatedReadings);
    }

    [Fact]
    public async Task PollHouseholdAsync_Against_The_Real_SwitchBotDeviceProvider_Issues_One_Transport_Call_Per_Device_And_Produces_Both_Projections()
    {
        // End-to-end regression test against the real production provider (not just
        // a fake): confirms SwitchBotDeviceProvider.GetStatusSnapshotAsync really
        // does one GET .../status per device, and that PollHouseholdAsync really
        // wires it up so a single Plug Mini poll produces both a state-change
        // DeviceEvent and a PlugMiniReading row from that one call.
        var device = SwitchBotDevice("BBBBBBBBBBBB");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(now));

        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"BBBBBBBBBBBB","deviceType":"Plug Mini (JP)","voltage":100.5,"weight":12.3,"electricityOfDay":30,"electricCurrent":314}}
                """
        };
        var provider = new SwitchBotDeviceProvider(client, Microsoft.Extensions.Logging.Abstractions.NullLogger<SwitchBotDeviceProvider>.Instance);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        // The one and only assertion that actually catches the reported bug: exactly
        // one live status call for this device this cycle, not two.
        Assert.Single(client.StatusRequests);
        Assert.Equal("BBBBBBBBBBBB", client.StatusRequests[0]);

        var createdEvent = Assert.Single(result.CreatedEvents);
        Assert.Equal("on", createdEvent.Event.State); // 314mA at 100.5V => ~31W, well in use

        var createdReading = Assert.Single(result.CreatedReadings);
        Assert.Equal(100.5, createdReading.Reading.VoltageV);
        Assert.Equal(314, createdReading.Reading.CurrentMa);
        Assert.Equal(12.3, createdReading.Reading.DailyEnergyWh);
        Assert.Equal(30, createdReading.Reading.UsageMinutesToday);
    }

    [Fact]
    public async Task PollHouseholdAsync_Deduplicates_A_Reading_For_The_Same_Cycle_Timestamp()
    {
        var device = SwitchBotDevice("plug-1");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(now));
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", 5.0, now);
        provider.PlugMiniReadings[device.ExternalDeviceId] = new PlugMiniPowerReading(
            device.ExternalDeviceId, VoltageV: 100.0, CurrentMa: 500.0, DailyEnergyWh: 1.0, UsageMinutesToday: 1, now);

        // Same clock (not advanced) -> same OccurredAtUtc for both calls; simulates a
        // retried/double-invoked cycle rather than two genuinely distinct polls.
        var first = await service.PollHouseholdAsync(db.HouseholdId, provider);
        var second = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Single(first.CreatedReadings);
        Assert.Empty(second.CreatedReadings);
        Assert.Equal(1, await db.Context.PlugMiniReadings.CountAsync());
    }

    [Fact]
    public async Task PollHouseholdAsync_Ignores_A_Device_Whose_Status_Cannot_Be_Read()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));

        // No status registered in the fake -> GetStatusAsync returns null (as a real
        // provider would for an unreachable/deleted device).
        var provider = new FakePollingDeviceProvider();

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal(1, result.DeviceCount);
        Assert.Empty(result.CreatedEvents);
    }

    /// <summary>
    /// A Plug Mini that stays energised all day while an appliance is switched on and
    /// off behind it must still produce life-rhythm events -- the socket state alone
    /// would report a single "on" in the morning and nothing else ever again.
    /// </summary>
    [Fact]
    public async Task PollHouseholdAsync_Records_Use_From_Power_Draw_While_The_Socket_Stays_On()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new SwitchBotPollingCycleService(db.Context, clock);
        var provider = new FakePollingDeviceProvider();

        // Socket on, nothing running behind it: standby draw only.
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 100.0, 2.0, 0, 0, clock.GetUtcNow());
        var idle = await service.PollHouseholdAsync(db.HouseholdId, provider);
        Assert.Equal("off", Assert.Single(idle.CreatedEvents).Event.State);

        // The socket never changes, but an appliance starts drawing power.
        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 100.0, 400.0, 3, 5, clock.GetUtcNow());
        var inUse = await service.PollHouseholdAsync(db.HouseholdId, provider);

        var started = Assert.Single(inUse.CreatedEvents);
        Assert.Equal("on", started.Event.State);
        Assert.Equal(40.0, started.Event.PowerWatts);

        // ...and stops again, still without the socket changing.
        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 100.0, 2.0, 6, 10, clock.GetUtcNow());
        var finished = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal("off", Assert.Single(finished.CreatedEvents).Event.State);
    }

    /// <summary>A switched-off socket is never "in use", however noisy the current sample.</summary>
    [Fact]
    public async Task PollHouseholdAsync_Never_Reports_Use_While_The_Socket_Is_Off()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(now));
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "off", null, now);
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 100.0, 400.0, 0, 0, now);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal("off", Assert.Single(result.CreatedEvents).Event.State);
    }

    /// <summary>Devices with no telemetry at all keep reporting their own state.</summary>
    [Fact]
    public async Task PollHouseholdAsync_Keeps_Reported_State_When_There_Is_No_Telemetry()
    {
        var device = SwitchBotDevice("dev-1", DeviceType.Light);
        using var db = await new TestDb().SeedAsync(device);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new SwitchBotPollingCycleService(db.Context, new FakeTimeProvider(now));
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, now);

        var result = await service.PollHouseholdAsync(db.HouseholdId, provider);

        Assert.Equal("on", Assert.Single(result.CreatedEvents).Event.State);
    }

    /// <summary>
    /// A second appliance starting on a plug that is already energised and already
    /// drawing power is real activity, so it has to reach the timeline -- but it must
    /// not be counted as another "use", because nobody switched anything on.
    /// </summary>
    [Fact]
    public async Task PollHouseholdAsync_Records_A_Change_In_Draw_While_The_Socket_Stays_On()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new SwitchBotPollingCycleService(db.Context, clock);
        var provider = new FakePollingDeviceProvider();

        // A lamp: ~33W, matching the real Plug Mini readings in production.
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 104.1, 314.0, 0, 0, clock.GetUtcNow());
        var lamp = await service.PollHouseholdAsync(db.HouseholdId, provider);
        Assert.Equal("on", Assert.Single(lamp.CreatedEvents).Event.State);

        // A kettle joins it. The socket state does not move.
        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 104.1, 8000.0, 10, 5, clock.GetUtcNow());
        var kettle = await service.PollHouseholdAsync(db.HouseholdId, provider);

        var surge = Assert.Single(kettle.CreatedEvents).Event;
        Assert.Equal("PowerChange", surge.EventType);
        Assert.Equal("increased", surge.State);
        Assert.Equal("W", surge.Unit);

        // It finishes: back down to the lamp alone.
        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 104.1, 314.0, 20, 10, clock.GetUtcNow());
        var done = await service.PollHouseholdAsync(db.HouseholdId, provider);
        Assert.Equal("decreased", Assert.Single(done.CreatedEvents).Event.State);

        // Two changes in draw, but the appliance was only switched on once.
        var summary = ActivityService.Summarize(
            DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
            await db.Context.DeviceEvents.OrderBy(e => e.OccurredAtUtc).ToListAsync());
        Assert.Equal(1, summary.DeviceUsageCount);
    }

    /// <summary>
    /// The real lamp jitters between 311mA and 314mA all day. If that reached the
    /// timeline the family would see dozens of meaningless entries and stop reading it.
    /// </summary>
    [Fact]
    public async Task PollHouseholdAsync_Ignores_Measurement_Jitter_In_The_Draw()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new SwitchBotPollingCycleService(db.Context, clock);
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 104.1, 314.0, 0, 0, clock.GetUtcNow());
        await service.PollHouseholdAsync(db.HouseholdId, provider);

        foreach (var mA in new[] { 311.0, 313.0, 312.0, 314.0 })
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
            provider.PlugMiniReadings[device.ExternalDeviceId] =
                new PlugMiniPowerReading(device.ExternalDeviceId, 104.1, mA, 0, 0, clock.GetUtcNow());
            Assert.Empty((await service.PollHouseholdAsync(db.HouseholdId, provider)).CreatedEvents);
        }

        Assert.Equal(1, await db.Context.DeviceEvents.CountAsync());
    }

    /// <summary>
    /// A sustained level is reported once, not once per poll: the comparison is against
    /// the last level we recorded, not against the previous sample.
    /// </summary>
    [Fact]
    public async Task PollHouseholdAsync_Reports_A_Sustained_Level_Only_Once()
    {
        var device = SwitchBotDevice("dev-1");
        using var db = await new TestDb().SeedAsync(device);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new SwitchBotPollingCycleService(db.Context, clock);
        var provider = new FakePollingDeviceProvider();

        provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
        provider.PlugMiniReadings[device.ExternalDeviceId] =
            new PlugMiniPowerReading(device.ExternalDeviceId, 104.1, 314.0, 0, 0, clock.GetUtcNow());
        await service.PollHouseholdAsync(db.HouseholdId, provider);

        var created = 0;
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            provider.Statuses[device.ExternalDeviceId] = new ProviderDeviceStatus(device.ExternalDeviceId, "on", null, clock.GetUtcNow());
            provider.PlugMiniReadings[device.ExternalDeviceId] =
                new PlugMiniPowerReading(device.ExternalDeviceId, 104.1, 8000.0, 10, 5, clock.GetUtcNow());
            created += (await service.PollHouseholdAsync(db.HouseholdId, provider)).CreatedEvents.Count;
        }

        Assert.Equal(1, created);
    }

    [Theory]
    [InlineData(33.0, 33.5, false)]   // jitter
    [InlineData(33.0, 40.0, false)]   // absolute swing too small
    [InlineData(900.0, 880.0, false)] // heater cycling: big in watts, small in proportion
    [InlineData(33.0, 833.0, true)]   // a kettle starts
    [InlineData(833.0, 33.0, true)]   // ...and finishes
    public void IsSignificantPowerChange_Separates_Real_Changes_From_Noise(
        double reference, double current, bool expected)
    {
        Assert.Equal(expected, SwitchBotPollingCycleService.IsSignificantPowerChange(reference, current));
    }
}
