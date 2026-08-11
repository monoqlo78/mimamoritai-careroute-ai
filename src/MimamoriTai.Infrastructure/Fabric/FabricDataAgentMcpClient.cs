using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Talks to a published Microsoft Fabric Data Agent over MCP (Model Context Protocol)
/// using JSON-RPC 2.0 messages over streamable HTTP, exactly the protocol the Fabric
/// Data Agent MCP endpoint expects: initialize -&gt; notifications/initialized -&gt;
/// tools/list -&gt; tools/call.
///
/// Authenticated the same way as <see cref="EventhouseStreamPublisher"/> via an
/// Azure.Identity <see cref="TokenCredential"/> (scope https://api.fabric.microsoft.com/.default):
/// a normal Entra service principal (client credentials) when one is configured -
/// required because Fabric Data Agent query auth does not support managed identities -
/// otherwise DefaultAzureCredential for local dev / fallback. See
/// <see cref="ServiceCollectionExtensions.CreateFabricTokenCredential"/>.
///
/// Must never throw out to the UI: any failure (auth, network, malformed response,
/// JSON-RPC error) is logged as a warning and reported as an unsuccessful
/// <see cref="FabricAnswer"/> so the caller (<c>AssistantOrchestrator</c>) falls back
/// to <see cref="ILocalDataQuestionService"/>.
/// </summary>
public sealed class FabricDataAgentMcpClient(
    HttpClient http,
    IOptions<FabricOptions> options,
    TokenCredential credential,
    ILogger<FabricDataAgentMcpClient> logger) : IFabricDataAgentClient
{
    private const string SourceName = "Fabric";
    private const string SessionHeaderName = "Mcp-Session-Id";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Substrings that indicate the Fabric Data Agent could not actually reach its
    /// datasource and merely returned an apology as text content with an HTTP 200
    /// (e.g. because the underlying datasource has not yet been migrated to a
    /// service-principal-compatible source such as a Lakehouse). Any match means the
    /// answer must be rejected so the caller falls back to
    /// <see cref="ILocalDataQuestionService"/>.
    /// </summary>
    private static readonly string[] FailurePhrases =
    [
        "データベースに接続できず",
        "データベースに接続できませんでした",
        "接続できませんでした",
        "システムのエラー",
        "システムエラー",
        "エラーが発生し",
        "取得できませんでした",
        "取得できません",
        "お答えできません",
        "アクセスできません",
        "アクセスできず",
        "見つかりませんでした",
        "確認できませんでした",
        "技術的な問題",
        "unable to connect",
        "unable to access",
        "unable to retrieve",
        "failed to connect",
        "an error occurred",
        "could not retrieve",
        "could not access",
        "i'm sorry, but i encountered an error"
    ];

    private readonly FabricOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;
    private string? _sessionId;
    private int _nextRequestId;

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Builds the MCP endpoint URL: an explicit <see cref="FabricOptions.McpUrl"/> always
    /// wins, otherwise it is constructed from the workspace and data agent ids.
    /// </summary>
    public static string BuildMcpUrl(FabricOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.McpUrl))
        {
            return options.McpUrl;
        }

        return "https://api.fabric.microsoft.com/v1/mcp/workspaces/" +
               $"{options.WorkspaceId}/dataagents/{options.DataAgentId}/agent";
    }

    public async Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return NotConfigured();
        }

        try
        {
            var url = BuildMcpUrl(_options);
            var token = await GetTokenAsync(ct);

            await SendAsync(url, token, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "MimamoriTai", version = "1.0" }
            }, ct);

            await SendNotificationAsync(url, token, "notifications/initialized", ct);

            var toolsResult = await SendAsync(url, token, "tools/list", new { }, ct);
            var toolName = ExtractFirstToolName(toolsResult);
            if (toolName is null)
            {
                logger.LogWarning("Fabric Data Agent MCP tools/list returned no tools.");
                return Failure("Fabric Data Agent published no callable tool.");
            }

            var argumentName = ExtractFirstToolArgumentName(toolsResult);

            var callResult = await SendAsync(url, token, "tools/call", new
            {
                name = toolName,
                arguments = new Dictionary<string, string> { [argumentName] = question }
            }, ct);

            var answer = ExtractAnswerText(callResult);
            if (answer is null)
            {
                logger.LogWarning("Fabric Data Agent MCP tools/call returned no text content.");
                return Failure("Fabric Data Agent returned an empty answer.");
            }

            if (IsErrorResult(callResult) || LooksLikeFailureAnswer(answer))
            {
                // The apology text is the only clue about WHY the agent could not read
                // its datasource (permissions, an unmigrated datasource, a missing
                // table...), and it is never shown to the family, so it is worth
                // surfacing for diagnosis. Truncated because a data agent can be
                // verbose, and because the tail may quote row data.
                var detail = Truncate(answer, 300);

                logger.LogWarning(
                    "Fabric Data Agent returned a failure-shaped answer; falling back to local data. Agent said: {Detail}",
                    detail);

                return Failure($"Fabric Data Agent could not access its datasource: {detail}");
            }

            return new FabricAnswer(true, answer, SourceName);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or CredentialUnavailableException
            or Azure.RequestFailedException
            or InvalidOperationException)
        {
            logger.LogWarning("Fabric Data Agent MCP call failed: {Type}.", ex.GetType().Name);
            return Failure(ex.GetType().Name);
        }
    }

    /// <summary>Sends a JSON-RPC request expecting a response and returns its "result" element.</summary>
    private async Task<JsonElement?> SendAsync(string url, AccessToken token, string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var payload = new { jsonrpc = "2.0", id, method, @params };

        using var response = await PostAsync(url, token, payload, ct);
        var envelopes = await ParseEnvelopesAsync(response, ct);

        foreach (var envelope in envelopes)
        {
            if (envelope.TryGetProperty("id", out var idProp) && idProp.ValueKind is JsonValueKind.Number
                && idProp.GetInt32() == id)
            {
                if (envelope.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown error";
                    throw new InvalidOperationException($"MCP error: {message}");
                }

                return envelope.TryGetProperty("result", out var result) ? result : null;
            }
        }

        throw new InvalidOperationException($"No matching MCP response for method '{method}'.");
    }

    /// <summary>Sends a JSON-RPC notification (no id, no response expected).</summary>
    private async Task SendNotificationAsync(string url, AccessToken token, string method, CancellationToken ct)
    {
        var payload = new { jsonrpc = "2.0", method, @params = new { } };
        using var response = await PostAsync(url, token, payload, ct);
        // Notifications may legitimately return an empty body; nothing to parse.
    }

    private async Task<HttpResponseMessage> PostAsync(string url, AccessToken token, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation(SessionHeaderName, _sessionId);
        }

        var response = await http.SendAsync(request, ct);

        if (response.Headers.TryGetValues(SessionHeaderName, out var sessionValues))
        {
            _sessionId = sessionValues.FirstOrDefault() ?? _sessionId;
        }

        return response;
    }

    /// <summary>
    /// Parses a response body as either plain JSON or Server-Sent Events (each
    /// "data: {...}" line is a JSON-RPC envelope), returning all JSON-RPC envelopes found.
    /// </summary>
    public static async Task<IReadOnlyList<JsonElement>> ParseEnvelopesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return ParseEnvelopes(body, response.Content.Headers.ContentType?.MediaType);
    }

    public static IReadOnlyList<JsonElement> ParseEnvelopes(string body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var isSse = contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
                    || body.TrimStart().StartsWith("data:", StringComparison.Ordinal);

        if (!isSse)
        {
            return TryParseSingle(body) is { } single ? [single] : [];
        }

        var results = new List<JsonElement>();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim('\r', '\n', ' ');
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = trimmed["data:".Length..].Trim();
            if (TryParseSingle(json) is { } parsed)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    private static JsonElement? TryParseSingle(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the first tool name out of a tools/list result payload.</summary>
    public static string? ExtractFirstToolName(JsonElement? result)
    {
        if (result is not { } r || r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!r.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the first required property name from the first tool's inputSchema.
    /// Fabric publishes <c>userQuestion</c>, but the name is schema-driven so it is
    /// discovered at runtime rather than hard-coded.
    /// </summary>
    public static string ExtractFirstToolArgumentName(JsonElement? result)
    {
        const string fallback = "userQuestion";

        if (result is not { } r || r.ValueKind != JsonValueKind.Object
            || !r.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (!tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    {
                        return name;
                    }
                }
            }

            if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    return prop.Name;
                }
            }
        }

        return fallback;
    }

    /// <summary>Reads the first text content block out of a tools/call result payload.</summary>
    public static string? ExtractAnswerText(JsonElement? result)
    {
        if (result is not { } r || r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!r.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }

    /// <summary>Checks whether a tools/call result is flagged with <c>isError: true</c>.</summary>
    public static bool IsErrorResult(JsonElement? result)
    {
        if (result is not { } r || r.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return r.TryGetProperty("isError", out var isError)
            && isError.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Heuristically detects an apology/failure answer returned by the Fabric Data
    /// Agent when it could not reach its datasource.
    /// </summary>
    public static bool LooksLikeFailureAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        foreach (var phrase in FailurePhrases)
        {
            if (answer.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<AccessToken> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow + RefreshMargin)
        {
            return cached;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is { } stillCached && stillCached.ExpiresOn > DateTimeOffset.UtcNow + RefreshMargin)
            {
                return stillCached;
            }

            var token = await credential.GetTokenAsync(new TokenRequestContext([_options.Scope]), ct);
            _cachedToken = token;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static FabricAnswer NotConfigured() =>
        new(false, string.Empty, SourceName, "Fabric Data Agent is not configured.");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static FabricAnswer Failure(string reason) =>
        new(false, string.Empty, SourceName, reason);
}
