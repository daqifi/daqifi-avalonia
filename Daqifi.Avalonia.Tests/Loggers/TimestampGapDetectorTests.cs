using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Characterisation tests for <see cref="TimestampGapDetector"/> — the class that decides whether
/// the live plot breaks its line between two samples.
///
/// <para>
/// It runs on the streaming hot path: <c>PlotLogger.Log</c> calls <see cref="TimestampGapDetector.IsGap"/>
/// once per sample per channel. It had no coverage at all, and its failure modes are both silent —
/// too eager and the plot is chopped into fragments on a healthy device; too lax and real data loss
/// renders as a straight line through the missing samples.
/// </para>
///
/// <para>
/// These pin what the detector DOES today, arithmetic included. The EMA sequence in
/// <see cref="IsGap_pins_the_exact_ema_and_threshold_arithmetic"/> is chosen so that every step
/// lands on a specific side of the threshold: a change to <see cref="TimestampGapDetector.EmaAlpha"/>
/// or <see cref="TimestampGapDetector.GapThresholdMultiplier"/>, or to the order in which the
/// average is updated, moves at least one of its assertions.
/// </para>
/// </summary>
public class TimestampGapDetectorTests
{
    private static readonly (string deviceSerial, string channelName) ChannelA = ("SERIAL-A", "AI0");
    private static readonly (string deviceSerial, string channelName) ChannelB = ("SERIAL-B", "AI0");

    #region No usable delta

    [Fact]
    public void IsGap_is_false_when_the_firmware_delta_is_absent()
    {
        var detector = new TimestampGapDetector();

        // Null is what ProcessStreamMessage passes for the first message of a session: there is no
        // previous frame to measure against, so no gap is knowable.
        Assert.False(detector.IsGap(ChannelA, null));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void IsGap_is_false_for_a_non_positive_delta(double delta)
    {
        var detector = new TimestampGapDetector();

        // `is not > 0` rejects NaN as well as zero and negatives, because NaN fails every comparison.
        Assert.False(detector.IsGap(ChannelA, delta));
    }

    [Fact]
    public void A_rejected_delta_does_not_seed_the_average()
    {
        var detector = new TimestampGapDetector();

        Assert.False(detector.IsGap(ChannelA, null));
        Assert.False(detector.IsGap(ChannelA, 0.0));

        // Still unseeded, so this first positive delta is the seed and cannot be a gap however
        // large it is.
        Assert.False(detector.IsGap(ChannelA, 100_000.0));
    }

    #endregion

    #region Seeding

    [Fact]
    public void The_first_positive_delta_only_seeds_and_is_never_a_gap()
    {
        var detector = new TimestampGapDetector();

        Assert.False(detector.IsGap(ChannelA, 5_000.0));
    }

    [Fact]
    public void A_steady_stream_never_reports_a_gap()
    {
        var detector = new TimestampGapDetector();

        for (var i = 0; i < 50; i++)
        {
            Assert.False(detector.IsGap(ChannelA, 10.0));
        }
    }

    #endregion

    #region Threshold

    [Fact]
    public void A_delta_at_exactly_the_threshold_is_not_a_gap()
    {
        var detector = new TimestampGapDetector();

        detector.IsGap(ChannelA, 100.0);

        // The comparison is strict (`delta > multiplier * avg`), so 2x exactly stays under it.
        Assert.False(detector.IsGap(ChannelA, 200.0));
    }

    [Fact]
    public void A_delta_past_the_threshold_is_a_gap()
    {
        var detector = new TimestampGapDetector();

        detector.IsGap(ChannelA, 100.0);

        Assert.True(detector.IsGap(ChannelA, 201.0));
    }

    [Fact]
    public void IsGap_pins_the_exact_ema_and_threshold_arithmetic()
    {
        var detector = new TimestampGapDetector();

        // Seed: avg = 100.
        Assert.False(detector.IsGap(ChannelA, 100.0));

        // 100 <= 2*100, so no gap; avg = 0.9*100 + 0.1*100 = 100.
        Assert.False(detector.IsGap(ChannelA, 100.0));

        // 200 is exactly 2*100 and the test is strict, so no gap; avg = 0.9*100 + 0.1*200 = 110.
        Assert.False(detector.IsGap(ChannelA, 200.0));

        // 220 is exactly 2*110, so still no gap; avg = 0.9*110 + 0.1*220 = 121.
        Assert.False(detector.IsGap(ChannelA, 220.0));

        // 243 > 2*121 = 242. Gap.
        Assert.True(detector.IsGap(ChannelA, 243.0));
    }

    #endregion

    #region Reset after a gap

    /// <summary>
    /// The load-bearing invariant. A detected gap drops the channel's tracking entirely, so the
    /// very next sample is a fresh seed and cannot itself be a gap — that is what stops one outage
    /// from either desensitising the channel (if the huge delta were folded into the average) or
    /// producing a run of gap markers.
    /// </summary>
    [Fact]
    public void A_gap_clears_the_channel_so_the_next_delta_re_seeds()
    {
        var detector = new TimestampGapDetector();

        detector.IsGap(ChannelA, 100.0);
        Assert.True(detector.IsGap(ChannelA, 1_000.0));

        // Re-seeding: this delta is 100x the pre-gap average and must still not report a gap.
        Assert.False(detector.IsGap(ChannelA, 10_000.0));

        // And the new baseline is that re-seed value, not the old one.
        Assert.False(detector.IsGap(ChannelA, 20_000.0));
        Assert.True(detector.IsGap(ChannelA, 100_000.0));
    }

    #endregion

    #region Per-channel isolation

    [Fact]
    public void Channels_are_tracked_independently()
    {
        var detector = new TimestampGapDetector();

        detector.IsGap(ChannelA, 100.0);
        detector.IsGap(ChannelB, 10.0);

        // 150 is well under A's threshold but 15x B's average.
        Assert.False(detector.IsGap(ChannelA, 150.0));
        Assert.True(detector.IsGap(ChannelB, 150.0));
    }

    [Fact]
    public void A_gap_on_one_channel_leaves_the_other_channels_untouched()
    {
        var detector = new TimestampGapDetector();

        detector.IsGap(ChannelA, 100.0);
        detector.IsGap(ChannelB, 100.0);

        Assert.True(detector.IsGap(ChannelA, 500.0));

        // B was never reset, so its average still stands and the same delta is still a gap there.
        Assert.True(detector.IsGap(ChannelB, 500.0));
    }

    #endregion

    #region Clear

    [Fact]
    public void Clear_drops_every_channel_so_the_next_delta_re_seeds()
    {
        var detector = new TimestampGapDetector();

        detector.IsGap(ChannelA, 100.0);
        detector.IsGap(ChannelB, 100.0);

        detector.Clear();

        Assert.False(detector.IsGap(ChannelA, 50_000.0));
        Assert.False(detector.IsGap(ChannelB, 50_000.0));
    }

    #endregion
}
