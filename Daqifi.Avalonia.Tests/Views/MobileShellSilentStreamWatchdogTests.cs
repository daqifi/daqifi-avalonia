using System.Reflection;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #358: the mobile shell's silent-stream watchdog. A device that stops delivering samples
/// while its socket stays open must be declared dead after
/// <c>SilentPollsBeforeStreamDeclaredDead</c> (160) render polls of silence, so the user is not
/// left staring at "Streaming 16 channel(s) @ 100 Hz" over a frozen plot.
/// </summary>
/// <remarks>
/// Everything below the view-model is real: the app's device wrapper over a real Core
/// <c>DaqifiStreamingDevice</c>, a transport that accepts writes and never answers (which is
/// exactly what a silent device looks like from the app's side), and channels populated by Core's
/// own status handling. The render timer is the one piece replaced — the tests call
/// <see cref="MobileShellViewModel.PollActiveSamples"/> directly, once per simulated 50 ms tick.
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because the shell's adopt step registers
/// the device with <c>ConnectionManager.Instance</c>, and its teardown unregisters it.
/// </para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellSilentStreamWatchdogTests : IDisposable
{
    /// <summary>Mirrors <c>MobileShellViewModel.SilentPollsBeforeStreamDeclaredDead</c>.</summary>
    private const int WatchdogPolls = 160;

    private const int AnalogPorts = 16;

    private readonly CapturingTransport _transport = new();
    private readonly CoreStreamingDevice _core;
    private readonly ShellTestDevice _device;
    private readonly MobileShellViewModel _shell = new();

    public MobileShellSilentStreamWatchdogTests()
    {
        _core = new CoreStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.PopulateChannelsFromStatus(Status(enabledMask: null));

        _device = new ShellTestDevice();
        _device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        _device.AttachCore(_core);
        _device.SyncFromCore(_core);

        Adopt(_shell, _device);
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

    [Fact]
    public void A_stream_that_delivers_nothing_is_declared_dead_once_the_window_elapses()
    {
        StartStreaming();

        Poll(WatchdogPolls - 1);
        Assert.True(_shell.IsStreaming, "The watchdog tripped before its window had elapsed.");

        Poll(1);

        Assert.False(_shell.IsStreaming);
        Assert.False(_shell.IsConnected);
        Assert.StartsWith("Lost connection to", _shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stream_that_keeps_delivering_is_never_declared_dead()
    {
        StartStreaming();
        var channel = ActiveAnalogChannels().First();

        for (var i = 0; i < WatchdogPolls * 3; i++)
        {
            channel.ActiveSample = new DataSample { Value = i, TimestampTicks = i + 1 };
            Poll(1);
        }

        Assert.True(_shell.IsStreaming);
        Assert.True(_shell.IsConnected);
        Assert.True(_shell.TotalSamples > 0);
    }

    /// <summary>
    /// The defect. A status frame arriving mid-stream carries the device's own enabled mask, and
    /// Core 1.8.0 resyncs every analog channel's <c>IsEnabled</c> from it IN PLACE — so the app's
    /// wrappers, which read <c>IsActive</c> straight through to Core, all report inactive. The
    /// shell's poll used to count zero monitored channels, take the "nothing to monitor" branch,
    /// and reset the silence counter on every tick: the watchdog was disarmed for the rest of the
    /// session while the screen still read "Streaming 16 channel(s)".
    /// </summary>
    /// <remarks>
    /// Driven through <c>PopulateChannelsFromStatus</c>, which is what Core's status handler calls
    /// for every status frame; a capturing transport cannot deliver a frame of its own.
    /// </remarks>
    [Fact]
    public void A_status_frame_that_clears_the_enabled_mask_mid_stream_does_not_disarm_the_watchdog()
    {
        StartStreaming();

        _core.PopulateChannelsFromStatus(Status(enabledMask: [0x00, 0x00]));

        // Guard the arrangement: if Core ever stops resyncing IsEnabled from the mask, this test
        // would pass without exercising the path it exists for.
        Assert.Empty(ActiveAnalogChannels());

        AssertStreamStoppedForNoEnabledChannels();
    }

    /// <summary>
    /// The same state reached from the app's side: every streamed channel deactivated under a
    /// running stream, the way the Channels and Profiles panes do it. Nothing is enabled, so the
    /// silence is explained — the stream is stopped and the user told why, but a transport that
    /// may be perfectly healthy is NOT torn down. That is the false positive the old
    /// reset-when-unmonitored gate existed to prevent, and it must stay prevented.
    /// </summary>
    [Fact]
    public void Deactivating_every_channel_mid_stream_stops_the_stream_but_keeps_the_connection()
    {
        StartStreaming();

        foreach (var channel in ActiveAnalogChannels())
        {
            _device.RemoveChannel(channel);
        }

        Assert.Empty(ActiveAnalogChannels());

        AssertStreamStoppedForNoEnabledChannels();
    }

    /// <summary>
    /// Silence banked while nothing was enabled is explained silence, and must not count toward
    /// declaring the transport dead once a channel comes back. Without this, a channel re-enabled
    /// late in the window tore the connection down on the very next poll, before the device had
    /// had a chance to send a single sample for it.
    /// </summary>
    [Fact]
    public void Silence_banked_with_no_channels_enabled_does_not_drop_the_connection_when_one_comes_back()
    {
        StartStreaming();
        var channels = ActiveAnalogChannels();
        foreach (var channel in channels)
        {
            _device.RemoveChannel(channel);
        }

        Poll(WatchdogPolls - 1);
        _device.AddChannel(channels[0]);
        Poll(1);

        Assert.True(_shell.IsConnected, "Silence banked while nothing was enabled dropped the connection.");
        Assert.True(_shell.IsStreaming);

        // The re-enabled channel still gets a full window of its own, and a device that really is
        // silent for all of it is still dropped.
        Poll(WatchdogPolls - 2);
        Assert.True(_shell.IsConnected, "The watchdog tripped before the re-enabled channel's window had elapsed.");
        Poll(1);
        Assert.False(_shell.IsConnected);
        Assert.StartsWith("Lost connection to", _shell.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// After the no-channels trip stops the stream, the user can start again on the same
    /// connection and gets a live, fully armed stream.
    /// </summary>
    [Fact]
    public void Streaming_restarts_cleanly_after_the_no_channels_trip()
    {
        StartStreaming();
        foreach (var channel in ActiveAnalogChannels())
        {
            _device.RemoveChannel(channel);
        }

        AssertStreamStoppedForNoEnabledChannels();

        StartStreaming();
        Assert.StartsWith($"Streaming {AnalogPorts} channel(s)", _shell.Status, StringComparison.Ordinal);

        Poll(WatchdogPolls);
        Assert.False(_shell.IsConnected, "The restarted stream's watchdog was not re-armed.");
    }

    private void AssertStreamStoppedForNoEnabledChannels()
    {
        Assert.True(_shell.IsStreaming);

        Poll(WatchdogPolls - 1);
        Assert.True(_shell.IsStreaming, "The watchdog tripped before its window had elapsed.");

        Poll(1);

        Assert.False(_shell.IsStreaming, "The watchdog never fired after the channels went inactive.");
        Assert.True(_shell.IsConnected, "A stream with nothing enabled is not evidence of a dead transport.");
        Assert.StartsWith("Streaming stopped — no channels are enabled", _shell.Status, StringComparison.Ordinal);
    }

    private void StartStreaming()
    {
        _shell.StreamToggleCommand.Execute(null);
        Assert.True(_shell.IsStreaming, $"Arrangement: streaming did not start ({_shell.Status}).");
        Assert.Equal(AnalogPorts, ActiveAnalogChannels().Count);
        Assert.Equal(0, _shell.TotalSamples);
    }

    private void Poll(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _shell.PollActiveSamples();
        }
    }

    private List<IChannel> ActiveAnalogChannels() =>
        _device.DataChannels
            .Where(c => c.Type == ChannelType.Analog && !c.IsOutput && c.IsActive)
            .ToList();

    private static DaqifiOutMessage Status(byte[]? enabledMask)
    {
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogPorts,
            AnalogInRes = 65535
        };
        if (enabledMask != null)
        {
            message.AnalogInPortEnabled = ByteString.CopyFrom(enabledMask);
        }

        return message;
    }

    /// <summary>
    /// The shell's adopt step is private — the public routes into it (WiFi connect, USB connect)
    /// each need a live transport or a platform connector. It is reached by name so the test
    /// exercises the shell's real adoption rather than a copy of it.
    /// </summary>
    private static void Adopt(MobileShellViewModel shell, AbstractStreamingDevice device)
    {
        var adopt = typeof(MobileShellViewModel).GetMethod(
            "AdoptConnectedDevice", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(adopt);
        adopt.Invoke(shell, [device]);
    }

    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/> over a real Core device. The wrapper's
    /// own <c>SendMessage</c> is a no-op: nothing here reads what the wrapper sends, and a silent
    /// device would not answer it anyway.
    /// </summary>
    private sealed class ShellTestDevice : AbstractStreamingDevice
    {
        public ShellTestDevice()
        {
            Name = "Watchdog test device";
        }

        public override ConnectionType ConnectionType => ConnectionType.Wifi;

        protected override void SendMessage(IOutboundMessage<string> message)
        {
        }

        public void AttachCore(CoreStreamingDevice coreDevice) => CoreDevice = coreDevice;

        public void SyncFromCore(DaqifiDevice coreDevice) => SyncFromCoreDevice(coreDevice);
    }
}
