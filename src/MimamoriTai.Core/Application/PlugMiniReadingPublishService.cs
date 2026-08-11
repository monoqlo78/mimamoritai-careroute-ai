using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>Outcome of one <see cref="PlugMiniReadingPublishService.PublishUnpublishedBatchAsync"/> cycle.</summary>
public sealed record PlugMiniReadingPublishBatchResult(int Attempted, int Published, bool Success, string? Error)
{
    public static readonly PlugMiniReadingPublishBatchResult Empty = new(0, 0, true, null);
}

/// <summary>
/// Projects <see cref="PlugMiniReading"/> rows into the Fabric Eventhouse wire shape
/// (<see cref="PlugMiniReadingRecord"/>) and publishes the incremental backlog via
/// <see cref="IPlugMiniReadingStreamPublisher"/>. Mirrors
/// <see cref="EventStreamPublishService"/>'s exact contract: only
/// <see cref="PublishUnpublishedBatchAsync"/> stamps
/// <see cref="PlugMiniReading.PublishedToStreamAtUtc"/>, and only when the publish
/// succeeds -- a failed or not-configured publisher leaves the batch unstamped so it
/// is retried on the next cycle instead of being silently dropped. Kept as a
/// separate service (rather than folding into EventStreamPublishService) because
/// readings and events are different tables/streams with independent failure modes.
/// </summary>
public sealed class PlugMiniReadingPublishService(
    IAppDbContext db, IPlugMiniReadingStreamPublisher publisher, TimeProvider clock)
{
    public const int DefaultBatchSize = 200;

    /// <summary>Joins <paramref name="readings"/> against Devices for Name/Room and shapes them for streaming.</summary>
    public async Task<List<PlugMiniReadingRecord>> ProjectAsync(
        IReadOnlyList<PlugMiniReading> readings, CancellationToken ct = default)
    {
        var deviceIds = readings.Select(r => r.DeviceId).Distinct().ToList();
        var devices = await db.Devices
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        return readings.Select(r =>
        {
            devices.TryGetValue(r.DeviceId, out var device);
            return new PlugMiniReadingRecord(
                r.Id,
                r.HouseholdId,
                r.DeviceId,
                device?.Name ?? string.Empty,
                device?.Room ?? string.Empty,
                r.VoltageV,
                r.CurrentMa,
                r.DailyEnergyWh,
                r.UsageMinutesToday,
                r.ApproxWatts,
                r.OccurredAtUtc.UtcDateTime);
        }).ToList();
    }

    /// <summary>
    /// Publishes up to <paramref name="batchSize"/> readings with a null
    /// PublishedToStreamAtUtc, oldest first, and stamps them only on success.
    /// </summary>
    public async Task<PlugMiniReadingPublishBatchResult> PublishUnpublishedBatchAsync(
        int batchSize = DefaultBatchSize, CancellationToken ct = default)
    {
        var pending = await db.PlugMiniReadings
            .Where(r => r.PublishedToStreamAtUtc == null)
            .OrderBy(r => r.OccurredAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return PlugMiniReadingPublishBatchResult.Empty;
        }

        var records = await ProjectAsync(pending, ct);
        var result = await publisher.PublishAsync(records, ct);

        if (!result.Success)
        {
            // Leave the rows unstamped so the next cycle retries them.
            return new PlugMiniReadingPublishBatchResult(pending.Count, 0, false, result.Error);
        }

        var stampedAtUtc = clock.GetUtcNow();
        foreach (var reading in pending)
        {
            reading.PublishedToStreamAtUtc = stampedAtUtc;
        }
        await db.SaveChangesAsync(ct);

        return new PlugMiniReadingPublishBatchResult(pending.Count, result.PublishedCount, true, null);
    }
}
