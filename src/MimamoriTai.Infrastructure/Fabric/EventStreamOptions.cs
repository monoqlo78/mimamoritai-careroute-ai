namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Fabric Eventstream (Event Hubs-compatible custom endpoint) settings.
/// ConnectionString must come from User Secrets, environment variables, or App
/// Service configuration. It is never written to appsettings.json.
/// </summary>
public sealed class EventStreamOptions
{
    public const string SectionName = "EventStream";

    public bool Enabled { get; set; }

    public string ConnectionString { get; set; } = string.Empty;

    public string EventHubName { get; set; } = string.Empty;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ConnectionString)
        && !string.IsNullOrWhiteSpace(EventHubName);
}
