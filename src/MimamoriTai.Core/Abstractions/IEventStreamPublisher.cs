namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// A single device event, shaped exactly like the Fabric Eventhouse KQL table
/// (DeviceEvents) so it can be serialized directly for streaming ingestion.
/// </summary>
public sealed record DeviceEventRecord(
    Guid EventId,
    Guid HouseholdId,
    Guid DeviceId,
    string DeviceName,
    string Room,
    string DeviceType,
    string EventType,
    string State,
    double? PowerWatts,
    string Source,
    DateTime OccurredAtUtc);

/// <summary>Result of a publish attempt, including router/host observability data.</summary>
public sealed record EventStreamPublishResult(
    bool Success,
    int PublishedCount,
    long DurationMs,
    string? Error = null);

/// <summary>
/// Streams device events to a real-time analytics sink (Fabric Eventhouse KQL
/// database) for near-real-time queries/dashboards. Azure SQL remains the source of
/// truth; this is a best-effort, fire-and-forget-safe secondary write path. Backed by
/// EventhouseStreamPublisher when configured, otherwise a deterministic mock so the
/// whole app stays demoable with no secrets.
/// </summary>
public interface IEventStreamPublisher
{
    bool IsConfigured { get; }
    string DisplayName { get; }

    Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<DeviceEventRecord> events,
        CancellationToken ct = default);
}
