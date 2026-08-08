namespace MimamoriTai.Infrastructure.Line;

/// <summary>
/// LINE Messaging API settings.
/// ChannelAccessToken / ChannelSecret must come from User Secrets or environment
/// variables. They are never written to appsettings.json.
/// </summary>
public sealed class LineOptions
{
    public const string SectionName = "Line";

    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://api.line.me";

    public string ChannelAccessToken { get; set; } = string.Empty;

    public string ChannelSecret { get; set; } = string.Empty;

    /// <summary>
    /// LINE group id or user id that watch/risk anomaly alerts are pushed to.
    /// Safe empty default; never committed as a real id. When empty, WatchAlertService
    /// still evaluates and records what it would have sent, but never calls PushAsync.
    /// </summary>
    public string AlertToId { get; set; } = string.Empty;

    /// <summary>Minimum risk level ("Low" | "Medium" | "High") that triggers an alert.</summary>
    public string AlertRiskThreshold { get; set; } = "Medium";

    /// <summary>Hours to suppress a repeat alert for the same person + risk level.</summary>
    public double AlertCooldownHours { get; set; } = 6;

    /// <summary>Minutes between automatic background evaluations of the watch alert.</summary>
    public double AlertPollIntervalMinutes { get; set; } = 5;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ChannelAccessToken)
        && !string.IsNullOrWhiteSpace(ChannelSecret);

    public bool HasChannelSecret => !string.IsNullOrWhiteSpace(ChannelSecret);
}
