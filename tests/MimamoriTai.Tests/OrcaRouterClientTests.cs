using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Ai;

namespace MimamoriTai.Tests;

/// <summary>
/// Verifies the OrcaRouter transport against the documented wire format
/// (https://docs.orcarouter.ai) without needing a live API key: request shape,
/// the auth header, json_object model pinning, the extra_body fallback chain,
/// observability-header capture, retry-on-429 and graceful degradation.
/// </summary>
public sealed class OrcaRouterClientTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string ApiKey = "orca-test-key-0123456789";

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<string> Bodies { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedHandler Then(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
        {
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return response;
            });

            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

            return _responses.Count > 0
                ? _responses.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok(), Encoding.UTF8, "application/json") };
        }
    }

    private static string Ok(string content = "こんにちは", string model = "openai/gpt-4.1-mini") =>
        JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new { message = new { role = "assistant", content } } }
        });

    private static OrcaRouterClient Create(ScriptedHandler handler, OrcaRouterOptions? options = null)
    {
        options ??= new OrcaRouterOptions { ApiKey = ApiKey };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/")
        };

        return new OrcaRouterClient(http, Options.Create(options), NullLogger<OrcaRouterClient>.Instance);
    }

    private static IReadOnlyList<AiMessage> Prompt() =>
        [new AiMessage("system", "あなたは見守りアシスタントです。"), new AiMessage("user", "リビングの電気を消して")];

    [Fact]
    public async Task Posts_openai_compatible_request_to_chat_completions()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler);

        var result = await client.CompleteAsync(Prompt(), "intent");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.orcarouter.ai/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ApiKey, request.Headers.Authorization.Parameter);

        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("orcarouter/auto", body.GetProperty("model").GetString());
        Assert.Equal(2, body.GetProperty("messages").GetArrayLength());
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());

        output.WriteLine("OrcaRouter request body: " + handler.Bodies[0]);

        Assert.True(result.Success);
        Assert.Equal("こんにちは", result.Content);
    }

    [Fact]
    public async Task Json_mode_pins_a_model_that_supports_response_format()
    {
        // orcarouter/auto may resolve to Anthropic, which does not support response_format
        // at all (https://docs.orcarouter.ai/advanced/structured-outputs), so JSON calls
        // must not use the auto router.
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok("{\"intent\":\"control_device\"}"));
        var client = Create(handler);

        await client.CompleteAsync(Prompt(), "intent", jsonMode: true);

        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("openai/gpt-4.1-mini", body.GetProperty("model").GetString());
        Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(0, body.GetProperty("temperature").GetDouble());

        output.WriteLine("json mode request body: " + handler.Bodies[0]);
    }

    [Fact]
    public async Task Non_json_calls_omit_response_format_so_any_provider_can_serve_them()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler);

        await client.CompleteAsync(Prompt(), "summary");

        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.False(body.TryGetProperty("response_format", out _));
        Assert.False(body.TryGetProperty("extra_body", out _));
    }

    [Fact]
    public async Task Fallback_chain_is_sent_as_extra_body_with_route_fallback()
    {
        var options = new OrcaRouterOptions
        {
            ApiKey = ApiKey,
            FallbackModels = ["openai/gpt-4.1-mini", "google/gemini-2.5-flash"]
        };

        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler, options);

        await client.CompleteAsync(Prompt(), "summary");

        var extra = JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("extra_body");
        Assert.Equal("fallback", extra.GetProperty("route").GetString());

        var models = extra.GetProperty("models").EnumerateArray().Select(m => m.GetString()!).ToArray();
        Assert.Equal(new[] { "orcarouter/auto", "openai/gpt-4.1-mini", "google/gemini-2.5-flash" }, models);

        output.WriteLine("fallback chain: " + extra.GetRawText());
    }

    [Fact]
    public async Task Fallback_chain_never_exceeds_the_five_entry_limit()
    {
        var options = new OrcaRouterOptions
        {
            ApiKey = ApiKey,
            FallbackModels = ["a/1", "b/2", "c/3", "d/4", "e/5", "f/6"]
        };

        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler, options);

        await client.CompleteAsync(Prompt(), "summary");

        var models = JsonDocument.Parse(handler.Bodies[0]).RootElement
            .GetProperty("extra_body").GetProperty("models").EnumerateArray().ToArray();

        Assert.Equal(5, models.Length);
    }

    [Fact]
    public async Task Resolved_model_and_router_come_from_the_observability_headers()
    {
        var handler = new ScriptedHandler().Then(
            HttpStatusCode.OK,
            Ok(model: "anthropic/claude-sonnet-4"),
            ("X-Orca-Router", "auto"),
            ("X-Orca-Resolved-Model", "anthropic/claude-sonnet-4"),
            ("X-Orca-Request-Id", "req_abc123"));

        var client = Create(handler);

        var result = await client.CompleteAsync(Prompt(), "summary");

        Assert.True(result.Success);
        Assert.Equal("auto", result.Router);
        Assert.Equal("anthropic/claude-sonnet-4", result.ResolvedModel);
    }

    [Fact]
    public async Task Rate_limited_request_is_retried_after_the_retry_after_delay()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.TooManyRequests, """{"error":"rate limited"}""", ("Retry-After", "1"))
            .Then(HttpStatusCode.OK, Ok("お母さんは今日も元気です。"));

        var client = Create(handler, new OrcaRouterOptions { ApiKey = ApiKey, MaxRetries = 2, MaxRetryDelaySeconds = 1 });

        var result = await client.CompleteAsync(Prompt(), "summary");

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(result.Success);
        Assert.Equal("お母さんは今日も元気です。", result.Content);
    }

    [Fact]
    public async Task Server_error_is_retried_then_reported_without_throwing()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.InternalServerError, "{}")
            .Then(HttpStatusCode.BadGateway, "{}");

        var client = Create(handler, new OrcaRouterOptions { ApiKey = ApiKey, MaxRetries = 1, MaxRetryDelaySeconds = 0.5 });

        var result = await client.CompleteAsync(Prompt(), "summary");

        Assert.Equal(2, handler.Requests.Count);
        Assert.False(result.Success);
        Assert.Contains("502", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_failure_is_not_retried()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.Unauthorized, """{"error":"invalid api key"}""");
        var client = Create(handler, new OrcaRouterOptions { ApiKey = ApiKey, MaxRetries = 2 });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.Single(handler.Requests);
        Assert.False(result.Success);
        Assert.Contains("401", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_client_reports_failure_without_calling_the_api()
    {
        var handler = new ScriptedHandler();
        var client = Create(handler, new OrcaRouterOptions { ApiKey = "" });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.Empty(handler.Requests);
        Assert.False(result.Success);
        Assert.Equal("OrcaRouter is not configured.", result.Error);
    }

    [Fact]
    public async Task Error_message_never_contains_the_api_key()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.Unauthorized, $$"""{"error":"key {{ApiKey}} rejected"}""");
        var client = Create(handler, new OrcaRouterOptions { ApiKey = ApiKey, MaxRetries = 0 });

        var result = await client.CompleteAsync(Prompt(), "intent");

        Assert.DoesNotContain(ApiKey, result.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, result.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Intent parsing must stay on the pinned JSON model no matter what else is
    /// configured: Anthropic silently ignores response_format, so letting the auto
    /// router pick here would intermittently break intent classification.
    /// </summary>
    [Fact]
    public void Json_mode_always_wins_over_a_purpose_pin()
    {
        var options = new OrcaRouterOptions
        {
            Model = "orcarouter/auto",
            JsonModel = "openai/gpt-4.1-mini",
            FastModel = "openai/gpt-4o-mini"
        };

        Assert.Equal("openai/gpt-4.1-mini", options.ResolveModel(jsonMode: true, purpose: "summary-fast"));
    }

    /// <summary>
    /// Only the deadline-bound variant is pinned. The plain summary keeps the auto
    /// router, which is what surfaces a different provider per request in the UI.
    /// </summary>
    [Fact]
    public void Only_a_fast_purpose_takes_the_pinned_model()
    {
        var options = new OrcaRouterOptions { Model = "orcarouter/auto", FastModel = "openai/gpt-4.1-mini" };

        Assert.Equal("openai/gpt-4.1-mini", options.ResolveModel(jsonMode: false, purpose: "summary-fast"));
        Assert.Equal("orcarouter/auto", options.ResolveModel(jsonMode: false, purpose: "summary"));
        Assert.Equal("orcarouter/auto", options.ResolveModel(jsonMode: false, purpose: "conversation"));
    }

    /// <summary>
    /// Clearing the pin is a supported way to force the auto router everywhere; the
    /// suffix must then be inert rather than resolving to an empty model name.
    /// </summary>
    [Fact]
    public void Fast_purpose_falls_back_to_the_general_model_when_unpinned()
    {
        var options = new OrcaRouterOptions { Model = "orcarouter/auto", FastModel = string.Empty };

        Assert.Equal("orcarouter/auto", options.ResolveModel(jsonMode: false, purpose: "summary-fast"));
    }

    [Fact]
    public async Task Fast_pin_is_sent_to_the_api()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler, new OrcaRouterOptions
        {
            ApiKey = ApiKey,
            Model = "orcarouter/auto",
            FastModel = "openai/gpt-4.1-mini"
        });

        await client.CompleteAsync(Prompt(), "summary-fast");

        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("openai/gpt-4.1-mini", body.GetProperty("model").GetString());
    }

    /// <summary>
    /// The web path must keep reaching the auto router: the resolved model name is
    /// shown to the user and is the visible evidence of OrcaRouter routing.
    /// </summary>
    [Fact]
    public async Task Plain_summary_still_sends_the_auto_router_to_the_api()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, Ok());
        var client = Create(handler, new OrcaRouterOptions
        {
            ApiKey = ApiKey,
            Model = "orcarouter/auto",
            FastModel = "openai/gpt-4.1-mini"
        });

        await client.CompleteAsync(Prompt(), "summary");

        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal("orcarouter/auto", body.GetProperty("model").GetString());
    }
}
