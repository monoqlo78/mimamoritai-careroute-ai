namespace MimamoriTai.Infrastructure.Devices;

/// <summary>
/// SwitchBot OpenAPI settings. Token/Secret must come from User Secrets or
/// environment variables — never from appsettings.json.
/// </summary>
public sealed class SwitchBotOptions
{
    public const string SectionName = "SwitchBot";

    public bool Enabled { get; set; }

    /// <summary>Public base address of the SwitchBot OpenAPI (v1.1).</summary>
    public string BaseUrl { get; set; } = "https://api.switch-bot.com";

    public string Token { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(Secret);
}
