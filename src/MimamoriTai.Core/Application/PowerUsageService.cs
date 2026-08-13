using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Core.Application;

/// <summary>One household-local day's electricity use, in watt-hours.</summary>
public sealed record PowerUsageDay(DateOnly Date, double EnergyWh);

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
    DateTimeOffset? MeasuredAtUtc)
{
    public static readonly PowerUsageSummary Empty = new(null, 0, 0, [], null);

    /// <summary>True when nothing has ever been measured, so the UI can stay silent.</summary>
    public bool HasData => MeasuredAtUtc is not null;
}

/// <summary>
/// Turns Plug Mini telemetry into daily electricity totals.
///
/// The important subtlety is that <see cref="Domain.PlugMiniReading.DailyEnergyWh"/> is
/// SwitchBot's own running total for the day, not the energy used since the previous
/// reading: it climbs all day and resets at local midnight. Summing the readings would
/// therefore multiply a day's consumption by the number of times we polled (288 at the
/// 5-minute cadence). The day's real figure is the largest value seen that day, per
/// device, which is what this takes.
///
/// Days are grouped in the household's local timezone rather than UTC, because a
/// resident's "yesterday" ends at their midnight, and because that is the boundary
/// SwitchBot itself resets on.
/// </summary>
public sealed class PowerUsageService(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Longest window offered, and therefore how much history is loaded.</summary>
    public const int WindowDays = 30;

    /// <param name="deviceId">Limits the figures to one appliance; null totals the household.</param>
    public async Task<PowerUsageSummary> GetAsync(
        Guid householdId, Guid? deviceId = null, CancellationToken ct = default)
    {
        var today = HouseholdTime.LocalDate(clock.GetUtcNow());
        var since = HouseholdTime.StartOfLocalDayUtc(today.AddDays(-(WindowDays - 1)));

        var rows = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId
                && r.OccurredAtUtc >= since
                && r.DailyEnergyWh != null
                && (deviceId == null || r.DeviceId == deviceId))
            .Select(r => new { r.DeviceId, r.OccurredAtUtc, r.DailyEnergyWh })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return PowerUsageSummary.Empty;
        }

        // Per device per local day, keep the running total's high-water mark, then add
        // the devices together so a household figure is the sum of its appliances.
        var byDay = rows
            .GroupBy(r => new { Date = HouseholdTime.LocalDate(r.OccurredAtUtc), r.DeviceId })
            .Select(g => new { g.Key.Date, Wh = g.Max(x => x.DailyEnergyWh!.Value) })
            .GroupBy(x => x.Date)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Wh));

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
            rows.Max(r => r.OccurredAtUtc));
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
