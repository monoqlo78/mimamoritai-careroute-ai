using System.Globalization;

namespace MimamoriTai.Web.Charts;

/// <summary>One day of a <c>ClockTrendChart</c>.</summary>
/// <param name="Label">Short axis label, e.g. "8/11".</param>
/// <param name="At">The clock time recorded that day, or null when nothing was recorded.</param>
/// <param name="IsHighlighted">Marks today so it stands out.</param>
public sealed record ClockTrendPoint(string Label, TimeOnly? At, bool IsHighlighted = false);

/// <summary>
/// The geometry behind the two "what time did the day start / end" charts.
///
/// These were drawn as bars standing on midnight, which quietly said two wrong things.
/// A bar's height means "how much", but a clock time has no size -- getting up at 8:00
/// is not "twice" getting up at 4:00. Worse, a day with nothing recorded became a bar of
/// height zero, which reads as "she got up at midnight" rather than "we have no record",
/// and that is the one mistake a watching screen must never make.
///
/// So each day is a mark at its own time on a shared scale, days with no record simply
/// have no mark, and the scale zooms to the times actually recorded -- half an hour late
/// is what the family wants to see, and it is invisible on a full midnight-to-midnight axis.
/// </summary>
public static class ClockTrendGeometry
{
    /// <summary>Smallest window the scale will zoom to, so a steady week is not magnified into chaos.</summary>
    public const double MinimumSpanHours = 3;

    /// <summary>Breathing room above and below the recorded times.</summary>
    public const double PaddingHours = 0.5;

    private const double TopPadding = 3;

    /// <summary>Height of a day's mark, in viewBox units.</summary>
    public const double MarkHeight = 1.4;

    /// <summary>
    /// The slice of the clock the chart covers. Falls back to the whole day when nothing
    /// has been recorded at all, so an empty chart still has an honest axis.
    /// </summary>
    public static (double Floor, double Ceiling) Window(IReadOnlyList<ClockTrendPoint> points)
    {
        var hours = points
            .Where(p => p.At is not null)
            .Select(p => p.At!.Value.ToTimeSpan().TotalHours)
            .ToList();

        if (hours.Count == 0)
        {
            return (0, 24);
        }

        var lowest = hours.Min();
        var highest = hours.Max();
        var span = Math.Max(highest - lowest + (PaddingHours * 2), MinimumSpanHours);
        var middle = (lowest + highest) / 2;

        var floor = middle - (span / 2);
        var ceiling = middle + (span / 2);

        // Slide the window back inside the day rather than squashing it, so the span the
        // caller asked for is what the family actually gets to read.
        if (floor < 0)
        {
            ceiling -= floor;
            floor = 0;
        }

        if (ceiling > 24)
        {
            floor -= ceiling - 24;
            ceiling = 24;
        }

        return (Math.Max(floor, 0), Math.Min(ceiling, 24));
    }

    /// <summary>Vertical position of a clock time. Later in the day sits higher.</summary>
    public static double Y(double hours, double floor, double ceiling)
    {
        var span = ceiling - floor;
        if (span <= 0)
        {
            return BarChartGeometry.PlotBottom;
        }

        var fraction = Math.Clamp((hours - floor) / span, 0, 1);
        return BarChartGeometry.PlotBottom - (fraction * (BarChartGeometry.PlotBottom - TopPadding));
    }

    public static double Y(TimeOnly at, double floor, double ceiling) =>
        Y(at.ToTimeSpan().TotalHours, floor, ceiling);

    /// <summary>
    /// The line joining consecutive days, split wherever a day has no record. One
    /// unbroken line across a gap would invent a measurement nobody took.
    /// </summary>
    public static IReadOnlyList<string> Segments(IReadOnlyList<ClockTrendPoint> points, double floor, double ceiling)
    {
        var segments = new List<string>();
        var current = new List<string>();

        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].At is not { } at)
            {
                Flush();
                continue;
            }

            current.Add($"{BarChartGeometry.F(BarChartGeometry.CenterX(i, points.Count))},{BarChartGeometry.F(Y(at, floor, ceiling))}");
        }

        Flush();
        return segments;

        void Flush()
        {
            // A lone day has nothing to join, and a one-point polyline draws nothing anyway.
            if (current.Count > 1)
            {
                segments.Add(string.Join(' ', current));
            }

            current.Clear();
        }
    }

    /// <summary>Axis label for a position on the scale, written the way a person says it.</summary>
    public static string Clock(double hours)
    {
        var clamped = Math.Clamp(hours, 0, 24);
        var total = (int)Math.Round(clamped * 60);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }
}
