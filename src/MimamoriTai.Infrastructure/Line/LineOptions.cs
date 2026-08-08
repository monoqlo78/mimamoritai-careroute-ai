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

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ChannelAccessToken)
        && !string.IsNullOrWhiteSpace(ChannelSecret);

    public bool HasChannelSecret => !string.IsNullOrWhiteSpace(ChannelSecret);
}
