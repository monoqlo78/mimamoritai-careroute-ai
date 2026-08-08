using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>Outcome of one <see cref="EventStreamPublishService.PublishUnpublishedBatchAsync"/> cycle.</summary>
public sealed record EventStreamPublishBatchResult(int Attempted, int Published, bool Success, string? Error)
{
    /// <summary>No unpublished rows were found; nothing to do.</summary>
    public static readonly EventStreamPublishBatchResult Empty = new(0, 0, true, null);
}

/// <summary>
/// Projects <see cref="DeviceEvent"/> rows into the Fabric Eventhouse wire shape
/// (<see cref="DeviceEventRecord"/>) and publishes the incremental backlog via
/// <see cref="IEventStreamPublisher"/>. Shared by
/// EventStreamPublishBackgroundService (automatic, incremental sync every cycle)
/// and POST /api/stream/publish (manual "republish the N most recent rows" demo
/// trigger), so both send devices exactly the same shape and neither duplicates the
/// DeviceEvent -&gt; Devices join.
///
/// Only <see cref="PublishUnpublishedBatchAsync"/> stamps
/// <see cref="DeviceEvent.PublishedToStreamAtUtc"/>, and only when the publish
/// succeeds -- a failed or not-configured publisher leaves the batch unstamped so it
/// is retried on the next cycle instead of being silently dropped.
/// </summary>
public sealed class EventStreamPublishService(IAppDbContext db, IEventStreamPublisher publisher, TimeProvider clock)
{
    /// <summary>Default cap on how many unpublished rows a single cycle will attempt.</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>Joins <paramref name="events"/> against Devices for Name/Room/DeviceType and shapes them for streaming.</summary>
    public async Task<List<DeviceEventRecord>> ProjectAsync(IReadOnlyList<DeviceEvent> events, CancellationToken ct = default)
    {
        var deviceIds = events.Select(e => e.DeviceId).Distinct().ToList();
        var devices = await db.Devices
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        return events.Select(e =>
        {
            devices.TryGetValue(e.DeviceId, out var device);
            return new DeviceEventRecord(
                e.Id,
                e.HouseholdId,
                e.DeviceId,
                device?.Name ?? string.Empty,
                device?.Room ?? string.Empty,
                device?.DeviceType.ToString() ?? string.Empty,
                e.EventType,
                e.State,
                e.PowerWatts,
                e.Source.ToString(),
                e.OccurredAtUtc.UtcDateTime);
        }).ToList();
    }

    /// <summary>
    /// Publishes up to <paramref name="batchSize"/> events with a null
    /// PublishedToStreamAtUtc, oldest first, and stamps them only on success.
    /// </summary>
    public async Task<EventStreamPublishBatchResult> PublishUnpublishedBatchAsync(
        int batchSize = DefaultBatchSize, CancellationToken ct = default)
    {
        var pending = await db.DeviceEvents
            .Where(e => e.PublishedToStreamAtUtc == null)
            .OrderBy(e => e.OccurredAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return EventStreamPublishBatchResult.Empty;
        }

        var records = await ProjectAsync(pending, ct);
        var result = await publisher.PublishAsync(records, ct);

        if (!result.Success)
        {
            // Leave the rows unstamped so the next cycle retries them.
            return new EventStreamPublishBatchResult(pending.Count, 0, false, result.Error);
        }

        var stampedAtUtc = clock.GetUtcNow();
        foreach (var deviceEvent in pending)
        {
            deviceEvent.PublishedToStreamAtUtc = stampedAtUtc;
        }
        await db.SaveChangesAsync(ct);

        return new EventStreamPublishBatchResult(pending.Count, result.PublishedCount, true, null);
    }
}
