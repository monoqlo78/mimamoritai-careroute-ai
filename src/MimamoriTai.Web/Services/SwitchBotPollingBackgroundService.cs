using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Periodically polls real device status for every Production household
/// individually, via that household's own <see cref="IHouseholdSwitchBotClientFactory"/>-
/// resolved provider, and records:
///   - DeviceEvent rows for observed on/off/motion/contact transitions (only on
///     change, exactly like before this household-scoping refactor), and
///   - PlugMiniReading rows on every single cycle for Plug Mini class devices,
///     regardless of whether the state changed, so voltage/current/energy
///     telemetry forms a real time series (see docs/FABRIC_SETUP.md).
///
/// Each household is resolved and polled inside its own short-lived DI scope: a
/// fresh IHouseholdSwitchBotClientFactory call decrypts that household's
/// credentials, builds a client bound only to them, and the scope (and therefore
/// that decrypted client) is disposed before the next household's iteration
/// begins. No decrypted credential is ever held past one household's turn, and
/// nothing here caches a client across households.
///
/// This service is entirely inert (no-op) when there are no Production households,
/// or when a given household has neither a configured SwitchBotConnection nor an
/// explicitly allowed legacy global-option fallback: the demo path
/// (MockDeviceProvider) and every existing test are unaffected. Every exception is
/// caught and logged: this must never crash the app.
///
/// Also re-fetches the household's device list (reusing
/// <see cref="DeviceSyncService"/>, the same class the "今すぐ同期する" button calls) once
/// <see cref="SwitchBotOptions.DeviceDiscoveryIntervalMinutes"/> has elapsed since the
/// last successful discovery, so a device added on the SwitchBot side (e.g. a second
/// Plug Mini) shows up without anyone pressing that button. This is intentionally a
/// coarser cadence than the per-device status poll -- see that option's own doc
/// comment for why. It is also intentionally add-only
/// (<c>deactivateMissing: false</c>): a device silently disappearing from one
/// GET /v1.1/devices response could just as easily be a transient SwitchBot API
/// hiccup as a real removal, and wrongly flipping IsActive=false would hide a
/// device from the dashboard/alerts on nothing more than one bad response. Real
/// removal stays a deliberate, operator-driven action via manual sync.
/// </summary>
public sealed class SwitchBotPollingBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<SwitchBotOptions> switchBotOptions,
    TimeProvider clock,
    ILogger<SwitchBotPollingBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    // In-memory only: a restart simply re-runs discovery on the next cycle, which is
    // harmless (SyncAsync is idempotent) and far simpler than persisting a per-household
    // "last discovered" timestamp for what is, at worst, one extra device-list call.
    private readonly Dictionary<Guid, DateTimeOffset> _lastDeviceDiscoveryAtUtc = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(switchBotOptions.Value.PollIntervalMinutes, 1));

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        List<Guid> productionHouseholdIds;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            // This service only ever polls real (Production) households; Sample/demo
            // households are simulated locally and must never be touched here.
            productionHouseholdIds = await db.Households
                .Where(h => h.DataSourceMode == DataSourceMode.Production)
                .Select(h => h.Id)
                .ToListAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SwitchBot polling could not list Production households; will retry next interval.");
            return;
        }

        if (productionHouseholdIds.Count == 0)
        {
            logger.LogDebug("SwitchBot polling skipped: no Production household exists yet.");
            return;
        }

        foreach (var householdId in productionHouseholdIds)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await PollOneHouseholdAsync(householdId, ct);
        }
    }

    /// <summary>
    /// Resolves and polls exactly one household inside its own DI scope, so its
    /// decrypted SwitchBot client never outlives this call or leaks into the next
    /// household's iteration. A failure here (auth error, network error, DB error)
    /// is isolated to this household and never aborts the rest of the cycle.
    /// </summary>
    private async Task PollOneHouseholdAsync(Guid householdId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var clientFactory = scope.ServiceProvider.GetRequiredService<IHouseholdSwitchBotClientFactory>();
            var provider = await clientFactory.GetDeviceProviderAsync(householdId, ct);

            if (!provider.IsConfigured)
            {
                // No connected SwitchBotConnection for this household, and no
                // legacy global-option fallback permitted -- nothing to poll.
                return;
            }

            // Runs before the status poll so a device discovered just now is already
            // included in this same cycle's status pass, not left waiting one more
            // interval.
            await DiscoverNewDevicesAsync(householdId, provider, scope.ServiceProvider, ct);

            var pollingService = scope.ServiceProvider.GetRequiredService<SwitchBotPollingCycleService>();
            var result = await pollingService.PollHouseholdAsync(householdId, provider, ct);

            if (result.CreatedEvents.Count > 0)
            {
                await PublishEventsToStreamAsync(scope.ServiceProvider, result.CreatedEvents, ct);
            }

            if (result.CreatedReadings.Count > 0)
            {
                await PublishReadingsToStreamAsync(scope.ServiceProvider, result.CreatedReadings, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SwitchBot polling failed for household {HouseholdId}; will retry next interval.", householdId);
        }
    }

    /// <summary>
    /// Re-fetches this household's device list and upserts new devices only, but no
    /// more often than <see cref="SwitchBotOptions.DeviceDiscoveryIntervalMinutes"/> --
    /// this is the fix for "SwitchBot側で追加したデバイスが同期を押すまで増えない": without
    /// it, only already-known devices (loaded from the Devices table) ever get polled,
    /// so a newly-added device is invisible until someone presses "今すぐ同期する".
    /// A discovery failure (e.g. a transient API error) leaves the last-discovered
    /// timestamp untouched, so the next cycle retries immediately rather than waiting
    /// out a full interval on top of the failure.
    /// </summary>
    private async Task DiscoverNewDevicesAsync(
        Guid householdId, IDeviceProvider provider, IServiceProvider scopedProvider, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(switchBotOptions.Value.DeviceDiscoveryIntervalMinutes, 1));
        var now = clock.GetUtcNow();

        if (_lastDeviceDiscoveryAtUtc.TryGetValue(householdId, out var lastRun) && now - lastRun < interval)
        {
            return;
        }

        try
        {
            var db = scopedProvider.GetRequiredService<IAppDbContext>();
            var syncService = new DeviceSyncService(db, provider, clock);
            // deactivateMissing: false -- see this class's own doc comment for why
            // auto-discovery only ever adds/updates, never deactivates.
            var result = await syncService.SyncAsync(householdId, deactivateMissing: false, ct: ct);
            _lastDeviceDiscoveryAtUtc[householdId] = now;

            if (result.Added > 0)
            {
                logger.LogInformation(
                    "SwitchBot auto-discovery added {Added} new device(s) for household {HouseholdId}.",
                    result.Added, householdId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SwitchBot device auto-discovery failed for household {HouseholdId}; will retry next cycle.", householdId);
        }
    }

    /// <summary>
    /// Best-effort secondary write to Fabric Eventhouse for near-real-time analytics.
    /// Azure SQL is already durable at this point; a Fabric publish failure must never
    /// interrupt or fail the polling loop -- EventStreamPublishBackgroundService
    /// retries any rows left unstamped by this best-effort attempt.
    /// </summary>
    private async Task PublishEventsToStreamAsync(
        IServiceProvider scopedProvider, IReadOnlyList<(DeviceEvent Event, Device Device)> createdEvents, CancellationToken ct)
    {
        try
        {
            var publisher = scopedProvider.GetRequiredService<IEventStreamPublisher>();

            var records = createdEvents.Select(x => new DeviceEventRecord(
                x.Event.Id,
                x.Event.HouseholdId,
                x.Event.DeviceId,
                x.Device.DisplayName,
                x.Device.DisplayRoom,
                x.Device.DeviceType.ToString(),
                x.Event.EventType,
                x.Event.State,
                x.Event.PowerWatts,
                x.Event.Source.ToString(),
                x.Event.OccurredAtUtc.UtcDateTime)).ToList();

            var result = await publisher.PublishAsync(records, ct);
            if (result.Success)
            {
                var db = scopedProvider.GetRequiredService<IAppDbContext>();
                var clock = scopedProvider.GetRequiredService<TimeProvider>();
                var stampedAtUtc = clock.GetUtcNow();
                foreach (var (deviceEvent, _) in createdEvents)
                {
                    deviceEvent.PublishedToStreamAtUtc = stampedAtUtc;
                }
                await db.SaveChangesAsync(ct);
            }
            else
            {
                logger.LogWarning(
                    "Eventhouse publish of {Count} device event(s) failed: {Error}",
                    records.Count, result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Eventhouse publish failed; SwitchBot polling continues normally.");
        }
    }

    /// <summary>Same best-effort contract as <see cref="PublishEventsToStreamAsync"/>, for Plug Mini readings.</summary>
    private async Task PublishReadingsToStreamAsync(
        IServiceProvider scopedProvider, IReadOnlyList<(PlugMiniReading Reading, Device Device)> createdReadings, CancellationToken ct)
    {
        try
        {
            var publisher = scopedProvider.GetRequiredService<IPlugMiniReadingStreamPublisher>();

            var records = createdReadings.Select(x => new PlugMiniReadingRecord(
                x.Reading.Id,
                x.Reading.HouseholdId,
                x.Reading.DeviceId,
                x.Device.DisplayName,
                x.Device.DisplayRoom,
                x.Reading.VoltageV,
                x.Reading.CurrentMa,
                x.Reading.DailyEnergyWh,
                x.Reading.UsageMinutesToday,
                x.Reading.ApproxWatts,
                x.Reading.OccurredAtUtc.UtcDateTime)).ToList();

            var result = await publisher.PublishAsync(records, ct);
            if (result.Success)
            {
                var db = scopedProvider.GetRequiredService<IAppDbContext>();
                var clock = scopedProvider.GetRequiredService<TimeProvider>();
                var stampedAtUtc = clock.GetUtcNow();
                foreach (var (reading, _) in createdReadings)
                {
                    reading.PublishedToStreamAtUtc = stampedAtUtc;
                }
                await db.SaveChangesAsync(ct);
            }
            else
            {
                logger.LogWarning(
                    "Eventhouse publish of {Count} Plug Mini reading(s) failed: {Error}",
                    records.Count, result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Eventhouse Plug Mini reading publish failed; SwitchBot polling continues normally.");
        }
    }
}
