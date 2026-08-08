using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Streams device events to a Microsoft Fabric Eventstream via its Event
/// Hubs-protocol-compatible custom endpoint, using the official
/// Azure.Messaging.EventHubs SDK.
///
/// Must never throw: Azure SQL is the source of truth and this is a best-effort
/// secondary write path used by the SwitchBot polling loop and the manual
/// /api/stream/publish endpoint. A failed result lets the caller retry later.
/// </summary>
public sealed class EventHubEventStreamPublisher : IEventStreamPublisher, IAsyncDisposable
{
    // Device names/rooms are Japanese; keep them human-readable in the JSON
    // payload instead of escaping to \uXXXX (both are valid JSON, this is just
    // for clarity when inspecting Eventhouse ingestion).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly EventStreamOptions _options;
    private readonly ILogger<EventHubEventStreamPublisher> _logger;
    private readonly EventHubProducerClient? _producer;

    public EventHubEventStreamPublisher(IOptions<EventStreamOptions> options, ILogger<EventHubEventStreamPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;

        // The producer client is thread-safe and expensive to create, so a single
        // instance is reused for the lifetime of this singleton. When not
        // configured, skip creating it entirely so this class stays a cheap no-op.
        if (_options.IsConfigured)
        {
            _producer = new EventHubProducerClient(_options.ConnectionString, _options.EventHubName);
        }
    }

    public bool IsConfigured => _options.IsConfigured;

    public string DisplayName => "EventHub";

    public async Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<DeviceEventRecord> events, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (!IsConfigured || _producer is null)
        {
            return new EventStreamPublishResult(false, 0, 0, "EventStream is not configured.");
        }

        if (events.Count == 0)
        {
            return new EventStreamPublishResult(true, 0, sw.ElapsedMilliseconds);
        }

        try
        {
            var publishedCount = await SendAllAsync(events, ct);
            return new EventStreamPublishResult(true, publishedCount, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (
            ex is EventHubsException
            or JsonException
            or TaskCanceledException)
        {
            _logger.LogWarning("EventStream publish failed: {Type}.", ex.GetType().Name);
            return new EventStreamPublishResult(false, 0, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Batches events into as many <see cref="EventDataBatch"/> instances as
    /// needed (a batch has a maximum wire size). An event that never fits on
    /// its own is logged and skipped rather than looping forever.
    /// </summary>
    private async Task<int> SendAllAsync(IReadOnlyList<DeviceEventRecord> events, CancellationToken ct)
    {
        var published = 0;
        var batch = await _producer!.CreateBatchAsync(ct);

        foreach (var e in events)
        {
            var data = new EventData(Encoding.UTF8.GetBytes(ToJson(e)));

            if (batch.TryAdd(data))
            {
                continue;
            }

            if (batch.Count == 0)
            {
                // The event does not fit in an otherwise-empty batch: it can
                // never be sent. Skip it instead of retrying forever.
                _logger.LogWarning(
                    "EventStream: device event {EventId} is too large for a single batch; skipping.", e.EventId);
                continue;
            }

            await _producer.SendAsync(batch, ct);
            published += batch.Count;
            batch.Dispose();

            batch = await _producer.CreateBatchAsync(ct);
            if (!batch.TryAdd(data))
            {
                _logger.LogWarning(
                    "EventStream: device event {EventId} is too large for a single batch; skipping.", e.EventId);
            }
        }

        if (batch.Count > 0)
        {
            await _producer.SendAsync(batch, ct);
            published += batch.Count;
        }

        batch.Dispose();
        return published;
    }

    /// <summary>Serializes a device event, shaped exactly like the Eventhouse DeviceEvents table.</summary>
    internal static string ToJson(DeviceEventRecord e) =>
        JsonSerializer.Serialize(new
        {
            eventId = e.EventId,
            householdId = e.HouseholdId,
            deviceId = e.DeviceId,
            deviceName = e.DeviceName,
            room = e.Room,
            deviceType = e.DeviceType,
            eventType = e.EventType,
            state = e.State,
            powerWatts = e.PowerWatts,
            source = e.Source,
            occurredAtUtc = e.OccurredAtUtc.ToString("o")
        }, JsonOptions);

    public async ValueTask DisposeAsync()
    {
        if (_producer is not null)
        {
            await _producer.DisposeAsync();
        }
    }
}
