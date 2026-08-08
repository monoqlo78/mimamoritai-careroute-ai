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
}
