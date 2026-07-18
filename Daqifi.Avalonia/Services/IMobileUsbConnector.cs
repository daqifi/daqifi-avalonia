using System.Threading.Tasks;
using Daqifi.Desktop.Device;

namespace Daqifi.Avalonia.Services;

/// <summary>Outcome of a USB connect attempt: the connected device (null on
/// failure) plus a human-readable status message. The device, when non-null, is
/// already connected and registered with ConnectionManager by the connector.</summary>
public sealed record UsbConnectResult(AbstractStreamingDevice? Device, string Message);

/// <summary>
/// Platform hook for connecting to a DAQiFi over a USB (OTG) host link. The
/// Android head registers an implementation at startup; heads with no USB-host
/// access (desktop / iOS / browser) leave <see cref="MobileUsbConnector.Current"/>
/// null, so <see cref="MobileUsbConnector.IsAvailable"/> is false and the USB
/// affordance stays hidden.
///
/// EXPERIMENTAL — the Android CDC transport this drives is unvalidated on hardware
/// (see docs/mobile-usb-feasibility.md). Connecting registers the device with
/// ConnectionManager, which lights up the SD-offload path (the Storage pane); live
/// streaming in the Stream tab additionally needs MobileShellViewModel generalized
/// off the WiFi device type.
/// </summary>
public interface IMobileUsbConnector
{
    /// <summary>True when a DAQiFi USB device is attached and enumerable right now.</summary>
    bool IsDeviceAttached { get; }

    /// <summary>
    /// Request permission, connect the USB device, and register it with
    /// ConnectionManager. Returns the connected device (null on failure) plus a
    /// status message for the caller to surface — never throws.
    /// </summary>
    Task<UsbConnectResult> ConnectAsync();
}

/// <summary>
/// Static registration point: the platform head sets <see cref="Current"/> at
/// startup (before any view loads), mirroring <see cref="NetworkDiscoveryScope"/>.
/// </summary>
public static class MobileUsbConnector
{
    public static IMobileUsbConnector? Current { get; set; }

    /// <summary>True on a platform that has a USB-host connector registered.</summary>
    public static bool IsAvailable => Current is not null;

    /// <summary>True when a connector is registered AND a device is attached.</summary>
    public static bool IsDeviceAttached => Current?.IsDeviceAttached == true;

    public static Task<UsbConnectResult> ConnectAsync() =>
        Current?.ConnectAsync()
        ?? Task.FromResult(new UsbConnectResult(null, "USB is not available on this platform."));
}
