using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Periodically polls real device status through <see cref="IDeviceProvider"/> and
/// records DeviceEvent rows for observed on/off/motion/contact transitions, so real
/// SwitchBot activity ("the light was turned on at 07:12") becomes real observed data
/// that the risk score and alert engine can read exactly like demo/simulated events.
///
/// This service is entirely inert (its ExecuteAsync returns immediately) unless
/// SwitchBot is the configured provider: the demo path (MockDeviceProvider) and every
/// existing test are completely unaffected by this service being registered.
/// Every exception is caught and logged: this must never crash the app.
/// </summary>
public sealed class SwitchBotPollingBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<SwitchBotOptions> switchBotOptions,
    ILogger<SwitchBotPollingBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!switchBotOptions.Value.IsConfigured)
        {
            // No-op: SwitchBot is not configured, so there is nothing real to poll.
            return;
        }

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
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = scope.ServiceProvider.GetRequiredService<IDeviceProvider>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

            if (provider.Kind != DeviceProviderKind.SwitchBot || !provider.IsConfigured)
            {
                return;
            }

            var devices = await db.Devices
                .Where(d => d.Provider == DeviceProviderKind.SwitchBot && d.IsActive)
                .ToListAsync(ct);

            var changedEvents = new List<(DeviceEvent Event, Device Device)>();

            foreach (var device in devices)
            {
                var changed = await PollDeviceAsync(db, provider, device, clock, ct);
                if (changed is not null)
                {
                    changedEvents.Add((changed, device));
                }
            }

            if (changedEvents.Count > 0)
            {
                await PublishToStreamAsync(scope.ServiceProvider, changedEvents, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SwitchBot polling run failed; will retry next interval.");
        }
    }

    private async Task<DeviceEvent?> PollDeviceAsync(
        AppDbContext db, IDeviceProvider provider, Device device, TimeProvider clock, CancellationToken ct)
    {
        var status = await provider.GetStatusAsync(device.ExternalDeviceId, ct);
        if (status is null)
        {
            return null;
        }

        var lastEvent = await db.DeviceEvents
            .Where(e => e.DeviceId == device.Id)
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
            OccurredAtUtc = status.ObservedAtUtc ?? clock.GetUtcNow(),
            ReceivedAtUtc = clock.GetUtcNow()
        };

        db.DeviceEvents.Add(deviceEvent);
        await db.SaveChangesAsync(ct);

        return deviceEvent;
    }

    /// <summary>
    /// Best-effort secondary write to Fabric Eventhouse for near-real-time analytics.
    /// Azure SQL is already durable at this point; a Fabric publish failure must never
    /// interrupt or fail the polling loop.
    /// </summary>
    private async Task PublishToStreamAsync(
        IServiceProvider scopedProvider, List<(DeviceEvent Event, Device Device)> changedEvents, CancellationToken ct)
    {
        try
        {
            var publisher = scopedProvider.GetRequiredService<IEventStreamPublisher>();

            var records = changedEvents.Select(x => new DeviceEventRecord(
                x.Event.Id,
                x.Event.HouseholdId,
                x.Event.DeviceId,
                x.Device.Name,
                x.Device.Room,
                x.Device.DeviceType.ToString(),
                x.Event.EventType,
                x.Event.State,
                x.Event.PowerWatts,
                x.Event.Source.ToString(),
                x.Event.OccurredAtUtc.UtcDateTime)).ToList();

            var result = await publisher.PublishAsync(records, ct);
            if (!result.Success)
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
}
