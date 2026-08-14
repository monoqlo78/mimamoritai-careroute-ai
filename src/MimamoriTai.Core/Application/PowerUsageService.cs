using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Core.Application;

/// <summary>One household-local day's electricity use, in watt-hours.</summary>
public sealed record PowerUsageDay(DateOnly Date, double EnergyWh);

/// <summary>
/// How a day compares with how this home normally uses electricity.
///
/// Deliberately relative to the resident's own habit rather than any absolute figure:
/// a household that runs one lamp and a household that runs an air conditioner have
/// nothing in common except that a sharp departure from their own routine is worth a
/// family knowing about.
/// </summary>
public enum PowerUsageTrend
{
    /// <summary>Not enough history to say what "normal" is yet.</summary>
    Unknown,

    /// <summary>Within the ordinary spread of recent days.</summary>
    Typical,

    /// <summary>Clearly more than usual.</summary>
    Higher,

    /// <summary>Clearly less than usual -- the direction that can mean nobody is up.</summary>
    Lower
}

/// <summary>
/// A day's use set against the typical day, ready to be read out to a family.
/// </summary>
/// <param name="Baseline">Median of the comparable days, so one holiday or one outage
/// does not redefine "normal".</param>
/// <param name="Ratio">Null when there is no baseline to divide by.</param>
public sealed record PowerUsageComparison(
    double EnergyWh, double? Baseline, double? Ratio, PowerUsageTrend Trend, int BaselineDays)
{
    public static readonly PowerUsageComparison Unknown =
        new(0, null, null, PowerUsageTrend.Unknown, 0);
}

/// <summary>
/// Electricity use over the windows a family actually asks about ("yesterday",
/// "last week", "last month"), plus the daily series behind them.
/// </summary>
/// <param name="YesterdayWh">Null when yesterday has no reading at all, which is
/// different from a genuine zero and must not be drawn as one.</param>
public sealed record PowerUsageSummary(
    double? YesterdayWh,
    double Last7DaysWh,
    double Last30DaysWh,
    IReadOnlyList<PowerUsageDay> Daily,
    DateTimeOffset? MeasuredAtUtc,
    double TodayWh = 0,
    PowerUsageComparison? Today = null,
    PowerUsageComparison? Yesterday = null)
{
    public static readonly PowerUsageSummary Empty = new(null, 0, 0, [], null);

    /// <summary>True when nothing has ever been measured, so the UI can stay silent.</summary>
    public bool HasData => MeasuredAtUtc is not null;

    /// <summary>The comparison worth leading with: today once it has anything to say.</summary>
    public PowerUsageComparison Headline =>
        Today is { Trend: not PowerUsageTrend.Unknown } t ? t
        : Yesterday ?? PowerUsageComparison.Unknown;
}

/// <summary>
/// Turns Plug Mini telemetry into daily electricity totals.
///
/// It does this by integrating the plug's own measured real power over time.
///
/// Two traps live here. The first is that SwitchBot's `weight` field -- carried by the
/// unfortunately named <see cref="Domain.PlugMiniReading.DailyEnergyWh"/> -- is not a
/// daily energy total at all but instantaneous watts, so taking a day's high-water mark
/// (or summing it) produces a number with no physical meaning. The second is that
/// <see cref="Domain.PlugMiniReading.ApproxWatts"/> is voltage times current, i.e.
/// apparent power: on a live plug drawing 314mA at 104V that computes to 32.7W while
/// the device reported 0.3W of real power, so integrating it overstates a reactive
/// load by two orders of magnitude.
///
/// Integrating the real-power samples avoids both, costs nothing because the poll
/// already stores them every five minutes, and lands on the same figure the SwitchBot
/// app shows. Each sample's draw is held until the next one (zero-order hold), which
/// is the right reading of a fixed-cadence meter.
///
/// Days are grouped in the household's local timezone rather than UTC, because a
/// resident's "yesterday" ends at their midnight.
/// </summary>
public sealed class PowerUsageService(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Longest window offered, and therefore how much history is loaded.</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// How long a single sample is allowed to stand for. Polling is every five minutes,
    /// but outages happen -- an eight-hour gap has been observed in production. Without
    /// a cap, the last sample before an outage would be treated as if the appliance had
    /// run at that draw all night, inventing consumption nobody used. Two cycles is
    /// enough to absorb ordinary jitter and one skipped poll.
    /// </summary>
    public static readonly TimeSpan MaxSampleSpan = TimeSpan.FromMinutes(10);

    /// <param name="deviceId">Limits the figures to one appliance; null totals the household.</param>
    public async Task<PowerUsageSummary> GetAsync(
        Guid householdId, Guid? deviceId = null, CancellationToken ct = default)
    {
        var today = HouseholdTime.LocalDate(clock.GetUtcNow());
        var since = HouseholdTime.StartOfLocalDayUtc(today.AddDays(-(WindowDays - 1)));

        // DailyEnergyWh carries SwitchBot's `weight`, which is instantaneous real watts
        // despite the name -- see the class remarks.
        var rows = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId
                && r.OccurredAtUtc >= since
                && r.DailyEnergyWh != null
                && (deviceId == null || r.DeviceId == deviceId))
            .OrderBy(r => r.OccurredAtUtc)
            .Select(r => new { r.DeviceId, r.OccurredAtUtc, Watts = r.DailyEnergyWh })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return PowerUsageSummary.Empty;
        }

        // Energy per local day. Each sample contributes its own draw for the stretch up
        // to the next sample from the same device, so appliances are integrated
        // independently and then added together for a household figure.
        //
        // byThisTime accumulates the same energy but only up to the current local
        // time of day, so today-so-far can be judged against how much a previous day
        // had used by this hour. Without it, a morning reading would be compared with
        // whole finished days and every morning would look like a collapse.
        var now = clock.GetUtcNow();
        var timeOfDay = HouseholdTime.LocalTime(now);
        var byDay = new Dictionary<DateOnly, double>();
        var byThisTime = new Dictionary<DateOnly, double>();
        foreach (var device in rows.GroupBy(r => r.DeviceId))
        {
            var samples = device.OrderBy(r => r.OccurredAtUtc).ToList();
            for (var i = 0; i < samples.Count; i++)
            {
                var span = i + 1 < samples.Count
                    ? samples[i + 1].OccurredAtUtc - samples[i].OccurredAtUtc
                    : MaxSampleSpan;

                if (span > MaxSampleSpan)
                {
                    span = MaxSampleSpan;
                }

                // The final sample of the newest day would otherwise be credited a full
                // cycle it has not lived through yet; clamping to "now" keeps today's
                // running total honest while it is still being accumulated.
                if (samples[i].OccurredAtUtc + span > now)
                {
                    span = now - samples[i].OccurredAtUtc;
                }

                if (span <= TimeSpan.Zero)
                {
                    continue;
                }

                var date = HouseholdTime.LocalDate(samples[i].OccurredAtUtc);
                var wh = samples[i].Watts!.Value * span.TotalHours;
                byDay[date] = byDay.TryGetValue(date, out var running) ? running + wh : wh;

                if (HouseholdTime.LocalTime(samples[i].OccurredAtUtc) <= timeOfDay)
                {
                    byThisTime[date] = byThisTime.TryGetValue(date, out var partial) ? partial + wh : wh;
                }
            }
        }

        // A continuous series, so a day the plug was unplugged reads as a gap in the
        // chart rather than silently shifting every later bar one place to the left.
        var daily = new List<PowerUsageDay>(WindowDays);
        for (var offset = WindowDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            daily.Add(new PowerUsageDay(date, byDay.TryGetValue(date, out var wh) ? wh : 0));
        }

        var yesterday = today.AddDays(-1);

        return new PowerUsageSummary(
            byDay.TryGetValue(yesterday, out var y) ? y : null,
            SumFrom(daily, today.AddDays(-6)),
            SumFrom(daily, today.AddDays(-(WindowDays - 1))),
            daily,
            rows.Max(r => r.OccurredAtUtc),
            byDay.TryGetValue(today, out var t) ? t : 0,
            Compare(byThisTime, today),
            Compare(byDay, yesterday));
    }

    /// <summary>Days of habit behind "usual". Long enough to average out one odd day.</summary>
    public const int BaselineDays = 14;

    /// <summary>
    /// Fewer comparable days than this and there is no habit yet, only a coincidence.
    /// </summary>
    public const int MinBaselineDays = 3;

    /// <summary>Departure from the usual day that is worth mentioning to a family.</summary>
    public const double HigherRatio = 1.4;

    /// <summary>
    /// Set lower than <see cref="HigherRatio"/> is high, because a quiet day is the
    /// direction that can mean nobody got up, and under-reacting there is the more
    /// expensive mistake for a watching service.
    /// </summary>
    public const double LowerRatio = 0.6;

    /// <summary>
    /// Judges one day against the days before it.
    ///
    /// Uses the median rather than the mean so a single holiday, guest or outage cannot
    /// redefine normal, and skips days with no measurement at all rather than reading
    /// them as zero -- a plug that was unplugged is not a day the resident used nothing.
    /// </summary>
    private static PowerUsageComparison Compare(Dictionary<DateOnly, double> byDay, DateOnly date)
    {
        if (!byDay.TryGetValue(date, out var energy))
        {
            return PowerUsageComparison.Unknown;
        }

        var history = Enumerable.Range(1, BaselineDays)
            .Select(back => date.AddDays(-back))
            .Where(byDay.ContainsKey)
            .Select(d => byDay[d])
            .OrderBy(wh => wh)
            .ToList();

        if (history.Count < MinBaselineDays)
        {
            return new PowerUsageComparison(energy, null, null, PowerUsageTrend.Unknown, history.Count);
        }

        var baseline = history.Count % 2 == 1
            ? history[history.Count / 2]
            : (history[(history.Count / 2) - 1] + history[history.Count / 2]) / 2;

        // A home that genuinely used nothing on every comparable day gives no ratio to
        // work with, so today being non-zero is reported as higher rather than infinite.
        if (baseline <= 0)
        {
            return new PowerUsageComparison(
                energy, baseline, null,
                energy > 0 ? PowerUsageTrend.Higher : PowerUsageTrend.Typical,
                history.Count);
        }

        var ratio = energy / baseline;
        var trend = ratio switch
        {
            >= HigherRatio => PowerUsageTrend.Higher,
            <= LowerRatio => PowerUsageTrend.Lower,
            _ => PowerUsageTrend.Typical
        };

        return new PowerUsageComparison(energy, baseline, ratio, trend, history.Count);
    }

    private static double SumFrom(IEnumerable<PowerUsageDay> daily, DateOnly from) =>
        daily.Where(d => d.Date >= from).Sum(d => d.EnergyWh);

    /// <summary>
    /// Formats watt-hours the way a bill does: kWh once the number gets long enough that
    /// the extra digits stop meaning anything to a reader.
    /// </summary>
    public static string Format(double? energyWh) => energyWh switch
    {
        null => "—",
        >= 1000 => $"{energyWh.Value / 1000:0.##} kWh",
        _ => $"{energyWh.Value:0.#} Wh",
    };
}
