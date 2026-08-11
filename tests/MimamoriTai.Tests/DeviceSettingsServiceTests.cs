using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Web.Services;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers the owner opt-in that <see cref="DeviceSyncService"/> deliberately leaves ungranted.
/// Sync discovers hardware but never authorizes it, so without these transitions a synced
/// device stays permanently unusable - the exact state real hardware was found in.
/// </summary>
public class DeviceSettingsServiceTests
{
    private static DeviceSettingsService Service(TestDb db, CurrentUser? user) =>
        new(db.Context, new HouseholdAccessService(db.Context, new FakeCurrentUserAccessor(user), TimeProvider.System));

    private static async Task<(Guid HouseholdId, Guid DeviceId, CurrentUser Owner)> SeedOwnedPlugAsync(TestDb db)
    {
        var owner = FakeCurrentUserAccessor.User(Guid.NewGuid(), "所有者");
        var access = new HouseholdAccessService(db.Context, new FakeCurrentUserAccessor(owner), TimeProvider.System);
        var householdId = await access.EnsureProductionHouseholdAsync("曽我部家");

        // Mirrors exactly what DeviceSyncService produces for a discovered plug.
        var device = new Device
        {
            HouseholdId = householdId,
            ExternalDeviceId = "8CFD49F79C92",
            Name = "プラグミニ 92",
            Alias = "plug-mini-92",
            DeviceType = DeviceType.Plug,
            Room = "リビング",
            Provider = DeviceProviderKind.SwitchBot,
            RemoteControlAllowed = false,
            SafetyClass = SafetyClass.Restricted
        };
        db.Context.Devices.Add(device);
        await db.Context.SaveChangesAsync();

        return (householdId, device.Id, owner);
    }

    [Fact]
    public async Task Owner_Can_Grant_RemoteControl_And_TurnOn_Becomes_Allowed()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);

        var result = await Service(db, owner).UpdatePermissionsAsync(deviceId, remoteControlAllowed: true, treatAsSafeAppliance: true);

        Assert.Equal(DeviceSettingsUpdateStatus.Updated, result.Status);

        var device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.True(device.RemoteControlAllowed);
        Assert.Equal(SafetyClass.Safe, device.SafetyClass);

        // The whole point of the opt-in: the safety policy now lets the device be switched on.
        Assert.Null(DeviceSafetyPolicy.Validate(device, DeviceAction.TurnOn, confidence: 1.0));
        Assert.Null(DeviceSafetyPolicy.Validate(device, DeviceAction.TurnOff, confidence: 1.0));
    }

    [Fact]
    public async Task Granting_RemoteControl_Without_SafeOptIn_Allows_Only_TurnOff()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);

        await Service(db, owner).UpdatePermissionsAsync(deviceId, remoteControlAllowed: true, treatAsSafeAppliance: false);

        var device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.True(device.RemoteControlAllowed);
        Assert.Equal(SafetyClass.Restricted, device.SafetyClass);

        // A plug may hide a heater, so turning it on unattended stays refused.
        Assert.NotNull(DeviceSafetyPolicy.Validate(device, DeviceAction.TurnOn, confidence: 1.0));
        Assert.Null(DeviceSafetyPolicy.Validate(device, DeviceAction.TurnOff, confidence: 1.0));
    }

    [Fact]
    public async Task Revoking_RemoteControl_Restores_The_DeviceTypes_Own_Classification()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);
        var service = Service(db, owner);

        await service.UpdatePermissionsAsync(deviceId, remoteControlAllowed: true, treatAsSafeAppliance: true);
        await service.UpdatePermissionsAsync(deviceId, remoteControlAllowed: false, treatAsSafeAppliance: false);

        var device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.False(device.RemoteControlAllowed);
        Assert.Equal(SafetyClass.Restricted, device.SafetyClass);
        Assert.NotNull(DeviceSafetyPolicy.Validate(device, DeviceAction.TurnOff, confidence: 1.0));
    }

    [Fact]
    public async Task Rename_Stores_An_Override_And_Keeps_The_Vendor_Label_Resolvable()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);

        // The vendor label cannot be reached by asking for "リビングの電気"...
        Assert.Empty(DeviceResolver.Resolve(db.Context.Devices.ToList(), "リビングの電気"));

        var result = await Service(db, owner).RenameAsync(deviceId, "リビングの電気");

        Assert.Equal(DeviceSettingsUpdateStatus.Updated, result.Status);
        var device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.Equal("リビングの電気", device.DisplayNameOverride);
        Assert.Equal("リビングの電気", device.DisplayName);

        // The provider values stay untouched, otherwise the next sync has nothing to match on.
        Assert.Equal("プラグミニ 92", device.Name);
        Assert.Equal("plug-mini-92", device.Alias);

        // ...and after the rename both the new and the original wording find it.
        Assert.Single(DeviceResolver.Resolve(db.Context.Devices.ToList(), "リビングの電気"));
        Assert.Single(DeviceResolver.Resolve(db.Context.Devices.ToList(), "プラグミニ 92"));
    }

    [Fact]
    public async Task Renaming_Back_To_The_Vendor_Label_Clears_The_Override()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);
        var service = Service(db, owner);

        await service.RenameAsync(deviceId, "リビングの電気");
        await service.RenameAsync(deviceId, "プラグミニ 92");

        var device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.Null(device.DisplayNameOverride);
        Assert.Equal("プラグミニ 92", device.DisplayName);
    }

    [Fact]
    public async Task UpdateNaming_Sets_Room_And_Clearing_It_Falls_Back_To_The_Provider_Room()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);
        var service = Service(db, owner);

        var result = await service.UpdateNamingAsync(deviceId, "テレビ", "寝室");

        Assert.Equal(DeviceSettingsUpdateStatus.Updated, result.Status);
        var device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.Equal("寝室", device.RoomOverride);
        Assert.Equal("寝室", device.DisplayRoom);
        Assert.Equal("リビング", device.Room);

        // Emptying the field means "use whatever the hub says" rather than "blank the room".
        await service.UpdateNamingAsync(deviceId, "テレビ", "   ");

        device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.Null(device.RoomOverride);
        Assert.Equal("リビング", device.DisplayRoom);
    }

    [Fact]
    public async Task UpdateNaming_Rejects_An_Overlong_Room()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);

        var result = await Service(db, owner).UpdateNamingAsync(deviceId, "テレビ", new string('あ', 65));

        Assert.Equal(DeviceSettingsUpdateStatus.InvalidName, result.Status);
        var device = db.Context.Devices.Single(d => d.Id == deviceId);
        Assert.Null(device.RoomOverride);
        Assert.Null(device.DisplayNameOverride);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_Rejects_Blank_Names(string candidate)
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, owner) = await SeedOwnedPlugAsync(db);

        var result = await Service(db, owner).RenameAsync(deviceId, candidate);

        Assert.Equal(DeviceSettingsUpdateStatus.InvalidName, result.Status);
        Assert.Equal("プラグミニ 92", db.Context.Devices.Single(d => d.Id == deviceId).DisplayName);
    }

    [Fact]
    public async Task Stranger_Cannot_Rename()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, _) = await SeedOwnedPlugAsync(db);
        var stranger = FakeCurrentUserAccessor.User(Guid.NewGuid(), "他人");

        var result = await Service(db, stranger).RenameAsync(deviceId, "乗っ取り");

        Assert.Equal(DeviceSettingsUpdateStatus.NotFoundOrDenied, result.Status);
        Assert.Equal("プラグミニ 92", db.Context.Devices.Single(d => d.Id == deviceId).DisplayName);
    }

    [Fact]
    public async Task Stranger_Cannot_Grant_RemoteControl()
    {
        using var db = await new TestDb().SeedAsync();
        var (_, deviceId, _) = await SeedOwnedPlugAsync(db);
        var stranger = FakeCurrentUserAccessor.User(Guid.NewGuid(), "他人");

        var result = await Service(db, stranger).UpdatePermissionsAsync(deviceId, remoteControlAllowed: true, treatAsSafeAppliance: true);

        Assert.Equal(DeviceSettingsUpdateStatus.NotFoundOrDenied, result.Status);
        Assert.False(db.Context.Devices.Single(d => d.Id == deviceId).RemoteControlAllowed);
    }

    [Fact]
    public async Task Sample_Household_Devices_Cannot_Be_Changed()
    {
        // Sample data is shared with every anonymous visitor, so one visitor must not be
        // able to change what the next one sees.
        using var db = await new TestDb().SeedAsync(TestDb.Light(remoteAllowed: false));
        var deviceId = db.Context.Devices.Single().Id;

        var result = await Service(db, null).UpdatePermissionsAsync(deviceId, remoteControlAllowed: true, treatAsSafeAppliance: true);

        Assert.Equal(DeviceSettingsUpdateStatus.SampleHouseholdNotEditable, result.Status);
        Assert.False(db.Context.Devices.Single().RemoteControlAllowed);
    }
}
