namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// How often the two Fabric Eventhouse publishers run, and how long they wait once
/// they start failing.
///
/// These used to be hardcoded at one minute each. That was survivable while App
/// Service went to sleep between visits, but Always On made them run around the
/// clock -- and the Plug Mini publisher had been taking 400 back on every single
/// attempt for a day and a half, so it was 1,440 calls a day that could never have
/// worked. The workspace sits on an F2 capacity (2 CU, the smallest paid size)
/// shared with the operator console, and it eventually started rejecting
/// everything with CapacityLimitExceeded, which took the console offline.
///
/// Being configurable matters more than the specific default. On a demo capacity
/// the right frequency is whatever leaves the console usable, and finding that out
/// should not need a redeploy.
/// </summary>
public sealed class FabricPublishOptions
{
    public const string SectionName = "FabricPublish";

    /// <summary>
    /// Minutes between publish cycles while they are succeeding. Five rather than
    /// one: the Eventhouse feeds the Data Agent's answers, which nobody reads to
    /// the second, and a 2 CU capacity has no headroom to spare.
    /// </summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Ceiling on the wait once a cycle keeps failing, so a broken integration
    /// costs a couple of calls an hour instead of sixty.
    /// </summary>
    public int MaxBackoffMinutes { get; set; } = 30;

    /// <summary>Clamped so a stray 0 or negative in configuration cannot spin the loop.</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));

    /// <summary>Never shorter than <see cref="Interval"/>, or backing off would speed things up.</summary>
    public TimeSpan MaxBackoff =>
        TimeSpan.FromMinutes(Math.Max(Math.Max(1, IntervalMinutes), MaxBackoffMinutes));
}
