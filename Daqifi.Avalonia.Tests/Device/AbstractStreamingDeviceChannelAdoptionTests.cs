using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Desktop.Device;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Covers <see cref="AbstractStreamingDevice.AdoptDeviceChannelSet"/> — the step
/// <see cref="AbstractStreamingDevice.Connect"/> runs so the app owns the enabled-channel set
/// rather than inheriting the device's.
///
/// This is the only place the failure in issue #165 is reproduced. It needs a device whose
/// firmware reports a non-empty <c>analog_in_port_enabled</c> mask on connect, and the bench Nq1
/// on firmware 3.7.2 reports an empty one — before and after enabling channels and reconnecting.
/// So the state is built here instead, out of the real pieces: a real
/// <see cref="DaqifiStreamingDevice"/> over a fake transport, its channels populated by Core's own
/// <see cref="DaqifiDevice.PopulateChannelsFromStatus"/> from a real protobuf status message
/// carrying the mask, and the app's real channel wrappers synced from it by the same
/// <c>SyncFromCoreDevice</c> the <c>ChannelsPopulated</c> handler calls.
/// </summary>
public class AbstractStreamingDeviceChannelAdoptionTests
{
    /// <summary>
    /// The SCPI command Core sends to replace the device's enabled-analog set. A zero mask is
    /// what "nothing is enabled" looks like on the wire, and it is what makes adoption stick:
    /// clearing only Core's in-memory flags would be undone by the next status message, which
    /// resyncs them from whatever the device still thinks is enabled.
    /// </summary>
    private const string ZeroAdcMaskCommand = "ENAble:VOLTage:DC 0";

    /// <summary>How long to wait for the producer thread to flush a command to the transport.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_device_reporting_channels_already_enabled_hands_the_app_an_active_set_it_never_asked_for()
    {
        // This is the bug, stated as the app sees it — the assertions here describe the state
        // adoption exists to prevent, and they hold both before and after the fix, because
        // adoption runs after this sync, not instead of it.
        using var harness = ConnectedDeviceReporting(analogPorts: 4, enabledMask: 0b0000_0111);

        harness.Device.SyncFromCore(harness.CoreDevice);

        var tiles = harness.Device.DataChannels;
        var tileCount = tiles.Count;
        var activeTiles = tiles.Count(channel => channel.IsActive);

        Assert.Equal(4, tileCount);
        Assert.Equal(3, activeTiles);

        // ChannelsPaneViewModel.SelectAll only toggles tiles that are not already active, and
        // toggling is the only thing that subscribes a channel to LoggingManager. With three
        // tiles already reporting active, Select All skips them, nothing is ever subscribed, and
        // DaqifiViewModel.CanToggleLogging — which counts subscribed channels — never turns on.
        var tilesSelectAllWouldTouch = tiles.Count(channel => !channel.IsActive);
        Assert.Equal(1, tilesSelectAllWouldTouch);
    }

    [Fact]
    public void Adopting_the_channel_set_clears_what_the_device_reported_and_tells_the_device()
    {
        using var harness = ConnectedDeviceReporting(analogPorts: 4, enabledMask: 0b0000_0111);
        harness.Device.SyncFromCore(harness.CoreDevice);
        var activeBefore = harness.Device.DataChannels.Count(channel => channel.IsActive);
        Assert.Equal(3, activeBefore);

        harness.Device.AdoptDeviceChannelSet(harness.CoreDevice);

        // Every tile the user sees is inactive, so Select All has something to select and
        // Start Logging becomes reachable once a channel is picked.
        Assert.All(harness.Device.DataChannels, channel => Assert.False(channel.IsActive));
        Assert.All(harness.CoreDevice.Channels, channel => Assert.False(channel.IsEnabled));

        // And the device itself was told, so the next status message resyncs to empty rather
        // than putting the inherited set straight back.
        Assert.True(
            harness.Transport.WaitForSentText(ZeroAdcMaskCommand, SendTimeout),
            $"Expected '{ZeroAdcMaskCommand}' on the wire; saw: {harness.Transport.SentText}");
    }

    [Fact]
    public void Adoption_sends_nothing_when_the_device_reports_nothing_enabled()
    {
        // The bench Nq1 (fw 3.7.2) case, and the overwhelmingly common one: the device reports an
        // empty mask, the app already starts empty, and connect must stay silent. An adoption step
        // that cleared unconditionally would put an extra SCPI command on every connect.
        using var harness = ConnectedDeviceReporting(analogPorts: 4, enabledMask: 0b0000_0000);
        harness.Device.SyncFromCore(harness.CoreDevice);
        Assert.All(harness.Device.DataChannels, channel => Assert.False(channel.IsActive));

        harness.Device.AdoptDeviceChannelSet(harness.CoreDevice);

        Assert.All(harness.Device.DataChannels, channel => Assert.False(channel.IsActive));
        Assert.False(
            harness.Transport.WaitForSentText(ZeroAdcMaskCommand, TimeSpan.FromMilliseconds(250)),
            $"Adoption should have sent nothing; saw: {harness.Transport.SentText}");
    }

    [Fact]
    public void Adoption_fails_the_connection_when_the_transport_has_already_gone()
    {
        // A device that is simply gone must not be swallowed: Connect() has to return false so
        // ConnectionManager never adds a device that is already disconnected. Core raises
        // DeviceNotConnectedException from DisableAllChannels' own guard, and the exception filter
        // classifies it as fatal, so it propagates to Connect()'s catch.
        using var harness = ConnectedDeviceReporting(analogPorts: 4, enabledMask: 0b0000_0111);
        harness.Device.SyncFromCore(harness.CoreDevice);
        harness.CoreDevice.Disconnect();

        Assert.Throws<DeviceNotConnectedException>(
            () => harness.Device.AdoptDeviceChannelSet(harness.CoreDevice));
    }

    [Fact]
    public void A_connected_device_that_merely_refuses_the_command_is_survivable()
    {
        // The whole point of the exception filter: the user can still pick channels by hand, so a
        // refusal is logged and the connection stands.
        using var harness = ConnectedDeviceReporting(analogPorts: 4, enabledMask: 0b0000_0111);

        Assert.False(AbstractStreamingDevice.IsConnectionFatal(
            harness.CoreDevice, new InvalidOperationException("device said no")));
    }

    [Fact]
    public void A_dropped_transport_is_fatal_however_it_surfaces()
    {
        using var harness = ConnectedDeviceReporting(analogPorts: 4, enabledMask: 0b0000_0111);

        Assert.True(AbstractStreamingDevice.IsConnectionFatal(
            harness.CoreDevice, new DeviceNotConnectedException()));
        Assert.True(AbstractStreamingDevice.IsConnectionFatal(
            harness.CoreDevice, new TransportNotConnectedException()));
    }

    [Fact]
    public void A_device_that_is_no_longer_connected_is_fatal_whatever_it_threw()
    {
        using var harness = ConnectedDeviceReporting(analogPorts: 4, enabledMask: 0b0000_0111);
        harness.CoreDevice.Disconnect();

        Assert.True(AbstractStreamingDevice.IsConnectionFatal(
            harness.CoreDevice, new InvalidOperationException("device said no")));
    }

    /// <summary>
    /// Builds a connected Core device whose channels report themselves enabled exactly as a
    /// device's own status message would, plus the app wrapper that reads them.
    /// </summary>
    /// <param name="analogPorts">How many analog input ports the device reports.</param>
    /// <param name="enabledMask">
    /// The bit-per-channel <c>analog_in_port_enabled</c> mask, little-endian, bit <c>n</c> =
    /// channel <c>n</c> — the layout confirmed on a real Nq1 (daqifi-core#409).
    /// </param>
    private static Harness ConnectedDeviceReporting(uint analogPorts, byte enabledMask)
    {
        var transport = new CapturingTransport();
        var coreDevice = new DaqifiStreamingDevice("TestDevice", transport, NullLogger.Instance);
        coreDevice.Connect();

        coreDevice.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = analogPorts,
            AnalogInRes = 65535,
            AnalogInPortEnabled = ByteString.CopyFrom(enabledMask)
        });

        // Guard the arrangement itself: if Core ever stops resyncing IsEnabled from the mask,
        // every assertion below would pass vacuously.
        var expectedEnabled = System.Numerics.BitOperations.PopCount(enabledMask);
        var actualEnabled = coreDevice.Channels
            .Count(channel => channel.Type == CoreChannelType.Analog && channel.IsEnabled);
        Assert.Equal(expectedEnabled, actualEnabled);

        // Only the commands adoption itself sends are interesting; connect and channel
        // population have already had their say.
        transport.ClearSent();

        return new Harness(transport, coreDevice, new TestDevice());
    }

    private sealed class Harness(CapturingTransport transport, DaqifiStreamingDevice coreDevice, TestDevice device)
        : IDisposable
    {
        public CapturingTransport Transport { get; } = transport;

        public DaqifiStreamingDevice CoreDevice { get; } = coreDevice;

        public TestDevice Device { get; } = device;

        public void Dispose()
        {
            // Release the parked reader before Core tears the session down: its consumer-thread
            // join is bounded, but leaving the thread blocked spends that whole bound on every
            // test. Closing the stream (rather than the transport) also avoids raising a
            // transport-level disconnect that Core would see as a drop rather than a teardown.
            Transport.CloseStream();
            CoreDevice.Dispose();
            Transport.Dispose();
        }
    }

    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/>. The base class needs two members
    /// implemented and neither is exercised here — nothing in this file sends to the device
    /// through the wrapper; the only traffic is what Core's own channel commands put on the
    /// transport.
    /// </summary>
    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();

        /// <summary>
        /// Exposes the protected sync the <c>ChannelsPopulated</c> handler performs, so the test
        /// builds <see cref="AbstractStreamingDevice.DataChannels"/> through the real code path
        /// rather than constructing channel wrappers by hand.
        /// </summary>
        public void SyncFromCore(DaqifiDevice coreDevice) => SyncFromCoreDevice(coreDevice);
    }
}
