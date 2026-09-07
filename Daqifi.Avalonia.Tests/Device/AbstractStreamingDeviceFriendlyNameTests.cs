using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Device;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins <see cref="AbstractStreamingDevice.SetFriendlyName"/> — the two commands it puts on the
/// wire, the names it refuses, and the order in which it refuses them.
///
/// <para>
/// The app used to compose this pair of commands as SCPI text itself and carry its own copy of
/// firmware's name-acceptance rule. Core 1.7.0 owns both
/// (<c>ScpiMessageProducer.SetDeviceName</c> / <c>SaveDeviceName</c>), so the app now asks Core
/// for the messages. These cases exist so that handing the work back stays byte-for-byte what the
/// device was already receiving, and so that the argument contract callers depend on does not
/// shift underneath them.
/// </para>
///
/// <para>
/// Two details here are load-bearing rather than incidental. The null case asserts
/// <see cref="ArgumentNullException"/> specifically: Core folds null into its general
/// <see cref="ArgumentException"/>, so the app's explicit null guard has to stay in front of it.
/// And validation is asserted to run <em>before</em> the connected check, because a name is
/// rejected the same way whether or not a device is listening — building the messages after that
/// check would silently downgrade an invalid name on a disconnected device to a no-op.
/// </para>
/// </summary>
public class AbstractStreamingDeviceFriendlyNameTests
{
    /// <summary>Longest name firmware's 32-byte NUL-terminated NVM buffer accepts.</summary>
    private const int MaxNameLength = 31;

    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/> that records what it is asked to
    /// send instead of needing a transport, and lets a test decide whether it looks connected.
    /// </summary>
    private sealed class RecordingDevice : AbstractStreamingDevice
    {
        public List<IOutboundMessage<string>> Sent { get; } = [];

        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) => Sent.Add(message);

        /// <summary>
        /// Makes the device report itself connected. <c>SetFriendlyName</c> gates on
        /// <c>CoreDevice.IsConnected</c>, and <c>SendMessage</c> is overridden above, so the Core
        /// device is only ever consulted for that flag — nothing reaches the transport.
        /// </summary>
        public void AttachCoreDevice(DaqifiStreamingDevice coreDevice) => CoreDevice = coreDevice;
    }

    /// <summary>A recording device that reports itself connected, plus the pieces to tear down.</summary>
    private sealed class ConnectedHarness : IDisposable
    {
        private readonly CapturingTransport _transport;
        private readonly DaqifiStreamingDevice _coreDevice;

        public ConnectedHarness()
        {
            _transport = new CapturingTransport();
            _coreDevice = new DaqifiStreamingDevice("TestDevice", _transport, NullLogger.Instance);
            _coreDevice.Connect();

            Device = new RecordingDevice();
            Device.AttachCoreDevice(_coreDevice);

            // Guard the arrangement: every "sends" assertion below passes vacuously if the
            // device does not actually read as connected.
            Assert.True(_coreDevice.IsConnected, "Harness device must report itself connected.");
        }

        public RecordingDevice Device { get; }

        public void Dispose()
        {
            // Release the parked reader before Core tears the session down, so the consumer
            // thread's bounded join does not cost this test its whole timeout.
            _transport.CloseStream();
            _coreDevice.Dispose();
        }
    }

    [Fact]
    public void Setting_a_name_sends_the_two_documented_commands_in_order()
    {
        using var harness = new ConnectedHarness();

        harness.Device.SetFriendlyName("Bench Nq1");

        Assert.Equal(2, harness.Device.Sent.Count);
        Assert.Equal("SYSTem:DEVice:NAME \"Bench Nq1\"", harness.Device.Sent[0].Data);
        Assert.Equal("SYSTem:DEVice:NAME:SAVE", harness.Device.Sent[1].Data);
    }

    [Fact]
    public void Setting_a_name_frames_both_commands_for_the_line_based_parser()
    {
        using var harness = new ConnectedHarness();

        harness.Device.SetFriendlyName("Bench Nq1");

        // The terminator is what keeps each command from merging with the next write and being
        // dropped by firmware's line-based parser.
        Assert.Equal(
            Encoding.ASCII.GetBytes("SYSTem:DEVice:NAME \"Bench Nq1\"\r\n"),
            harness.Device.Sent[0].GetBytes());
        Assert.Equal(
            Encoding.ASCII.GetBytes("SYSTem:DEVice:NAME:SAVE\r\n"),
            harness.Device.Sent[1].GetBytes());
    }

    [Fact]
    public void The_staged_name_is_applied_locally_without_waiting_for_the_device()
    {
        using var harness = new ConnectedHarness();

        harness.Device.SetFriendlyName("Bench Nq1");

        // The device does not echo the new name back synchronously and may not stream another
        // status frame for a while, so the drawer would otherwise show the old name.
        Assert.Equal("Bench Nq1", harness.Device.FriendlyName);
    }

    [Fact]
    public void A_name_of_the_maximum_length_is_accepted()
    {
        using var harness = new ConnectedHarness();
        var name = new string('a', MaxNameLength);

        harness.Device.SetFriendlyName(name);

        Assert.Equal($"SYSTem:DEVice:NAME \"{name}\"", harness.Device.Sent[0].Data);
    }

    [Fact]
    public void A_name_one_character_past_the_firmware_buffer_is_refused()
    {
        using var harness = new ConnectedHarness();

        Assert.Throws<ArgumentException>(
            () => harness.Device.SetFriendlyName(new string('a', MaxNameLength + 1)));
        Assert.Empty(harness.Device.Sent);
    }

    [Fact]
    public void An_empty_name_is_refused()
    {
        using var harness = new ConnectedHarness();

        Assert.Throws<ArgumentException>(() => harness.Device.SetFriendlyName(string.Empty));
        Assert.Empty(harness.Device.Sent);
    }

    [Theory]
    [InlineData("say \"hi\"")]   // would close the SCPI string literal early
    [InlineData("back\\slash")]  // would escape inside it
    [InlineData("tab\there")]    // below 0x20
    [InlineData("café")]    // above 0x7E
    public void A_name_firmware_would_reject_is_refused_before_it_reaches_the_wire(string name)
    {
        using var harness = new ConnectedHarness();

        Assert.Throws<ArgumentException>(() => harness.Device.SetFriendlyName(name));
        Assert.Empty(harness.Device.Sent);
    }

    [Fact]
    public void A_null_name_is_an_argument_null_exception()
    {
        using var harness = new ConnectedHarness();

        // Distinct from the ArgumentException above: Core's validator treats null as merely
        // invalid, so the app's own null guard has to run first to keep this contract.
        Assert.Throws<ArgumentNullException>(() => harness.Device.SetFriendlyName(null!));
        Assert.Empty(harness.Device.Sent);
    }

    [Fact]
    public void A_name_is_validated_even_when_no_device_is_listening()
    {
        // No Core device attached, so the connected check would return early. Validation still
        // has to have happened by then — the caller learns the name is bad either way.
        var device = new RecordingDevice();

        Assert.Throws<ArgumentException>(
            () => device.SetFriendlyName(new string('a', MaxNameLength + 1)));
    }

    [Fact]
    public void A_disconnected_device_is_told_nothing_and_keeps_its_name()
    {
        var device = new RecordingDevice();

        device.SetFriendlyName("Bench Nq1");

        // A valid name with nothing to send it to is a logged no-op, not a failure.
        Assert.Empty(device.Sent);
        Assert.Equal(string.Empty, device.FriendlyName);
    }
}
