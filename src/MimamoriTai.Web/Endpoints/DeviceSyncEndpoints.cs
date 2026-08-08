using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Endpoints;

public sealed record SyncDevicesRequest(Guid? HouseholdId);

/// <summary>
/// Manual trigger for syncing real provider devices (e.g. SwitchBot) into the
/// Devices table, mirroring the button on the dashboard.
/// </summary>
public static class DeviceSyncEndpoints
{
    public static IEndpointRouteBuilder MapDeviceSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/devices/sync", async (
            SyncDevicesRequest? request,
            AppDbContext db,
            DeviceSyncService sync,
            CancellationToken ct) =>
        {
            var householdId = request?.HouseholdId
                ?? await db.Households.OrderBy(h => h.CreatedAtUtc).Select(h => h.Id).FirstOrDefaultAsync(ct);

            if (householdId == Guid.Empty)
            {
                return Results.NotFound(new { error = "No household is registered." });
            }

            var result = await sync.SyncAsync(householdId, ct);

            return Results.Ok(new
            {
                added = result.Added,
                updated = result.Updated,
                deactivated = result.Deactivated,
                totalChanges = result.TotalChanges
            });
        }).WithName("PostDevicesSync").DisableAntiforgery();

        app.MapPost("/api/stream/publish", async (
            int? take,
            AppDbContext db,
            IEventStreamPublisher publisher,
            CancellationToken ct) =>
        {
            var count = Math.Clamp(take ?? 50, 1, 500);

            var recent = await db.DeviceEvents
                .OrderByDescending(e => e.OccurredAtUtc)
                .Take(count)
                .ToListAsync(ct);

            var deviceIds = recent.Select(e => e.DeviceId).Distinct().ToList();
            var devices = await db.Devices
                .Where(d => deviceIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, ct);

            var records = recent.Select(e =>
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

            var result = await publisher.PublishAsync(records, ct);

            return Results.Ok(new
            {
                publisher = publisher.DisplayName,
                configured = publisher.IsConfigured,
                published = result.PublishedCount,
                durationMs = result.DurationMs,
                error = result.Error
            });
        }).WithName("PostStreamPublish").DisableAntiforgery();

        return app;
    }
}

