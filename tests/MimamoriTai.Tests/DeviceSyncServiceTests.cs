using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>Returns a canned device list; TurnOn/TurnOff/Toggle/GetStatus are unused by DeviceSyncService tests.</summary>
public sealed class FakeDeviceProvider : IDeviceProvider
{
    public DeviceProviderKind Kind => DeviceProviderKind.SwitchBot;

    public bool IsConfigured { get; init; } = true;

    public List<ProviderDevice> Devices { get; set; } = [];

    public Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderDevice>>(Devices);

    public Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult<ProviderDeviceStatus?>(null);

    public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());

    public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());

    public Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Ok());
}

public class DeviceSyncServiceTests
{
    private static DeviceSyncService Create(TestDb db, FakeDeviceProvider provider, FakeTimeProvider? clock = null) =>
        new(db.Context, provider, clock ?? new FakeTimeProvider(DateTimeOffset.UtcNow));

    [Fact]
    public async Task SyncAsync_Adds_New_Devices_From_Provider()
    {
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices =
            [
                new ProviderDevice("sb-light-1", "リビング照明", DeviceType.Light, "リビング"),
                new ProviderDevice("sb-fan-1", "扇風機", DeviceType.Fan, "リビング")
            ]
        };
        var sync = Create(db, provider);

        var result = await sync.SyncAsync(db.HouseholdId);

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Deactivated);
        Assert.Equal(2, db.Context.Devices.Count());
        Assert.All(db.Context.Devices, d => Assert.True(d.IsActive));
        // Sync only discovers devices; it never grants remote control on its own.
        Assert.All(db.Context.Devices, d => Assert.False(d.RemoteControlAllowed));
    }

    [Fact]
    public async Task SyncAsync_Is_Idempotent_Running_Twice_Adds_Rows_Once()
    {
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices = [new ProviderDevice("sb-light-1", "リビング照明", DeviceType.Light, "リビング")]
        };
        var sync = Create(db, provider);

        var first = await sync.SyncAsync(db.HouseholdId);
        var second = await sync.SyncAsync(db.HouseholdId);

        Assert.Equal(1, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Deactivated);
        Assert.Single(db.Context.Devices);
    }

    [Fact]
    public async Task SyncAsync_Updates_Name_And_Type_When_Provider_Data_Changes()
    {
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices = [new ProviderDevice("sb-light-1", "リビング照明", DeviceType.Light, "リビング")]
        };
        var sync = Create(db, provider);
        await sync.SyncAsync(db.HouseholdId);

        provider.Devices = [new ProviderDevice("sb-light-1", "リビング照明（改名）", DeviceType.Light, "居間")];
        var result = await sync.SyncAsync(db.HouseholdId);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        var device = db.Context.Devices.Single();
        Assert.Equal("リビング照明（改名）", device.Name);
        Assert.Equal("居間", device.Room);
    }

    [Fact]
    public async Task SyncAsync_Never_Overwrites_The_Name_And_Room_The_Family_Set_By_Hand()
    {
        // The reason overrides exist: SwitchBot has no notion of a room, so the raw values
        // are wrong on screen, and before this the next poll silently undid every correction.
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices = [new ProviderDevice("sb-light-1", "Plug Mini 92", DeviceType.Light, "Hub 02-202502")]
        };
        var sync = Create(db, provider);
        await sync.SyncAsync(db.HouseholdId);

        var device = db.Context.Devices.Single();
        device.DisplayNameOverride = "台所の電気";
        device.RoomOverride = "台所";
        await db.Context.SaveChangesAsync();

        await sync.SyncAsync(db.HouseholdId);
        // Even when the provider then reports something different, the correction stands.
        provider.Devices = [new ProviderDevice("sb-light-1", "Plug Mini 92 (renamed)", DeviceType.Light, "Hub 02-202502")];
        await sync.SyncAsync(db.HouseholdId);

        device = db.Context.Devices.Single();
        Assert.Equal("台所の電気", device.DisplayName);
        Assert.Equal("台所", device.DisplayRoom);
        // The provider values keep tracking the hub so future syncs can still match on them.
        Assert.Equal("Plug Mini 92 (renamed)", device.Name);
        Assert.Equal("Hub 02-202502", device.Room);
    }

    [Fact]
    public async Task SyncAsync_Deactivates_Device_That_Vanished_From_Provider()
    {
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices =
            [
                new ProviderDevice("sb-light-1", "リビング照明", DeviceType.Light, "リビング"),
                new ProviderDevice("sb-fan-1", "扇風機", DeviceType.Fan, "リビング")
            ]
        };
        var sync = Create(db, provider);
        await sync.SyncAsync(db.HouseholdId);

        // The fan disappears from the provider's device list (e.g. unplugged/removed).
        provider.Devices = [new ProviderDevice("sb-light-1", "リビング照明", DeviceType.Light, "リビング")];
        var result = await sync.SyncAsync(db.HouseholdId);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Deactivated);

        var fan = db.Context.Devices.Single(d => d.ExternalDeviceId == "sb-fan-1");
        Assert.False(fan.IsActive);
        // Deactivation never deletes the row.
        Assert.Equal(2, db.Context.Devices.Count());
    }

    [Fact]
    public async Task SyncAsync_Reactivates_Device_That_Reappears_In_Provider()
    {
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices = [new ProviderDevice("sb-fan-1", "扇風機", DeviceType.Fan, "リビング")]
        };
        var sync = Create(db, provider);
        await sync.SyncAsync(db.HouseholdId);

        provider.Devices = [];
        await sync.SyncAsync(db.HouseholdId);
        Assert.False(db.Context.Devices.Single().IsActive);

        provider.Devices = [new ProviderDevice("sb-fan-1", "扇風機", DeviceType.Fan, "リビング")];
        var result = await sync.SyncAsync(db.HouseholdId);

        Assert.Equal(1, result.Updated);
        Assert.True(db.Context.Devices.Single().IsActive);
    }

    [Fact]
    public async Task SyncAsync_With_DeactivateMissing_False_Still_Adds_New_Devices()
    {
        // This is the mode SwitchBotPollingBackgroundService's periodic auto-discovery
        // uses: a second Plug Mini added on the SwitchBot side must still show up as a
        // new Devices row without anyone pressing "今すぐ同期する".
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices = [new ProviderDevice("sb-plug-1", "プラグミニ", DeviceType.Fan, "リビング")]
        };
        var sync = Create(db, provider);
        await sync.SyncAsync(db.HouseholdId, deactivateMissing: false);

        provider.Devices =
        [
            new ProviderDevice("sb-plug-1", "プラグミニ", DeviceType.Fan, "リビング"),
            new ProviderDevice("sb-plug-2", "プラグミニ76", DeviceType.Fan, "リビング")
        ];
        var result = await sync.SyncAsync(db.HouseholdId, deactivateMissing: false);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, db.Context.Devices.Count());
        Assert.All(db.Context.Devices, d => Assert.True(d.IsActive));
    }

    [Fact]
    public async Task SyncAsync_With_DeactivateMissing_False_Never_Deactivates_A_Vanished_Device()
    {
        // Auto-discovery must not flip IsActive=false on its own: one bad/incomplete
        // GET /v1.1/devices response could otherwise hide a real device from the
        // dashboard/alerts until an operator investigates. Real removal stays manual
        // (the "今すぐ同期する" button, which keeps deactivateMissing at its true default).
        using var db = await new TestDb().SeedAsync();
        var provider = new FakeDeviceProvider
        {
            Devices =
            [
                new ProviderDevice("sb-light-1", "リビング照明", DeviceType.Light, "リビング"),
                new ProviderDevice("sb-fan-1", "扇風機", DeviceType.Fan, "リビング")
            ]
        };
        var sync = Create(db, provider);
        await sync.SyncAsync(db.HouseholdId, deactivateMissing: false);

        // The fan is momentarily missing from this response (e.g. a transient API hiccup).
        provider.Devices = [new ProviderDevice("sb-light-1", "リビング照明", DeviceType.Light, "リビング")];
        var result = await sync.SyncAsync(db.HouseholdId, deactivateMissing: false);

        Assert.Equal(0, result.Deactivated);
        var fan = db.Context.Devices.Single(d => d.ExternalDeviceId == "sb-fan-1");
        Assert.True(fan.IsActive);
    }
}
