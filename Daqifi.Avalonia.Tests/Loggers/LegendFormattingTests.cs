using Daqifi.Desktop.Logger;
using OxyPlot;
using OxyPlot.Series;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Characterisation tests for the three small formatters behind the legend and the summary flyout:
/// <see cref="DeviceLegendGroup.FormatFrequency"/> (the sampling-rate line under each device),
/// <see cref="SummaryLogger.FormatStatuses"/> (the status-code cell) and
/// <see cref="LoggedSeriesLegendItem.TruncatedSerialNo"/> (the shortened serial on each legend row).
///
/// <para>
/// None had coverage, and all are pure string rendering — the kind of code where a refactor
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

    #region LoggedSeriesLegendItem

    /// <summary>
    /// A legend item with nothing behind it but the plumbing its constructor insists on. The
    /// optional <c>databaseLogger</c> is left null on purpose: it is the only argument whose
    /// absence keeps <see cref="LoggedSeriesLegendItem.IsVisible"/>'s dispatcher path out of
    /// reach, and this project constructs no Avalonia application to pump one (see the comment at
    /// the top of Daqifi.Avalonia.Tests.csproj).
    /// </summary>
    private static LoggedSeriesLegendItem LegendItem(string deviceSerialNo, string channelName = "AI0") =>
        new(
            displayName: $"{channelName} ({deviceSerialNo})",
            channelName: channelName,
            deviceSerialNo: deviceSerialNo,
            seriesColor: OxyColors.Red,
            isVisible: true,
            actualSeries: new LineSeries(),
            plotModel: new PlotModel());

    [Theory]
    // The example from the property's own doc comment.
    [InlineData("1234104", "...4104")]
    // Five characters is the shortest serial that truncates at all.
    [InlineData("41041", "...1041")]
    [InlineData("DAQiFi-Nq1-0004104", "...4104")]
    public void A_serial_longer_than_four_characters_shows_only_its_last_four(string serial, string expected)
    {
        Assert.Equal(expected, LegendItem(serial).TruncatedSerialNo);
    }

    [Theory]
    // Exactly four is NOT truncated — the test that pins the boundary, since ">" rather than
    // ">=" is the difference between "4104" and a leading ellipsis on every short serial.
    [InlineData("4104")]
    [InlineData("104")]
    [InlineData("4")]
    [InlineData("")]
    public void A_serial_of_four_characters_or_fewer_is_shown_whole(string serial)
    {
        Assert.Equal(serial, LegendItem(serial).TruncatedSerialNo);
    }

    /// <summary>
    /// The parameter is declared non-nullable, but the property guards null twice over
    /// (<c>?.Length</c> and <c>?? string.Empty</c>) and a legend row is built from database rows
    /// that predate that guarantee. Pinned so the guards cannot be dropped as dead code: without
    /// them this is a NullReferenceException on the legend, not an empty cell.
    /// </summary>
    [Fact]
    public void A_missing_serial_renders_as_an_empty_string()
    {
        Assert.Equal(string.Empty, LegendItem(null!).TruncatedSerialNo);
    }

    /// <summary>
    /// The generated <c>[ObservableProperty]</c> getters hand back the very instances the
    /// constructor stored in their backing fields — no copy, no trimming, no normalisation. That
    /// is what makes reading the property interchangeable with reading the field, which is the
    /// substitution <see cref="LoggedSeriesLegendItem.IsVisible"/>'s minimap-sync call relies on:
    /// it forwards these two values to
    /// <see cref="DatabaseLogger.SetMinimapSeriesVisibility"/>, where they are matched against
    /// series keys by exact string equality.
    /// </summary>
    [Fact]
    public void The_generated_properties_hand_back_exactly_what_the_constructor_stored()
    {
        var serial = new string("1234104".ToCharArray());
        var channel = new string("AI3".ToCharArray());

        var item = LegendItem(serial, channel);

        Assert.Same(serial, item.DeviceSerialNo);
        Assert.Same(channel, item.ChannelName);
    }

    #endregion
}
