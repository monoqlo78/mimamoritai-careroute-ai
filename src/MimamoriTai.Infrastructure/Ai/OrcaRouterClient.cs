using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Ai;

/// <summary>
/// OpenAI-compatible Chat Completions client pointed at OrcaRouter.
/// Captures the router observability headers so the dashboard can prove which
/// model OrcaRouter actually resolved.
///
/// Wire format verified against https://docs.orcarouter.ai:
/// - POST {BaseUrl}/chat/completions with the OpenAI request shape.
/// - The standard OpenAI bearer-token authorization header carries OrcaRouter:ApiKey.
/// - Response headers X-Orca-Router / X-Orca-Resolved-Model are present when the
///   request targeted a named router such as "orcarouter/auto";
///   X-Orca-Fallback-Model is present when a fallback chain entry served it.
///   (https://docs.orcarouter.ai/routing/response-headers)
/// - 429 responses carry Retry-After, in seconds.
/// </summary>
public sealed class OrcaRouterClient(
    HttpClient http,
    IOptions<OrcaRouterOptions> options,
    ILogger<OrcaRouterClient> logger) : IAiRouterClient
{
    public const string RouterHeader = "X-Orca-Router";
    public const string ResolvedModelHeader = "X-Orca-Resolved-Model";
    public const string FallbackModelHeader = "X-Orca-Fallback-Model";
    public const string RequestIdHeader = "X-Orca-Request-Id";

    private readonly OrcaRouterOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public string DisplayName => "OrcaRouter";

    public async Task<AiCompletionResult> CompleteAsync(
        IReadOnlyList<AiMessage> messages,
        string purpose,
        bool jsonMode = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var model = _options.ResolveModel(jsonMode, purpose);

        if (!IsConfigured)
        {
            return new AiCompletionResult(false, string.Empty, DisplayName, model, 0, "OrcaRouter is not configured.");
        }

        var attempts = Math.Max(_options.MaxRetries, 0) + 1;
        AiCompletionResult? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var (result, retryAfter) = await SendOnceAsync(messages, purpose, jsonMode, model, sw, ct);
            last = result;

            if (result.Success || retryAfter is null || attempt == attempts)
            {
                return result;
            }

            logger.LogWarning(
                "OrcaRouter request for {Purpose} is retryable ({Error}); attempt {Attempt}/{Attempts} after {Delay}s.",
                purpose, result.Error, attempt, attempts, retryAfter.Value.TotalSeconds);

            try
            {
                await Task.Delay(retryAfter.Value, ct);
            }
            catch (TaskCanceledException)
            {
                return result;
            }
        }

        return last!;
    }

    private async Task<(AiCompletionResult Result, TimeSpan? RetryAfter)> SendOnceAsync(
        IReadOnlyList<AiMessage> messages,
        string purpose,
        bool jsonMode,
        string model,
        Stopwatch sw,
        CancellationToken ct)
    {
        try
        {
            var payload = new ChatCompletionRequest
            {
                Model = model,
                Temperature = jsonMode ? 0 : 0.4,
                Messages = [.. messages.Select(m => new ChatMessage { Role = m.Role, Content = m.Content })],
                ResponseFormat = jsonMode ? new ResponseFormat { Type = "json_object" } : null,
                ExtraBody = BuildFallbackChain(model)
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await http.SendAsync(request, ct);

            var router = ReadHeader(response, RouterHeader) ?? DisplayName;
            var resolvedModel = ReadHeader(response, FallbackModelHeader)
                ?? ReadHeader(response, ResolvedModelHeader)
                ?? model;

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                // Deliberately does not include the response body, which may echo request data.
                // The OrcaRouter request id is safe to log and is what their support asks for.
                logger.LogWarning(
                    "OrcaRouter request for {Purpose} failed with {Status} (model {Model}, request id {RequestId}).",
                    purpose, status, model, ReadHeader(response, RequestIdHeader) ?? "(none)");

                var result = new AiCompletionResult(false, string.Empty, router, resolvedModel, sw.ElapsedMilliseconds,
                    $"OrcaRouter returned {status}.");

                return (result, IsRetryable(status) ? ResolveRetryDelay(response) : null);
            }

            var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
            var content = body?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(body?.Model))
            {
                resolvedModel = body.Model;
            }

            return (new AiCompletionResult(true, content, router, resolvedModel, sw.ElapsedMilliseconds), null);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation is not a router failure and must not be retried.
            logger.LogWarning("OrcaRouter request for {Purpose} was cancelled by the caller.", purpose);
            return (new AiCompletionResult(false, string.Empty, DisplayName, model, sw.ElapsedMilliseconds, "Canceled"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("OrcaRouter request for {Purpose} failed: {Type}.", purpose, ex.GetType().Name);

            var result = new AiCompletionResult(false, string.Empty, DisplayName, model, sw.ElapsedMilliseconds, ex.GetType().Name);

            // A timeout or transport error is worth one more try; a malformed body is not.
            var retryable = ex is HttpRequestException or TaskCanceledException;
            return (result, retryable ? TimeSpan.FromSeconds(1) : null);
        }
    }

    /// <summary>
    /// Builds the extra_body fallback chain. OrcaRouter only activates the chain when
    /// route == "fallback", and caps it at 5 entries. The primary model is always the
    /// first entry so the configured model stays the first attempt.
    /// </summary>
    private ExtraBody? BuildFallbackChain(string model)
    {
        if (_options.FallbackModels.Count == 0)
        {
            return null;
        }

        var chain = new List<string> { model };
        chain.AddRange(_options.FallbackModels
            .Where(m => !string.IsNullOrWhiteSpace(m) && !chain.Contains(m, StringComparer.OrdinalIgnoreCase)));

        return new ExtraBody { Models = [.. chain.Take(5)], Route = "fallback" };
    }

    /// <summary>429 (throttled) and 5xx (upstream trouble) are worth another attempt; 4xx is not.</summary>
    private static bool IsRetryable(int status) => status == 429 || status >= 500;

    private TimeSpan ResolveRetryDelay(HttpResponseMessage response)
    {
        var max = TimeSpan.FromSeconds(Math.Max(_options.MaxRetryDelaySeconds, 0.5));
        var suggested = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } when
                ? when - DateTimeOffset.UtcNow
                : null);

        if (suggested is null || suggested <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        return suggested.Value > max ? max : suggested.Value;
    }

    private static string? ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; set; }

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponseFormat? ResponseFormat { get; set; }

        [JsonPropertyName("extra_body")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ExtraBody? ExtraBody { get; set; }
    }

    private sealed class ExtraBody
    {
        [JsonPropertyName("models")] public List<string> Models { get; set; } = [];
        [JsonPropertyName("route")] public string Route { get; set; } = "fallback";
    }

    private sealed class ResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "json_object";
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
