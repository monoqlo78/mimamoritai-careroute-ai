using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Periodically extends the demo household's synthetic device events up to "now"
/// (<see cref="DemoDataSeeder.TopUpAsync"/>), so the demo never goes stale: without
/// this, the ~14 days of data generated once at first startup would fall further
/// into the past every day, and <c>/api/activity/today</c> would show zero activity
/// for good after day 14. Entirely harmless if the demo household does not exist
/// (e.g. a Production-only deployment) or when it is already current.
/// Every exception is caught and logged: this must never crash the app.
/// </summary>
public sealed class DemoDataTopUpBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<DemoDataTopUpBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

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
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

            await DemoDataSeeder.TopUpAsync(db, clock, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            // The background top-up must never take the app down.
            logger.LogWarning(ex, "Demo data top-up failed; will retry next interval.");
        }
    }
}
