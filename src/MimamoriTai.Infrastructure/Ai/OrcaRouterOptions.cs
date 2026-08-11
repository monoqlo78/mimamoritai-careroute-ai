namespace MimamoriTai.Infrastructure.Ai;

/// <summary>
/// OrcaRouter settings. BaseUrl and Model are public configuration; ApiKey must be
/// supplied through User Secrets or environment variables only.
///
/// Verified against the official documentation (https://docs.orcarouter.ai):
/// - Base URL: https://api.orcarouter.ai/v1 (also reported by the service's own
///   /api/status as "api_base_url").
/// - Auth: the standard OpenAI bearer-token authorization header carries ApiKey.
/// - Endpoint: POST /chat/completions, OpenAI-compatible request/response shape.
/// - "orcarouter/auto" is a named router seeded on every account.
/// </summary>
public sealed class OrcaRouterOptions
{
    public const string SectionName = "OrcaRouter";

    public string BaseUrl { get; set; } = "https://api.orcarouter.ai/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "orcarouter/auto";

    /// <summary>
    /// Model used for calls that require <c>response_format: json_object</c>.
    ///
    /// This is deliberately NOT the auto router. Per
    /// https://docs.orcarouter.ai/advanced/structured-outputs, Anthropic models do
    /// not support <c>response_format</c> at all, and "orcarouter/auto" is free to
    /// resolve to one per request. Pinning a model whose provider honours
    /// json_object keeps intent parsing deterministic.
    /// </summary>
    public string JsonModel { get; set; } = "openai/gpt-4.1-mini";

    /// <summary>
    /// Model used only on paths that carry a hard deadline, selected by a purpose
    /// ending in "-fast".
    ///
    /// The auto router is kept everywhere else on purpose: routing each request to a
    /// different provider is the point of OrcaRouter, and the resolved model name is
    /// shown to the user. But "orcarouter/auto" resolves the summary to reasoning
    /// models (qwen3.7-plus, deepseek-v4-pro, glm-5.2) with a very wide spread --
    /// measured between 5.6s and 51s for the same prompt. The LINE webhook cancels an
    /// event after 8s, so that spread cannot be carried there: a slow draw silently
    /// replaces the summary with a generic timeout message.
    ///
    /// Pinning a fast chat model here (openai/gpt-4.1-mini, measured 3-5s end to end)
    /// buys predictability where a deadline exists, without taking the auto router
    /// away from the screens that can afford to wait. See docs/AI_DEVICE_SETUP.md.
    /// </summary>
    public string FastModel { get; set; } = "openai/gpt-4.1-mini";

    /// <summary>
    /// Optional ordered fallback chain (max 5, enforced by OrcaRouter) sent as
    /// <c>extra_body.models</c> with <c>extra_body.route = "fallback"</c>. When the
    /// primary model fails upstream (5xx / 429 / network) the next entry is tried.
    /// See https://docs.orcarouter.ai/routing/model-fallbacks.
    /// </summary>
    public List<string> FallbackModels { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How many times a single completion is retried after a retryable failure
    /// (HTTP 429 or 5xx). 0 disables retrying. The <c>Retry-After</c> header is
    /// honoured when present, capped by <see cref="MaxRetryDelaySeconds"/>.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Upper bound applied to any server-suggested Retry-After delay.</summary>
    public double MaxRetryDelaySeconds { get; set; } = 8;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>
    /// Model actually used for a request. JSON mode wins over everything (intent
    /// parsing must stay deterministic). Otherwise a purpose ending in "-fast" marks
    /// a caller that has a deadline and gets the pinned fast model; every other
    /// purpose keeps the general model so the auto router stays in play.
    /// </summary>
    public string ResolveModel(bool jsonMode, string? purpose = null)
    {
        if (jsonMode && !string.IsNullOrWhiteSpace(JsonModel))
        {
            return JsonModel;
        }

        if (purpose is not null
            && purpose.EndsWith(FastSuffix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(FastModel))
        {
            return FastModel;
        }

        return Model;
    }

    /// <summary>Purpose suffix marking a caller that cannot wait for the auto router.</summary>
    public const string FastSuffix = "-fast";
}
