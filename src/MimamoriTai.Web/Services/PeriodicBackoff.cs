namespace MimamoriTai.Web.Services;

/// <summary>
/// Decides how long a periodic background cycle should wait before its next run,
/// so a cycle that keeps failing stops asking at full rate.
///
/// The Plug Mini publisher spent a day and a half calling the Fabric Eventhouse
/// once a minute and getting 400 back every single time. Nothing about that
/// retrying was useful -- a 400 is the request being wrong, so the next identical
/// request is wrong too -- but the calls still landed on an F2 capacity (2 CU,
/// the smallest paid size) that also hosts the operator console. Once App Service
/// got Always On, those wasted calls ran around the clock instead of only while
/// somebody had the app awake, and the capacity started rejecting everything with
/// CapacityLimitExceeded, which took the console offline.
///
/// So: keep the normal interval while things work, and back off geometrically
/// while they do not. A broken integration then costs a handful of calls an hour
/// instead of sixty, and it still recovers on its own the moment it starts
/// succeeding -- no human has to remember to turn it back on.
/// </summary>
internal sealed class PeriodicBackoff(TimeSpan interval, TimeSpan maxInterval)
{
    private int _consecutiveFailures;

    /// <summary>Consecutive failed cycles. Exposed so callers can log the streak.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Records the outcome of a cycle and returns how long to wait before the next
    /// one. Success returns to the normal interval immediately: a single good cycle
    /// is enough evidence that whatever was broken is fixed, and staying slow after
    /// that would delay real data for no reason.
    /// </summary>
    public TimeSpan Next(bool succeeded)
    {
        if (succeeded)
        {
            _consecutiveFailures = 0;
            return interval;
        }

        // Saturates rather than overflowing: the shift below is only meaningful for
        // small counts, and an integration can stay broken for days.
        if (_consecutiveFailures < 30)
        {
            _consecutiveFailures++;
        }

        // 1 failure keeps the normal interval, then 2x, 4x, ... up to the cap. The
        // first failure gets a free full-speed retry because transient blips (a
        // token refresh, a brief capacity hiccup) usually clear within one cycle.
        var multiplier = 1L << Math.Min(_consecutiveFailures - 1, 20);
        var ticks = interval.Ticks * multiplier;

        return ticks >= maxInterval.Ticks || ticks < 0 ? maxInterval : TimeSpan.FromTicks(ticks);
    }
}
