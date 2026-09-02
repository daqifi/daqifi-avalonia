using Daqifi.Desktop.Logger;
using OxyPlot;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Characterisation tests for <c>PlotLogger.BuildPlotStatsSummary</c>, the diagnostic string the
/// live plot publishes once a second.
///
/// <para>
/// Its own doc comment already said it was "pure and side-effect-free so it can be unit-tested",
/// and nothing tested it. It is the harness's only window onto what the plot is actually holding
/// (issue #573), so a change that quietly shifts one field is invisible in the app and misleading
/// everywhere the string is read.
/// </para>
///
/// <para>
/// The min/max cases below deliberately use all-positive and all-negative data. A summariser that
/// starts its extremes at zero rather than at "nothing seen yet" passes on mixed-sign data and
/// fails on these.
/// </para>
/// </summary>
public class PlotStatsSummaryTests
{
    private static List<DataPoint> Points(params (double X, double Y)[] points) =>
        [.. points.Select(p => new DataPoint(p.X, p.Y))];

    [Fact]
    public void No_points_reports_every_measure_as_NaN()
    {
        Assert.Equal(
            "series=0;points=0;nonfinite=0;last=NaN;min=NaN;max=NaN;firstx=NaN;lastx=NaN",
            PlotLogger.BuildPlotStatsSummary(0, []));
    }

    [Fact]
    public void A_single_point_is_its_own_minimum_and_maximum()
    {
        Assert.Equal(
            "series=1;points=1;nonfinite=0;last=2.5;min=2.5;max=2.5;firstx=1;lastx=1",
            PlotLogger.BuildPlotStatsSummary(1, [Points((1, 2.5))]));
    }

    [Fact]
    public void Extremes_are_correct_when_every_value_is_positive()
    {
        // min must be 3, not 0: the summariser has seen no value below 3.
        Assert.Equal(
            "series=1;points=3;nonfinite=0;last=5;min=3;max=7;firstx=0;lastx=2",
            PlotLogger.BuildPlotStatsSummary(1, [Points((0, 3), (1, 7), (2, 5))]));
    }

    [Fact]
    public void Extremes_are_correct_when_every_value_is_negative()
    {
        // max must be -3, not 0.
        Assert.Equal(
            "series=1;points=2;nonfinite=0;last=-7;min=-7;max=-3;firstx=0;lastx=1",
            PlotLogger.BuildPlotStatsSummary(1, [Points((0, -3), (1, -7))]));
    }

    [Fact]
    public void Gap_markers_are_not_data()
    {
        // DataPoint.Undefined carries a NaN X. PlotLogger inserts it to break the line; it must not
        // count as a point, and must not be mistaken for a non-finite sample value.
        List<DataPoint> withGap = [new DataPoint(0, 1), DataPoint.Undefined, new DataPoint(1, 2)];

        Assert.Equal(
            "series=1;points=2;nonfinite=0;last=2;min=1;max=2;firstx=0;lastx=1",
            PlotLogger.BuildPlotStatsSummary(1, [withGap]));
    }

    [Fact]
    public void A_non_finite_value_is_counted_but_excluded_from_the_extremes()
    {
        Assert.Equal(
            "series=1;points=3;nonfinite=2;last=5;min=5;max=5;firstx=2;lastx=2",
            PlotLogger.BuildPlotStatsSummary(
                1, [Points((0, double.NaN), (1, double.PositiveInfinity), (2, 5))]));
    }

    [Fact]
    public void Points_whose_values_are_all_non_finite_leave_the_extremes_unset()
    {
        Assert.Equal(
            "series=1;points=2;nonfinite=2;last=NaN;min=NaN;max=NaN;firstx=NaN;lastx=NaN",
            PlotLogger.BuildPlotStatsSummary(
                1, [Points((0, double.NaN), (1, double.NegativeInfinity))]));
    }

    [Fact]
    public void Extremes_span_every_series()
    {
        Assert.Equal(
            "series=2;points=4;nonfinite=0;last=8;min=1;max=9;firstx=0;lastx=3",
            PlotLogger.BuildPlotStatsSummary(
                2, [Points((0, 4), (1, 9)), Points((2, 1), (3, 8))]));
    }

    [Fact]
    public void The_last_value_is_the_one_at_the_greatest_x_and_ties_go_to_the_later_series()
    {
        // The comparison is `point.X >= lastX`, so a second series sharing the greatest X wins.
        Assert.Equal(
            "series=2;points=2;nonfinite=0;last=2;min=1;max=2;firstx=5;lastx=5",
            PlotLogger.BuildPlotStatsSummary(2, [Points((5, 1)), Points((5, 2))]));
    }

    [Fact]
    public void The_first_x_is_the_smallest_seen_even_when_a_later_series_starts_earlier()
    {
        // firstx tracks the minimum X while lastx tracks the maximum, independently of the order
        // the series arrive in — so `last` here stays the value at x=9, not the last one read.
        Assert.Equal(
            "series=2;points=2;nonfinite=0;last=2;min=1;max=2;firstx=0;lastx=9",
            PlotLogger.BuildPlotStatsSummary(2, [Points((9, 2)), Points((0, 1))]));
    }
}
