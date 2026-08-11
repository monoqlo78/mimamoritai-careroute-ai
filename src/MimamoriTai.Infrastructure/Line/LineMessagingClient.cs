using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Line;

/// <summary>LINE Messaging API client. Credentials come from configuration providers only.</summary>
public sealed class LineMessagingClient(
    HttpClient http,
    IOptions<LineOptions> options,
    ILogger<LineMessagingClient> logger) : ILineMessagingClient
{
    private readonly LineOptions _options = options.Value;

    /// <summary>
    /// Per-message name/icon override applied to everything the bot sends.
    /// Resolved once: it depends only on configuration, and re-deriving it per message
    /// would just repeat the same string work on every alert.
    /// </summary>
    private readonly LineSender? _sender = LineSenderFactory.Create(options.Value);

    public bool IsConfigured => _options.IsConfigured;

    public Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default) =>
        PostAsync("/v2/bot/message/reply", new
        {
            replyToken,
            messages = new[] { BuildTextMessage(text) }
        }, ct);

    public Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default) =>
        PostAsync("/v2/bot/message/push", new
        {
            to,
            messages = new[] { BuildTextMessage(text) }
        }, ct);

    /// <summary>
    /// Wraps a plain text body in a message object, adding the mascot sender override
    /// when one is configured.
    ///
    /// A Dictionary rather than an anonymous type because `sender` has to be absent --
    /// not null -- when it cannot be built: LINE rejects `"sender": null` outright.
    /// </summary>
    private Dictionary<string, object> BuildTextMessage(string text)
    {
        var message = new Dictionary<string, object>
        {
            ["type"] = "text",
            ["text"] = text
        };

        ApplySender(message);
        return message;
    }

    /// <summary>Adds `sender` to a message object when the override is available.</summary>
    private void ApplySender(Dictionary<string, object> message)
    {
        if (_sender is { } sender)
        {
            message["sender"] = new { name = sender.Name, iconUrl = sender.IconUrl };
        }
    }

    /// <summary>
    /// Pushes the alert as a Flex bubble carrying the mascot.
    ///
    /// Falls back to the plain text push when no image URL is available, because a
    /// bubble whose hero image 404s renders as a grey box and looks like a broken
    /// app -- worse than the text the family would otherwise have read.
    /// </summary>
    public Task<LineSendResult> PushAlertAsync(string to, LineAlertCard card, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(card.ImageUrl))
        {
            return PushAsync(to, card.Text, ct);
        }

        return PostAsync("/v2/bot/message/push", new
        {
            to,
            messages = new object[] { BuildAlertMessage(card) }
        }, ct);
    }

    /// <summary>Builds the Flex message envelope, carrying the same mascot sender as a text message.</summary>
    private Dictionary<string, object> BuildAlertMessage(LineAlertCard card)
    {
        var message = new Dictionary<string, object>
        {
            ["type"] = "flex",
            // Shown in the chat list and in the phone's notification banner,
            // where a Flex bubble cannot be rendered.
            ["altText"] = card.Text,
            ["contents"] = BuildAlertBubble(card)
        };

        ApplySender(message);
        return message;
    }

    private static object BuildAlertBubble(LineAlertCard card)
    {
        var body = new List<object>
        {
            new
            {
                type = "text",
                text = card.RiskLabel,
                size = "sm",
                weight = "bold",
                color = "#C2410C"
            },
            new
            {
                type = "text",
                text = card.Title,
                size = "lg",
                weight = "bold",
                color = "#1F2937",
                margin = "sm",
                wrap = true
            },
            new
            {
                type = "text",
                text = card.Text,
                size = "sm",
                color = "#374151",
                margin = "md",
                wrap = true
            }
        };

        object? footer = string.IsNullOrWhiteSpace(card.LinkUrl)
            ? null
            : new
            {
                type = "box",
                layout = "vertical",
                contents = new object[]
                {
                    new
                    {
                        type = "button",
                        style = "primary",
                        color = "#2563EB",
                        height = "sm",
                        action = new { type = "uri", label = "様子をみる", uri = card.LinkUrl }
                    }
                }
            };

        var bubble = new Dictionary<string, object>
        {
            ["type"] = "bubble",
            ["hero"] = new
            {
                type = "image",
                url = card.ImageUrl,
                size = "full",
                // The mascot artwork is close to square; 20:13 keeps the head and the
                // cape in frame without LINE cropping into the face.
                aspectRatio = "20:13",
                aspectMode = "fit",
                backgroundColor = "#EFF6FF"
            },
            ["body"] = new
            {
                type = "box",
                layout = "vertical",
                contents = body
            }
        };

        if (footer is not null)
        {
            bubble["footer"] = footer;
        }

        return bubble;
    }

    public bool VerifySignature(string rawBody, string? signatureHeader) =>
        LineSignature.Verify(_options.ChannelSecret, rawBody, signatureHeader);

    private async Task<LineSendResult> PostAsync(string path, object payload, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return new LineSendResult(false, "LINE is not configured.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), path))
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ChannelAccessToken);

            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LINE API returned {Status}.", (int)response.StatusCode);
                return new LineSendResult(false, $"LINE API returned {(int)response.StatusCode}.");
            }

            return new LineSendResult(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("LINE API request failed: {Type}.", ex.GetType().Name);
            return new LineSendResult(false, ex.GetType().Name);
        }
    }
}
