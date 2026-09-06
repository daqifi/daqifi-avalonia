using System.Net;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;
using SerialWrapper = Daqifi.Desktop.Device.SerialDevice.SerialStreamingDevice;
using UsbWrapper = Daqifi.Desktop.Device.UsbDevice.UsbStreamingDevice;
using WifiWrapper = Daqifi.Desktop.Device.WiFiDevice.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Covers issue #291: the channel commands must survive the device going away between the
/// wrapper's connectivity check and Core's write.
/// </summary>
/// <remarks>
/// <para>
/// This is the same crash #214 fixed for the logging setters, one family of commands over.
/// Core documents the race itself — <c>DaqifiDevice.Send&lt;T&gt;</c> throws
/// <see cref="DeviceNotConnectedException"/> "when a disconnect, dispose or auto-reconnect on
/// another thread tears the send path down after this call has passed its connectivity guard —
/// an ordinary race a long-lived sender should expect, not a defect" (Daqifi.Core 1.7.0). The
/// serial and USB wrappers called <c>Send</c> straight after their own <c>IsConnected</c> check,
/// so the throw left the device layer.
/// </para>
/// <para>
/// What that costs the user is the whole point. <c>ChannelsPaneViewModel.ToggleChannel</c> is
/// bound as a synchronous <c>RelayCommand</c> and calls <c>AddChannel</c>/<c>RemoveChannel</c>
/// with no <c>try</c> anywhere between; <c>ProfilesPaneViewModel.ApplyProfileToDevices</c> does
/// the same with <c>AddChannels</c>/<c>RemoveAllChannels</c>. Nothing in that chain catches (see
/// <see cref="A_throw_from_a_channel_command_is_not_swallowed_by_the_binding_layer"/>), so the
/// exception reaches <c>Dispatcher.UIThread.UnhandledException</c>, and
/// <c>App.OnDispatcherUnhandledException</c> only logs it — it never sets <c>Handled</c>. The
/// process ends, taking the un-written part of the session with it. That last leg is the same
/// one <c>DeviceRefusalCrashTests</c> establishes for #214.
/// </para>
/// <para>
/// The arrangement is a REAL wrapper of each transport over a REAL Core streaming device that is
/// genuinely connected — so the wrapper's own guard passes, exactly as it does on hardware — and
/// whose <c>Send</c> throws the way Core says it can. Every test asserts <c>SendAttempts</c> so a
/// wrapper that quietly returned at its guard could not pass vacuously.
/// </para>
/// </remarks>
public class ChannelCommandDisconnectRaceTests
{
    [Fact]
    public void Toggling_a_channel_as_a_serial_device_drops_does_not_end_the_app()
    {
        using var harness = Harness.Serial();
        harness.Channel.IsActive = false;

        harness.Device.AddChannel(harness.Channel);

        Assert.Equal(1, harness.Core.SendAttempts);
        Assert.True(harness.Channel.IsActive);
    }

    [Fact]
    public void Untoggling_a_channel_as_a_serial_device_drops_does_not_end_the_app()
    {
        using var harness = Harness.Serial();

        harness.Device.RemoveChannel(harness.Channel);

        Assert.Equal(1, harness.Core.SendAttempts);
        Assert.False(harness.Channel.IsActive);
    }

    [Fact]
    public void Activating_a_profile_as_a_serial_device_drops_does_not_end_the_app()
    {
        // ProfilesPaneViewModel.ApplyProfileToDevices(activate: true). Its window is wider than
        // the channel pane's: a confirm overlay is awaited between the device snapshot and this.
        using var harness = Harness.Serial();
        harness.Channel.IsActive = false;

        harness.Device.AddChannels([harness.Channel]);

        Assert.Equal(1, harness.Core.SendAttempts);
    }

    [Fact]
    public void Deactivating_a_profile_as_a_serial_device_drops_does_not_end_the_app()
    {
        // ApplyProfileToDevices(activate: false). Two commands, and the second must still be
        // attempted after the first has failed — each is guarded on its own.
        using var harness = Harness.Serial();

        harness.Device.RemoveAllChannels();

        Assert.Equal(2, harness.Core.SendAttempts);
        Assert.False(harness.Channel.IsActive);
    }

    [Fact]
    public void Toggling_a_channel_as_a_usb_device_drops_does_not_end_the_app()
    {
        using var harness = Harness.Usb();
        harness.Channel.IsActive = false;

        harness.Device.AddChannel(harness.Channel);

        Assert.Equal(1, harness.Core.SendAttempts);
        Assert.True(harness.Channel.IsActive);
    }

    [Fact]
    public void Deactivating_a_profile_as_a_usb_device_drops_does_not_end_the_app()
    {
        using var harness = Harness.Usb();

        harness.Device.RemoveAllChannels();

        Assert.Equal(2, harness.Core.SendAttempts);
    }

    [Fact]
    public void The_wifi_transport_already_survived_the_same_drop()
    {
        // Passes before the fix as well as after — the WiFi override was the one transport that
        // already wrapped Send. It is here so the consolidation cannot silently regress the
        // behaviour it was consolidated from.
        using var harness = Harness.Wifi();
        harness.Channel.IsActive = false;

        harness.Device.AddChannel(harness.Channel);

        Assert.Equal(1, harness.Core.SendAttempts);
        Assert.True(harness.Channel.IsActive);
    }

    [Fact]
    public void A_throw_from_a_channel_command_is_not_swallowed_by_the_binding_layer()
    {
        // Pins the premise the tests above rest on, and it holds on both heads: the channel
        // commands are plain synchronous RelayCommands, which do not catch. Whatever leaves the
        // device layer leaves the command invocation, and the dispatcher is the next thing to
        // see it.
        var command = new RelayCommand<int>(_ => throw new DeviceNotConnectedException());

        Assert.Throws<DeviceNotConnectedException>(() => command.Execute(0));
    }

    /// <summary>
    /// A real wrapper of one transport, holding a real Core streaming device that is connected
    /// and whose sends fail the way Core documents.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ParkingTransport _transport;

        private Harness(ParkingTransport transport, DroppedCoreDevice core, IStreamingDevice device)
        {
            _transport = transport;
            Core = core;
            Device = device;

            Channel = new FakeChannel("AI0", "SN-291") { IsActive = true };
            device.DataChannels.Add(Channel);

            // Guard the arrangement: if the Core device were not connected, every wrapper would
            // return at its own connectivity check and none of these tests would touch the race.
            Assert.True(core.IsConnected);
            core.FailSends = true;
        }

        public DroppedCoreDevice Core { get; }

        public IStreamingDevice Device { get; }

        public FakeChannel Channel { get; }

        public static Harness Serial()
        {
            var (transport, core) = ConnectedCore();
            return new Harness(transport, core, new SerialWrapper("COM-TEST-291", core));
        }

        public static Harness Usb()
        {
            var (transport, core) = ConnectedCore();
            return new Harness(transport, core, new TestUsbDevice(transport, core));
        }

        public static Harness Wifi()
        {
            var (transport, core) = ConnectedCore();
            return new Harness(transport, core, new TestWifiDevice(core));
        }

        private static (ParkingTransport, DroppedCoreDevice) ConnectedCore()
        {
            var transport = new ParkingTransport();
            var core = new DroppedCoreDevice(transport);
            core.Connect();
            return (transport, core);
        }

        public void Dispose()
        {
            // Teardown must not be answered with the simulated failure: what is under test is the
            // command path, not Core's disposal.
            Core.FailSends = false;
            _transport.CloseStream();
            Core.Dispose();
            _transport.Dispose();
        }
    }

    /// <summary>
    /// A connected Core streaming device whose <c>Send</c> reports the disconnect that landed
    /// after the caller's connectivity guard had already passed.
    /// </summary>
    private sealed class DroppedCoreDevice(IStreamTransport transport)
        : CoreStreamingDevice("core-291", transport, NullLogger.Instance)
    {
        /// <summary>Commands that reached Core, whether or not they were allowed to fail.</summary>
        public int SendAttempts { get; private set; }

        public bool FailSends { get; set; }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            SendAttempts++;

            if (FailSends)
            {
                throw new DeviceNotConnectedException(
                    "the transport went away after the connectivity guard passed");
            }
        }
    }

    /// <summary>
    /// Exposes <see cref="UsbWrapper"/> over an already-built Core device. The USB wrapper builds
    /// its own inside <c>Connect</c>, which needs real hardware; the state under test is the one
    /// after that has happened.
    /// </summary>
    private sealed class TestUsbDevice : UsbWrapper
    {
        public TestUsbDevice(IStreamTransport transport, CoreStreamingDevice core)
            : base(transport, "USB-TEST-291")
        {
            CoreDevice = core;
        }
    }

    /// <summary>The <see cref="WifiWrapper"/> counterpart of <see cref="TestUsbDevice"/>.</summary>
    private sealed class TestWifiDevice : WifiWrapper
    {
        public TestWifiDevice(CoreStreamingDevice core)
            : base(IPAddress.Loopback, 9760, "WIFI-TEST-291")
        {
            CoreDevice = core;
        }
    }

    /// <summary>
    /// The least transport that lets Core reach <see cref="ConnectionStatus.Connected"/>. Reads
    /// park until disposal so Core's consumer thread sits idle rather than spinning on an
    /// end-of-stream; nothing is written, because <see cref="DroppedCoreDevice"/> never gets that
    /// far.
    /// </summary>
    private sealed class ParkingTransport : IStreamTransport
    {
        private readonly ParkingStream _stream = new();

        public Stream Stream => _stream;

        public bool IsConnected { get; private set; }

        public string ConnectionInfo => "parking-transport";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public void CloseStream() => _stream.Dispose();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            Connect();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            Disconnect();
            return Task.CompletedTask;
        }

        public void Connect()
        {
            if (IsConnected)
            {
                return;
            }

            IsConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
        }

        public void Disconnect()
        {
            if (!IsConnected)
            {
                return;
            }

            IsConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
        }

        public void Dispose()
        {
            Disconnect();
            _stream.Dispose();
        }
    }

    /// <summary>Discards writes and parks reads until disposal.</summary>
    private sealed class ParkingStream : Stream
    {
        private readonly ManualResetEventSlim _closed = new(false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _closed.Wait();
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => _closed.Wait(cancellationToken), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closed.Set();
            }

            base.Dispose(disposing);
        }
    }
}
