using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;

namespace MimamoriTai.Tests;

/// <summary>
/// The dashboard's "家族にLINEで送る" panel spent a long time looking like it worked while
/// delivering nothing: it ran the text through the assistant and printed the reply on the
/// page. These tests pin down that the panel's path now actually pushes, and that when it
/// cannot push it says so instead of failing silently again.
/// </summary>
public class LineConversationRelayTests
{
    private sealed class FakeClient(bool configured, bool succeeds = true) : ILineMessagingClient
    {
        public List<(string To, string Text)> Pushed { get; } = [];

        public bool IsConfigured => configured;

        public bool VerifySignature(string body, string? signature) => true;

        public Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default) =>
            Task.FromResult(new LineSendResult(true));

        public Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default)
        {
            Pushed.Add((to, text));
            return Task.FromResult(new LineSendResult(succeeds, succeeds ? null : "rejected"));
        }
    }

    private sealed class FakeRecipients(params string[] ids) : ILineRecipientResolver
    {
        public Task<IReadOnlyList<string>> ResolveAsync(Guid householdId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(ids);
    }

    [Fact]
    public async Task Sending_From_The_Dashboard_Reaches_Every_Recipient()
    {
        var client = new FakeClient(configured: true);
        var relay = new LineConversationRelay(client, new FakeRecipients("U1", "U2"));

        var result = await relay.SendAsync(Guid.NewGuid(), "たろう", "そろそろ電話するね");

        Assert.Equal(LineRelayOutcome.Sent, result.Outcome);
        Assert.Equal(2, result.Delivered);
        Assert.Equal(2, client.Pushed.Count);
        Assert.Contains("そろそろ電話するね", client.Pushed[0].Text);
    }

    [Fact]
    public void The_Message_Names_Who_Wrote_It()
    {
        // A message landing in a shared family talk with no name leaves the resident
        // guessing which of their children sent it.
        var text = LineConversationRelay.ComposeMessage("はなこ", "今日は寒いね");

        Assert.StartsWith("はなこさんからのメッセージ", text);
        Assert.Contains("今日は寒いね", text);
    }

    [Fact]
    public void An_Unnamed_Sender_Still_Gets_An_Attribution()
    {
        var text = LineConversationRelay.ComposeMessage("  ", "起きてる？");

        Assert.StartsWith("ご家族さんからのメッセージ", text);
    }

    [Fact]
    public async Task Without_Credentials_Nothing_Is_Pushed_And_The_Family_Is_Told()
    {
        var client = new FakeClient(configured: false);
        var relay = new LineConversationRelay(client, new FakeRecipients("U1"));

        var result = await relay.SendAsync(Guid.NewGuid(), "たろう", "ただいま");

        Assert.Equal(LineRelayOutcome.NotConfigured, result.Outcome);
        Assert.Empty(client.Pushed);
        Assert.NotNull(result.Explanation);
    }

    [Fact]
    public async Task Nobody_Added_The_Bot_Yet_Is_Reported_Rather_Than_Silently_Dropped()
    {
        var client = new FakeClient(configured: true);
        var relay = new LineConversationRelay(client, new FakeRecipients());

        var result = await relay.SendAsync(Guid.NewGuid(), "たろう", "ただいま");

        Assert.Equal(LineRelayOutcome.NoRecipient, result.Outcome);
        Assert.Empty(client.Pushed);
        Assert.Contains("友だち追加", result.Explanation);
    }

    [Fact]
    public async Task A_Rejected_Push_Is_Surfaced_As_A_Failure()
    {
        var client = new FakeClient(configured: true, succeeds: false);
        var relay = new LineConversationRelay(client, new FakeRecipients("U1"));

        var result = await relay.SendAsync(Guid.NewGuid(), "たろう", "ただいま");

        Assert.Equal(LineRelayOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.Delivered);
        Assert.NotNull(result.Explanation);
    }

    [Fact]
    public async Task One_Blocked_Family_Member_Does_Not_Silence_The_Others()
    {
        var client = new PartlyBlockedClient();
        var relay = new LineConversationRelay(client, new FakeRecipients("blocked", "U2"));

        var result = await relay.SendAsync(Guid.NewGuid(), "たろう", "ただいま");

        Assert.Equal(LineRelayOutcome.Sent, result.Outcome);
        Assert.Equal(1, result.Delivered);
    }

    private sealed class PartlyBlockedClient : ILineMessagingClient
    {
        public bool IsConfigured => true;

        public bool VerifySignature(string body, string? signature) => true;

        public Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default) =>
            Task.FromResult(new LineSendResult(true));

        public Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default) =>
            Task.FromResult(new LineSendResult(to != "blocked", to == "blocked" ? "blocked" : null));
    }
}
