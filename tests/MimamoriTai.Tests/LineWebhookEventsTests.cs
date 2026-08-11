using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Line;
using MimamoriTai.Web.Endpoints;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers the LINE webhook's event parsing (follow/unfollow/message, malformed JSON,
/// missing source, group fallback) and the recipient resolver's precedence rules.
/// Fake ids below are obviously-fake (never a real LINE user/group id).
/// </summary>
public class LineWebhookEventsTests
{
    private const string FakeUserId = "Utestuser0000000000000000000000000";
    private const string FakeGroupId = "Ctestgroup0000000000000000000000000";

    private static string FollowEventJson(string userId, string replyToken = "reply-1") =>
        "{\"events\":[{\"type\":\"follow\",\"replyToken\":\"" + replyToken + "\",\"source\":{\"type\":\"user\",\"userId\":\"" + userId + "\"}}]}";

    private static string UnfollowEventJson(string userId) =>
        "{\"events\":[{\"type\":\"unfollow\",\"source\":{\"type\":\"user\",\"userId\":\"" + userId + "\"}}]}";

    private static string MessageEventJson(string userId, string text, string replyToken = "reply-1") =>
        "{\"events\":[{\"type\":\"message\",\"replyToken\":\"" + replyToken + "\",\"source\":{\"type\":\"user\",\"userId\":\"" + userId +
        "\"},\"message\":{\"type\":\"text\",\"text\":\"" + text + "\"}}]}";

    private static string GroupMessageEventJson(string groupId, string text) =>
        "{\"events\":[{\"type\":\"message\",\"replyToken\":\"reply-1\",\"source\":{\"type\":\"group\",\"groupId\":\"" + groupId +
        "\"},\"message\":{\"type\":\"text\",\"text\":\"" + text + "\"}}]}";

    /// <summary>The shape LINE really sends for a group message: the group AND the speaker.</summary>
    private static string GroupMessageWithSpeakerJson(string groupId, string speakerUserId, string text) =>
        "{\"events\":[{\"type\":\"message\",\"replyToken\":\"reply-1\",\"source\":{\"type\":\"group\",\"groupId\":\"" + groupId +
        "\",\"userId\":\"" + speakerUserId + "\"},\"message\":{\"type\":\"text\",\"text\":\"" + text + "\"}}]}";

    private static string JoinEventJson(string groupId, string speakerUserId) =>
        "{\"events\":[{\"type\":\"join\",\"replyToken\":\"reply-1\",\"source\":{\"type\":\"group\",\"groupId\":\"" + groupId +
        "\",\"userId\":\"" + speakerUserId + "\"}}]}";

    private static string LeaveEventJson(string groupId) =>
        "{\"events\":[{\"type\":\"leave\",\"source\":{\"type\":\"group\",\"groupId\":\"" + groupId + "\"}}]}";

    private static string PostbackEventJson(string userId, string data, string replyToken = "reply-1") =>
        "{\"events\":[{\"type\":\"postback\",\"replyToken\":\"" + replyToken + "\",\"source\":{\"type\":\"user\",\"userId\":\"" + userId +
        "\"},\"postback\":{\"data\":\"" + data + "\"}}]}";

    [Fact]
    public void ParseEvents_Extracts_A_Follow_Event()
    {
        var events = WebhookEndpoints.ParseEvents(FollowEventJson(FakeUserId));

        var evt = Assert.Single(events);
        Assert.Equal("follow", evt.Type);
        Assert.Equal("reply-1", evt.ReplyToken);
        Assert.Equal(FakeUserId, evt.SourceId);
        Assert.Equal("user", evt.SourceType);
        Assert.Null(evt.Text);
    }

    [Fact]
    public void ParseEvents_Extracts_An_Unfollow_Event()
    {
        var events = WebhookEndpoints.ParseEvents(UnfollowEventJson(FakeUserId));

        var evt = Assert.Single(events);
        Assert.Equal("unfollow", evt.Type);
        Assert.Equal(FakeUserId, evt.SourceId);
    }

    [Fact]
    public void ParseEvents_Extracts_A_Message_Event()
    {
        var events = WebhookEndpoints.ParseEvents(MessageEventJson(FakeUserId, "こんにちは"));

        var evt = Assert.Single(events);
        Assert.Equal("message", evt.Type);
        Assert.Equal("こんにちは", evt.Text);
        Assert.Equal(FakeUserId, evt.SourceId);
    }

    [Fact]
    public void ParseEvents_GroupSource_FallsBackTo_GroupId()
    {
        var events = WebhookEndpoints.ParseEvents(GroupMessageEventJson(FakeGroupId, "みんな元気？"));

        var evt = Assert.Single(events);
        Assert.Equal(FakeGroupId, evt.SourceId);
        Assert.Equal("group", evt.SourceType);
    }

    /// <summary>
    /// A real group message carries the group id AND the speaking member's user id.
    /// The group must win: registering the speaker instead would send every later alert
    /// to that one person privately, and the rest of the family would never see it.
    /// </summary>
    [Fact]
    public void ParseEvents_GroupMessage_Prefers_The_Group_Over_The_Speaker()
    {
        var events = WebhookEndpoints.ParseEvents(
            GroupMessageWithSpeakerJson(FakeGroupId, FakeUserId, "おばあちゃん大丈夫？"));

        var evt = Assert.Single(events);
        Assert.Equal(FakeGroupId, evt.SourceId);
        Assert.Equal("group", evt.SourceType);
    }

    /// <summary>A multi-person "room" behaves exactly like a group.</summary>
    [Fact]
    public void ParseEvents_RoomSource_Prefers_The_Room_Over_The_Speaker()
    {
        const string roomId = "Rtestroom00000000000000000000000000";
        var json = "{\"events\":[{\"type\":\"message\",\"replyToken\":\"reply-1\",\"source\":{\"type\":\"room\",\"roomId\":\"" +
                   roomId + "\",\"userId\":\"" + FakeUserId + "\"},\"message\":{\"type\":\"text\",\"text\":\"やあ\"}}]}";

        var events = WebhookEndpoints.ParseEvents(json);

        var evt = Assert.Single(events);
        Assert.Equal(roomId, evt.SourceId);
        Assert.Equal("room", evt.SourceType);
    }

    /// <summary>
    /// "join" fires when the bot is invited into the family group. It is the group-chat
    /// counterpart of "follow", so it has to survive parsing to be able to register the group.
    /// </summary>
    [Fact]
    public void ParseEvents_Extracts_A_Join_Event_For_The_Group()
    {
        var events = WebhookEndpoints.ParseEvents(JoinEventJson(FakeGroupId, FakeUserId));

        var evt = Assert.Single(events);
        Assert.Equal("join", evt.Type);
        Assert.Equal(FakeGroupId, evt.SourceId);
        Assert.Equal("reply-1", evt.ReplyToken);
    }

    [Fact]
    public void ParseEvents_Extracts_A_Leave_Event_For_The_Group()
    {
        var events = WebhookEndpoints.ParseEvents(LeaveEventJson(FakeGroupId));

        var evt = Assert.Single(events);
        Assert.Equal("leave", evt.Type);
        Assert.Equal(FakeGroupId, evt.SourceId);
        Assert.Null(evt.ReplyToken);
    }

    [Fact]
    public void ParseEvents_MalformedJson_ReturnsEmptyList_NeverThrows()
    {
        var events = WebhookEndpoints.ParseEvents("{ this is not valid json");

        Assert.Empty(events);
    }

    [Fact]
    public void ParseEvents_MissingSource_StillReturnsEventWithNullSourceId()
    {
        const string json = """{"events":[{"type":"follow","replyToken":"reply-1"}]}""";

        var events = WebhookEndpoints.ParseEvents(json);

        var evt = Assert.Single(events);
        Assert.Equal("follow", evt.Type);
        Assert.Null(evt.SourceId);
    }

    [Fact]
    public void ParseEvents_Extracts_A_Postback_Event_With_Data()
    {
        var events = WebhookEndpoints.ParseEvents(PostbackEventJson(FakeUserId, "action=emergency"));

        var evt = Assert.Single(events);
        Assert.Equal("postback", evt.Type);
        Assert.Equal("reply-1", evt.ReplyToken);
        Assert.Equal(FakeUserId, evt.SourceId);
        Assert.Equal("action=emergency", evt.PostbackData);
        Assert.Null(evt.Text);
    }

    [Fact]
    public void ParseEvents_MessageEvent_HasNullPostbackData()
    {
        var events = WebhookEndpoints.ParseEvents(MessageEventJson(FakeUserId, "こんにちは"));

        var evt = Assert.Single(events);
        Assert.Null(evt.PostbackData);
    }

    [Fact]
    public void ParseTextEvents_LegacyWrapper_StillExtractsMessageEvents()
    {
        var events = WebhookEndpoints.ParseTextEvents(MessageEventJson(FakeUserId, "点灯して"));

        var (replyToken, text) = Assert.Single(events);
        Assert.Equal("reply-1", replyToken);
        Assert.Equal("点灯して", text);
    }
}

/// <summary>Covers <see cref="LineRecipient"/> upsert/deactivate semantics and resolver precedence.</summary>
public class LineRecipientTests
{
    private static async Task UpsertAsync(TestDb db, Guid householdId, string lineUserId, bool isActive, DateTimeOffset now)
    {
        var existing = await db.Context.LineRecipients
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.LineUserId == lineUserId);

        if (existing is null)
        {
            db.Context.LineRecipients.Add(new LineRecipient
            {
                HouseholdId = householdId,
                LineUserId = lineUserId,
                IsActive = isActive,
                CreatedAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            existing.IsActive = isActive;
            existing.LastSeenAt = now;
        }

        await db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task ApplyDataSource_Switches_To_Production_So_Real_Devices_Are_Reachable()
    {
        using var db = await new TestDb().SeedAsync();
        var production = new Household
        {
            Id = Guid.NewGuid(),
            Name = "わが家",
            DataSourceMode = DataSourceMode.Production
        };
        db.Context.Households.Add(production);
        await db.Context.SaveChangesAsync();
        // The context defaults to Sample, which routes every command to the mock provider.
        var context = new DataSourceContext();

        await WebhookEndpoints.ApplyDataSourceAsync(db.Context, context, production.Id, CancellationToken.None);

        Assert.Equal(DataSourceMode.Production, context.Mode);
        Assert.Equal(production.Id, context.HouseholdId);
    }

    [Fact]
    public async Task ApplyDataSource_Keeps_Sample_For_A_Sample_Household()
    {
        using var db = await new TestDb().SeedAsync();
        var context = new DataSourceContext { Mode = DataSourceMode.Production };

        await WebhookEndpoints.ApplyDataSourceAsync(db.Context, context, db.HouseholdId, CancellationToken.None);

        Assert.Equal(DataSourceMode.Sample, context.Mode);
    }

    [Fact]
    public async Task ApplyDataSource_Leaves_Context_Untouched_For_An_Unknown_Household()
    {
        using var db = await new TestDb().SeedAsync();
        var context = new DataSourceContext { Mode = DataSourceMode.Production };

        await WebhookEndpoints.ApplyDataSourceAsync(db.Context, context, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(DataSourceMode.Production, context.Mode);
    }

    [Fact]
    public async Task Upsert_SameUser_Twice_Produces_One_Row_With_Refreshed_LastSeenAt()
    {
        using var db = await new TestDb().SeedAsync();
        const string userId = "Utestuser0000000000000000000000000";
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var second = first.AddHours(1);

        await UpsertAsync(db, db.HouseholdId, userId, isActive: true, first);
        await UpsertAsync(db, db.HouseholdId, userId, isActive: true, second);

        var recipient = Assert.Single(db.Context.LineRecipients);
        Assert.Equal(userId, recipient.LineUserId);
        Assert.Equal(first, recipient.CreatedAt);
        Assert.Equal(second, recipient.LastSeenAt);
        Assert.True(recipient.IsActive);
    }

    [Fact]
    public async Task Unfollow_Deactivates_The_Recipient()
    {
        using var db = await new TestDb().SeedAsync();
        const string userId = "Utestuser0000000000000000000000000";
        var now = DateTimeOffset.UtcNow;

        await UpsertAsync(db, db.HouseholdId, userId, isActive: true, now);
        await UpsertAsync(db, db.HouseholdId, userId, isActive: false, now.AddMinutes(1));

        var recipient = Assert.Single(db.Context.LineRecipients);
        Assert.False(recipient.IsActive);
    }

    [Fact]
    public async Task Resolver_Prefers_ConfiguredAlertToId_Over_DbRows()
    {
        using var db = await new TestDb().SeedAsync();
        await UpsertAsync(db, db.HouseholdId, "Utestuser0000000000000000000000001", isActive: true, DateTimeOffset.UtcNow);

        var settings = new WatchAlertSettings { ToId = "configured-group-id" };
        var resolver = new LineRecipientResolver(db.Context, settings);

        var targets = await resolver.ResolveAsync(db.HouseholdId);

        Assert.Equal(["configured-group-id"], targets);
    }

    [Fact]
    public async Task Resolver_UsesActiveDbRows_WhenConfigIsEmpty()
    {
        using var db = await new TestDb().SeedAsync();
        await UpsertAsync(db, db.HouseholdId, "Utestuser0000000000000000000000001", isActive: true, DateTimeOffset.UtcNow);
        await UpsertAsync(db, db.HouseholdId, "Utestuser0000000000000000000000002", isActive: false, DateTimeOffset.UtcNow);

        var settings = new WatchAlertSettings { ToId = string.Empty };
        var resolver = new LineRecipientResolver(db.Context, settings);

        var targets = await resolver.ResolveAsync(db.HouseholdId);

        Assert.Equal(["Utestuser0000000000000000000000001"], targets);
    }

    [Fact]
    public async Task Resolver_ReturnsEmpty_WhenNeitherConfigNorRecipientsExist()
    {
        using var db = await new TestDb().SeedAsync();
        var settings = new WatchAlertSettings { ToId = string.Empty };
        var resolver = new LineRecipientResolver(db.Context, settings);

        var targets = await resolver.ResolveAsync(db.HouseholdId);

        Assert.Empty(targets);
    }
}
