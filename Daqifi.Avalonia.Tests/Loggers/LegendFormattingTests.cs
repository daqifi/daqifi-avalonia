using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Characterisation tests for the two small formatters behind the legend and the summary flyout:
/// <see cref="DeviceLegendGroup.FormatFrequency"/> (the sampling-rate line under each device) and
/// <see cref="SummaryLogger.FormatStatuses"/> (the status-code cell).
///
/// <para>
/// Neither had coverage, and both are pure string rendering — the kind of code where a refactor
/// that changes an output by one character is invisible until a user reads it off the screen. The
/// values below pin the current rendering exactly, including the cases that are arguably odd (a
/// frequency just under a megahertz rounds up within the kHz band to "1000 kHz" rather than
/// rolling over to "1 MHz").
/// </para>
/// </summary>
public class LegendFormattingTests
{
    #region FormatFrequency

    [Fact]
    public void An_unknown_frequency_renders_as_an_empty_string()
    {
        Assert.Equal(string.Empty, DeviceLegendGroup.FormatFrequency(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_non_positive_frequency_renders_as_an_empty_string(int hz)
    {
        Assert.Equal(string.Empty, DeviceLegendGroup.FormatFrequency(hz));
    }

    [Theory]
    // Below 1 kHz: the raw integer, no scaling.
    [InlineData(1, "1 Hz")]
    [InlineData(100, "100 Hz")]
    [InlineData(999, "999 Hz")]
    // Whole kilohertz drop the decimal point entirely.
    [InlineData(1_000, "1 kHz")]
    [InlineData(30_000, "30 kHz")]
    // Fractional kilohertz keep at most two decimals, with trailing zeros trimmed.
    [InlineData(1_500, "1.5 kHz")]
    [InlineData(1_234, "1.23 kHz")]
    // Just under a megahertz rounds within the kHz band rather than rolling over.
    [InlineData(999_999, "1000 kHz")]
    // Whole megahertz, same rules one band up.
    [InlineData(1_000_000, "1 MHz")]
    [InlineData(2_500_000, "2.5 MHz")]
    [InlineData(42_000_000, "42 MHz")]
    [InlineData(int.MaxValue, "2147.48 MHz")]
    public void A_frequency_renders_in_the_largest_unit_that_fits(int hz, string expected)
    {
        Assert.Equal(expected, DeviceLegendGroup.FormatFrequency(hz));
    }

    #endregion

    #region FormatStatuses

    [Fact]
    public void No_statuses_render_as_a_dash()
    {
        Assert.Equal("-", SummaryLogger.FormatStatuses([]));
    }

    [Fact]
    public void A_single_status_renders_as_its_own_number()
    {
        Assert.Equal("7", SummaryLogger.FormatStatuses([7]));
    }

    [Fact]
    public void Several_statuses_are_joined_with_a_comma_and_a_space()
    {
        var rendered = SummaryLogger.FormatStatuses([1, 2, 3]);

        // Asserted order-independently on purpose: the source is a HashSet, so its enumeration
        // order is an implementation detail. What this pins is the separator and the per-item
        // rendering, which is what a rewrite of the join could change.
        Assert.Equal(new[] { "1", "2", "3" }, rendered.Split(", ").Order().ToArray());
        Assert.DoesNotContain(",,", rendered);
    }

    #endregion
}
