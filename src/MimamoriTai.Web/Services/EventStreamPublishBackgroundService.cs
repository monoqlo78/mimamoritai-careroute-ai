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

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
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
            var publisher = scope.ServiceProvider.GetRequiredService<IEventStreamPublisher>();

            if (!publisher.IsConfigured)
            {
                return;
            }

            var publishService = scope.ServiceProvider.GetRequiredService<EventStreamPublishService>();
            var result = await publishService.PublishUnpublishedBatchAsync(ct: ct);

            if (result.Attempted == 0)
            {
                return;
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Published {Published}/{Attempted} device event(s) to the Fabric Eventhouse.",
                    result.Published, result.Attempted);
            }
            else
            {
                logger.LogWarning(
                    "Fabric Eventhouse publish failed for {Attempted} pending device event(s) ({Error}); will retry next cycle.",
                    result.Attempted, result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            // The background publisher must never take the app down.
            logger.LogWarning(ex, "Event stream publish cycle failed; will retry next interval.");
        }
    }
}
