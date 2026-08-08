using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Endpoints;

public sealed record SimulatorRequest(Guid? HouseholdId, string Scenario, string? DeviceAlias);

/// <summary>
/// DEMO ONLY. Injects synthetic device events so the risk card can be shown changing
/// during a demo. Registered only in the Development environment.
/// </summary>
public static class SimulatorEndpoints
{
    public static readonly string[] Scenarios =
        ["device_on", "device_off", "normal_day", "no_activity", "night_activity"];

    public static IEndpointRouteBuilder MapSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        var environment = app.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment())
        {
            return app;
        }

        app.MapPost("/api/simulator/events", async (
            SimulatorRequest request,
            AppDbContext db,
            IDeviceProviderFactory providerFactory,
            HouseholdAccessService householdAccess,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var householdId = request.HouseholdId
                ?? await householdAccess.ResolveDefaultAsync(ct);

            if (householdId is null || householdId == Guid.Empty)
            {
                return Results.NotFound(new { error = "No household is registered." });
            }

            if (!await householdAccess.CanAccessAsync(householdId.Value, ct))
            {
                return Results.Json(new { error = "このご家庭のデータにアクセスする権限がありません。" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var household = await db.Households.FirstOrDefaultAsync(h => h.Id == householdId.Value, ct);
            if (household is null)
            {
                return Results.NotFound(new { error = "No household is registered." });
            }

            if (household.DataSourceMode != DataSourceMode.Sample)
            {
                return Results.BadRequest(new { error = "本番データのご家庭ではシミュレーターを使用できません。" });
            }

            var devices = await db.Devices.Where(d => d.HouseholdId == householdId.Value).ToListAsync(ct);
            if (devices.Count == 0)
            {
                return Results.NotFound(new { error = "No devices are registered." });
            }

            var provider = providerFactory.Get(DataSourceMode.Sample);
            var created = await ApplyScenarioAsync(request, db, provider, clock, householdId.Value, devices, ct);

            return created < 0
                ? Results.BadRequest(new { error = $"Unknown scenario. Allowed: {string.Join(", ", Scenarios)}" })
                : Results.Ok(new { scenario = request.Scenario, eventsCreated = created });
        }).WithName("PostSimulatorEvents").DisableAntiforgery();

        return app;
    }

    private static async Task<int> ApplyScenarioAsync(
        SimulatorRequest request,
        AppDbContext db,
        IDeviceProvider provider,
        TimeProvider clock,
        Guid householdId,
        List<Device> devices,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = HouseholdTime.LocalDate(now);
        var dayStart = HouseholdTime.StartOfLocalDayUtc(today);
        var scenario = request.Scenario?.Trim().ToLowerInvariant();

        var target = request.DeviceAlias is null
            ? devices[0]
            : devices.FirstOrDefault(d => d.Alias == request.DeviceAlias) ?? devices[0];

        switch (scenario)
        {
            case "device_on":
            case "device_off":
            {
                var on = scenario == "device_on";
                if (on)
                {
                    await provider.TurnOnAsync(target.ExternalDeviceId, ct);
                }
                else
                {
                    await provider.TurnOffAsync(target.ExternalDeviceId, ct);
                }

                db.DeviceEvents.Add(Event(householdId, target.Id, on ? "on" : "off", now));
                await db.SaveChangesAsync(ct);
                return 1;
            }

            case "normal_day":
            {
                await ClearTodayAsync(db, householdId, dayStart, ct);
                var light = devices.FirstOrDefault(d => d.Alias == "living-light") ?? devices[0];
                var bedroom = devices.FirstOrDefault(d => d.Alias == "bedroom-light") ?? devices[0];

                db.DeviceEvents.AddRange(
                    Event(householdId, bedroom.Id, "on", dayStart.AddMinutes(7 * 60)),
                    Event(householdId, bedroom.Id, "off", dayStart.AddMinutes(7 * 60 + 12)),
                    Event(householdId, light.Id, "on", dayStart.AddMinutes(7 * 60 + 20)),
                    Event(householdId, light.Id, "off", dayStart.AddMinutes(12 * 60)),
                    Event(householdId, light.Id, "on", dayStart.AddMinutes(18 * 60)));

                await db.SaveChangesAsync(ct);
                return 5;
            }

            case "no_activity":
            {
                await ClearTodayAsync(db, householdId, dayStart, ct);
                await db.SaveChangesAsync(ct);
                return 0;
            }

            case "night_activity":
            {
                var light = devices.FirstOrDefault(d => d.Alias == "living-light") ?? devices[0];
                var bedroom = devices.FirstOrDefault(d => d.Alias == "bedroom-light") ?? devices[0];

                db.DeviceEvents.AddRange(
                    Event(householdId, light.Id, "on", dayStart.AddMinutes(2 * 60 + 10)),
                    Event(householdId, light.Id, "off", dayStart.AddMinutes(2 * 60 + 45)),
                    Event(householdId, bedroom.Id, "on", dayStart.AddMinutes(3 * 60 + 5)),
                    Event(householdId, bedroom.Id, "off", dayStart.AddMinutes(3 * 60 + 25)));

                await db.SaveChangesAsync(ct);
                return 4;
            }

            default:
                return -1;
        }
    }

    private static async Task ClearTodayAsync(AppDbContext db, Guid householdId, DateTimeOffset dayStart, CancellationToken ct)
    {
        var existing = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= dayStart)
            .ToListAsync(ct);

        db.DeviceEvents.RemoveRange(existing);
    }

    private static DeviceEvent Event(Guid householdId, Guid deviceId, string state, DateTimeOffset at) => new()
    {
        HouseholdId = householdId,
        DeviceId = deviceId,
        EventType = "PowerState",
        State = state,
        PowerWatts = state == "on" ? 32.0 : 0.0,
        Source = EventSource.Simulator,
        OccurredAtUtc = at,
        ReceivedAtUtc = at
    };
}
