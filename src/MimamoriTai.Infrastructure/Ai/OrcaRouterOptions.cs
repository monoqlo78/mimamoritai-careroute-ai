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
    /// Optional model pinned for the family-facing summary.
    ///
    /// Empty by default so the auto router keeps choosing. Measured against this
    /// account, "orcarouter/auto" resolves the summary to reasoning models
    /// (qwen3.7-plus, deepseek-v4-pro) and takes 20-30s end to end, which is a long
    /// time for someone waiting on a phone. Pinning a fast chat model here (for
    /// example "openai/gpt-4.1-mini", measured at ~2s) trades model choice for
    /// responsiveness. See docs/AI_DEVICE_SETUP.md.
    /// </summary>
    public string SummaryModel { get; set; } = string.Empty;

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
    /// parsing must stay deterministic); otherwise a purpose-specific pin is used
    /// when configured, falling back to the general model.
    /// </summary>
    public string ResolveModel(bool jsonMode, string? purpose = null)
    {
        if (jsonMode && !string.IsNullOrWhiteSpace(JsonModel))
        {
            return JsonModel;
        }

        if (string.Equals(purpose, "summary", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(SummaryModel))
        {
            return SummaryModel;
        }

        return Model;
    }
}
