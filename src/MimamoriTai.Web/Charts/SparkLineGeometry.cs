using System.Globalization;

namespace MimamoriTai.Web.Charts;

/// <summary>One sample of a measured quantity, at the moment the plug reported it.</summary>
public sealed record SparkPoint(DateTimeOffset At, double Value);

/// <summary>
/// The geometry behind the plug telemetry sparklines (watts, volts, milliamps).
///
/// Two things separate this from <see cref="BarChartGeometry"/>, and both come from
/// what the data actually looks like:
///
/// The vertical scale is the observed range, not zero to max. Mains voltage sits
/// between about 100V and 105V, so a chart anchored at zero draws every reading as
/// the same flat line at the top and hides the only thing worth seeing. Anchoring at
/// zero is right for a total (a day's energy) and wrong for a level.
///
/// The horizontal position comes from the timestamp rather than the sample's index.
/// The poll runs every five minutes but has been observed to stop for hours, and
/// spacing samples evenly would quietly compress that outage into a normal-looking
/// stretch of line. Gaps longer than <see cref="MaxGap"/> break the line instead, so
/// an outage reads as missing rather than as a measurement.
/// </summary>
public static class SparkLineGeometry
{
    public const double ViewWidth = 100;
    public const double ViewHeight = 28;

    private const double TopPadding = 2;
    public const double PlotBottom = ViewHeight - TopPadding;

    /// <summary>
    /// Longest silence still drawn as a continuous line. Polling is every five
    /// minutes; two cycles absorbs ordinary jitter and one skipped poll, matching
    /// <c>PowerUsageService.MaxSampleSpan</c> so the chart and the totals agree on
    /// what counts as a gap.
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The vertical extent to draw, padded so the extremes are not glued to the edges.
    ///
    /// A run of identical readings has no range at all, which would divide by zero and
    /// then collapse the line onto the axis. Those are spread into a band around the
    /// value so the line sits mid-height and reads as "steady" rather than "zero".
    ///
    /// The padding never carries the floor below zero when nothing measured was
    /// negative: watts, volts and milliamps cannot be, and a kettle's 1200W spike beside
    /// a 0.3W idle would otherwise label the bottom of the chart "-121.5W".
    /// </summary>
    public static (double Min, double Max) Range(IReadOnlyList<SparkPoint> points)
    {
        if (points.Count == 0)
        {
            return (0, 1);
        }

        var min = points.Min(p => p.Value);
        var max = points.Max(p => p.Value);

        if (max - min < 1e-9)
        {
            // Scale the band to the value so both a 0.3W reading and a 104V one get a
            // sensible band; a fixed +/-1 would swamp the first and vanish on the second.
            var pad = Math.Max(Math.Abs(max) * 0.1, 0.5);
            return (Floor(min - pad, min), max + pad);
        }

        var headroom = (max - min) * 0.1;
        return (Floor(min - headroom, min), max + headroom);
    }

    private static double Floor(double padded, double observed) =>
        observed >= 0 && padded < 0 ? 0 : padded;

    public static double X(DateTimeOffset at, DateTimeOffset from, DateTimeOffset to)
    {
        var span = (to - from).TotalSeconds;
        if (span <= 0)
        {
            return ViewWidth;
        }

        var offset = (at - from).TotalSeconds / span;
        return Math.Clamp(offset, 0, 1) * ViewWidth;
    }

    public static double Y(double value, double min, double max)
    {
        if (max - min < 1e-9)
        {
            return (PlotBottom + TopPadding) / 2;
        }

        var fraction = Math.Clamp((value - min) / (max - min), 0, 1);
        return PlotBottom - (fraction * (PlotBottom - TopPadding));
    }

    /// <summary>
    /// The polyline point lists to draw, one per unbroken stretch of samples.
    ///
    /// Returns several strings rather than one because a single polyline cannot
    /// contain a hole: the renderer emits one element per stretch, so a poller outage
    /// leaves a visible gap instead of a straight line bridging it.
    /// </summary>
    public static IReadOnlyList<string> Segments(IReadOnlyList<SparkPoint> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var ordered = points.OrderBy(p => p.At).ToList();
        var (min, max) = Range(ordered);
        var from = ordered[0].At;
        var to = ordered[^1].At;

        var segments = new List<string>();
        var current = new List<string>();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && ordered[i].At - ordered[i - 1].At > MaxGap)
            {
                Flush(segments, current);
            }

            current.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{X(ordered[i].At, from, to):0.##},{Y(ordered[i].Value, min, max):0.##}"));
        }

        Flush(segments, current);
        return segments;
    }

    /// <summary>
    /// A lone sample cannot be a line. It is still a measurement, so it is dropped from
    /// the path here and drawn as a dot by the caller rather than silently discarded.
    /// </summary>
    private static void Flush(List<string> segments, List<string> current)
    {
        if (current.Count >= 2)
        {
            segments.Add(string.Join(' ', current));
        }

        current.Clear();
    }

    /// <summary>Samples with no neighbour close enough to join, so they are drawn as dots.</summary>
    public static IReadOnlyList<SparkPoint> Isolated(IReadOnlyList<SparkPoint> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var ordered = points.OrderBy(p => p.At).ToList();
        if (ordered.Count == 1)
        {
            return ordered;
        }

        var alone = new List<SparkPoint>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var joinedLeft = i > 0 && ordered[i].At - ordered[i - 1].At <= MaxGap;
            var joinedRight = i + 1 < ordered.Count && ordered[i + 1].At - ordered[i].At <= MaxGap;

            if (!joinedLeft && !joinedRight)
            {
                alone.Add(ordered[i]);
            }
        }

        return alone;
    }

    public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
