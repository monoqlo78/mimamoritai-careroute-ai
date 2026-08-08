using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Web.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/line", async (
            HttpRequest httpRequest,
            ILineMessagingClient line,
            AssistantOrchestrator orchestrator,
            AppDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LineWebhook");

            httpRequest.EnableBuffering();
            using var reader = new StreamReader(httpRequest.Body, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(ct);
            httpRequest.Body.Position = 0;

            var signature = httpRequest.Headers[LineSignature.HeaderName].FirstOrDefault();

            // When a channel secret is configured the signature must be valid.
            // Requests that fail verification are dropped without processing.
            if (!line.VerifySignature(rawBody, signature))
            {
                logger.LogWarning("Rejected a LINE webhook request with an invalid or missing signature.");
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var householdId = await db.Households.OrderBy(h => h.CreatedAtUtc).Select(h => h.Id).FirstOrDefaultAsync(ct);
            if (householdId == Guid.Empty)
            {
                return Results.Ok();
            }

            foreach (var (replyToken, text) in ParseTextEvents(rawBody))
            {
                var response = await orchestrator.HandleAsync(
                    new AssistantRequest(householdId, null, text, CommandSource.Line), ct);

                if (!string.IsNullOrWhiteSpace(replyToken))
                {
                    await line.ReplyAsync(replyToken, response.Reply, ct);
                }
            }

            return Results.Ok();
        }).WithName("LineWebhook").DisableAntiforgery();

        app.MapPost("/webhooks/switchbot", async (
            HttpRequest httpRequest,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            // Placeholder endpoint. The payload contract is mapped once the physical
            // devices arrive and the official SwitchBot webhook specification is verified.
            var logger = loggerFactory.CreateLogger("SwitchBotWebhook");
            using var reader = new StreamReader(httpRequest.Body);
            _ = await reader.ReadToEndAsync(ct);
            logger.LogInformation("Received a SwitchBot webhook callback. Payload mapping is not implemented yet.");
            return Results.Ok();
        }).WithName("SwitchBotWebhook").DisableAntiforgery();

        return app;
    }

    /// <summary>Extracts (replyToken, text) pairs from a LINE webhook body, ignoring anything malformed.</summary>
    internal static List<(string? ReplyToken, string Text)> ParseTextEvents(string rawBody)
    {
        var result = new List<(string?, string)>();

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var evt in events.EnumerateArray())
            {
                if (!evt.TryGetProperty("type", out var type) || type.GetString() != "message")
                {
                    continue;
                }

                if (!evt.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("type", out var messageType)
                    || messageType.GetString() != "text")
                {
                    continue;
                }

                var text = message.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var replyToken = evt.TryGetProperty("replyToken", out var tokenElement) ? tokenElement.GetString() : null;
                result.Add((replyToken, text));
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }
}
