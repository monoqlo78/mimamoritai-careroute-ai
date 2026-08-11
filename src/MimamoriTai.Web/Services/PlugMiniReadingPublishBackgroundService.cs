using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Periodically publishes PlugMiniReading rows that have not yet reached the Fabric
/// Eventhouse (PublishedToStreamAtUtc is null). Mirrors
/// <see cref="EventStreamPublishBackgroundService"/> exactly (same interval,
/// same "cheap no-op when unconfigured" behavior, same retry-next-cycle contract via
/// <see cref="PlugMiniReadingPublishService"/>) but is a fully separate cycle so a
/// Plug Mini ingestion outage/misconfiguration can never affect DeviceEvent
/// publishing, and vice versa. Every exception is caught and logged: this must never
/// crash the app.
/// </summary>
public sealed class PlugMiniReadingPublishBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlugMiniReadingPublishBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(25);
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
            var publisher = scope.ServiceProvider.GetRequiredService<IPlugMiniReadingStreamPublisher>();

            if (!publisher.IsConfigured)
            {
                return;
            }

            var publishService = scope.ServiceProvider.GetRequiredService<PlugMiniReadingPublishService>();
            var result = await publishService.PublishUnpublishedBatchAsync(ct: ct);

            if (result.Attempted == 0)
            {
                return;
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Published {Published}/{Attempted} Plug Mini reading(s) to the Fabric Eventhouse.",
                    result.Published, result.Attempted);
            }
            else
            {
                logger.LogWarning(
                    "Fabric Eventhouse publish failed for {Attempted} pending Plug Mini reading(s) ({Error}); will retry next cycle.",
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
            logger.LogWarning(ex, "Plug Mini reading publish cycle failed; will retry next interval.");
        }
    }
}
