using MimamoriTai.Web.Services;

namespace MimamoriTai.Tests;

/// <summary>
/// Guards the retry schedule that background cycles use when they keep failing.
///
/// This exists because of a real outage: the Plug Mini publisher took 400 from the
/// Fabric Eventhouse once a minute for a day and a half, and once App Service got
/// Always On those wasted calls ran around the clock and helped push an F2 capacity
/// into CapacityLimitExceeded -- which took the operator console offline. The cost
/// of a broken integration has to be bounded.
/// </summary>
public class PeriodicBackoffTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(30);

    [Fact]
    public void Healthy_Cycles_Keep_The_Normal_Interval()
    {
        var backoff = new PeriodicBackoff(Interval, Max);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(Interval, backoff.Next(succeeded: true));
        }

        Assert.Equal(0, backoff.ConsecutiveFailures);
    }

    [Fact]
    public void First_Failure_Still_Retries_At_Full_Speed()
    {
        var backoff = new PeriodicBackoff(Interval, Max);

        // A single blip -- a token refresh, a brief capacity hiccup -- usually
        // clears within one cycle, so it must not be penalised.
        Assert.Equal(Interval, backoff.Next(succeeded: false));
    }

    [Fact]
    public void Repeated_Failures_Back_Off_And_Stop_At_The_Cap()
    {
        var backoff = new PeriodicBackoff(Interval, Max);

        Assert.Equal(TimeSpan.FromMinutes(1), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromMinutes(2), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromMinutes(4), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromMinutes(8), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromMinutes(16), backoff.Next(succeeded: false));
        Assert.Equal(Max, backoff.Next(succeeded: false));
    }

    [Fact]
    public void A_Long_Outage_Never_Exceeds_The_Cap()
    {
        var backoff = new PeriodicBackoff(Interval, Max);

        // Days of failure must not overflow into a negative or absurd delay; the
        // cycle has to still be alive when the integration is finally fixed.
        for (var i = 0; i < 5_000; i++)
        {
            var wait = backoff.Next(succeeded: false);
            Assert.True(wait > TimeSpan.Zero);
            Assert.True(wait <= Max);
        }
    }

    [Fact]
    public void One_Success_Restores_Full_Speed()
    {
        var backoff = new PeriodicBackoff(Interval, Max);

        for (var i = 0; i < 10; i++)
        {
            backoff.Next(succeeded: false);
        }

        // Recovery must be automatic. Nobody should have to restart the app to get
        // fresh data flowing again after the underlying problem is fixed.
        Assert.Equal(Interval, backoff.Next(succeeded: true));
        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Equal(Interval, backoff.Next(succeeded: false));
    }

    [Fact]
    public void Backoff_Is_Relative_To_The_Configured_Interval()
    {
        // The console sync runs every 15 minutes, not every minute, so the schedule
        // has to scale off whatever interval it was given.
        var backoff = new PeriodicBackoff(TimeSpan.FromMinutes(15), TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromMinutes(15), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromMinutes(30), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromMinutes(60), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromHours(2), backoff.Next(succeeded: false));
        Assert.Equal(TimeSpan.FromHours(2), backoff.Next(succeeded: false));
    }
}
