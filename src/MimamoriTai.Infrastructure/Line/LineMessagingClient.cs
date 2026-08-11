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

    public bool IsConfigured => _options.IsConfigured;

    public Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default) =>
        PostAsync("/v2/bot/message/reply", new
        {
            replyToken,
            messages = new[] { new { type = "text", text } }
        }, ct);

    /// <summary>
    /// Replies with quick-reply chips. Falls back to the plain reply when nothing
    /// usable survives <see cref="BuildQuickReplyItems"/>, because LINE rejects the
    /// whole message for a malformed quickReply block -- and a dropped answer is far
    /// worse than a missing row of buttons.
    /// </summary>
    public Task<LineSendResult> ReplyAsync(
        string replyToken,
        string text,
        IReadOnlyList<LineQuickReply> quickReplies,
        CancellationToken ct = default)
    {
        var items = BuildQuickReplyItems(quickReplies);
        if (items.Length == 0)
        {
            return ReplyAsync(replyToken, text, ct);
        }

        return PostAsync("/v2/bot/message/reply", new
        {
            replyToken,
            messages = new object[]
            {
                new
                {
                    type = "text",
                    text,
                    quickReply = new { items }
                }
            }
        }, ct);
    }

    /// <summary>
    /// LINE caps quick replies at 13 items with labels of at most 20 characters and
    /// rejects the entire message when either is exceeded, so both are enforced here
    /// rather than trusted from the caller.
    /// </summary>
    private static object[] BuildQuickReplyItems(IReadOnlyList<LineQuickReply> quickReplies)
    {
        const int maxItems = 13;
        const int maxLabel = 20;

        var items = new List<object>();
        foreach (var chip in quickReplies)
        {
            if (items.Count == maxItems || string.IsNullOrWhiteSpace(chip.Label))
            {
                break;
            }

            var label = chip.Label.Length > maxLabel ? chip.Label[..maxLabel] : chip.Label;

            object action;
            if (!string.IsNullOrWhiteSpace(chip.PostbackData))
            {
                // displayText echoes the label into the chat, so the resident sees what
                // they just asked for instead of a silent, unexplained reply.
                action = new { type = "postback", label, data = chip.PostbackData, displayText = label };
            }
            else if (!string.IsNullOrWhiteSpace(chip.MessageText))
            {
                action = new { type = "message", label, text = chip.MessageText };
            }
            else
            {
                continue;
            }

            items.Add(new { type = "action", action });
        }

        return [.. items];
    }

    public Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default) =>
        PostAsync("/v2/bot/message/push", new
        {
            to,
            messages = new[] { new { type = "text", text } }
        }, ct);

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
            messages = new object[]
            {
                new
                {
                    type = "flex",
                    // Shown in the chat list and in the phone's notification banner,
                    // where a Flex bubble cannot be rendered.
                    altText = card.Text,
                    contents = BuildAlertBubble(card)
                }
            }
        }, ct);
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
