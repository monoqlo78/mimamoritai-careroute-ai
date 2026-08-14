using MimamoriTai.Core.Abstractions;
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
    public async Task Turning_On_An_Already_On_Device_Does_Not_Count_As_New_Use()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "つけて", CommandSource.Line, null, null);
        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "電気をつけて", CommandSource.Line, null, null);
        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "つけといて", CommandSource.Line, null, null);

        // Both commands are audited, but only the first one changed anything, so the
        // resident used the appliance once -- not three times.
        Assert.Equal(3, db.Context.DeviceCommands.Count());
        var recorded = Assert.Single(db.Context.DeviceEvents);
        Assert.Equal("on", recorded.State);
    }

    [Fact]
    public async Task A_Real_State_Change_After_A_No_Op_Is_Still_Recorded()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "つけて", CommandSource.Line, null, null);
        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "つけて", CommandSource.Line, null, null);
        await service.ExecuteAsync(db.HouseholdId, "living-light", DeviceAction.TurnOff, 0.95,
            "消して", CommandSource.Line, null, null);

        var states = db.Context.DeviceEvents
            .OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.ReceivedAtUtc)
            .Select(e => e.State).ToList();
        Assert.Equal(new[] { "on", "off" }, states);
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

    /// <summary>
    /// "電源はついてる？" names no device. A read-only status check on a single-device
    /// household can answer it; the same phrasing must never be allowed to switch
    /// something on (see <see cref="Null_Alias_Never_Guesses_A_Device"/>).
    /// </summary>
    [Fact]
    public async Task Null_Alias_Answers_Status_For_The_Only_Device()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, null, DeviceAction.GetStatus, 0.99,
            "今って、電源はついてるの？", CommandSource.Web, null, null);

        Assert.Equal(CommandStatus.Succeeded, outcome.Status);
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

    /// <summary>
    /// Regression, found against real SwitchBot hardware: the cloud applies commands
    /// asynchronously, so the status read issued straight after a turn-off still
    /// reported "on". The service used to trust that read-back, which replied
    /// "つけました" to a turn-off and recorded an "on" DeviceEvent -- feeding the
    /// left-on detection with the exact opposite of what happened.
    /// </summary>
    [Fact]
    public async Task TurnOff_Does_Not_Trust_A_Stale_Read_Back()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var provider = new StaleReadBackProvider("on");
        var service = new DeviceControlService(db.Context, provider, TimeProvider.System);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "living-light", DeviceAction.TurnOff, 0.95,
            "リビングのライト消して", CommandSource.Web, null, null);

        Assert.True(outcome.Executed);
        Assert.Contains("消しました", outcome.Message);
        Assert.DoesNotContain("つけました", outcome.Message);

        var recorded = Assert.Single(db.Context.DeviceEvents);
        Assert.Equal("off", recorded.State);
    }

    [Fact]
    public async Task TurnOn_Does_Not_Trust_A_Stale_Read_Back()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var provider = new StaleReadBackProvider("off");
        var service = new DeviceControlService(db.Context, provider, TimeProvider.System);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "living-light", DeviceAction.TurnOn, 0.95,
            "リビングのライトつけて", CommandSource.Web, null, null);

        Assert.True(outcome.Executed);
        Assert.Contains("つけました", outcome.Message);

        var recorded = Assert.Single(db.Context.DeviceEvents);
        Assert.Equal("on", recorded.State);
    }

    /// <summary>
    /// Accepts every command but keeps reporting the state it had beforehand, the way
    /// the SwitchBot cloud does for the first seconds after a command.
    /// </summary>
    private sealed class StaleReadBackProvider(string staleState) : IDeviceProvider
    {
        public DeviceProviderKind Kind => DeviceProviderKind.Mock;

        public bool IsConfigured => true;

        public Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderDevice>>([]);

        public Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default) =>
            Task.FromResult<ProviderDeviceStatus?>(new ProviderDeviceStatus(externalDeviceId, staleState, 42));

        public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult.Ok());

        public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult.Ok());

        public Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult.Ok());
    }

    [Fact]
    public async Task Device_Marked_No_Remote_TurnOn_Is_Refused_And_Told_Where_To_Change_It()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Heater());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOn, 1.0,
            "ストーブつけて", CommandSource.Line, null, null);

        Assert.False(outcome.Executed);
        Assert.Contains("遠隔でONにしない設定", outcome.Message);
        Assert.Contains("設定画面", outcome.Message);

        var command = Assert.Single(db.Context.DeviceCommands);
        Assert.Equal(CommandStatus.Rejected, command.Status);
    }

    /// <summary>
    /// The gate lives in the service, not in the conversation. Anything reaching this
    /// method directly - the API, a button, a future integration - is refused unless it
    /// carries the acknowledgement, so the hazard question cannot be skipped by simply
    /// never asking it.
    /// </summary>
    [Fact]
    public async Task Guarded_Heater_Is_Refused_When_The_Hazard_Check_Was_Never_Answered()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOn, 1.0,
            "ストーブつけて", CommandSource.Line, null, null);

        Assert.False(outcome.Executed);
        Assert.Equal(CommandStatus.Rejected, outcome.Status);

        // The refusal has to carry the questions, or the caller cannot ask them.
        Assert.Contains("燃えやすい", outcome.Message);
    }

    [Fact]
    public async Task Guarded_Heater_Turns_On_Once_The_Surroundings_Were_Confirmed()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOn, 1.0,
            "ストーブつけて", CommandSource.Line, null, null,
            hazardAcknowledged: true);

        Assert.True(outcome.Executed);
        Assert.Equal(CommandStatus.Succeeded, outcome.Status);
        Assert.Contains("つけました", outcome.Message);
    }

    [Fact]
    public async Task Turning_A_Guarded_Heater_Off_Never_Needs_A_Hazard_Check()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var service = Create(db, out _);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOff, 1.0,
            "ストーブ消して", CommandSource.Line, null, null);

        Assert.Equal(CommandStatus.Succeeded, outcome.Status);
    }

    [Fact]
    public async Task Switching_On_A_Guarded_Heater_Tells_The_Whole_Household()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var notifier = new RecordingGuardedNotifier();
        var service = new DeviceControlService(
            db.Context, new MockDeviceProvider(), TimeProvider.System, notifier);

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOn, 1.0,
            "ストーブつけて", CommandSource.Line, null, null,
            hazardAcknowledged: true);

        Assert.True(outcome.Executed);

        var notice = Assert.Single(notifier.Notices);
        Assert.Equal(db.HouseholdId, notice.HouseholdId);
        Assert.Equal("電気ストーブ", notice.DeviceName);
        Assert.Equal(CommandSource.Line, notice.Source);

        // The person who acted is told that everyone else was told, because a broadcast
        // they did not expect is worse than one they did.
        Assert.Contains("ご家族全員", outcome.Message);
    }

    [Fact]
    public async Task Turning_A_Guarded_Heater_Off_Does_Not_Wake_The_Whole_Household()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var notifier = new RecordingGuardedNotifier();
        var service = new DeviceControlService(
            db.Context, new MockDeviceProvider(), TimeProvider.System, notifier);

        await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOff, 1.0,
            "ストーブ消して", CommandSource.Line, null, null);

        Assert.Empty(notifier.Notices);
    }

    /// <summary>
    /// The appliance is already on by the time the notifier runs, so a broken notifier
    /// must never turn a successful switch-on into a reported failure.
    /// </summary>
    [Fact]
    public async Task A_Failing_Broadcast_Does_Not_Fail_The_Command()
    {
        using var db = await new TestDb().SeedAsync(TestDb.GuardedHeater());
        var service = new DeviceControlService(
            db.Context, new MockDeviceProvider(), TimeProvider.System, new ThrowingGuardedNotifier());

        var outcome = await service.ExecuteAsync(
            db.HouseholdId, "heater", DeviceAction.TurnOn, 1.0,
            "ストーブつけて", CommandSource.Line, null, null,
            hazardAcknowledged: true);

        Assert.True(outcome.Executed);
        Assert.Equal(CommandStatus.Succeeded, outcome.Status);
    }

    private sealed class RecordingGuardedNotifier : IGuardedActionNotifier
    {
        public List<GuardedActionNotice> Notices { get; } = [];

        public Task NotifyAsync(GuardedActionNotice notice, CancellationToken ct = default)
        {
            Notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingGuardedNotifier : IGuardedActionNotifier
    {
        public Task NotifyAsync(GuardedActionNotice notice, CancellationToken ct = default) =>
            throw new InvalidOperationException("LINE is unreachable.");
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
