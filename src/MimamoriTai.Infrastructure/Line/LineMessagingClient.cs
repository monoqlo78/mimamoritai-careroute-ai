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

    public Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default) =>
        PostAsync("/v2/bot/message/push", new
        {
            to,
            messages = new[] { new { type = "text", text } }
        }, ct);

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
