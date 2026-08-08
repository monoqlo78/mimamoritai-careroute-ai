using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Endpoints;

public sealed record AssistantMessageRequest(Guid? HouseholdId, Guid? PersonId, string Message, string? Source);

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }))
            .WithName("Health");

        app.MapGet("/api/devices", async (AppDbContext db, CancellationToken ct) =>
        {
            var devices = await db.Devices
                .OrderBy(d => d.Name)
                .Select(d => new
                {
                    d.Id,
                    d.Name,
                    d.Alias,
                    d.Room,
                    DeviceType = d.DeviceType.ToString(),
                    Provider = d.Provider.ToString(),
                    d.IsEnabled,
                    d.RemoteControlAllowed,
                    SafetyClass = d.SafetyClass.ToString()
                })
                .ToListAsync(ct);

            return Results.Ok(devices);
        }).WithName("GetDevices");

        app.MapGet("/api/devices/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null)
            {
                return Results.NotFound();
            }

            var lastEvent = await db.DeviceEvents
                .Where(e => e.DeviceId == id)
                .OrderByDescending(e => e.OccurredAtUtc)
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                device.Id,
                device.Name,
                device.Alias,
                device.Room,
                DeviceType = device.DeviceType.ToString(),
                Provider = device.Provider.ToString(),
                device.IsEnabled,
                device.RemoteControlAllowed,
                SafetyClass = device.SafetyClass.ToString(),
                LastState = lastEvent?.State,
                LastEventUtc = lastEvent?.OccurredAtUtc
            });
        }).WithName("GetDevice");

        app.MapPost("/api/assistant/message", async (
            AssistantMessageRequest request,
            AssistantOrchestrator orchestrator,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "Message is required." });
            }

            var householdId = request.HouseholdId
                ?? await db.Households.OrderBy(h => h.CreatedAtUtc).Select(h => h.Id).FirstOrDefaultAsync(ct);

            if (householdId == Guid.Empty)
            {
                return Results.Problem("No household is registered.");
            }

            var source = Enum.TryParse<CommandSource>(request.Source, ignoreCase: true, out var parsed)
                ? parsed
                : CommandSource.Web;

            var response = await orchestrator.HandleAsync(
                new AssistantRequest(householdId, request.PersonId, request.Message, source), ct);

            return Results.Ok(response);
        }).WithName("PostAssistantMessage").DisableAntiforgery();

        app.MapGet("/api/activity/today", async (
            Guid? householdId, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = householdId ?? await db.Households.OrderBy(h => h.CreatedAtUtc).Select(h => h.Id).FirstOrDefaultAsync(ct);
            if (id == Guid.Empty)
            {
                return Results.NotFound();
            }

            var activity = new ActivityService(db);
            var today = HouseholdTime.LocalDate(clock.GetUtcNow());
            return Results.Ok(await activity.GetDailyAsync(id, today, ct));
        }).WithName("GetTodayActivity");

        app.MapGet("/api/activity/recent", async (
            Guid? householdId, int? days, AppDbContext db, CancellationToken ct) =>
        {
            var id = householdId ?? await db.Households.OrderBy(h => h.CreatedAtUtc).Select(h => h.Id).FirstOrDefaultAsync(ct);
            if (id == Guid.Empty)
            {
                return Results.NotFound();
            }

            var activity = new ActivityService(db);
            return Results.Ok(await activity.GetRecentAsync(id, Math.Clamp(days ?? 14, 1, 60), ct));
        }).WithName("GetRecentActivity");

        return app;
    }
}
