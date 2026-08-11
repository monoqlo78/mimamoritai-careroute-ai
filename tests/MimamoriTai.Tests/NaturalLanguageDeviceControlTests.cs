using System.Text.Json;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// End-to-end cover for "say it in plain Japanese and the appliance actually switches",
/// including after the family has renamed the appliance on screen.
/// </summary>
/// <remarks>
/// These deliberately do NOT use <see cref="MimamoriTai.Infrastructure.Ai.MockAiRouterClient"/>:
/// it maps a hard-coded keyword list onto a hard-coded alias, so it would answer
/// "living-light" no matter what the appliance is really called and would prove nothing
/// about renamed devices. <see cref="EchoingAiRouterClient"/> instead behaves like the
/// real model is instructed to - it repeats back the words the speaker used - which puts
/// <see cref="DeviceResolver"/> (the part that actually has to cope with two names) under test.
/// </remarks>
public class NaturalLanguageDeviceControlTests
{
    private static AssistantOrchestrator Create(TestDb db, string spokenDeviceWords) =>
        new(db.Context,
            new EchoingAiRouterClient(spokenDeviceWords),
            new MockDeviceProvider(),
            new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System,
            new InMemoryPendingActionStore());

    private static AssistantRequest Say(TestDb db, string message) =>
        new(db.HouseholdId, null, message, CommandSource.Web);

    /// <summary>Runs the full propose-then-confirm handshake and returns the final reply.</summary>
    private static async Task<AssistantResponse> SayAndConfirmAsync(
        AssistantOrchestrator orchestrator, TestDb db, string message)
    {
        var proposal = await orchestrator.HandleAsync(Say(db, message));
        Assert.True(proposal.AwaitingConfirmation, $"「{message}」が確認待ちになりませんでした: {proposal.Reply}");
        return await orchestrator.HandleAsync(Say(db, "はい"));
    }

    [Fact]
    public async Task The_vendor_name_still_switches_the_appliance_on_and_off()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(name: "リビング照明"));
        var orchestrator = Create(db, "リビング照明");

        var on = await SayAndConfirmAsync(orchestrator, db, "リビング照明つけて");
        Assert.True(on.DeviceChanged);
        Assert.Contains("つけました", on.Reply);

        var off = await SayAndConfirmAsync(orchestrator, db, "リビング照明消して");
        Assert.True(off.DeviceChanged);
        Assert.Contains("消しました", off.Reply);
    }

    [Fact]
    public async Task The_name_the_family_typed_in_switches_the_appliance_on_and_off()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(name: "リビング照明"));
        await RenameAsync(db, "寝室のあかり");

        var orchestrator = Create(db, "寝室のあかり");

        var on = await SayAndConfirmAsync(orchestrator, db, "寝室のあかりつけて");
        Assert.True(on.DeviceChanged);
        // The reply has to use the family's own name, not the vendor label they corrected.
        Assert.Contains("寝室のあかり", on.Reply);
        Assert.DoesNotContain("リビング照明", on.Reply);

        var off = await SayAndConfirmAsync(orchestrator, db, "寝室のあかり消して");
        Assert.True(off.DeviceChanged);
        Assert.Contains("消しました", off.Reply);
    }

    [Fact]
    public async Task The_old_name_keeps_working_after_a_rename()
    {
        // Grandma keeps calling it what it was always called; the device must still answer.
        using var db = await new TestDb().SeedAsync(TestDb.Light(name: "リビング照明"));
        await RenameAsync(db, "寝室のあかり");

        var orchestrator = Create(db, "リビング照明");

        var response = await SayAndConfirmAsync(orchestrator, db, "リビング照明つけて");

        Assert.True(response.DeviceChanged);
        Assert.Contains("寝室のあかり", response.Reply);
    }

    [Fact]
    public async Task Asking_whether_it_is_on_reports_the_real_state_under_the_new_name()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(name: "リビング照明"));
        await RenameAsync(db, "寝室のあかり");

        var orchestrator = Create(db, "寝室のあかり");
        await SayAndConfirmAsync(orchestrator, db, "寝室のあかりつけて");

        // A status question is read-only, so it answers immediately with no confirmation.
        var status = await orchestrator.HandleAsync(Say(db, "寝室のあかりついてる？"));

        Assert.False(status.AwaitingConfirmation);
        Assert.Contains("寝室のあかり", status.Reply);
        Assert.Contains("ON", status.Reply);
    }

    [Fact]
    public async Task An_appliance_nobody_owns_is_never_invented()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(name: "リビング照明"));
        var orchestrator = Create(db, "ガレージのシャッター");

        var response = await orchestrator.HandleAsync(Say(db, "ガレージのシャッターつけて"));

        Assert.False(response.DeviceChanged);
        Assert.False(response.AwaitingConfirmation);
        Assert.Empty(db.Context.DeviceCommands.Where(c => c.Status == CommandStatus.Succeeded));
    }

    private static async Task RenameAsync(TestDb db, string displayName)
    {
        var device = db.Context.Devices.Single();
        device.DisplayNameOverride = displayName;
        await db.Context.SaveChangesAsync();
    }

    /// <summary>
    /// Stands in for the real LLM: it reports the appliance exactly as the speaker named it
    /// and derives the action from the same verbs the production prompt is built around.
    /// </summary>
    private sealed class EchoingAiRouterClient(string deviceWords) : IAiRouterClient
    {
        public bool IsConfigured => true;

        public string DisplayName => "EchoRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            var message = messages.LastOrDefault()?.Content ?? string.Empty;

            var turnOff = message.Contains("消して", StringComparison.Ordinal);
            var turnOn = message.Contains("つけて", StringComparison.Ordinal);
            var status = message.Contains("ついてる", StringComparison.Ordinal);

            var (intent, action) = status
                ? ("device_status", "get_status")
                : turnOff
                    ? ("control_device", "turn_off")
                    : turnOn
                        ? ("control_device", "turn_on")
                        : ("conversation", (string?)null);

            var json = JsonSerializer.Serialize(new
            {
                intent,
                deviceAlias = intent == "conversation" ? null : deviceWords,
                action,
                confidence = 0.95,
                question = (string?)null
            });

            return Task.FromResult(new AiCompletionResult(true, json, DisplayName, "echo/model", 1));
        }
    }
}
