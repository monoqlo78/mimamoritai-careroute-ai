using System.Globalization;
using MimamoriTai.Web.Charts;

namespace MimamoriTai.Tests;

/// <summary>
/// The dashboard charts are the family's "is today normal?" glance, so the geometry has to
/// be right: a quiet day must look shorter than a busy one, a day with nothing recorded must
/// still be visible, and no bar may escape the plot area.
/// </summary>
public class BarChartGeometryTests
{
    [Fact]
    public void A_busier_day_gets_a_taller_bar()
    {
        var quiet = BarChartGeometry.BarHeight(2, max: 10);
        var busy = BarChartGeometry.BarHeight(10, max: 10);

        Assert.True(busy > quiet, $"10回のほうが低い: {busy} vs {quiet}");
    }

    [Fact]
    public void The_busiest_day_fills_the_plot_area()
    {
        var height = BarChartGeometry.BarHeight(10, max: 10);

        Assert.Equal(BarChartGeometry.PlotBottom - 2, height, 3);
    }

    [Fact]
    public void A_day_with_nothing_recorded_still_shows_a_sliver()
    {
        // A silent gap is the alarming case; it must never vanish from the chart.
        var empty = BarChartGeometry.BarHeight(0, max: 8);

        Assert.Equal(BarChartGeometry.MinBarHeight, empty);
        Assert.True(empty < BarChartGeometry.BarHeight(8, max: 8));
    }

    [Fact]
    public void An_all_zero_series_does_not_divide_by_zero()
    {
        var height = BarChartGeometry.BarHeight(0, max: 0);

        Assert.Equal(BarChartGeometry.MinBarHeight, height);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(500, 10)] // a caller passing a stale max must not push a bar off the top
    public void Bars_stay_inside_the_plot_area(double value, double max)
    {
        var top = BarChartGeometry.BarTop(value, max);
        var height = BarChartGeometry.BarHeight(value, max);

        Assert.True(top >= 0, $"上にはみ出しています: y={top}");
        Assert.True(top + height <= BarChartGeometry.PlotBottom + 0.001, $"下にはみ出しています: y={top} h={height}");
    }

    [Fact]
    public void Bars_never_overlap_their_neighbours()
    {
        const int count = 14;
        var width = BarChartGeometry.BarWidth(count);

        for (var i = 1; i < count; i++)
        {
            var previousRight = BarChartGeometry.BarX(i - 1, count) + width;
            Assert.True(BarChartGeometry.BarX(i, count) > previousRight, $"{i}番目の棒が重なっています");
        }
    }

    [Fact]
    public void Bars_fit_within_the_chart_width()
    {
        const int count = 14;

        Assert.True(BarChartGeometry.BarX(0, count) >= 0);
        Assert.True(
            BarChartGeometry.BarX(count - 1, count) + BarChartGeometry.BarWidth(count) <= BarChartGeometry.ViewWidth);
    }

    [Fact]
    public void A_single_bar_still_gets_a_sensible_width()
    {
        var width = BarChartGeometry.BarWidth(1);

        Assert.True(width is > 0 and <= BarChartGeometry.ViewWidth);
        Assert.True(BarChartGeometry.BarX(0, 1) >= 0);
    }

    [Fact]
    public void Today_is_the_only_bar_drawn_solid()
    {
        Assert.Equal("bar-chart-bar", BarChartGeometry.BarClass(new BarChartPoint("7/1", 3)));
        Assert.Equal("bar-chart-bar is-highlighted",
            BarChartGeometry.BarClass(new BarChartPoint("7/2", 3, IsHighlighted: true)));
    }

    [Fact]
    public void The_tooltip_prefers_a_human_reading_over_the_raw_number()
    {
        var wakeUp = new BarChartPoint("7/2", 6.5, Display: "6:30");

        Assert.Equal("6:30", BarChartGeometry.Describe(wakeUp, "時"));
    }

    [Fact]
    public void The_tooltip_falls_back_to_the_value_and_unit()
    {
        Assert.Equal("12回", BarChartGeometry.Describe(new BarChartPoint("7/2", 12), "回"));
    }

    /// <summary>
    /// SVG coordinates must use a dot decimal separator whatever culture the request runs
    /// under - a de-DE thread emitting "1,5" would silently drop the bar in the browser.
    /// </summary>
    [Fact]
    public void Coordinates_are_written_with_a_dot_decimal_separator()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal("1.5", BarChartGeometry.F(1.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Max_reads_the_busiest_point()
    {
        BarChartPoint[] points =
        [
            new("7/1", 3),
            new("7/2", 11),
            new("7/3", 0)
        ];

        Assert.Equal(11, BarChartGeometry.Max(points));
        Assert.Equal(0, BarChartGeometry.Max([]));
    }

    // ---- Trend line ---------------------------------------------------------

    [Fact]
    public void The_trend_line_threads_the_top_of_every_bar()
    {
        BarChartPoint[] points = [new("7/1", 2), new("7/2", 6), new("7/3", 4)];

        var line = BarChartGeometry.LinePoints(points, max: 6);
        var pairs = line.Split(' ');

        Assert.Equal(3, pairs.Length);
        for (var i = 0; i < points.Length; i++)
        {
            var parts = pairs[i].Split(',');
            Assert.Equal(BarChartGeometry.CenterX(i, points.Length), double.Parse(parts[0], CultureInfo.InvariantCulture), 2);
            Assert.Equal(BarChartGeometry.BarTop(points[i].Value, 6), double.Parse(parts[1], CultureInfo.InvariantCulture), 2);
        }
    }

    [Fact]
    public void The_trend_line_sits_over_the_middle_of_its_bar()
    {
        const int count = 14;
        var centre = BarChartGeometry.CenterX(3, count);
        var left = BarChartGeometry.BarX(3, count);

        Assert.Equal(left + (BarChartGeometry.BarWidth(count) / 2), centre, 3);
    }

    [Fact]
    public void The_filled_area_closes_back_down_to_the_baseline()
    {
        BarChartPoint[] points = [new("7/1", 2), new("7/2", 6)];

        var path = BarChartGeometry.AreaPath(points, max: 6);

        Assert.StartsWith("M", path);
        Assert.EndsWith("Z", path);
        // Both ends must return to the axis or the wash bleeds over the whole card.
        Assert.Contains($"{BarChartGeometry.F(BarChartGeometry.PlotBottom)}", path);
    }

    [Fact]
    public void An_empty_series_produces_no_line_and_no_area()
    {
        Assert.Equal(string.Empty, BarChartGeometry.LinePoints([], max: 0));
        Assert.Equal(string.Empty, BarChartGeometry.AreaPath([], max: 0));
    }

    [Fact]
    public void A_single_day_still_draws_a_point_on_the_line()
    {
        // A household that signed up yesterday: one bar, and nothing may divide by zero.
        BarChartPoint[] points = [new("7/1", 3, IsHighlighted: true)];

        var line = BarChartGeometry.LinePoints(points, max: 3);

        Assert.Single(line.Split(' '));
        Assert.DoesNotContain("NaN", line);
        Assert.DoesNotContain("NaN", BarChartGeometry.AreaPath(points, max: 3));
    }

    [Fact]
    public void An_all_zero_fortnight_still_draws_a_flat_line_rather_than_nothing()
    {
        BarChartPoint[] points = [new("7/1", 0), new("7/2", 0), new("7/3", 0)];

        var line = BarChartGeometry.LinePoints(points, max: 0);

        Assert.DoesNotContain("NaN", line);
        Assert.Equal(3, line.Split(' ').Length);
    }

    [Fact]
    public void The_dash_length_covers_the_whole_line()
    {
        // Used for the draw-on animation: too short and the line would stay visibly cut off.
        var count = 14;
        var longestPossible = count * BarChartGeometry.ViewHeight;

        Assert.True(BarChartGeometry.LineDashLength(count) >= longestPossible);
        Assert.True(BarChartGeometry.LineDashLength(0) > 0);
    }

    [Fact]
    public void Line_coordinates_are_written_with_a_dot_decimal_separator()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var line = BarChartGeometry.LinePoints([new("7/1", 1), new("7/2", 3)], max: 3);

            // "10,5,20,3" - a comma decimal separator would corrupt the x,y pairs themselves.
            foreach (var pair in line.Split(' '))
            {
                Assert.Equal(2, pair.Split(',').Length);
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_top_of_the_scale_is_stated_so_a_lone_bar_can_be_read()
    {
        // A household on its first day has one bar, and a bar drawn against itself always
        // reaches full height. Without the scale, one visit and twenty look identical.
        var top = BarChartGeometry.ScaleTop([new("8/12", 1)], "回");

        Assert.Equal("1回", top);
    }

    [Fact]
    public void The_top_of_the_scale_borrows_the_busiest_days_own_wording()
    {
        // 8.25 hours must read as a clock time, not as a decimal.
        var top = BarChartGeometry.ScaleTop(
            [new("8/11", 6.5, Display: "6:30"), new("8/12", 8.25, Display: "8:15")], "");

        Assert.Equal("8:15", top);
    }

    [Fact]
    public void The_top_of_the_scale_stays_at_zero_when_nothing_happened()
    {
        Assert.Equal("0回", BarChartGeometry.ScaleTop([new("8/12", 0)], "回"));
        Assert.Equal(string.Empty, BarChartGeometry.ScaleTop([], "回"));
    }

    [Fact]
    public void A_single_day_still_produces_a_drawable_bar_line_and_area()
    {
        // One day of data is drawn as one bar rather than hidden behind an explanation.
        List<BarChartPoint> single = [new("8/12", 2, IsHighlighted: true)];
        var max = BarChartGeometry.Max(single);

        Assert.True(BarChartGeometry.BarWidth(1) > 0);
        Assert.True(BarChartGeometry.BarHeight(2, max) > BarChartGeometry.MinBarHeight);
        Assert.NotEmpty(BarChartGeometry.LinePoints(single, max));
        Assert.StartsWith("M", BarChartGeometry.AreaPath(single, max));
    }
}
