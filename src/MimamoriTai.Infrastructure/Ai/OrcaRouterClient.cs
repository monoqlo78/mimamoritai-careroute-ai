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
/// </summary>
public sealed class OrcaRouterClient(
    HttpClient http,
    IOptions<OrcaRouterOptions> options,
    ILogger<OrcaRouterClient> logger) : IAiRouterClient
{
    public const string RouterHeader = "X-Orca-Router";
    public const string ResolvedModelHeader = "X-Orca-Resolved-Model";

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

        if (!IsConfigured)
        {
            return new AiCompletionResult(false, string.Empty, DisplayName, _options.Model, 0, "OrcaRouter is not configured.");
        }

        try
        {
            var payload = new ChatCompletionRequest
            {
                Model = _options.Model,
                Temperature = jsonMode ? 0 : 0.4,
                Messages = [.. messages.Select(m => new ChatMessage { Role = m.Role, Content = m.Content })],
                ResponseFormat = jsonMode ? new ResponseFormat { Type = "json_object" } : null
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await http.SendAsync(request, ct);

            var router = ReadHeader(response, RouterHeader) ?? DisplayName;
            var resolvedModel = ReadHeader(response, ResolvedModelHeader) ?? _options.Model;

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately does not include the response body, which may echo request data.
                logger.LogWarning("OrcaRouter request for {Purpose} failed with {Status}.", purpose, (int)response.StatusCode);
                return new AiCompletionResult(false, string.Empty, router, resolvedModel, sw.ElapsedMilliseconds,
                    $"OrcaRouter returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
            var content = body?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(body?.Model))
            {
                resolvedModel = body.Model;
            }

            return new AiCompletionResult(true, content, router, resolvedModel, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("OrcaRouter request for {Purpose} failed: {Type}.", purpose, ex.GetType().Name);
            return new AiCompletionResult(false, string.Empty, DisplayName, _options.Model, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
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
