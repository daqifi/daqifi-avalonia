using Android.Content;
using Android.Hardware.Usb;
using Daqifi.Avalonia.Services;

// NOTE: inside a `Daqifi.Avalonia.Android` namespace the root `Android.*`
// namespace is shadowed — reference Android types UNqualified via the usings
// above, never as `Android.Content.Context` (see MainActivity.cs).
namespace Daqifi.Avalonia.Android.Usb;

/// <summary>
/// Android implementation of <see cref="IMobileUsbConnector"/>: enumerates the
/// attached DAQiFi over the USB host API and connects it through
/// <see cref="UsbDeviceConnector"/>. Registered on <see cref="MobileUsbConnector"/>
/// by MainActivity at startup. EXPERIMENTAL — see AndroidUsbStreamTransport.
/// </summary>
public sealed class AndroidUsbConnector : IMobileUsbConnector
{
    private readonly Context _context;
    private readonly UsbManager? _usbManager;

    public AndroidUsbConnector(Context context)
    {
        // ApplicationContext — the UsbManager (a system service) outlives the activity.
        _context = context.ApplicationContext ?? context;
        _usbManager = _context.GetSystemService(Context.UsbService) as UsbManager;
    }

    public bool IsDeviceAttached =>
        _usbManager is not null && UsbDeviceConnector.FindDaqifiDevice(_usbManager) is not null;

    public async Task<string> ConnectAsync()
    {
        if (_usbManager is null)
        {
            return "USB host service is unavailable on this device.";
        }

        var device = UsbDeviceConnector.FindDaqifiDevice(_usbManager);
        if (device is null)
        {
            return "No DAQiFi USB device found — attach it via a USB-C OTG cable and grant permission.";
        }

        var connected = await UsbDeviceConnector
            .BuildConnectedDeviceAsync(_context, _usbManager, device)
            .ConfigureAwait(false);

        return connected is not null
            ? $"USB connected: {connected.Name} (SN {connected.DeviceSerialNo}). Open the Storage tab for SD files."
            : "USB connect failed — permission denied or transport error (see device log).";
    }
}
