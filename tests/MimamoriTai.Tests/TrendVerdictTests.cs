using MimamoriTai.Web.Charts;

namespace MimamoriTai.Tests;

/// <summary>
/// The badge beside each chart is the sentence a family reads instead of comparing bars by
/// eye, so it must be hard to mislead: no verdict without a baseline, and the concerning
/// direction has to be the one that gets flagged.
/// </summary>
public class TrendVerdictTests
{
    [Fact]
    public void A_typical_day_reads_as_usual()
    {
        var verdict = TrendVerdicts.ForUsage(8, [7, 8, 9, 8]);

        Assert.NotNull(verdict);
        Assert.Equal(TrendVerdicts.Usual, verdict.Text);
        Assert.Equal(TrendVerdicts.Good, verdict.Class);
    }

    [Fact]
    public void A_much_quieter_day_is_flagged_for_attention()
    {
        // Half the usual appliance use is the "did something happen?" case.
        var verdict = TrendVerdicts.ForUsage(2, [8, 8, 9, 7]);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより少ない", verdict.Text);
        Assert.Equal(TrendVerdicts.Attention, verdict.Class);
    }

    [Fact]
    public void A_busy_day_is_reported_without_alarming_anyone()
    {
        var verdict = TrendVerdicts.ForUsage(20, [8, 8, 9, 7]);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより多い", verdict.Text);
        Assert.Equal(TrendVerdicts.Neutral, verdict.Class);
    }

    [Fact]
    public void No_verdict_before_there_is_a_usual_to_compare_against()
    {
        // A brand-new household must not be told today is unusual on two days of data.
        Assert.Null(TrendVerdicts.ForUsage(5, [4, 6]));
        Assert.Null(TrendVerdicts.ForUsage(5, []));
        Assert.Null(TrendVerdicts.ForNight(1, [0, 0]));
        Assert.Null(TrendVerdicts.ForWakeUp(7, [6.5, 7]));
    }

    [Fact]
    public void No_verdict_when_today_has_nothing_recorded_yet()
    {
        Assert.Null(TrendVerdicts.ForUsage(null, [8, 8, 9]));
        Assert.Null(TrendVerdicts.ForWakeUp(null, [6, 6.5, 7]));
    }

    [Fact]
    public void Night_movement_appearing_for_the_first_time_is_flagged()
    {
        // The baseline is zero, so a ratio cannot express this - and it is exactly the
        // change the family installed the service to hear about.
        var verdict = TrendVerdicts.ForNight(2, [0, 0, 0, 0]);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより多い", verdict.Text);
        Assert.Equal(TrendVerdicts.Attention, verdict.Class);
    }

    [Fact]
    public void A_quiet_night_after_quiet_nights_is_reassuring()
    {
        var verdict = TrendVerdicts.ForNight(0, [0, 0, 0]);

        Assert.NotNull(verdict);
        Assert.Equal(TrendVerdicts.Good, verdict.Class);
    }

    [Fact]
    public void More_night_movement_than_usual_is_the_flagged_direction()
    {
        var verdict = TrendVerdicts.ForNight(6, [2, 2, 3, 2]);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより多い", verdict.Text);
        Assert.Equal(TrendVerdicts.Attention, verdict.Class);
    }

    [Fact]
    public void Fewer_night_disturbances_is_good_news()
    {
        var verdict = TrendVerdicts.ForNight(0, [4, 4, 5, 4]);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより少ない", verdict.Text);
        Assert.Equal(TrendVerdicts.Good, verdict.Class);
    }

    [Fact]
    public void Getting_up_much_later_than_usual_is_flagged()
    {
        var verdict = TrendVerdicts.ForWakeUp(9.5, [6.5, 6.0, 7.0, 6.5]);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより遅い", verdict.Text);
        Assert.Equal(TrendVerdicts.Attention, verdict.Class);
    }

    [Fact]
    public void Getting_up_early_is_reported_but_not_treated_as_a_problem()
    {
        var verdict = TrendVerdicts.ForWakeUp(4.0, [6.5, 6.0, 7.0, 6.5]);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより早い", verdict.Text);
        Assert.Equal(TrendVerdicts.Neutral, verdict.Class);
    }

    [Fact]
    public void Half_an_hour_either_way_is_still_usual()
    {
        // Nobody gets up at the same minute every day; flagging that would train the
        // family to ignore the badge.
        Assert.Equal(TrendVerdicts.Usual, TrendVerdicts.ForWakeUp(7.0, [6.5, 6.5, 6.5])?.Text);
        Assert.Equal(TrendVerdicts.Usual, TrendVerdicts.ForWakeUp(6.0, [6.5, 6.5, 6.5])?.Text);
    }

    /// <summary>
    /// One extraordinary day - a hospital stay, grandchildren visiting - must not become
    /// the yardstick every later day is judged against.
    /// </summary>
    [Fact]
    public void One_extreme_day_does_not_move_the_baseline()
    {
        var verdict = TrendVerdicts.ForUsage(8, [8, 8, 9, 8, 80]);

        Assert.NotNull(verdict);
        Assert.Equal(TrendVerdicts.Usual, verdict.Text);
    }

    [Fact]
    public void The_median_takes_the_middle_of_an_even_run()
    {
        Assert.Equal(3, TrendVerdicts.Median([1, 2, 4, 8]));
        Assert.Equal(4, TrendVerdicts.Median([8, 4, 1]));
        Assert.Null(TrendVerdicts.Median([1, 2]));
    }

    [Fact]
    public void An_all_zero_history_cannot_produce_a_usage_verdict()
    {
        // Devices offline all fortnight: silence is not evidence that today is unusual.
        Assert.Null(TrendVerdicts.ForUsage(0, [0, 0, 0, 0]));
    }

    // ---- A day that is still in progress ------------------------------------

    /// <summary>
    /// The bug this guards: at 00:18 today's count is naturally zero, and the family was
    /// told "いつもより少ない" every single morning. A badge that cries wolf daily is worse
    /// than no badge, because the one morning it matters nobody will look.
    /// </summary>
    [Fact]
    public void A_quiet_morning_is_not_reported_as_a_quiet_day()
    {
        Assert.Null(TrendVerdicts.ForUsage(0, [8, 8, 9, 7], hoursIntoDay: 0.3));
        Assert.Null(TrendVerdicts.ForUsage(1, [8, 8, 9, 7], hoursIntoDay: 9));
    }

    [Fact]
    public void An_unusually_busy_morning_is_reported_straight_away()
    {
        // Already past a whole normal day before lunch: true whatever the hour.
        var verdict = TrendVerdicts.ForUsage(20, [8, 8, 9, 7], hoursIntoDay: 9);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより多い", verdict.Text);
    }

    [Fact]
    public void By_the_evening_a_quiet_day_is_worth_saying()
    {
        var verdict = TrendVerdicts.ForUsage(2, [8, 8, 9, 7], hoursIntoDay: 21);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより少ない", verdict.Text);
        Assert.Equal(TrendVerdicts.Attention, verdict.Class);
    }

    [Fact]
    public void A_calm_night_is_not_promised_before_the_night_has_happened()
    {
        Assert.Null(TrendVerdicts.ForNight(0, [0, 0, 0], hoursIntoDay: 8));
        Assert.Null(TrendVerdicts.ForNight(0, [3, 3, 4], hoursIntoDay: 8));
    }

    [Fact]
    public void Night_movement_is_flagged_the_moment_it_happens_whatever_the_hour()
    {
        // 02:00 wandering must not wait until evening to be surfaced.
        var verdict = TrendVerdicts.ForNight(3, [0, 0, 0], hoursIntoDay: 2);

        Assert.NotNull(verdict);
        Assert.Equal("いつもより多い", verdict.Text);
        Assert.Equal(TrendVerdicts.Attention, verdict.Class);
    }
}
