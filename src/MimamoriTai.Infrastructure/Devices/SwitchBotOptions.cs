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

    /// <summary>
    /// Minutes between background polls of real device status once SwitchBot is the
    /// active provider. Only used by SwitchBotPollingBackgroundService, which is a
    /// no-op entirely when SwitchBot is not configured.
    /// </summary>
    public double PollIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Minutes between background re-fetches of the household's full device list
    /// (GET /v1.1/devices), so a device added on the SwitchBot side (e.g. a second
    /// Plug Mini) is picked up without anyone pressing "今すぐ同期する". Deliberately
    /// much coarser than <see cref="PollIntervalMinutes"/>: SwitchBot's OpenAPI has a
    /// daily call-count limit per token, and the device list rarely changes, whereas
    /// per-device status needs to stay fine-grained for timely activity detection.
    /// Only used by SwitchBotPollingBackgroundService.
    /// </summary>
    public double DeviceDiscoveryIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// When true, a Production household with no per-household
    /// <c>SwitchBotConnection</c> row may fall back to these global bootstrap
    /// Token/Secret. This exists only for local/dev bring-up before the Settings UI
    /// has been used; it defaults to false so a shared/production deployment never
    /// silently binds every household to one operator's SwitchBot account. See
    /// docs/SECURITY.md for the full precedence rules.
    /// </summary>
    public bool AllowGlobalFallback { get; set; }

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(Secret);
}
