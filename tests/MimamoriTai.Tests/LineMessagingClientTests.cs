using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Tests;

/// <summary>Captures the request LINE would have received instead of sending it.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    public string? Path { get; private set; }
    public JsonElement Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Path = request.RequestUri!.AbsolutePath;
        var json = await request.Content!.ReadAsStringAsync(cancellationToken);
        Body = JsonDocument.Parse(json).RootElement.Clone();
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>
/// The Flex payload is only ever validated by LINE itself, and a malformed bubble is
/// rejected with a 400 that the family never sees. These tests pin the shape.
/// </summary>
public class LineMessagingClientTests
{
    private static (LineMessagingClient Client, CapturingHandler Handler) Create()
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

    /// <summary>Same client, but with the public origin that makes the mascot sender override possible.</summary>
    private static (LineMessagingClient Client, CapturingHandler Handler) CreateWithMascotSender(
        string publicBaseUrl = "https://mimamoritai.example",
        string senderName = "ミマモ")
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new LineOptions
        {
            Enabled = true,
            ChannelAccessToken = "test-token",
            ChannelSecret = "test-secret",
            PublicBaseUrl = publicBaseUrl,
            SenderName = senderName
        });

        return (new LineMessagingClient(new HttpClient(handler), options, NullLogger<LineMessagingClient>.Instance), handler);
    }

    [Fact]
    public async Task Alert_With_An_Image_Is_Sent_As_A_Flex_Bubble()
    {
        var (client, handler) = Create();
        var card = new LineAlertCard(
            "見守りのお知らせ",
            "今朝はまだ動きがありません。",
            "至急ご確認ください",
            "https://example.invalid/images/mimamo-line-alert.png",
            "https://example.invalid");

        var result = await client.PushAlertAsync("U123", card);

        Assert.True(result.Success);
        Assert.Equal("/v2/bot/message/push", handler.Path);

        var message = handler.Body.GetProperty("messages")[0];
        Assert.Equal("flex", message.GetProperty("type").GetString());

        // altText is what the phone's notification banner shows, so it has to be the
        // message itself and not a placeholder like "アラート".
        Assert.Equal(card.Text, message.GetProperty("altText").GetString());

        var contents = message.GetProperty("contents");
        Assert.Equal("bubble", contents.GetProperty("type").GetString());
        Assert.Equal(card.ImageUrl, contents.GetProperty("hero").GetProperty("url").GetString());
        Assert.Equal(
            card.LinkUrl,
            contents.GetProperty("footer").GetProperty("contents")[0].GetProperty("action").GetProperty("uri").GetString());

        var body = contents.GetProperty("body").GetProperty("contents");
        Assert.Equal(card.RiskLabel, body[0].GetProperty("text").GetString());
        Assert.Equal(card.Title, body[1].GetProperty("text").GetString());
        Assert.Equal(card.Text, body[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Alert_Without_An_Image_Falls_Back_To_A_Text_Message()
    {
        var (client, handler) = Create();

        var result = await client.PushAlertAsync("U123", new LineAlertCard("見守りのお知らせ", "本文", "見守り中です"));

        Assert.True(result.Success);
        var message = handler.Body.GetProperty("messages")[0];
        Assert.Equal("text", message.GetProperty("type").GetString());
        Assert.Equal("本文", message.GetProperty("text").GetString());
    }

    /// <summary>A bubble with no link must simply omit the footer rather than emit an empty one.</summary>
    [Fact]
    public async Task Bubble_Without_A_Link_Has_No_Footer()
    {
        var (client, handler) = Create();

        await client.PushAlertAsync(
            "U123",
            new LineAlertCard("見守りのお知らせ", "本文", "見守り中です", "https://example.invalid/m.png"));

        var contents = handler.Body.GetProperty("messages")[0].GetProperty("contents");
        Assert.False(contents.TryGetProperty("footer", out _));
    }

    // --- Mascot sender override --------------------------------------------
    // The LINE Official Account's own icon can only be changed by a human in LINE
    // Official Account Manager. `sender` is the only part of the account's appearance
    // reachable from code, so these tests pin it: a family that opens the chat should
    // see ミマモ, not whatever placeholder the channel was registered with.

    [Fact]
    public async Task Push_Carries_The_Mascot_Name_And_Avatar()
    {
        var (client, handler) = CreateWithMascotSender();

        await client.PushAsync("U123", "本文");

        var sender = handler.Body.GetProperty("messages")[0].GetProperty("sender");
        Assert.Equal("ミマモ", sender.GetProperty("name").GetString());
        Assert.Equal("https://mimamoritai.example/images/mimamo-avatar.png", sender.GetProperty("iconUrl").GetString());
    }

    [Fact]
    public async Task Reply_Carries_The_Mascot_Name_And_Avatar()
    {
        var (client, handler) = CreateWithMascotSender();

        await client.ReplyAsync("reply-token", "本文");

        var sender = handler.Body.GetProperty("messages")[0].GetProperty("sender");
        Assert.Equal("ミマモ", sender.GetProperty("name").GetString());
        Assert.Equal("https://mimamoritai.example/images/mimamo-avatar.png", sender.GetProperty("iconUrl").GetString());
    }

    /// <summary>The alert card is the message families actually act on, so it must carry the mascot too.</summary>
    [Fact]
    public async Task Alert_Bubble_Carries_The_Mascot_Name_And_Avatar()
    {
        var (client, handler) = CreateWithMascotSender();

        await client.PushAlertAsync(
            "U123",
            new LineAlertCard("見守りのお知らせ", "本文", "見守り中です", "https://example.invalid/m.png"));

        var message = handler.Body.GetProperty("messages")[0];
        Assert.Equal("flex", message.GetProperty("type").GetString());
        Assert.Equal("ミマモ", message.GetProperty("sender").GetProperty("name").GetString());
    }

    /// <summary>
    /// A trailing slash on the configured origin must not produce a double slash: LINE
    /// fetches the icon itself and a malformed URL silently falls back to the account
    /// picture, which is the exact bug this feature exists to fix.
    /// </summary>
    [Fact]
    public async Task Icon_Url_Is_Built_Without_A_Double_Slash()
    {
        var (client, handler) = CreateWithMascotSender("https://mimamoritai.example/");

        await client.PushAsync("U123", "本文");

        Assert.Equal(
            "https://mimamoritai.example/images/mimamo-avatar.png",
            handler.Body.GetProperty("messages")[0].GetProperty("sender").GetProperty("iconUrl").GetString());
    }

    /// <summary>
    /// Without a public https origin there is no URL LINE could fetch, so the override
    /// has to be omitted entirely -- `"sender": null` is rejected with a 400 and would
    /// cost the family the whole message.
    /// </summary>
    [Fact]
    public async Task Sender_Is_Omitted_When_No_Public_Origin_Is_Configured()
    {
        var (client, handler) = Create();

        await client.PushAsync("U123", "本文");

        Assert.False(handler.Body.GetProperty("messages")[0].TryGetProperty("sender", out _));
    }

    /// <summary>An http origin cannot be fetched by LINE, so it must not produce a sender either.</summary>
    [Fact]
    public async Task Sender_Is_Omitted_For_A_Non_Https_Origin()
    {
        var (client, handler) = CreateWithMascotSender("http://localhost:5000");

        await client.PushAsync("U123", "本文");

        Assert.False(handler.Body.GetProperty("messages")[0].TryGetProperty("sender", out _));
    }

    /// <summary>LINE rejects a name over 20 characters; dropping the label beats dropping the message.</summary>
    [Fact]
    public async Task Sender_Is_Omitted_When_The_Display_Name_Is_Too_Long()
    {
        var (client, handler) = CreateWithMascotSender(senderName: new string('み', LineSenderFactory.MaxNameLength + 1));

        await client.PushAsync("U123", "本文");

        Assert.False(handler.Body.GetProperty("messages")[0].TryGetProperty("sender", out _));
    }

    [Fact]
    public void An_Absolute_Icon_Url_Is_Used_Verbatim()
    {
        var sender = LineSenderFactory.Create(
            "https://mimamoritai.example",
            "https://cdn.example/mimamo.png",
            "ミマモ");

        Assert.NotNull(sender);
        Assert.Equal("https://cdn.example/mimamo.png", sender.IconUrl);
    }

    [Fact]
    public void A_Blank_Icon_Path_Produces_No_Sender() =>
        Assert.Null(LineSenderFactory.Create("https://mimamoritai.example", "  ", "ミマモ"));
}
