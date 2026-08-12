using System.Text.Json;
using System.Text.Json.Serialization;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Raw shape returned by the language model. Everything is nullable on purpose:
/// the model output is never trusted, it is validated into <see cref="AssistantPlan"/>.
/// </summary>
public sealed class IntentPayload
{
    [JsonPropertyName("intent")] public string? Intent { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
    [JsonPropertyName("deviceAlias")] public string? DeviceAlias { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("question")] public string? Question { get; set; }
}

/// <summary>
/// How far back a data question reaches. Decides whether the Fabric Data Agent is
/// worth consulting: it answers analytical questions the local database cannot,
/// but costs seconds, so questions about the current state are served locally.
/// </summary>
public enum QueryScope
{
    /// <summary>Current or same-day state. Answerable from the local database alone.</summary>
    Recent = 0,

    /// <summary>Comparison, trend or aggregate spanning multiple days.</summary>
    Analysis = 1
}

/// <summary>
/// The first-stage routing decision: which specialist should answer.
///
/// This is carried on the same JSON the intent model already returns, not asked for in a
/// second call. Two model round trips would not fit: the LINE webhook cancels an event
/// after 8 seconds and one classification already spends ~1.7s of it, so a second one
/// would reintroduce the "しばらくたってからお試しください" failure the fast model was
/// pinned to remove.
/// </summary>
public enum AssistantTopic
{
    /// <summary>
    /// How the product works. Must be answered from <see cref="AssistantKnowledgeBase"/>
    /// facts only — an invented button name sends an elderly user hunting for a word that
    /// is not on the screen.
    /// </summary>
    Faq = 0,

    /// <summary>Something in the home should be switched.</summary>
    Device = 1,

    /// <summary>A question about the resident's recorded day.</summary>
    Data = 2,

    /// <summary>Ordinary conversation and common-sense questions.</summary>
    General = 3,

    /// <summary>Health, medicine, care certification, money or law. Never answered here.</summary>
    Expert = 4,

    /// <summary>Something is happening to the person right now.</summary>
    Emergency = 5
}

public sealed record AssistantPlan(
    AssistantIntent Intent,
    string? DeviceAlias,
    DeviceAction? Action,
    double Confidence,
    string? Question,
    QueryScope Scope = QueryScope.Recent,
    AssistantTopic Topic = AssistantTopic.General);

public static class IntentParser
{
    public const double MinimumConfidence = 0.85;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Parses and validates a model response. Returns null when the payload is not
    /// usable so the caller can ask for one correction and then give up.
    /// </summary>
    public static AssistantPlan? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return null;
        }

        IntentPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<IntentPayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        var intent = payload.Intent?.Trim().ToLowerInvariant() switch
        {
            "control_device" => AssistantIntent.ControlDevice,
            "device_status" => AssistantIntent.DeviceStatus,
            "query_data" => AssistantIntent.QueryData,
            "conversation" => AssistantIntent.Conversation,
            _ => (AssistantIntent?)null
        };

        if (intent is null)
        {
            return null;
        }

        DeviceAction? action = payload.Action?.Trim().ToLowerInvariant() switch
        {
            "turn_on" => DeviceAction.TurnOn,
            "turn_off" => DeviceAction.TurnOff,
            "toggle" => DeviceAction.Toggle,
            "get_status" => DeviceAction.GetStatus,
            null or "" or "null" => null,
            _ => null
        };

        // A control intent without a permitted action is not actionable.
        if (intent == AssistantIntent.ControlDevice && action is null)
        {
            return null;
        }

        var confidence = double.IsFinite(payload.Confidence)
            ? Math.Clamp(payload.Confidence, 0d, 1d)
            : 0d;

        var alias = string.IsNullOrWhiteSpace(payload.DeviceAlias) || payload.DeviceAlias == "null"
            ? null
            : payload.DeviceAlias.Trim();

        var question = string.IsNullOrWhiteSpace(payload.Question) || payload.Question == "null"
            ? null
            : payload.Question.Trim();

        // Unknown or missing scope means the model gave no usable signal. Defaulting
        // to Recent keeps the fast local-only path: a missed analytical question loses
        // enrichment, while a wrong Analysis would spend the budget on every "元気?".
        var scope = payload.Scope?.Trim().ToLowerInvariant() switch
        {
            "analysis" => QueryScope.Analysis,
            _ => QueryScope.Recent
        };

        return new AssistantPlan(intent.Value, alias, action, confidence, question, scope, ResolveTopic(intent.Value, payload.Topic));
    }

    /// <summary>
    /// Derives the specialist to hand off to.
    ///
    /// The topic is only ever read from the model for <see cref="AssistantIntent.Conversation"/>.
    /// For a device or data intent it is derived instead, because the two fields can disagree
    /// and the intent is the one that has been in production: letting a hallucinated
    /// "topic": "general" outrank intent "control_device" would silently stop turning the
    /// lights off. A missing topic — every response from a model that has not seen the new
    /// schema — lands on the same behaviour the code had before the field existed.
    /// </summary>
    private static AssistantTopic ResolveTopic(AssistantIntent intent, string? raw)
    {
        if (intent is AssistantIntent.ControlDevice or AssistantIntent.DeviceStatus)
        {
            return AssistantTopic.Device;
        }

        if (intent is AssistantIntent.QueryData)
        {
            return AssistantTopic.Data;
        }

        return raw?.Trim().ToLowerInvariant() switch
        {
            "faq" => AssistantTopic.Faq,
            "expert" => AssistantTopic.Expert,
            "emergency" => AssistantTopic.Emergency,
            _ => AssistantTopic.General
        };
    }

    /// <summary>Pulls the first balanced JSON object out of a response that may contain prose or fences.</summary>
    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped) { escaped = false; }
                else if (c == '\\') { escaped = true; }
                else if (c == '"') { inString = false; }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return raw[start..(i + 1)];
                    }
                    break;
            }
        }

        return null;
    }
}
