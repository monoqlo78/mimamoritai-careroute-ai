namespace MimamoriTai.Infrastructure.Ai;

/// <summary>
/// OrcaRouter settings. BaseUrl and Model are public configuration; ApiKey must be
/// supplied through User Secrets or environment variables only.
/// </summary>
public sealed class OrcaRouterOptions
{
    public const string SectionName = "OrcaRouter";

    public string BaseUrl { get; set; } = "https://api.orcarouter.ai/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "orcarouter/auto";

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);
}
