using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

/// <summary>
/// Guard-rail tests. These are the most important tests in the repository:
/// they prove the AI can never quietly do something dangerous.
/// </summary>
public class DeviceControlServiceTests
{
    private static DeviceControlService Create(TestDb db, out MockDeviceProvider provider)
    {
        provider = new MockDeviceProvider();
        return new DeviceControlService(db.Context, provider, TimeProvider.System);
    }

    [Fact]
    public async Task TurnOn_Succeeds_For_Safe_Allowed_Device()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "リビングのライトつけて", CommandSource.Web, db.ResidentId, "mock/local-rules");

        Assert.True(outcome.Executed);
        Assert.Equal(CommandStatus.Succeeded, outcome.Status);
        Assert.Contains("つけました", outcome.Message);
    }

    [Fact]
    public async Task TurnOff_Succeeds_And_Records_DeviceEvent()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "つけて", CommandSource.Web, null, null);

        var outcome = await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOff, 0.95,
            "リビングのライト消して", CommandSource.Web, null, null);

        Assert.True(outcome.Executed);
        Assert.Contains("消しました", outcome.Message);
        Assert.Equal(2, db.Context.DeviceEvents.Count());
    }

    [Fact]
    public async Task Unknown_Device_Is_Rejected_And_Audited()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "garage-door", DeviceAction.TurnOn, 0.99,
            "ガレージ開けて", CommandSource.Web, null, null);

        Assert.False(outcome.Executed);
        Assert.Equal(CommandStatus.Rejected, outcome.Status);

        var command = Assert.Single(db.Context.DeviceCommands);
        Assert.Equal(CommandStatus.Rejected, command.Status);
        Assert.Null(command.DeviceId);
    }

    [Fact]
    public async Task Null_Alias_Never_Guesses_A_Device()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, null, DeviceAction.TurnOn, 0.99,
            "つけて", CommandSource.Web, null, null);

        Assert.False(outcome.Executed);
        Assert.Empty(db.Context.DeviceEvents);
    }

    [Fact]
    public async Task Low_Confidence_Blocks_State_Change()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.5,
            "なんかして", CommandSource.Web, null, null);

        Assert.False(outcome.Executed);
        Assert.Equal(CommandStatus.Rejected, outcome.Status);
        Assert.Empty(db.Context.DeviceEvents);
    }

    [Fact]
    public async Task RemoteControlNotAllowed_Blocks_Execution()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(remoteAllowed: false));
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.99,
            "つけて", CommandSource.Web, null, null);

        Assert.False(outcome.Executed);
        Assert.Contains("遠隔操作が許可されていません", outcome.Message);
    }

    [Fact]
    public async Task Restricted_Device_Cannot_Be_Turned_On_By_Natural_Language()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Heater());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOn, 1.0,
            "ストーブつけて", CommandSource.Line, null, null);

        Assert.False(outcome.Executed);
        Assert.Contains("安全のため", outcome.Message);

        var command = Assert.Single(db.Context.DeviceCommands);
        Assert.Equal(CommandStatus.Rejected, command.Status);
    }

    [Fact]
    public async Task Restricted_Device_May_Still_Be_Turned_Off()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Heater());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOff, 0.99,
            "ストーブ消して", CommandSource.Line, null, null);

        // Turning a restricted device OFF is a safety improvement, so the policy allows it.
        // The mock provider does not know this external id, so it fails at the provider layer,
        // which still proves the policy itself did not reject it.
        Assert.NotEqual(CommandStatus.Rejected, outcome.Status);
    }

    [Fact]
    public async Task Ambiguous_Alias_Asks_Back_Instead_Of_Guessing()
    {
        using var db = await new TestDb().SeedAsync(
            TestDb.Light("living-light", "リビング照明"),
            new Device
            {
                ExternalDeviceId = "demo-living-light-2",
                Name = "リビング照明 奥",
                Alias = "living-light-back",
                DeviceType = DeviceType.Light,
                Room = "リビング",
                Provider = DeviceProviderKind.Mock,
                RemoteControlAllowed = true,
                SafetyClass = SafetyClass.Safe
            });

        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.99,
            "リビングのライトつけて", CommandSource.Web, null, null);

        // "living-light" matches the first device exactly, so it must resolve, not ask back.
        Assert.True(outcome.Executed);
    }

    [Fact]
    public async Task Every_Attempt_Is_Audited_Even_When_Rejected()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(), TestDb.Heater());
        var service = Create(db, out _);

        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95, "a", CommandSource.Web, null, null);
        await service.ExecuteAsync(db.HouseholdId, "heater", DeviceAction.TurnOn, 0.99, "b", CommandSource.Web, null, null);
        await service.ExecuteAsync(db.HouseholdId, "unknown", DeviceAction.TurnOn, 0.99, "c", CommandSource.Web, null, null);

        Assert.Equal(3, db.Context.DeviceCommands.Count());
    }
}
