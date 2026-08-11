using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers the rich-menu postback actions (助けて / 体調が悪い / 大丈夫 / 今日の様子 / 家族に連絡):
/// the sender must never receive their own push, "okay" must never push, and every action
/// that pushes must persist a FamilyMessage so it shows up in the web family feed.
/// </summary>
public class LinePostbackActionServiceTests
{
    private const string SenderId = "Utestsender0000000000000000000000";
    private const string OtherFamilyId = "Utestfamily00000000000000000000000";

    private static LinePostbackActionService Create(
        TestDb db,
        FakeLineMessagingClient line,
        IReadOnlyList<string> recipients,
        FakeTimeProvider? clock = null,
        IFabricDataAgentClient? fabric = null)
    {
        var resolver = new FakeLineRecipientResolver(recipients);
        var localData = new LocalDataQuestionService(db.Context, TimeProvider.System);

        return new LinePostbackActionService(
            db.Context,
            line,
            resolver,
            fabric ?? new MockFabricDataAgentClient(),
            localData,
            clock ?? TimeProvider.System);
    }

    [Fact]
    public async Task Emergency_Excludes_The_Sender_And_Pushes_Every_Other_Recipient()
    {
        using var db = await new TestDb().SeedAsync();
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId, OtherFamilyId, "Utestfamily00000000000000000000001"]);

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, LinePostbackActionService.Emergency);

        Assert.Equal(2, outcome.RecipientsNotified);
        Assert.Equal(2, line.Pushed.Count);
        Assert.DoesNotContain(line.Pushed, p => p.To == SenderId);
        Assert.Contains(line.Pushed, p => p.To == OtherFamilyId);
        Assert.Contains("助けて", line.Pushed[0].Text);
        Assert.Contains("家族に伝えました", outcome.ReplyText);

        var saved = Assert.Single(db.Context.FamilyMessages);
        Assert.Equal(MessageType.Notice, saved.MessageType);
        Assert.Equal(CommandSource.Line, saved.Source);
        Assert.Contains("助けて", saved.Content);
    }

    [Fact]
    public async Task Emergency_With_No_Other_Recipients_Tells_The_Sender_To_Call_119()
    {
        using var db = await new TestDb().SeedAsync();
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId]); // only the tapping user is registered

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, LinePostbackActionService.Emergency);

        Assert.Equal(0, outcome.RecipientsNotified);
        Assert.Empty(line.Pushed);
        Assert.Contains("119", outcome.ReplyText);
        Assert.Single(db.Context.FamilyMessages); // still recorded, even without a push
    }

    [Fact]
    public async Task Unwell_Pushes_Others_But_Not_The_Sender()
    {
        using var db = await new TestDb().SeedAsync();
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId, OtherFamilyId]);

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, LinePostbackActionService.Unwell);

        Assert.Equal(1, outcome.RecipientsNotified);
        var pushed = Assert.Single(line.Pushed);
        Assert.Equal(OtherFamilyId, pushed.To);
        Assert.Contains("体調が悪い", pushed.Text);
        Assert.Single(db.Context.FamilyMessages);
    }

    [Fact]
    public async Task ContactFamily_Pushes_Others_But_Not_The_Sender()
    {
        using var db = await new TestDb().SeedAsync();
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId, OtherFamilyId]);

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, LinePostbackActionService.ContactFamily);

        Assert.Equal(1, outcome.RecipientsNotified);
        Assert.Equal(OtherFamilyId, Assert.Single(line.Pushed).To);
        Assert.Single(db.Context.FamilyMessages);
    }

    [Fact]
    public async Task Okay_Never_Pushes_But_Still_Records_A_FamilyMessage()
    {
        using var db = await new TestDb().SeedAsync();
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId, OtherFamilyId]);

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, LinePostbackActionService.Okay);

        Assert.Equal(0, outcome.RecipientsNotified);
        Assert.Empty(line.Pushed);
        Assert.Equal("大丈夫を受け付けました", outcome.ReplyText);
        Assert.Single(db.Context.FamilyMessages);
    }

    [Fact]
    public async Task Status_Uses_Deterministic_Data_Query_And_Replies_With_Its_Answer()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId]);

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, LinePostbackActionService.Status);

        Assert.False(string.IsNullOrWhiteSpace(outcome.ReplyText));
        Assert.Empty(line.Pushed); // status never pushes, it only replies
        Assert.Single(db.Context.FamilyMessages);
    }

    [Fact]
    public async Task Status_Falls_Back_To_Local_Data_When_Fabric_Fails()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId], fabric: new FailingFabricClient());

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, LinePostbackActionService.Status);

        Assert.Contains("家電", outcome.ReplyText);
        Assert.Empty(line.Pushed);
    }

    [Fact]
    public async Task Unknown_Postback_Data_Falls_Back_Without_Throwing_Or_Pushing()
    {
        using var db = await new TestDb().SeedAsync();
        var line = new FakeLineMessagingClient();
        var service = Create(db, line, [SenderId, OtherFamilyId]);

        var outcome = await service.HandleAsync(db.HouseholdId, SenderId, "action=unknown-future-button");

        Assert.False(string.IsNullOrWhiteSpace(outcome.ReplyText));
        Assert.Empty(line.Pushed);
        Assert.Empty(db.Context.FamilyMessages);
    }

    private sealed class FailingFabricClient : IFabricDataAgentClient
    {
        public bool IsConfigured => true;

        public Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default) =>
            Task.FromResult(new FabricAnswer(false, string.Empty, "test", "unavailable"));
    }
}
