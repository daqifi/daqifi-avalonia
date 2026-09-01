using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;
// Both assemblies declare a DataSample. This file always means the app's EF entity, which is
// what LoggingSessionSampleSource's in-memory constructor takes.
using DataSample = Daqifi.Desktop.Channel.DataSample;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins the one rule this app orders channel names by.
///
/// <para>
/// Channel names are a prefix plus an index — <c>AI0</c>…<c>AI15</c>, <c>DIO0</c>…, <c>DI0</c>… —
/// and byte-wise ordering puts <c>AI10</c> and <c>AI11</c> between <c>AI1</c> and <c>AI2</c>. On a
/// board with fewer than ten channels that is invisible; the moment one has ten or more it is
/// wrong everywhere the order is visible, and in an exported CSV it is wrong *silently*: every
/// column holds correct values, they are just not in the columns a reader expects.
/// </para>
///
/// <para>
/// These tests exist because the app stated that rule three separate times — <c>NaturalSortHelper</c>
/// for the panes and the plot, <see cref="StringComparer.Ordinal"/> on the exporter's database
/// path, and no comparer at all on its in-memory path — and only the first of the three was right.
/// They assert the ORDER, never which comparer produced it, so they went on holding when the three
/// were collapsed onto the one, and will go on holding when that one is eventually replaced by
/// Daqifi.Core's <c>ChannelNameComparer</c> (not in Core 1.7.0, the pinned version).
/// </para>
/// </summary>
public class ChannelOrderingTests
{
    private const string DeviceName = "Nq1";
    private const string Serial = "SERIAL-A";

    /// <summary>
    /// Ten or more channels of one prefix — the smallest input on which byte-wise and numeric
    /// ordering disagree, and the shape a 16-channel Nq1 actually presents.
    /// </summary>
    private static readonly string[] SixteenAnalogInputs =
        [.. Enumerable.Range(0, 16).Select(i => $"AI{i}")];

    private static DataSample Sample(string channelName, string device = DeviceName, string serial = Serial) =>
        new()
        {
            DeviceName = device,
            DeviceSerialNo = serial,
            ChannelName = channelName,
            Type = ChannelType.Analog,
            Color = "#FF000000",
        };

    private static LoggingSessionSampleSource InMemorySource(params DataSample[] samples) =>
        new(new LoggingSession { ID = 1 }, samples);

    #region The exporter's channel (i.e. CSV column) order

    /// <summary>
    /// The case the whole file is about: <c>AI2</c> must precede <c>AI10</c> in the exported
    /// column order. Both of <see cref="LoggingSessionSampleSource"/>'s paths feed
    /// <c>CsvExporter</c>, so both have to agree with Core's documented column order.
    /// </summary>
    [Fact]
    public void GetChannels_orders_a_two_digit_channel_index_numerically()
    {
        // Shuffled into the order a byte-wise sort would produce, so a test that passes cannot
        // be passing because the input happened to arrive sorted.
        var source = InMemorySource([.. new[] { "AI0", "AI1", "AI10", "AI11", "AI2", "AI9" }.Select(n => Sample(n))]);

        Assert.Equal(
            ["AI0", "AI1", "AI2", "AI9", "AI10", "AI11"],
            source.GetChannels().Select(c => c.ChannelName));
    }

    [Fact]
    public void GetChannels_orders_a_full_sixteen_channel_board()
    {
        var source = InMemorySource([.. SixteenAnalogInputs.Reverse().Select(n => Sample(n))]);

        Assert.Equal(SixteenAnalogInputs, source.GetChannels().Select(c => c.ChannelName));
    }

    /// <summary>
    /// Device name, then serial number, then channel name — Core's stated column order. Two
    /// boards both expose <c>AI0</c>, so the channel name alone cannot order the set.
    /// </summary>
    [Fact]
    public void GetChannels_orders_by_device_then_serial_then_channel()
    {
        var source = InMemorySource(
            Sample("AI10", device: "Nq2", serial: "SERIAL-B"),
            Sample("AI2", device: "Nq2", serial: "SERIAL-B"),
            Sample("AI10", device: "Nq1", serial: "SERIAL-B"),
            Sample("AI2", device: "Nq1", serial: "SERIAL-B"),
            Sample("AI10", device: "Nq1", serial: "SERIAL-A"),
            Sample("AI2", device: "Nq1", serial: "SERIAL-A"));

        Assert.Equal(
            [
                "Nq1:SERIAL-A:AI2", "Nq1:SERIAL-A:AI10",
                "Nq1:SERIAL-B:AI2", "Nq1:SERIAL-B:AI10",
                "Nq2:SERIAL-B:AI2", "Nq2:SERIAL-B:AI10",
            ],
            source.GetChannels().Select(c => c.Key));
    }

    /// <summary>
    /// Digital and analog channels sort by their prefix, so a board's DIO block stays contiguous
    /// and follows its AI block rather than interleaving with it.
    /// </summary>
    [Fact]
    public void GetChannels_keeps_each_prefix_contiguous()
    {
        var source = InMemorySource(
            [.. new[] { "DIO10", "AI10", "DIO2", "AI2", "DI10", "DI2" }.Select(n => Sample(n))]);

        Assert.Equal(
            ["AI2", "AI10", "DI2", "DI10", "DIO2", "DIO10"],
            source.GetChannels().Select(c => c.ChannelName));
    }

    /// <summary>
    /// The device name and serial number are compared ORDINALLY, not by the running machine's
    /// culture. <c>CsvExporter</c> writes columns in the order this method returns, and the
    /// database path orders inside SQLite under BINARY collation — so a culture-sensitive
    /// comparison here would make a session's exported bytes depend on the exporting machine's
    /// locale, and would make the two paths disagree with each other.
    ///
    /// <para>
    /// <c>"a"</c> vs <c>"B"</c> is the discriminating pair: ordinal puts uppercase first
    /// (<c>'B'</c> is 0x42, <c>'a'</c> is 0x61), every common culture puts <c>a</c> first.
    /// </para>
    /// </summary>
    [Fact]
    public void GetChannels_compares_device_identity_ordinally_not_by_culture()
    {
        var source = InMemorySource(
            Sample("AI0", device: "aardvark", serial: Serial),
            Sample("AI0", device: "Bison", serial: Serial));

        Assert.Equal(["Bison", "aardvark"], source.GetChannels().Select(c => c.DeviceName));
    }

    [Fact]
    public void GetChannels_collapses_repeated_samples_to_one_channel_each()
    {
        var source = InMemorySource(Sample("AI0"), Sample("AI0"), Sample("AI1"));

        Assert.Equal(["AI0", "AI1"], source.GetChannels().Select(c => c.ChannelName));
    }

    #endregion

    #region The plot/legend channel order

    /// <summary>
    /// <c>SessionDataRepository.DeduplicateChannelInfo</c> orders the series and legend entries a
    /// reopened session is plotted with. It reaches the same rule as the exporter and must give
    /// the same answer — a session whose legend and whose CSV disagree about which channel is
    /// second is worse than either order on its own.
    /// </summary>
    [Fact]
    public void DeduplicateChannelInfo_orders_a_two_digit_channel_index_numerically()
    {
        var rows = new[] { "AI0", "AI1", "AI10", "AI11", "AI2", "AI9" }
            .Select(n => new SessionChannelInfo(n, Serial, ChannelType.Analog, "#FF000000"));

        Assert.Equal(
            ["AI0", "AI1", "AI2", "AI9", "AI10", "AI11"],
            SessionDataRepository.DeduplicateChannelInfo(rows).Select(r => r.ChannelName));
    }

    [Fact]
    public void DeduplicateChannelInfo_orders_a_full_sixteen_channel_board()
    {
        var rows = SixteenAnalogInputs.Reverse()
            .Select(n => new SessionChannelInfo(n, Serial, ChannelType.Analog, "#FF000000"));

        Assert.Equal(
            SixteenAnalogInputs,
            SessionDataRepository.DeduplicateChannelInfo(rows).Select(r => r.ChannelName));
    }

    /// <summary>
    /// Two boards each expose <c>AI0</c>. Dedup is keyed on (serial, name), so both survive —
    /// dropping one would silently delete a channel's series from a two-board session's plot.
    /// </summary>
    [Fact]
    public void DeduplicateChannelInfo_keeps_the_same_channel_name_from_two_devices()
    {
        var rows = new[]
        {
            new SessionChannelInfo("AI0", "SERIAL-B", ChannelType.Analog, "#FF000000"),
            new SessionChannelInfo("AI0", "SERIAL-A", ChannelType.Analog, "#FF00FF00"),
            new SessionChannelInfo("AI0", "SERIAL-A", ChannelType.Analog, "#FF0000FF"),
        };

        var result = SessionDataRepository.DeduplicateChannelInfo(rows);

        Assert.Equal(2, result.Count);
        Assert.Equal(["SERIAL-B", "SERIAL-A"], result.Select(r => r.DeviceSerialNo));
        // First occurrence wins: the duplicate SERIAL-A row's colour must not replace it, or a
        // channel's plotted colour would depend on how many duplicate rows a session happened to
        // contain.
        Assert.Equal("#FF00FF00", result.Single(r => r.DeviceSerialNo == "SERIAL-A").Color);
    }

    [Fact]
    public void DeduplicateChannelInfo_keeps_each_prefix_contiguous()
    {
        var rows = new[] { "DIO10", "AI10", "DIO2", "AI2", "DI10", "DI2" }
            .Select(n => new SessionChannelInfo(n, Serial, ChannelType.Analog, "#FF000000"));

        Assert.Equal(
            ["AI2", "AI10", "DI2", "DI10", "DIO2", "DIO10"],
            SessionDataRepository.DeduplicateChannelInfo(rows).Select(r => r.ChannelName));
    }

    #endregion

    #region The two orders agree

    /// <summary>
    /// The property that motivated collapsing the three rules into one: for any channel set, the
    /// exporter's column order and the plot's series order are the same sequence. Asserted
    /// directly rather than left to be inferred from the two blocks above, so a future change to
    /// either one alone fails here.
    /// </summary>
    [Fact]
    public void The_exporter_and_the_plot_order_the_same_channel_set_identically()
    {
        string[] names = ["DIO2", "AI10", "AI2", "DIO10", "AI0", "DI3"];

        var exported = InMemorySource([.. names.Select(n => Sample(n))])
            .GetChannels()
            .Select(c => c.ChannelName);

        var plotted = SessionDataRepository
            .DeduplicateChannelInfo(names.Select(n => new SessionChannelInfo(n, Serial, ChannelType.Analog, "#FF000000")))
            .Select(r => r.ChannelName);

        Assert.Equal(plotted, exported);
    }

    #endregion
}
