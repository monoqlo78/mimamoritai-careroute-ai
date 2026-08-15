using MimamoriTai.Web.Charts;

namespace MimamoriTai.Tests;

/// <summary>
/// "What time did the day start" is the question this chart answers, and the family reads
/// it at a glance. So a day with no record must leave a hole rather than a mark at midnight,
/// a later morning must sit higher than an earlier one, and half an hour of difference has
/// to be visible instead of being lost on a full midnight-to-midnight axis.
/// </summary>
public class ClockTrendGeometryTests
{
    private static ClockTrendPoint At(string label, string time) =>
        new(label, TimeOnly.Parse(time));

    private static ClockTrendPoint Missing(string label) => new(label, null);

    [Fact]
    public void A_day_with_no_record_is_left_blank_rather_than_placed_at_midnight()
    {
        var points = new[] { Missing("8/11"), At("8/12", "6:30"), At("8/13", "7:00") };

        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        // The blank day must not drag the scale down to midnight, which is what made the
        // old bar chart read as "she got up at 0:00".
        Assert.True(floor > 0, $"記録なしの日で目盛りが真夜中まで下がった: {floor}");
        Assert.True(ceiling > floor);
    }

    [Fact]
    public void A_later_morning_sits_higher_than_an_earlier_one()
    {
        var points = new[] { At("8/12", "6:00"), At("8/13", "8:00") };
        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        var early = ClockTrendGeometry.Y(TimeOnly.Parse("6:00"), floor, ceiling);
        var late = ClockTrendGeometry.Y(TimeOnly.Parse("8:00"), floor, ceiling);

        Assert.True(late < early, $"8時のほうが下にある: {late} vs {early}");
    }

    [Fact]
    public void Half_an_hour_late_is_visible_because_the_scale_zooms_to_the_week()
    {
        var points = new[] { At("8/12", "6:00"), At("8/13", "6:30") };
        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        var usual = ClockTrendGeometry.Y(TimeOnly.Parse("6:00"), floor, ceiling);
        var late = ClockTrendGeometry.Y(TimeOnly.Parse("6:30"), floor, ceiling);

        Assert.True(usual - late > 1, $"30分の差が潰れている: {usual - late}");
    }

    [Fact]
    public void A_steady_week_is_not_magnified_into_chaos()
    {
        var points = new[] { At("8/12", "6:00"), At("8/13", "6:02") };

        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        Assert.True(ceiling - floor >= ClockTrendGeometry.MinimumSpanHours,
            $"2分の差で目盛りが極端に狭まった: {ceiling - floor}");
    }

    [Fact]
    public void The_window_never_leaves_the_day()
    {
        var points = new[] { At("8/12", "0:05"), At("8/13", "23:50") };

        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        Assert.True(floor >= 0, $"目盛りが0時より前: {floor}");
        Assert.True(ceiling <= 24, $"目盛りが24時より後: {ceiling}");
    }

    [Fact]
    public void An_early_morning_still_gets_a_full_width_window()
    {
        var points = new[] { At("8/12", "0:10"), At("8/13", "0:20") };

        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        Assert.Equal(ClockTrendGeometry.MinimumSpanHours, ceiling - floor, 3);
    }

    [Fact]
    public void The_line_breaks_where_a_day_has_no_record()
    {
        var points = new[]
        {
            At("8/11", "6:00"), At("8/12", "6:30"),
            Missing("8/13"),
            At("8/14", "7:00"), At("8/15", "6:45"),
        };
        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        var segments = ClockTrendGeometry.Segments(points, floor, ceiling);

        // Two runs, not one line drawn straight through a day nobody measured.
        Assert.Equal(2, segments.Count);
    }

    [Fact]
    public void A_single_recorded_day_draws_no_line_at_all()
    {
        var points = new[] { Missing("8/11"), At("8/12", "6:00"), Missing("8/13") };
        var (floor, ceiling) = ClockTrendGeometry.Window(points);

        Assert.Empty(ClockTrendGeometry.Segments(points, floor, ceiling));
    }

    [Fact]
    public void A_week_with_nothing_recorded_falls_back_to_the_whole_day()
    {
        var (floor, ceiling) = ClockTrendGeometry.Window([Missing("8/11"), Missing("8/12")]);

        Assert.Equal(0, floor, 3);
        Assert.Equal(24, ceiling, 3);
    }

    [Theory]
    [InlineData(6.5, "6:30")]
    [InlineData(0, "0:00")]
    [InlineData(23.75, "23:45")]
    public void The_scale_is_written_the_way_a_person_says_it(double hours, string expected) =>
        Assert.Equal(expected, ClockTrendGeometry.Clock(hours));

    [Fact]
    public void The_scale_is_written_with_dots_and_colons_whatever_the_culture()
    {
        // A ja-JP or de-DE request thread must not produce "6,5" and break the axis.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("6:30", ClockTrendGeometry.Clock(6.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
