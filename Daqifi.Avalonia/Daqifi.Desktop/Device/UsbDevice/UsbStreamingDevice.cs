// Downstream-only (mobile USB-OTG support). No upstream Daqifi.Desktop counterpart:
// the WPF desktop reaches USB only through SerialStreamingDevice (System.IO.Ports),
// which throws PlatformNotSupported on mobile. This device is the transport-injected
// sibling of SerialStreamingDevice — same AbstractStreamingDevice contract and the same
// Core streaming/SD pipeline, but with the concrete USB transport supplied by the host
// platform (e.g. the Android head's AndroidUsbStreamTransport over CDC-ACM) instead of a
// SerialPort. It is platform-neutral: it references only Core + the shared base, never
// System.IO.Ports and never any Android type, so it lives in the shared library.
//
// EXPERIMENTAL, UNVALIDATED ON HARDWARE — see docs/mobile-usb-feasibility.md.
// The transport it drives (the Android CDC-ACM implementation) needs on-device validation;
// this class only wires that transport into the existing device/SD/streaming machinery.

using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using ScpiMessageProducer = Daqifi.Core.Communication.Producers.ScpiMessageProducer;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Desktop.Device.UsbDevice;

/// <summary>
/// A USB streaming device whose transport is injected rather than owning a
/// <c>SerialPort</c>. Mirrors <see cref="SerialDevice.SerialStreamingDevice"/> — including its
/// initial-status-wait pattern and its <c>CoreDeviceForSd =&gt; CoreDevice</c> override that
/// lights up the SD-card offload path — but stays platform-neutral by depending only on
/// <see cref="IStreamTransport"/>. The concrete transport (Android CDC-ACM, a test double, etc.)
/// is constructed by the host platform and passed to the constructor.
/// </summary>
public class UsbStreamingDevice : AbstractStreamingDevice
{
    private static readonly TimeSpan InitialStatusTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InitialStatusPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan InitialStatusRequestInterval = TimeSpan.FromSeconds(1);

    #region Properties
    private readonly IStreamTransport _transport;
    private TaskCompletionSource<bool>? _initialStatusReceivedSource;

    public override ConnectionType ConnectionType => ConnectionType.Usb;

    /// <summary>
    /// SD-card operations run over the same Core device as streaming for USB devices; returning
    /// it here (rather than the base's <c>null</c>) is what enables the SD offload path.
    /// </summary>
    protected override CoreStreamingDevice? CoreDeviceForSd => CoreDevice;
    #endregion

    #region Constructor
    /// <summary>
    /// Creates a USB streaming device over the supplied transport.
    /// </summary>
    /// <param name="transport">The connected-or-connectable USB transport to drive. The device
    /// takes ownership and disconnects/disposes it on cleanup.</param>
    /// <param name="name">Display name for the device.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is null.</exception>
    public UsbStreamingDevice(IStreamTransport transport, string name)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Name = string.IsNullOrWhiteSpace(name) ? "DAQiFi USB Device" : name;
        IsStreaming = false;
    }

    /// <summary>
    /// Creates a USB streaming device with device info already known from discovery/probe.
    /// The 4-arg sibling of <see cref="SerialDevice.SerialStreamingDevice"/>'s discovery ctor.
    /// </summary>
    /// <param name="transport">The USB transport to drive.</param>
    /// <param name="name">Display name for the device.</param>
    /// <param name="serialNumber">The device serial number, if known.</param>
    /// <param name="firmwareVersion">The device firmware version, if known.</param>
    public UsbStreamingDevice(IStreamTransport transport, string name, string? serialNumber, string? firmwareVersion)
        : this(transport, name)
    {
        Metadata.SerialNumber = serialNumber ?? string.Empty;
        Metadata.FirmwareVersion = firmwareVersion ?? string.Empty;
    }
    #endregion

    #region Override Methods
    /// <summary>
    /// Connects the injected transport, builds a Core streaming device over it, and connects
    /// that for the shared <see cref="AbstractStreamingDevice.Connect"/> template. Mirrors
    /// <see cref="SerialDevice.SerialStreamingDevice.CreateCoreDevice"/>: <see cref="CoreDevice"/>
    /// is assigned before its connect so every failure path is torn down by
    /// <see cref="CleanupConnection"/>.
    /// </summary>
    protected override CoreStreamingDevice CreateCoreDevice()
    {
        _initialStatusReceivedSource = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // The transport manages the actual USB connection internally; for CDC-ACM the DTR line
        // must be asserted by the transport or the device will not stream over USB.
        _transport.Connect();

        CoreDevice = new CoreStreamingDevice(Name, _transport);
        CoreDevice.Connect();
        return CoreDevice;
    }

    /// <summary>
    /// Signals the initial-status wait so <see cref="OnCoreDeviceInitialized"/> can return,
    /// then runs the shared Core-to-desktop sync.
    /// </summary>
    protected override void OnCoreChannelsPopulated(object? sender, Daqifi.Core.Device.ChannelsPopulatedEventArgs e)
    {
        _initialStatusReceivedSource?.TrySetResult(true);
        base.OnCoreChannelsPopulated(sender, e);
    }

    /// <summary>
    /// Blocks until the device reports its initial status message, re-requesting device info
    /// periodically. Gates <see cref="AbstractStreamingDevice.Connect"/> returning, matching the
    /// serial device's post-initialize behavior.
    /// </summary>
    protected override void OnCoreDeviceInitialized()
    {
        var coreDevice = CoreDevice
            ?? throw new InvalidOperationException("Core device was not initialized.");

        var statusReceivedSource = _initialStatusReceivedSource
            ?? throw new InvalidOperationException("Initial status wait source was not initialized.");

        var deadline = DateTime.UtcNow + InitialStatusTimeout;
        var nextDeviceInfoRequestAt = DateTime.UtcNow + InitialStatusRequestInterval;

        while (DateTime.UtcNow < deadline)
        {
            if (statusReceivedSource.Task.Wait(InitialStatusPollInterval))
            {
                return;
            }

            if (DateTime.UtcNow < nextDeviceInfoRequestAt)
            {
                continue;
            }

            try
            {
                coreDevice.Send(ScpiMessageProducer.GetDeviceInfo);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, $"Failed to re-request device info on {Name}");
            }

            nextDeviceInfoRequestAt = DateTime.UtcNow + InitialStatusRequestInterval;
        }

        throw new TimeoutException(
            $"USB device {Name} did not report status within {InitialStatusTimeout.TotalSeconds:F0} seconds of connect.");
    }

    /// <summary>
    /// Sends a message to the device using Core's DaqifiDevice.
    /// </summary>
    protected override void SendMessage(IOutboundMessage<string> message)
    {
        if (CoreDevice == null || !CoreDevice.IsConnected)
        {
            AppLogger.Warning($"Cannot send to {Name}: Core device not connected");
            return;
        }
        CoreDevice.Send(message);
    }

    /// <summary>
    /// Writes a raw ASCII command directly to the transport stream.
    /// </summary>
    public override bool Write(string command)
    {
        try
        {
            if (_transport.IsConnected)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(command);
                _transport.Stream.Write(bytes, 0, bytes.Length);
                return true;
            }

            AppLogger.Warning($"Cannot write to {Name}: transport not connected");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to write in UsbStreamingDevice");
            return false;
        }
    }

    /// <summary>
    /// Tears down the Core device (base) then disconnects and disposes the transport.
    /// </summary>
    protected override void CleanupConnection()
    {
        _initialStatusReceivedSource = null;

        // Unsubscribe Core device events and dispose the Core device first.
        base.CleanupConnection();

        try
        {
            _transport.Disconnect();
            _transport.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning(ex, "Error disconnecting USB transport during cleanup");
        }
    }

    /// <summary>
    /// USB devices have no COM port name on mobile; surface the display name (falling back to
    /// the base "USB") so <see cref="AbstractStreamingDevice.DisplayIdentifier"/> is meaningful.
    /// </summary>
    protected override string GetUsbDisplayIdentifier()
    {
        return string.IsNullOrWhiteSpace(Name) ? "USB" : Name;
    }
    #endregion
}
