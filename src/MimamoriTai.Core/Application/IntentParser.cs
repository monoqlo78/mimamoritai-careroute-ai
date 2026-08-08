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
    [JsonPropertyName("deviceAlias")] public string? DeviceAlias { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("question")] public string? Question { get; set; }
}

public sealed record AssistantPlan(
    AssistantIntent Intent,
    string? DeviceAlias,
    DeviceAction? Action,
    double Confidence,
    string? Question);

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

        return new AssistantPlan(intent.Value, alias, action, confidence, question);
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
