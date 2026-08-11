using System.Globalization;

namespace MimamoriTai.Web.Charts;

/// <summary>One bar of a <c>BarChart</c>.</summary>
/// <param name="Label">Short axis label, e.g. "8/11".</param>
/// <param name="Value">The measured value. Never negative in practice; clamped if it is.</param>
/// <param name="IsHighlighted">Marks today (or the selected day) so it stands out.</param>
/// <param name="Display">
/// Overrides the tooltip text when the raw number is not how a person would say it -
/// a wake-up time of 6.5 should read "6:30", not "6.5時".
/// </param>
public sealed record BarChartPoint(string Label, double Value, bool IsHighlighted = false, string? Display = null);

/// <summary>
/// The geometry behind the dashboard charts, kept separate from the Razor markup so the
/// maths can be tested directly without spinning up a renderer or taking a UI-testing
/// dependency. Coordinates are in the SVG's own viewBox units (the chart is stretched to
/// the card width by CSS), so only their ratios matter.
/// </summary>
public static class BarChartGeometry
{
    public const double ViewWidth = 100;
    public const double ViewHeight = 34;

    /// <summary>Y coordinate of the baseline the bars stand on.</summary>
    public const double PlotBottom = ViewHeight - 1;

    /// <summary>
    /// Kept above zero on purpose: a day with nothing recorded still has to appear, because
    /// "nothing happened today" is exactly what a worried family is looking for.
    /// </summary>
    public const double MinBarHeight = 0.6;

    private const double TopPadding = 2;

    public static double Slot(int count) => ViewWidth / Math.Max(count, 1);

    public static double BarWidth(int count) => Math.Max(Slot(count) * 0.62, 0.6);

    public static double BarX(int index, int count)
    {
        var slot = Slot(count);
        return (slot * index) + ((slot - BarWidth(count)) / 2);
    }

    /// <summary>
    /// Scales against the busiest bar rather than a fixed pixels-per-unit factor, so a
    /// quiet fortnight and a busy one both use the full height and stay comparable
    /// within themselves.
    /// </summary>
    public static double BarHeight(double value, double max)
    {
        if (max <= 0 || value <= 0 || double.IsNaN(value) || double.IsNaN(max))
        {
            return MinBarHeight;
        }

        var scaled = value / max * (PlotBottom - TopPadding);

        // Clamp so a value above the stated max (a caller bug) can never escape the plot.
        return Math.Clamp(scaled, MinBarHeight, PlotBottom - TopPadding);
    }

    public static double BarTop(double value, double max) => PlotBottom - BarHeight(value, max);

    public static double Max(IReadOnlyList<BarChartPoint> points) =>
        points.Count == 0 ? 0 : points.Max(p => p.Value);

    /// <summary>Horizontal centre of a bar - where the trend line and its dots sit.</summary>
    public static double CenterX(int index, int count) => BarX(index, count) + (BarWidth(count) / 2);

    /// <summary>
    /// The trend line threading the tops of the bars. Bars answer "how much on that day";
    /// the line answers "which way is it going", which is the question a family actually asks.
    /// </summary>
    public static string LinePoints(IReadOnlyList<BarChartPoint> points, double max)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(' ', points.Select((p, i) =>
            $"{F(CenterX(i, points.Count))},{F(BarTop(p.Value, max))}"));
    }

    /// <summary>The same line closed down to the baseline, so it can be filled with a soft wash.</summary>
    public static string AreaPath(IReadOnlyList<BarChartPoint> points, double max)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        var line = string.Join(' ', points.Select((p, i) =>
            $"L{F(CenterX(i, points.Count))},{F(BarTop(p.Value, max))}"));

        var first = F(CenterX(0, points.Count));
        var last = F(CenterX(points.Count - 1, points.Count));

        return $"M{first},{F(PlotBottom)} {line} L{last},{F(PlotBottom)} Z";
    }

    /// <summary>
    /// Length the trend line is padded to for the draw-on animation. Deliberately generous:
    /// a dash array shorter than the real path would leave the line visibly cut off.
    /// </summary>
    public static double LineDashLength(int count) => Math.Max(count, 1) * ViewHeight;

    public static string BarClass(BarChartPoint point) =>
        point.IsHighlighted ? "bar-chart-bar is-highlighted" : "bar-chart-bar";

    /// <summary>Tooltip text: the caller's own wording when given, otherwise value + unit.</summary>
    public static string Describe(BarChartPoint point, string unit) =>
        point.Display ?? $"{F(point.Value)}{unit}";

    /// <summary>
    /// How the top of the scale should be written, borrowing the busiest bar's own wording so
    /// a wake-up chart reads "8:15" rather than "8.25". Bars are scaled against that busiest
    /// day, so stating it is what stops one lone bar from being unreadable.
    /// </summary>
    public static string ScaleTop(IReadOnlyList<BarChartPoint> points, string unit)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        var max = Max(points);
        var top = points.FirstOrDefault(p => p.Value == max) ?? points[0];

        return max <= 0 ? $"0{unit}" : Describe(top, unit);
    }

    /// <summary>
    /// SVG attributes must use a dot decimal separator regardless of the request culture:
    /// a ja-JP or de-DE thread would otherwise emit "1,5" and the browser would drop the bar.
    /// </summary>
    public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
