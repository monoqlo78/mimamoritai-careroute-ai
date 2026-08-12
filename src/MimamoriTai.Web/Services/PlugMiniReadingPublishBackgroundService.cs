using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Infrastructure.Fabric;

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
    IOptions<FabricPublishOptions> options,
    ILogger<PlugMiniReadingPublishBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(25);

    private readonly FabricPublishOptions _options = options.Value;

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

        // This is the cycle that spent a day and a half taking 400 from the
        // Eventhouse once a minute and helped push an F2 capacity into
        // CapacityLimitExceeded. See PeriodicBackoff and FabricPublishOptions.
        var backoff = new PeriodicBackoff(_options.Interval, _options.MaxBackoff);

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
            var publisher = scope.ServiceProvider.GetRequiredService<IPlugMiniReadingStreamPublisher>();

            if (!publisher.IsConfigured)
            {
                return true;
            }

            var publishService = scope.ServiceProvider.GetRequiredService<PlugMiniReadingPublishService>();
            var result = await publishService.PublishUnpublishedBatchAsync(ct: ct);

            if (result.Attempted == 0)
            {
                return true;
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Published {Published}/{Attempted} Plug Mini reading(s) to the Fabric Eventhouse.",
                    result.Published, result.Attempted);
                return true;
            }

            logger.LogWarning(
                "Fabric Eventhouse publish failed for {Attempted} pending Plug Mini reading(s) ({Error}); will retry, backing off while it keeps failing.",
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
            logger.LogWarning(ex, "Plug Mini reading publish cycle failed; will retry next interval.");
            return false;
        }
    }
}
