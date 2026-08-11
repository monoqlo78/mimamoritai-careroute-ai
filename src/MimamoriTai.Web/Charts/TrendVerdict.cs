namespace MimamoriTai.Web.Charts;

/// <summary>
/// The one-line verdict shown beside a chart caption.
/// </summary>
/// <param name="Text">What a family member reads, e.g. "いつもより少ない".</param>
/// <param name="Class">Styling hint: empty for neutral, <c>is-good</c> or <c>is-attention</c>.</param>
public sealed record TrendVerdict(string Text, string Class);

/// <summary>
/// Turns a fortnight of numbers into the sentence a family actually wants: is today normal?
/// A chart alone makes people compare bars by eye; the verdict does that for them.
/// <para>
/// Comparisons use the median of the earlier days, not the mean, because one unusual day
/// (a hospital visit, visitors staying over) would otherwise drag the baseline and make
/// every following day look wrong.
/// </para>
/// </summary>
public static class TrendVerdicts
{
    public const string Usual = "いつもどおり";
    public const string Neutral = "";
    public const string Good = "is-good";
    public const string Attention = "is-attention";

    /// <summary>
    /// Below this many recorded days there is no "usual" to compare against, and a confident
    /// verdict from two data points would be worse than saying nothing.
    /// </summary>
    public const int MinimumBaselineDays = 3;

    private const double MoreRatio = 1.5;
    private const double LessRatio = 0.5;

    /// <summary>
    /// A day is only comparable with whole days once most of it has happened. Before this
    /// hour, "less than usual" is simply "it is still morning", and a badge that cries wolf
    /// every morning teaches the family to ignore it.
    /// </summary>
    public const double ComparableFromHour = 18;

    /// <summary>Hours of difference before a wake-up time counts as early or late.</summary>
    private const double WakeUpToleranceHours = 1.5;

    /// <summary>Appliance use: markedly less than usual is the direction worth noticing.</summary>
    /// <param name="hoursIntoDay">
    /// How far today has got, so a quiet morning is not reported as a quiet day.
    /// </param>
    public static TrendVerdict? ForUsage(double? today, IReadOnlyList<double> baseline, double hoursIntoDay = 24)
    {
        var median = Median(baseline);
        if (today is not { } value || median is not { } usual || usual <= 0)
        {
            return null;
        }

        var ratio = value / usual;

        if (ratio >= MoreRatio)
        {
            // Already past a normal day's worth: true whatever the hour.
            return new TrendVerdict("いつもより多い", Neutral);
        }

        if (hoursIntoDay < ComparableFromHour)
        {
            return null;
        }

        return ratio <= LessRatio
            ? new TrendVerdict("いつもより少ない", Attention)
            : new TrendVerdict(Usual, Good);
    }

    /// <summary>
    /// Night-time movement: here <em>more</em> is the concerning direction, so the colours are
    /// the other way round from appliance use.
    /// </summary>
    public static TrendVerdict? ForNight(double? today, IReadOnlyList<double> baseline, double hoursIntoDay = 24)
    {
        if (today is not { } value || Median(baseline) is not { } usual)
        {
            return null;
        }

        // A household that never stirs at night suddenly doing so is the whole point of
        // this chart, and a ratio cannot express it because the baseline is zero.
        if (usual <= 0)
        {
            if (value > 0)
            {
                return new TrendVerdict("いつもより多い", Attention);
            }

            return hoursIntoDay < ComparableFromHour
                ? null
                : new TrendVerdict("落ち着いています", Good);
        }

        var ratio = value / usual;

        if (ratio >= MoreRatio)
        {
            return new TrendVerdict("いつもより多い", Attention);
        }

        // Saying "calmer than usual" at breakfast, before the night it describes has even
        // arrived, would be a promise the data cannot keep.
        if (hoursIntoDay < ComparableFromHour)
        {
            return null;
        }

        return ratio <= LessRatio
            ? new TrendVerdict("いつもより少ない", Good)
            : new TrendVerdict(Usual, Good);
    }

    /// <summary>Getting-up time, in hours past midnight. A late start is what matters.</summary>
    public static TrendVerdict? ForWakeUp(double? todayHours, IReadOnlyList<double> baselineHours)
    {
        if (todayHours is not { } value || Median(baselineHours) is not { } usual)
        {
            return null;
        }

        var difference = value - usual;

        if (difference >= WakeUpToleranceHours)
        {
            return new TrendVerdict("いつもより遅い", Attention);
        }

        return difference <= -WakeUpToleranceHours
            ? new TrendVerdict("いつもより早い", Neutral)
            : new TrendVerdict(Usual, Good);
    }

    /// <summary>
    /// The middle value, or null when there are too few days to call anything usual.
    /// </summary>
    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count < MinimumBaselineDays)
        {
            return null;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
