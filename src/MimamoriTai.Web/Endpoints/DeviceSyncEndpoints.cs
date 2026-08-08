using Microsoft.EntityFrameworkCore;
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

        return app;
    }
}
