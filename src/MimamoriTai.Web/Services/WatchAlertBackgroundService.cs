using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Application;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Periodically evaluates the watch/risk alert for the default household so the LINE
/// push notification fires unattended (not only when a human hits the manual demo
/// endpoint). Runs on a configurable interval (Line:AlertPollIntervalMinutes, default
/// 5 minutes) and does an early first run shortly after startup so a demo doesn't have
/// to wait. Every exception is caught and logged: this must never crash the app.
/// </summary>
public sealed class WatchAlertBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<LineOptions> lineOptions,
    ILogger<WatchAlertBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

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

        var interval = TimeSpan.FromMinutes(Math.Max(lineOptions.Value.AlertPollIntervalMinutes, 1));

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
            var alerts = scope.ServiceProvider.GetRequiredService<WatchAlertService>();

            var householdId = await db.Households
                .OrderBy(h => h.CreatedAtUtc)
                .Select(h => h.Id)
                .FirstOrDefaultAsync(ct);

            if (householdId == Guid.Empty)
            {
                return;
            }

            var outcome = await alerts.EvaluateAsync(householdId, ct);
            if (outcome.Sent)
            {
                logger.LogInformation(
                    "Watch alert sent (success={Success}) for household {HouseholdId}.",
                    outcome.SendResult?.Success, householdId);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            // The background poller must never take the app down.
            logger.LogWarning(ex, "Watch alert background evaluation failed; will retry next interval.");
        }
    }
}
