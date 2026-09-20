using Avalonia.Media;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #433: what colour the mobile live plot draws a channel in. On that plot the colour IS the
/// channel identifier — <c>LivePlot</c> labels each trace with the channel name and its latest
/// value in the trace's own colour — so it has to be the channel's colour, the one the mobile
/// Channels tab and the logged-session viewer already show. It used to be a private 8-colour
/// palette indexed by the channel's position in the selection.
/// </summary>
/// <remarks>
/// Everything below the view-model is real: the app's device wrapper over a real Core
/// <c>DaqifiStreamingDevice</c>, channels created by Core's own status handling, and the shell's
/// real <c>StreamToggle</c> command. The transport accepts writes and never answers, which is all
/// these tests need — the series are built when streaming starts, before any sample arrives.
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because the shell's adopt step registers
/// the device with <c>ConnectionManager.Instance</c>, and its teardown unregisters it.
/// </para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellSeriesColorTests : IDisposable
{
    private const int AnalogPorts = 16;

    private readonly CapturingTransport _transport = new();
    private readonly CoreStreamingDevice _core;
    private readonly ShellTestDevice _device;
    private readonly MobileShellViewModel _shell = new();

    public MobileShellSeriesColorTests()
    {
        _core = new CoreStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogPorts,
            AnalogInRes = 65535
        });

        _device = new ShellTestDevice("Series colour test device");
        _device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        _device.AttachCore(_core);
        _device.SyncFromCore(_core);

        _device.AdoptInto(_shell);
        Assert.True(_shell.IsConnected);
        Assert.Equal(AnalogPorts, _shell.Channels.Count);
    }

    public void Dispose()
    {
        _shell.Dispose();
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
    }

    /// <summary>
    /// Repro A: the same channel was one colour while streaming and another in the Channels tab and
    /// the session viewer, both of which show <c>ChannelColorBrush</c> (the viewer via the copy
    /// <c>DataSample</c> persists from it).
    /// </summary>
    [Fact]
    public void Every_trace_is_drawn_in_its_own_channels_colour()
    {
        StartStreaming();

        Assert.Equal(AnalogPorts, _shell.Series.Count);
        foreach (var series in _shell.Series)
        {
            var channel = AnalogChannels().Single(c => c.Name == series.Name);
            Assert.Equal(ExpectedArgb(channel), series.ColorArgb);
        }
    }

    /// <summary>
    /// Repro B: the colour was the channel's index in the SELECTED set, so deselecting an earlier
    /// channel shifted every later channel's colour — a channel changing colour between two runs
    /// with no colour action taken.
    /// </summary>
    [Fact]
    public void Deselecting_an_earlier_channel_does_not_recolour_the_others()
    {
        StartStreaming();
        var before = _shell.Series.ToDictionary(s => s.Name, s => s.ColorArgb, StringComparer.Ordinal);

        _shell.StreamToggleCommand.Execute(null);
        Assert.False(_shell.IsStreaming);

        _shell.Channels[0].IsSelected = false;
        StartStreaming();

        Assert.Equal(AnalogPorts - 1, _shell.Series.Count);
        foreach (var series in _shell.Series)
        {
            Assert.Equal(before[series.Name], series.ColorArgb);
        }
    }

    /// <summary>
    /// The palette held 8 colours and was indexed modulo its length, so on a 16-channel device two
    /// traces shared a colour on a plot where the colour is the only thing telling them apart. The
    /// channels' own colours come from a 38-entry set, so 16 of them are always distinct.
    /// </summary>
    [Fact]
    public void Sixteen_channels_get_sixteen_distinct_colours()
    {
        StartStreaming();

        Assert.Equal(AnalogPorts, _shell.Series.Select(s => s.ColorArgb).Distinct().Count());
    }

    private void StartStreaming()
    {
        _shell.StreamToggleCommand.Execute(null);
        Assert.True(_shell.IsStreaming, $"Arrangement: streaming did not start ({_shell.Status}).");
    }

    private List<IChannel> AnalogChannels() =>
        _device.DataChannels.Where(c => c.Type == ChannelType.Analog && !c.IsOutput).ToList();

    /// <summary>
    /// The channel's colour as the plot needs it, derived here from <c>ChannelColorBrush</c> rather
    /// than from the view-model's own helper — the test would otherwise assert that the shell
    /// agrees with itself.
    /// </summary>
    private static uint ExpectedArgb(IChannel channel)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(channel.ChannelColorBrush);
        var c = brush.Color;
        return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }
}
