using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Tests;

/// <summary>
/// A chip that leads nowhere is worse than no chip: the resident taps something the
/// product itself offered and is told it cannot help. These tests hold every choice
/// to the answer behind it, and pin the payload shape LINE will accept.
/// </summary>
public class LineQuickReplyTests
{
    private static (LineMessagingClient Client, CapturingHandler Handler) CreateClient()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new LineOptions
        {
            Enabled = true,
            ChannelAccessToken = "test-token",
            ChannelSecret = "test-secret"
        });

        return (new LineMessagingClient(new HttpClient(handler), options, NullLogger<LineMessagingClient>.Instance), handler);
    }

    [Fact]
    public void The_Menu_Offers_Adding_Family()
    {
        var chip = Assert.Single(LineQuickReplyMenu.Default, c => c.Label == "家族の追加");

        Assert.Equal("家族の追加方法は", chip.MessageText);
        Assert.Null(chip.PostbackData);
    }

    /// <summary>
    /// The reason the chip exists: tapping it must produce the real linking procedure,
    /// with no model call, so it works on the slowest phone and during an AI outage.
    /// </summary>
    [Fact]
    public void Tapping_Add_Family_Answers_With_The_Real_Procedure()
    {
        var chip = Assert.Single(LineQuickReplyMenu.Default, c => c.Label == "家族の追加");

        var answer = AssistantKnowledgeBase.TryAnswer(chip.MessageText!, FaqMatchMode.Strict);

        Assert.NotNull(answer);
        Assert.Contains("家族の追加", answer!.Reply);
        Assert.Contains("連携コードを発行する", answer.Reply);
        Assert.Contains("連携 123456", answer.Reply);
    }

    /// <summary>
    /// Every text chip must be answerable before the intent model runs. Strict mode is
    /// the pre-intent gate, so this also proves a tap costs zero model round-trips and
    /// cannot be lost to the webhook's 8 second budget.
    /// </summary>
    [Fact]
    public void Every_Message_Chip_Is_Answered_Without_A_Model()
    {
        var messageChips = LineQuickReplyMenu.Default.Where(c => c.MessageText is not null).ToList();

        Assert.NotEmpty(messageChips);
        foreach (var chip in messageChips)
        {
            var answer = AssistantKnowledgeBase.TryAnswer(chip.MessageText!, FaqMatchMode.Strict);
            Assert.True(answer is not null, $"Quick reply '{chip.Label}' has no knowledge-base answer.");
        }
    }

    /// <summary>
    /// Postback chips must reuse the existing rich-menu actions rather than invent new
    /// ones, otherwise a tap reaches code LinePostbackActionService never handles and
    /// the resident gets silence.
    /// </summary>
    [Fact]
    public void Every_Postback_Chip_Reuses_A_Known_Action()
    {
        string[] known =
        [
            LinePostbackActionService.Emergency,
            LinePostbackActionService.Unwell,
            LinePostbackActionService.Okay,
            LinePostbackActionService.Status,
            LinePostbackActionService.ContactFamily
        ];

        var postbackChips = LineQuickReplyMenu.Default.Where(c => c.PostbackData is not null).ToList();

        Assert.NotEmpty(postbackChips);
        foreach (var chip in postbackChips)
        {
            Assert.Contains(chip.PostbackData, known);
        }
    }

    /// <summary>
    /// An accidental tap must never be able to summon an ambulance, so the destructive
    /// one-touch actions stay on the rich menu where they are deliberate.
    /// </summary>
    [Fact]
    public void The_Menu_Does_Not_Offer_The_Emergency_Action()
    {
        Assert.DoesNotContain(LineQuickReplyMenu.Default, c => c.PostbackData == LinePostbackActionService.Emergency);
    }

    /// <summary>LINE rejects the whole message when either limit is broken.</summary>
    [Fact]
    public void The_Menu_Stays_Inside_The_Line_Limits()
    {
        Assert.InRange(LineQuickReplyMenu.Default.Count, 1, 13);
        foreach (var chip in LineQuickReplyMenu.Default)
        {
            Assert.InRange(chip.Label.Length, 1, 20);
        }
    }

    [Fact]
    public async Task Chips_Are_Sent_As_A_Quick_Reply_Block()
    {
        var (client, handler) = CreateClient();

        var result = await client.ReplyAsync("token", "こんにちは", LineQuickReplyMenu.Default);

        Assert.True(result.Success);
        Assert.Equal("/v2/bot/message/reply", handler.Path);

        var message = handler.Body.GetProperty("messages")[0];
        Assert.Equal("text", message.GetProperty("type").GetString());

        var items = message.GetProperty("quickReply").GetProperty("items");
        Assert.Equal(LineQuickReplyMenu.Default.Count, items.GetArrayLength());

        var first = items[0];
        Assert.Equal("action", first.GetProperty("type").GetString());

        var action = first.GetProperty("action");
        Assert.Equal("message", action.GetProperty("type").GetString());
        Assert.Equal("家族の追加", action.GetProperty("label").GetString());
        Assert.Equal("家族の追加方法は", action.GetProperty("text").GetString());
    }

    /// <summary>
    /// A postback chip echoes its label into the chat, so the resident can see what
    /// they asked for rather than watching an answer appear out of nowhere.
    /// </summary>
    [Fact]
    public async Task Postback_Chips_Carry_Their_Data_And_Echo_The_Label()
    {
        var (client, handler) = CreateClient();

        await client.ReplyAsync("token", "本文", [LineQuickReply.Postback("今日の様子", LinePostbackActionService.Status)]);

        var action = handler.Body.GetProperty("messages")[0]
            .GetProperty("quickReply").GetProperty("items")[0]
            .GetProperty("action");

        Assert.Equal("postback", action.GetProperty("type").GetString());
        Assert.Equal(LinePostbackActionService.Status, action.GetProperty("data").GetString());
        Assert.Equal("今日の様子", action.GetProperty("displayText").GetString());
    }

    /// <summary>Over-long labels are truncated rather than allowed to fail the send.</summary>
    [Fact]
    public async Task An_Over_Long_Label_Is_Truncated_Instead_Of_Rejected()
    {
        var (client, handler) = CreateClient();
        var longLabel = new string('あ', 30);

        var result = await client.ReplyAsync("token", "本文", [LineQuickReply.Message(longLabel, "使い方")]);

        Assert.True(result.Success);
        var label = handler.Body.GetProperty("messages")[0]
            .GetProperty("quickReply").GetProperty("items")[0]
            .GetProperty("action").GetProperty("label").GetString();

        Assert.Equal(20, label!.Length);
    }

    /// <summary>
    /// More than 13 chips would have LINE reject the message outright, losing the
    /// answer as well as the buttons.
    /// </summary>
    [Fact]
    public async Task No_More_Than_Thirteen_Chips_Are_Sent()
    {
        var (client, handler) = CreateClient();
        var many = Enumerable.Range(0, 20).Select(i => LineQuickReply.Message($"選択{i}")).ToList();

        await client.ReplyAsync("token", "本文", many);

        var items = handler.Body.GetProperty("messages")[0].GetProperty("quickReply").GetProperty("items");
        Assert.Equal(13, items.GetArrayLength());
    }

    /// <summary>
    /// With nothing usable to attach, the reply must still go out as plain text: the
    /// answer matters, the chips do not.
    /// </summary>
    [Fact]
    public async Task An_Empty_Menu_Still_Sends_The_Answer()
    {
        var (client, handler) = CreateClient();

        var result = await client.ReplyAsync("token", "本文", []);

        Assert.True(result.Success);
        var message = handler.Body.GetProperty("messages")[0];
        Assert.Equal("本文", message.GetProperty("text").GetString());
        Assert.False(message.TryGetProperty("quickReply", out _));
    }

    /// <summary>
    /// An implementation that never learned about chips (the demo mock, any future
    /// transport) must still deliver the reply through the interface default.
    /// </summary>
    [Fact]
    public async Task A_Client_Without_Chip_Support_Still_Delivers_The_Reply()
    {
        ILineMessagingClient mock = new MockLineMessagingClient(Options.Create(new LineOptions()));

        var result = await mock.ReplyAsync("token", "本文", LineQuickReplyMenu.Default);

        Assert.True(result.Success);
        Assert.Contains("本文", ((MockLineMessagingClient)mock).SentMessages);
    }
}
