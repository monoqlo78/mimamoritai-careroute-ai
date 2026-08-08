using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Tests;

public class AssistantOrchestratorTests
{
    private static AssistantOrchestrator Create(TestDb db, IAiRouterClient? ai = null, IFabricDataAgentClient? fabric = null)
    {
        var provider = new MockDeviceProvider();
        return new AssistantOrchestrator(
            db.Context,
            ai ?? new MockAiRouterClient(),
            provider,
            fabric ?? new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System);
    }

    [Fact]
    public async Task Natural_Language_Turns_A_Light_On()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, db.ResidentId, "リビングのライトつけて", CommandSource.Web));

        Assert.Equal(AssistantIntent.ControlDevice, response.Intent);
        Assert.True(response.DeviceChanged);
        Assert.Equal(MockAiRouterClient.MockModelName, response.ResolvedModel);
    }

    [Fact]
    public async Task Natural_Language_Turns_A_Light_Off()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(new AssistantRequest(db.HouseholdId, null, "リビングのライトつけて", CommandSource.Web));
        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "リビングのライト消して", CommandSource.Web));

        Assert.True(response.DeviceChanged);
        Assert.Contains("消しました", response.Reply);
    }

    [Fact]
    public async Task Data_Question_Falls_Back_To_Local_Service_When_Fabric_Is_Unconfigured()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "今日のお母さんどう？", CommandSource.Line));

        Assert.Equal(AssistantIntent.QueryData, response.Intent);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
    }

    [Fact]
    public async Task Every_Turn_Is_Logged_As_AiRequestLog_And_FamilyMessage()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        await orchestrator.HandleAsync(new AssistantRequest(db.HouseholdId, db.ResidentId, "リビングのライトつけて", CommandSource.Web));

        Assert.NotEmpty(db.Context.AiRequestLogs);
        // one user message + one AI reply
        Assert.Equal(2, db.Context.FamilyMessages.Count());
    }

    [Fact]
    public async Task Unparsable_Model_Output_Never_Executes_Anything()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var broken = new BrokenAiRouterClient();
        var orchestrator = Create(db, broken);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "リビングのライトつけて", CommandSource.Web));

        Assert.False(response.DeviceChanged);
        Assert.Empty(db.Context.DeviceCommands);
        // exactly one repair attempt, then give up
        Assert.Equal(2, broken.CallCount);
    }

    private sealed class BrokenAiRouterClient : IAiRouterClient
    {
        public int CallCount { get; private set; }

        public bool IsConfigured => true;

        public string DisplayName => "BrokenRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new AiCompletionResult(true, "申し訳ありません、よく分かりません。", DisplayName, "broken/model", 1));
        }
    }
}

public class MockIntegrationTests
{
    [Fact]
    public async Task MockDeviceProvider_Turns_Devices_On_And_Off()
    {
        var provider = new MockDeviceProvider();
        var id = MockDeviceProvider.SeedDevices[0].ExternalDeviceId;

        Assert.True((await provider.TurnOnAsync(id)).Success);
        Assert.True((await provider.GetStatusAsync(id))!.IsOn);

        Assert.True((await provider.TurnOffAsync(id)).Success);
        Assert.False((await provider.GetStatusAsync(id))!.IsOn);
    }

    [Fact]
    public async Task MockDeviceProvider_Rejects_Unknown_Device()
    {
        var provider = new MockDeviceProvider();
        Assert.False((await provider.TurnOnAsync("no-such-device")).Success);
        Assert.Null(await provider.GetStatusAsync("no-such-device"));
    }

    /// <summary>
    /// The demo seeder builds Device rows from <see cref="MockDeviceProvider.SeedDevices"/>.
    /// If the two ever diverge, commands are accepted by the safety policy and then fail at
    /// the provider with "unknown device", which silently breaks the demo.
    /// </summary>
    [Fact]
    public async Task MockDeviceProvider_Knows_Every_Seed_Device()
    {
        var provider = new MockDeviceProvider();

        foreach (var device in MockDeviceProvider.SeedDevices)
        {
            Assert.NotNull(await provider.GetStatusAsync(device.ExternalDeviceId));
            Assert.True(MockDeviceProvider.SeedAliases.ContainsKey(device.ExternalDeviceId));
        }
    }

    /// <summary>
    /// The headline safety demo requires at least one Restricted device in the seed set.
    /// </summary>
    [Fact]
    public void SeedDevices_Contain_A_Restricted_Device()
    {
        Assert.Contains(
            MockDeviceProvider.SeedDevices,
            d => DeviceSafetyPolicy.Classify(d.DeviceType) == SafetyClass.Restricted);
    }

    [Fact]
    public async Task MockFabric_Reports_Unconfigured()
    {
        var fabric = new MockFabricDataAgentClient();
        Assert.False(fabric.IsConfigured);
        Assert.False((await fabric.AskAsync("今日どう？")).Success);
    }

    [Fact]
    public async Task MockAiRouter_Emits_Parsable_Intent_Json()
    {
        var ai = new MockAiRouterClient();
        var result = await ai.CompleteAsync(
            [AiMessage.User("リビングのライトつけて")], "intent", jsonMode: true);

        var plan = IntentParser.TryParse(result.Content);
        Assert.NotNull(plan);
        Assert.Equal(AssistantIntent.ControlDevice, plan!.Intent);
        Assert.Equal(DeviceAction.TurnOn, plan.Action);
        Assert.True(plan.Confidence >= IntentParser.MinimumConfidence);
    }

    [Fact]
    public async Task MockAiRouter_Uses_Low_Confidence_When_Action_Is_Unclear()
    {
        var ai = new MockAiRouterClient();
        var result = await ai.CompleteAsync(
            [AiMessage.User("リビングのライト")], "intent", jsonMode: true);

        var plan = IntentParser.TryParse(result.Content);
        Assert.NotNull(plan);
        Assert.True(plan!.Confidence < IntentParser.MinimumConfidence);
    }
}

public class LineSignatureTests
{
    private const string Secret = "test-channel-secret";

    private static string Sign(string body, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void Valid_Signature_Is_Accepted()
    {
        const string body = """{"events":[]}""";
        Assert.True(LineSignature.Verify(Secret, body, Sign(body, Secret)));
    }

    [Fact]
    public void Signature_From_A_Different_Secret_Is_Rejected()
    {
        const string body = """{"events":[]}""";
        Assert.False(LineSignature.Verify(Secret, body, Sign(body, "attacker-secret")));
    }

    [Fact]
    public void Tampered_Body_Is_Rejected()
    {
        const string body = """{"events":[]}""";
        var signature = Sign(body, Secret);
        Assert.False(LineSignature.Verify(Secret, """{"events":[{"evil":true}]}""", signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    public void Missing_Or_Malformed_Signature_Is_Rejected(string? signature)
    {
        Assert.False(LineSignature.Verify(Secret, "{}", signature));
    }

    [Fact]
    public void Missing_Channel_Secret_Rejects_Everything()
    {
        Assert.False(LineSignature.Verify(null, "{}", Sign("{}", Secret)));
    }

    [Fact]
    public void MockLineClient_Still_Verifies_A_Configured_Secret()
    {
        var client = new MockLineMessagingClient(
            Options.Create(new LineOptions { ChannelSecret = Secret }));

        const string body = """{"events":[]}""";
        Assert.True(client.VerifySignature(body, Sign(body, Secret)));
        Assert.False(client.VerifySignature(body, Sign(body, "wrong")));
    }
}
