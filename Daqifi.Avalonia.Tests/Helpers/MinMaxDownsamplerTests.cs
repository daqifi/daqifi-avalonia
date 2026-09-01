using Daqifi.Desktop.Helpers;
using OxyPlot;
using Xunit;

namespace Daqifi.Avalonia.Tests.Helpers;

/// <summary>
/// Characterisation tests for <see cref="MinMaxDownsampler"/>.
///
/// This class is the last thing between the samples on disk and the pixels a user reads a
/// measurement off. Every logged-data plot in the app goes through it — the main plot, the
/// minimap, and every zoom/pan recompute (seven call sites in
/// <c>Daqifi.Desktop.Loggers.DatabaseLogger</c>) — and it is pure arithmetic with no
/// observable failure mode: a bucket boundary that is off by one drops a sample, and the
/// only symptom is a spike that is no longer on the screen. Nothing logs, nothing throws.
///
/// These tests therefore pin what the code DOES today, quirks included, rather than what a
/// reader might argue it should do. Where the current answer is arguably odd — the
/// one-point range <see cref="MinMaxDownsampler.FindVisibleRange"/> returns for a window
/// that misses the data entirely, say — the test says so in a comment and asserts the
/// current answer anyway. That is the point: a later refactor has to change a test on
/// purpose instead of changing the plot by accident.
/// </summary>
public class MinMaxDownsamplerTests
{
    /// <summary>x = 0..count-1, y from <paramref name="y"/>.</summary>
    private static DataPoint[] Ramp(int count, Func<int, double> y) =>
        [.. Enumerable.Range(0, count).Select(i => new DataPoint(i, y(i)))];

    #region Guard clauses and empty inputs

    [Fact]
    public void Downsample_null_points_throws()
    {
        Assert.Throws<ArgumentNullException>(() => MinMaxDownsampler.Downsample(null!, 10));
        Assert.Throws<ArgumentNullException>(() => MinMaxDownsampler.Downsample(null!, 0, 0, 10));
    }

    [Fact]
    public void Downsample_empty_input_returns_empty()
    {
        Assert.Empty(MinMaxDownsampler.Downsample([], 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Downsample_non_positive_bucket_count_returns_empty(int bucketCount)
    {
        Assert.Empty(MinMaxDownsampler.Downsample(Ramp(1000, i => i), bucketCount));
    }

    [Fact]
    public void Downsample_range_rejects_a_negative_start_and_an_end_past_the_input()
    {
        var points = Ramp(10, i => i);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MinMaxDownsampler.Downsample(points, -1, 5, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MinMaxDownsampler.Downsample(points, 0, 11, 2));
    }

    [Theory]
    // An empty range and an inverted one are both "no points", not an error.
    [InlineData(5, 5)]
    [InlineData(7, 3)]
    public void Downsample_range_with_no_points_returns_empty(int startIndex, int endIndex)
    {
        Assert.Empty(MinMaxDownsampler.Downsample(Ramp(10, i => i), startIndex, endIndex, 2));
    }

    #endregion

    #region Small inputs pass straight through

    [Fact]
    public void Downsample_returns_the_input_verbatim_at_exactly_two_points_per_bucket()
    {
        // count == bucketCount * 2 is the boundary of the passthrough branch: still
        // verbatim. One point more and the bucketing path below takes over.
        var points = Ramp(6, i => i * i);

        var result = MinMaxDownsampler.Downsample(points, 3);

        Assert.Equal(points, result);
    }

    [Fact]
    public void Downsample_range_returns_the_sub_range_verbatim_when_it_is_small_enough()
    {
        var points = Ramp(100, i => i);

        var result = MinMaxDownsampler.Downsample(points, 40, 46, 3);

        Assert.Equal(points[40..46], result);
    }

    #endregion

    #region Bucketing

    [Fact]
    public void Downsample_never_emits_more_than_two_points_per_bucket()
    {
        var random = new Random(20260901);
        var points = Ramp(50_000, _ => random.NextDouble());

        var result = MinMaxDownsampler.Downsample(points, 500);

        Assert.True(result.Count <= 500 * 2, $"emitted {result.Count} points for 500 buckets");
    }

    [Fact]
    public void Downsample_output_is_ordered_by_x()
    {
        var random = new Random(20260902);
        var points = Ramp(50_000, _ => random.NextDouble());

        var result = MinMaxDownsampler.Downsample(points, 500);

        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].X <= result[i].X,
                $"x went backwards at index {i}: {result[i - 1].X} then {result[i].X}");
        }
    }

    [Fact]
    public void Downsample_keeps_an_isolated_spike()
    {
        // The whole reason this class exists rather than a plain stride-N decimation: a
        // single-sample excursion in a 100k-point session has to survive to the screen.
        // A naive "every Nth point" would lose it with probability 1 - 1/N.
        var points = Ramp(100_000, i => i == 54_321 ? 999.0 : 0.0);

        var result = MinMaxDownsampler.Downsample(points, 2_000);

        Assert.Contains(new DataPoint(54_321, 999.0), result);
        // And the trough either side of it is still the flat line, not an artefact.
        Assert.Equal(0.0, result.Min(p => p.Y));
    }

    [Fact]
    public void Downsample_emits_one_point_for_a_bucket_whose_values_never_move()
    {
        // min == max collapses to a single point, so a dead-flat channel costs one point
        // per bucket rather than two. 100 points over 10 buckets fills every bucket.
        var points = Ramp(100, _ => 3.5);

        var result = MinMaxDownsampler.Downsample(points, 10);

        Assert.Equal(10, result.Count);
        Assert.All(result, p => Assert.Equal(3.5, p.Y));
    }

    [Fact]
    public void Downsample_emits_the_extremes_at_their_own_x_lowest_first_when_it_comes_first()
    {
        // One bucket, four points: the min precedes the max in x.
        var points = new DataPoint[]
        {
            new(0, 0), new(1, -10), new(2, 10), new(3, 0),
        };

        var result = MinMaxDownsampler.Downsample(points, 1);

        // The extremes keep the x they were sampled at — not the bucket's centre or edge.
        Assert.Equal([new DataPoint(1, -10), new DataPoint(2, 10)], result);
    }

    [Fact]
    public void Downsample_emits_the_extremes_in_x_order_even_when_the_max_comes_first()
    {
        // Same bucket, max before min. The output is re-ordered so the line does not
        // double back on itself.
        var points = new DataPoint[]
        {
            new(0, 0), new(1, 10), new(2, -10), new(3, 0),
        };

        var result = MinMaxDownsampler.Downsample(points, 1);

        Assert.Equal([new DataPoint(1, 10), new DataPoint(2, -10)], result);
    }

    [Fact]
    public void Downsample_includes_the_final_point_in_the_last_bucket()
    {
        // The last bucket's upper edge is inclusive, deliberately: the final point sits
        // exactly on xMax, and a half-open test would drop it. Making the final point the
        // global maximum is what makes its loss visible.
        var points = Ramp(1_000, i => i == 999 ? 500.0 : 0.0);

        var result = MinMaxDownsampler.Downsample(points, 10);

        Assert.Contains(new DataPoint(999, 500.0), result);
    }

    [Fact]
    public void Downsample_skips_buckets_that_contain_no_points()
    {
        // Clustered data with one far outlier: nine of the ten buckets are empty, and an
        // empty bucket emits nothing at all rather than a placeholder.
        var points = new List<DataPoint>();
        for (var i = 0; i < 100; i++)
        {
            points.Add(new DataPoint(i * 0.01, i));
        }
        points.Add(new DataPoint(1_000, -1));

        var result = MinMaxDownsampler.Downsample(points, 10);

        // Bucket 0 holds the cluster (min and max), the last bucket holds the outlier
        // alone; the eight in between are empty and contribute nothing.
        Assert.Equal(3, result.Count);
        Assert.Equal(new DataPoint(1_000, -1), result[^1]);
    }

    #endregion

    #region Degenerate time ranges

    [Fact]
    public void Downsample_renders_identical_timestamps_as_a_vertical_segment()
    {
        // Every point shares one x, which a LineSeries would draw as nothing. The value
        // spread is returned as two points at that x instead — low then high.
        var points = Ramp(100, i => i % 2 == 0 ? -4.0 : 7.0)
            .Select(p => new DataPoint(42, p.Y))
            .ToArray();

        var result = MinMaxDownsampler.Downsample(points, 10);

        Assert.Equal([new DataPoint(42, -4), new DataPoint(42, 7)], result);
    }

    [Fact]
    public void Downsample_returns_a_single_point_when_neither_x_nor_y_moves()
    {
        var points = Ramp(100, _ => 1.25).Select(p => new DataPoint(42, p.Y)).ToArray();

        var result = MinMaxDownsampler.Downsample(points, 10);

        Assert.Equal([new DataPoint(42, 1.25)], result);
    }

    #endregion

    #region FindVisibleRange

    [Fact]
    public void FindVisibleRange_on_an_empty_list_is_an_empty_range()
    {
        Assert.Equal((0, 0), MinMaxDownsampler.FindVisibleRange([], 0, 100));
    }

    [Fact]
    public void FindVisibleRange_pads_one_point_beyond_each_edge_of_the_window()
    {
        // x = 0..9, window [3, 6]. The first point at or after 3 is index 3, backed up one
        // to 2; the first point after 6 is index 7, advanced one to 8. The padding is what
        // keeps the line entering and leaving the viewport instead of starting mid-air.
        var points = Ramp(10, i => i);

        Assert.Equal((2, 8), MinMaxDownsampler.FindVisibleRange(points, 3, 6));
    }

    [Fact]
    public void FindVisibleRange_clamps_the_padding_at_both_ends()
    {
        var points = Ramp(10, i => i);

        Assert.Equal((0, 10), MinMaxDownsampler.FindVisibleRange(points, 0, 9));
    }

    [Fact]
    public void FindVisibleRange_for_a_window_before_all_the_data_returns_the_first_point()
    {
        // Not an empty range: the trailing padding still advances the end by one, so the
        // caller gets a single point rather than nothing. Odd on its face, and current.
        var points = Ramp(10, i => i);

        Assert.Equal((0, 1), MinMaxDownsampler.FindVisibleRange(points, -5, -1));
    }

    [Fact]
    public void FindVisibleRange_for_a_window_after_all_the_data_returns_the_last_point()
    {
        // The mirror image, by the leading padding rather than the trailing one.
        var points = Ramp(10, i => i);

        Assert.Equal((9, 10), MinMaxDownsampler.FindVisibleRange(points, 15, 20));
    }

    [Fact]
    public void FindVisibleRange_spans_every_point_sharing_a_boundary_timestamp()
    {
        // Duplicate timestamps are real here — a device delivers a whole message's worth of
        // samples on one firmware tick. The lower bound seeks the FIRST point at the
        // window's start and the upper bound the LAST at its end, so a run of equal x is
        // taken whole rather than clipped in the middle.
        var points = new DataPoint[]
        {
            new(0, 0), new(5, 1), new(5, 2), new(5, 3), new(9, 4),
        };

        // First x >= 5 is index 1, backed up to 0. First x > 5 is index 4, advanced to 5.
        Assert.Equal((0, 5), MinMaxDownsampler.FindVisibleRange(points, 5, 5));
    }

    [Fact]
    public void FindVisibleRange_feeds_the_range_overload_a_window_that_still_covers_the_viewport()
    {
        // The pair as DatabaseLogger actually uses them: find the visible slice, then
        // downsample only that slice. The result has to reach past both edges of the
        // requested window, or the plotted line stops short of the viewport border.
        var points = Ramp(20_000, i => Math.Sin(i / 100.0));
        const double xMin = 4_000;
        const double xMax = 12_000;

        var (startIndex, endIndex) = MinMaxDownsampler.FindVisibleRange(points, xMin, xMax);
        var result = MinMaxDownsampler.Downsample(points, startIndex, endIndex, 500);

        Assert.NotEmpty(result);
        Assert.True(result[0].X <= xMin, $"first plotted x {result[0].X} is inside the window");
        Assert.True(result[^1].X >= xMax, $"last plotted x {result[^1].X} is inside the window");
        Assert.True(result.Count <= 500 * 2, $"emitted {result.Count} points for 500 buckets");
    }

    #endregion
}
