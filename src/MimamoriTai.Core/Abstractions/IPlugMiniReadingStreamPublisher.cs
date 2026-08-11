namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// A single Plug Mini telemetry reading, shaped for the Fabric Eventhouse
/// SwitchBotPlugReadings table (see docs/FABRIC_SETUP.md for the exact schema).
/// Kept as its own record/interface (rather than folding into
/// <see cref="DeviceEventRecord"/>/<see cref="IEventStreamPublisher"/>) because
/// readings are captured every poll cycle regardless of state change, unlike
/// DeviceEvent's "only on change" semantics -- mixing the two shapes into one
/// table/interface would blur that distinction for downstream KQL consumers.
/// </summary>
public sealed record PlugMiniReadingRecord(
    Guid ReadingId,
    Guid HouseholdId,
    Guid DeviceId,
    string DeviceName,
    string Room,
    double? VoltageV,
    double? CurrentMa,
    double? DailyEnergyWh,
    int? UsageMinutesToday,
    double? ApproxWatts,
    DateTime OccurredAtUtc);

/// <summary>
/// Streams Plug Mini readings to the Fabric Eventhouse SwitchBotPlugReadings table.
/// Mirrors <see cref="IEventStreamPublisher"/>'s "never throw, best-effort secondary
/// write" contract exactly, but is a separate interface/table so a Plug Mini
/// ingestion outage/misconfiguration can never affect DeviceEvent publishing (and
/// vice versa).
/// </summary>
public interface IPlugMiniReadingStreamPublisher
{
    bool IsConfigured { get; }
    string DisplayName { get; }

    Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<PlugMiniReadingRecord> readings,
        CancellationToken ct = default);
}
