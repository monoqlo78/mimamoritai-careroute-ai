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
            HouseholdAccessService householdAccess,
            TimeProvider clock,
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

            // The LINE platform calls this endpoint directly (no signed-in web user),
            // so it resolves the default household rather than doing a per-user access check.
            var householdId = await householdAccess.ResolveDefaultAsync(ct);
            if (householdId is null || householdId == Guid.Empty)
            {
                return Results.Ok();
            }

            foreach (var evt in ParseEvents(rawBody))
            {
                switch (evt.Type)
                {
                    case "follow":
                        await UpsertRecipientAsync(db, householdId.Value, evt.SourceId, isActive: true, clock, ct);
                        if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                        {
                            await line.ReplyAsync(evt.ReplyToken, "見守り隊へようこそ。今後、異常を検知した際にこちらへ通知します。", ct);
                        }
                        break;

                    case "unfollow":
                        await DeactivateRecipientAsync(db, householdId.Value, evt.SourceId, ct);
                        break;

                    case "message":
                        await UpsertRecipientAsync(db, householdId.Value, evt.SourceId, isActive: true, clock, ct);

                        var response = await orchestrator.HandleAsync(
                            new AssistantRequest(householdId.Value, null, evt.Text ?? string.Empty, CommandSource.Line), ct);

                        if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
                        {
                            await line.ReplyAsync(evt.ReplyToken, response.Reply, ct);
                        }
                        break;
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

    /// <summary>Creates or refreshes a <see cref="LineRecipient"/> row for the given source id.</summary>
    private static async Task UpsertRecipientAsync(
        AppDbContext db, Guid householdId, string? lineUserId, bool isActive, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineUserId))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var existing = await db.LineRecipients
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.LineUserId == lineUserId, ct);

        if (existing is null)
        {
            db.LineRecipients.Add(new LineRecipient
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

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks a recipient inactive after an `unfollow` event. A no-op if it was never registered.</summary>
    private static async Task DeactivateRecipientAsync(AppDbContext db, Guid householdId, string? lineUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineUserId))
        {
            return;
        }

        var existing = await db.LineRecipients
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.LineUserId == lineUserId, ct);

        if (existing is not null)
        {
            existing.IsActive = false;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>A single LINE webhook event, normalized for both message handling and recipient capture.</summary>
    internal sealed record LineWebhookEvent(string Type, string? ReplyToken, string? Text, string? SourceId, string? SourceType);

    /// <summary>Extracts (replyToken, text) pairs from a LINE webhook body. Kept for backward compatibility.</summary>
    internal static List<(string? ReplyToken, string Text)> ParseTextEvents(string rawBody) =>
        ParseEvents(rawBody)
            .Where(e => e.Type == "message" && !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => (e.ReplyToken, e.Text!))
            .ToList();

    /// <summary>
    /// Parses every event in a LINE webhook body into a small, defensive representation.
    /// Malformed JSON (or any unexpected shape) never throws; it just yields no events.
    /// </summary>
    internal static List<LineWebhookEvent> ParseEvents(string rawBody)
    {
        var result = new List<LineWebhookEvent>();

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var evt in events.EnumerateArray())
            {
                if (!evt.TryGetProperty("type", out var typeElement) || typeElement.GetString() is not { } type)
                {
                    continue;
                }

                var replyToken = evt.TryGetProperty("replyToken", out var tokenElement) ? tokenElement.GetString() : null;
                var (sourceId, sourceType) = ExtractSource(evt);

                string? text = null;
                if (type == "message"
                    && evt.TryGetProperty("message", out var message)
                    && message.TryGetProperty("type", out var messageType)
                    && messageType.GetString() == "text"
                    && message.TryGetProperty("text", out var textElement))
                {
                    text = textElement.GetString();
                }

                // Only "message" events require text; "follow"/"unfollow" carry no message body.
                if (type == "message" && string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                result.Add(new LineWebhookEvent(type, replyToken, text, sourceId, sourceType));
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    /// <summary>
    /// Resolves the id used as the LINE push `to` value: `userId` for 1:1 chats, or `groupId`
    /// (source.type == "group") for group chats. `userId` is preferred when both are present.
    /// </summary>
    private static (string? SourceId, string? SourceType) ExtractSource(JsonElement evt)
    {
        if (!evt.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var sourceType = source.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        if (source.TryGetProperty("userId", out var userIdElement) && userIdElement.GetString() is { } userId)
        {
            return (userId, sourceType);
        }

        if (source.TryGetProperty("groupId", out var groupIdElement) && groupIdElement.GetString() is { } groupId)
        {
            return (groupId, sourceType);
        }

        return (null, sourceType);
    }
}
