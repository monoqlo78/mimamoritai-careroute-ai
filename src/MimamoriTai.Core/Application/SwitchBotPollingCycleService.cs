using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Outcome of one <see cref="SwitchBotPollingCycleService.PollHouseholdAsync"/> call:
/// every state-change DeviceEvent and every Plug Mini reading row that was actually
/// inserted this cycle, paired with their Device for downstream Fabric publish
/// projection without a second database round-trip.
/// </summary>
public sealed record SwitchBotPollingCycleResult(
    int DeviceCount,
    IReadOnlyList<(DeviceEvent Event, Device Device)> CreatedEvents,
    IReadOnlyList<(PlugMiniReading Reading, Device Device)> CreatedReadings)
{
    public static readonly SwitchBotPollingCycleResult Empty = new(0, [], []);
}

/// <summary>
/// Polls every active SwitchBot device belonging to exactly one household through an
/// already-resolved, household-scoped <see cref="IDeviceProvider"/> and records:
///   1. a DeviceEvent row, but only when the observed on/off/motion/contact state
///      actually changed since the last recorded event (unchanged from the
///      single-global-provider design this replaces), and
///   2. for Plug Mini class devices, a PlugMiniReading row on every single poll,
///      regardless of whether the state changed, so voltage/current/energy
///      telemetry forms a real time series.
///
/// Both of the above are derived from exactly ONE upstream status request per
/// device per cycle: when the resolved provider also implements
/// <see cref="IDeviceStatusSnapshotProvider"/> (true for the real
/// <c>SwitchBotDeviceProvider</c>), this service calls
/// <see cref="IDeviceStatusSnapshotProvider.GetStatusSnapshotAsync"/> exactly once
/// per device and reuses that single parsed response for both the state-change
/// projection and the Plug Mini telemetry -- it never separately calls a
/// state-only method and then a Plug-Mini-only method against the live API for the
/// same device in the same cycle. Providers with no Plug Mini concept at all
/// (e.g. the mock/demo provider) don't need to implement that interface: this
/// service falls back to plain <see cref="IDeviceProvider.GetStatusAsync"/> for
/// them and simply never attempts a Plug Mini reading (no Plug-specific work is
/// incurred for non-Plug-Mini providers/devices either way).
///
/// Deliberately takes an already-resolved <see cref="IDeviceProvider"/> rather than
/// resolving one itself: the caller (SwitchBotPollingBackgroundService) is
/// responsible for building that provider from a short-lived,
/// household-scoped IHouseholdSwitchBotClientFactory call so one household's
/// decrypted credentials are never reused for another household's devices. This
/// separation also keeps this class trivially unit-testable with a fake
/// IDeviceProvider/IDeviceStatusSnapshotProvider and an in-memory TestDb -- no DI
/// scope or credential factory involved.
/// </summary>
public sealed class SwitchBotPollingCycleService(IAppDbContext db, TimeProvider clock)
{
    public async Task<SwitchBotPollingCycleResult> PollHouseholdAsync(
        Guid householdId, IDeviceProvider provider, CancellationToken ct = default)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.Provider == DeviceProviderKind.SwitchBot && d.IsActive)
            .ToListAsync(ct);

        if (devices.Count == 0)
        {
            return SwitchBotPollingCycleResult.Empty;
        }

        // One fixed "now" for the whole cycle: keeps the PlugMiniReading dedupe key
        // (HouseholdId+DeviceId+OccurredAtUtc) trivially deterministic/testable, and
        // means every reading from the same cycle shares one clearly-grouped timestamp.
        var now = clock.GetUtcNow();
        var snapshotProvider = provider as IDeviceStatusSnapshotProvider;

        var createdEvents = new List<(DeviceEvent, Device)>();
        var createdReadings = new List<(PlugMiniReading, Device)>();

        foreach (var device in devices)
        {
            // Exactly one upstream status request per device per cycle, however the
            // response is shaped: the snapshot path pulls both projections out of
            // that single parsed response; the fallback path (a provider with no
            // Plug Mini capability) only ever needed the state half anyway.
            var snapshot = snapshotProvider is not null
                ? await snapshotProvider.GetStatusSnapshotAsync(device.ExternalDeviceId, ct)
                : new DeviceStatusSnapshot(await provider.GetStatusAsync(device.ExternalDeviceId, ct), null);

            var effective = EffectiveState(snapshot.Status, snapshot.PlugMiniReading);
            var deviceEvent = await PollStateChangeAsync(device, effective, now, ct);
            if (deviceEvent is not null)
            {
                createdEvents.Add((deviceEvent, device));
            }
            else
            {
                // The socket did not switch, but the draw behind it may still have
                // moved -- someone starting a kettle on a plug that was already
                // energised. That is activity, so it must not be invisible.
                var changeEvent = await PollPowerChangeAsync(device, effective, now, ct);
                if (changeEvent is not null)
                {
                    createdEvents.Add((changeEvent, device));
                }
            }

            if (snapshot.PlugMiniReading is not null)
            {
                var reading = await PollPlugMiniReadingAsync(device, snapshot.PlugMiniReading, householdId, now, ct);
                if (reading is not null)
                {
                    createdReadings.Add((reading, device));
                }
            }
        }

        if (createdEvents.Count > 0 || createdReadings.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new SwitchBotPollingCycleResult(devices.Count, createdEvents, createdReadings);
    }

    /// <summary>
    /// Draw at or above which the appliance plugged into a Plug Mini counts as actually
    /// in use. A Plug Mini that is switched on but has nothing running behind it still
    /// reports a small standby draw, so a bare "the socket is energised" reading is not
    /// evidence that anyone used anything.
    ///
    /// Deliberately low. For a watching service, failing to notice a low-power appliance
    /// someone did use is far worse than counting a standby load as use, so this only
    /// has to clear the plug's own vampire draw -- it is not a "meaningful appliance"
    /// threshold.
    /// </summary>
    public const double InUseWattsThreshold = 1.0;

    /// <summary>
    /// Rewrites the raw socket state into what the resident actually did.
    ///
    /// Without this, life rhythm is only ever derived from the socket being switched
    /// on or off -- which for a Plug Mini left permanently energised means a rice
    /// cooker, kettle or vacuum being used behind it produces no activity at all. When
    /// this cycle carries Plug Mini telemetry the observed power draw decides the
    /// state instead: rising above <see cref="InUseWattsThreshold"/> is a use starting,
    /// falling back below it is that use ending. Devices with no telemetry (a bot, a
    /// motion sensor, the demo provider) keep their reported state untouched.
    /// </summary>
    internal static ProviderDeviceStatus? EffectiveState(
        ProviderDeviceStatus? status, PlugMiniPowerReading? reading)
    {
        if (status is null || reading?.ApproxWatts is not { } watts)
        {
            return status;
        }

        // A socket switched off cannot have an appliance running behind it, whatever a
        // stale or noisy current sample says.
        var inUse = status.IsOn && watts >= InUseWattsThreshold;

        return status with { State = inUse ? "on" : "off", PowerWatts = watts };
    }

    private async Task<DeviceEvent?> PollStateChangeAsync(
        Device device, ProviderDeviceStatus? status, DateTimeOffset now, CancellationToken ct)
    {
        if (status is null)
        {
            return null;
        }

        // Only PowerState rows describe the socket's on/off state. Power-change rows
        // carry a different State value, so including them here would make the next
        // poll think the socket had changed and write a duplicate "on".
        var lastEvent = await db.DeviceEvents
            .Where(e => e.DeviceId == device.Id && e.EventType == "PowerState")
            .OrderByDescending(e => e.OccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        // Never create a duplicate event when the observed state has not changed.
        if (lastEvent is not null && string.Equals(lastEvent.State, status.State, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var deviceEvent = new DeviceEvent
        {
            HouseholdId = device.HouseholdId,
            DeviceId = device.Id,
            EventType = "PowerState",
            State = status.State,
            PowerWatts = status.PowerWatts,
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = status.ObservedAtUtc ?? now,
            ReceivedAtUtc = now
        };

        db.DeviceEvents.Add(deviceEvent);
        return deviceEvent;
    }

    /// <summary>
    /// Smallest absolute swing in draw that is worth recording, in watts. Below this a
    /// change cannot be a different appliance -- it is measurement jitter or a
    /// thermostat nudging a load that was already running.
    /// </summary>
    public const double PowerChangeMinWatts = 10.0;

    /// <summary>
    /// The swing must also be this fraction of the larger of the two levels. Without it
    /// a heater cycling 900W -> 880W would be reported as if someone had done something,
    /// and the family would learn to ignore the timeline.
    /// </summary>
    public const double PowerChangeMinRatio = 0.25;

    /// <summary>
    /// True when the draw moved enough to mean a different load, rather than noise.
    /// Requires both an absolute and a proportional swing so the rule behaves the same
    /// for a bedside lamp and for an air conditioner.
    /// </summary>
    internal static bool IsSignificantPowerChange(double reference, double current)
    {
        var delta = Math.Abs(current - reference);
        if (delta < PowerChangeMinWatts)
        {
            return false;
        }

        var scale = Math.Max(Math.Max(reference, current), 1.0);
        return delta / scale >= PowerChangeMinRatio;
    }

    /// <summary>
    /// Records a change in draw while the socket stayed on.
    ///
    /// Deliberately a separate EventType with its own State: "how many times was an
    /// appliance used today" counts on-events, and a kettle boiling behind an
    /// already-on plug must not silently inflate that count. It does still move the
    /// first/last activity window, because it is real evidence that somebody is up and
    /// about -- which is the whole point of watching a plug rather than a switch.
    ///
    /// Compares against the draw recorded on the previous event rather than the
    /// previous sample, so a load that ramps up over several cycles is still caught,
    /// and a level that simply persists is not re-reported every five minutes.
    /// </summary>
    private async Task<DeviceEvent?> PollPowerChangeAsync(
        Device device, ProviderDeviceStatus? status, DateTimeOffset now, CancellationToken ct)
    {
        if (status is null || !status.IsOn || status.PowerWatts is not { } watts)
        {
            return null;
        }

        var reference = await db.DeviceEvents
            .Where(e => e.DeviceId == device.Id && e.PowerWatts != null)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Select(e => e.PowerWatts)
            .FirstOrDefaultAsync(ct);

        // No measured level on record yet: nothing to compare against, and inventing a
        // change here would report the first telemetry-bearing poll as an event.
        if (reference is not { } previous || !IsSignificantPowerChange(previous, watts))
        {
            return null;
        }

        var deviceEvent = new DeviceEvent
        {
            HouseholdId = device.HouseholdId,
            DeviceId = device.Id,
            EventType = "PowerChange",
            State = watts > previous ? "increased" : "decreased",
            PowerWatts = watts,
            NumericValue = Math.Round(watts - previous, 1),
            Unit = "W",
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = status.ObservedAtUtc ?? now,
            ReceivedAtUtc = now
        };

        db.DeviceEvents.Add(deviceEvent);
        return deviceEvent;
    }

    /// <summary>
    /// Inserts a PlugMiniReading row for this poll cycle unless one already exists
    /// for the same (household, device, cycle timestamp) -- the dedupe guard that
    /// backs the HouseholdId+DeviceId+OccurredAtUtc unique index, and makes retried
    /// or double-invoked cycles safe. Takes the already-fetched reading (from this
    /// cycle's single status snapshot) rather than fetching it itself.
    /// </summary>
    private async Task<PlugMiniReading?> PollPlugMiniReadingAsync(
        Device device, PlugMiniPowerReading powerReading, Guid householdId, DateTimeOffset now, CancellationToken ct)
    {
        var alreadyExists = await db.PlugMiniReadings.AnyAsync(
            r => r.HouseholdId == householdId && r.DeviceId == device.Id && r.OccurredAtUtc == now, ct);
        if (alreadyExists)
        {
            return null;
        }

        var reading = new PlugMiniReading
        {
            HouseholdId = householdId,
            DeviceId = device.Id,
            VoltageV = powerReading.VoltageV,
            CurrentMa = powerReading.CurrentMa,
            DailyEnergyWh = powerReading.DailyEnergyWh,
            UsageMinutesToday = powerReading.UsageMinutesToday,
            ApproxWatts = powerReading.ApproxWatts,
            OccurredAtUtc = now,
            ReceivedAtUtc = now
        };

        db.PlugMiniReadings.Add(reading);
        return reading;
    }
}
