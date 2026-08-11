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
}
