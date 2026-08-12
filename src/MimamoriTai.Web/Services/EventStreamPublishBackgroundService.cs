using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Periodically publishes DeviceEvent rows that have not yet reached the Fabric
/// Eventhouse (PublishedToStreamAtUtc is null), so the Fabric Data Agent's answers
/// stay current without a human ever hitting POST /api/stream/publish. Runs roughly
/// once a minute and stamps only the rows a successful publish actually covered, via
/// EventStreamPublishService, so a partial/failed cycle retries the same backlog
/// next time instead of losing events. Entirely harmless (a cheap no-op query) when
/// IEventStreamPublisher.IsConfigured is false, matching how the other optional
/// integrations behave when unconfigured. Every exception is caught and logged: this
/// must never crash the app.
/// </summary>
public sealed class EventStreamPublishBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<EventStreamPublishBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // Fabric rejects the whole capacity once it is overloaded, so a publisher that
    // keeps failing must not keep asking every minute. See PeriodicBackoff.
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(30);

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

        var backoff = new PeriodicBackoff(Interval, MaxInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = await RunOnceAsync(stoppingToken);
            var wait = backoff.Next(succeeded);

            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns>False only when a cycle actually failed, so the caller can slow down.</returns>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventStreamPublisher>();

            if (!publisher.IsConfigured)
            {
                return true;
            }

            var publishService = scope.ServiceProvider.GetRequiredService<EventStreamPublishService>();
            var result = await publishService.PublishUnpublishedBatchAsync(ct: ct);

            if (result.Attempted == 0)
            {
                return true;
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Published {Published}/{Attempted} device event(s) to the Fabric Eventhouse.",
                    result.Published, result.Attempted);
                return true;
            }

            logger.LogWarning(
                "Fabric Eventhouse publish failed for {Attempted} pending device event(s) ({Error}); will retry, backing off while it keeps failing.",
                result.Attempted, result.Error);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
            return true;
        }
        catch (Exception ex)
        {
            // The background publisher must never take the app down.
            logger.LogWarning(ex, "Event stream publish cycle failed; will retry next interval.");
            return false;
        }
    }
}
