using MimamoriTai.Web.Charts;

namespace MimamoriTai.Tests;

public class SparkLineGeometryTests
{
    private static SparkPoint At(int minutes, double value) =>
        new(new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.FromHours(9)).AddMinutes(minutes), value);

    [Fact]
    public void Range_pads_so_the_extremes_are_not_glued_to_the_edges()
    {
        var (min, max) = SparkLineGeometry.Range([At(0, 100), At(5, 110)]);

        Assert.True(min < 100);
        Assert.True(max > 110);
    }

    [Fact]
    public void Range_gives_a_flat_series_a_band_instead_of_collapsing_it()
    {
        var (min, max) = SparkLineGeometry.Range([At(0, 104), At(5, 104), At(10, 104)]);

        Assert.True(max - min > 0);
        Assert.True(min < 104 && max > 104);
    }

    [Fact]
    public void Range_never_labels_the_floor_negative_when_nothing_measured_was()
    {
        // An idle plug beside a kettle: 10% of that spread is far below zero.
        var (min, _) = SparkLineGeometry.Range([At(0, 0.3), At(5, 1220)]);

        Assert.Equal(0, min);
    }

    [Fact]
    public void Range_keeps_the_floor_off_zero_when_the_readings_sit_well_above_it()
    {
        // Mains voltage: the whole point is to see the wobble, not the distance to zero.
        var (min, _) = SparkLineGeometry.Range([At(0, 102.1), At(5, 104.7)]);

        Assert.InRange(min, 101, 102.1);
    }

    [Fact]
    public void A_flat_series_is_drawn_mid_height_rather_than_on_the_axis()
    {
        var points = new[] { At(0, 104), At(5, 104) };
        var (min, max) = SparkLineGeometry.Range(points);

        var y = SparkLineGeometry.Y(104, min, max);

        Assert.InRange(y, 10, 18);
    }

    [Fact]
    public void Y_puts_the_larger_value_higher_up_the_chart()
    {
        var (min, max) = SparkLineGeometry.Range([At(0, 0.3), At(5, 40)]);

        Assert.True(SparkLineGeometry.Y(40, min, max) < SparkLineGeometry.Y(0.3, min, max));
    }

    [Fact]
    public void X_places_a_sample_by_its_timestamp_not_its_position()
    {
        var from = At(0, 0).At;
        var to = At(60, 0).At;

        // Three quarters of the way through the window, whatever index it happens to be.
        Assert.Equal(75, SparkLineGeometry.X(At(45, 0).At, from, to), 3);
    }

    [Fact]
    public void Samples_polled_normally_form_one_unbroken_line()
    {
        var segments = SparkLineGeometry.Segments([At(0, 1), At(5, 2), At(10, 3)]);

        Assert.Single(segments);
        Assert.Equal(3, segments[0].Split(' ').Length);
    }

    [Fact]
    public void An_outage_breaks_the_line_rather_than_being_bridged()
    {
        // Five-minute cadence, then eight hours of silence, then cadence resumes.
        var segments = SparkLineGeometry.Segments(
            [At(0, 1), At(5, 2), At(485, 3), At(490, 4)]);

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(2, s.Split(' ').Length));
    }

    [Fact]
    public void A_sample_with_no_neighbour_is_reported_for_drawing_as_a_dot()
    {
        var alone = SparkLineGeometry.Isolated([At(0, 1), At(600, 2), At(1200, 3)]);

        Assert.Equal(3, alone.Count);
    }

    [Fact]
    public void Samples_that_join_a_line_are_not_also_drawn_as_dots()
    {
        var alone = SparkLineGeometry.Isolated([At(0, 1), At(5, 2), At(600, 3)]);

        Assert.Single(alone);
        Assert.Equal(3, alone[0].Value);
    }

    [Fact]
    public void Unordered_samples_are_sorted_before_being_drawn()
    {
        var segments = SparkLineGeometry.Segments([At(10, 3), At(0, 1), At(5, 2)]);

        Assert.Single(segments);
    }

    [Fact]
    public void No_samples_draws_nothing_rather_than_throwing()
    {
        Assert.Empty(SparkLineGeometry.Segments([]));
        Assert.Empty(SparkLineGeometry.Isolated([]));
    }

    [Fact]
    public void Coordinates_use_invariant_punctuation_so_the_svg_parses_under_any_culture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture where a decimal point is written as a comma would otherwise turn
            // "12.5,3.4" into "12,5,3,4" and silently corrupt every polyline.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var segment = SparkLineGeometry.Segments([At(0, 1.25), At(5, 2.5)])[0];

            Assert.Equal(2, segment.Split(' ').Length);
            Assert.All(segment.Split(' '), pair => Assert.Equal(2, pair.Split(',').Length));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
