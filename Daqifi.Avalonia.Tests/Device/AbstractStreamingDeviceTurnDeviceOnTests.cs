using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins the bytes <see cref="AbstractStreamingDevice.TurnDeviceOn"/> puts on the wire.
///
/// The mobile shell used to power the acquisition subsystem on by hand-writing
/// <c>"SYSTem:POWer:STATe 1\r\n"</c> into the device's raw-write escape hatch — the view-model
/// owned both the command mnemonic and the line terminator, and a caller that got either wrong
/// had the command silently merged with the next write and dropped by the firmware's line-based
/// parser. These cases exist so that moving the call into the device layer stays byte-for-byte
/// what the view-model was sending, and so that a future edit to the device layer cannot change
/// those bytes unnoticed.
/// </summary>
public class AbstractStreamingDeviceTurnDeviceOnTests
{
    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/> that records what it is asked to
    /// send instead of needing a transport.
    /// </summary>
    private sealed class RecordingDevice : AbstractStreamingDevice
    {
        public List<IOutboundMessage<string>> Sent { get; } = [];

        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) => Sent.Add(message);
    }

    [Fact]
    public void TurnDeviceOn_sends_exactly_one_message()
    {
        var device = new RecordingDevice();

        device.TurnDeviceOn();

        Assert.Single(device.Sent);
    }

    [Fact]
    public void TurnDeviceOn_sends_the_documented_power_on_command()
    {
        var device = new RecordingDevice();

        device.TurnDeviceOn();

        Assert.Equal("SYSTem:POWer:STATe 1", device.Sent[0].Data);
    }

    [Fact]
    public void TurnDeviceOn_frames_the_command_for_the_line_based_parser()
    {
        var device = new RecordingDevice();

        device.TurnDeviceOn();

        // The terminator is the part the view-model used to supply itself. An unterminated
        // command merges with the next write and both are dropped.
        Assert.Equal(
            Encoding.ASCII.GetBytes("SYSTem:POWer:STATe 1\r\n"),
            device.Sent[0].GetBytes());
    }
}
