// ============================================================================
// EXPERIMENTAL — UNVALIDATED ON HARDWARE.
//
// Android-head glue for the phone->DAQiFi USB (OTG) path: enumerate a DAQiFi CDC
// device, obtain USB permission, then build + connect a UsbStreamingDevice over an
// AndroidUsbStreamTransport and publish it into the shared ConnectionManager (the
// same bridge the mobile WiFi shell uses via RegisterConnectedDevice). None of this
// touches the WiFi/TCP path.
//
// Needs on-device validation: the permission grant surviving replug, and that the
// enumerated device is the CDC-ACM one the transport expects. See
// docs/mobile-usb-feasibility.md §"Must be validated on device".
//
// NOTE on the namespace shadow: inside a `Daqifi.Avalonia.Android` namespace the root
// `Android.*` namespace is shadowed — every Android type here is referenced UNqualified
// via the usings below; never write `Android.`-qualified names (see MainActivity.cs).
// ============================================================================

using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Daqifi.Desktop;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Device.UsbDevice;

namespace Daqifi.Avalonia.Android.Usb;

/// <summary>
/// Static helpers to discover a DAQiFi USB device, request permission for it, and bring up a
/// connected <see cref="UsbStreamingDevice"/> registered with <see cref="ConnectionManager"/>.
/// </summary>
public static class UsbDeviceConnector
{
    /// <summary>Microchip vendor id (0x04D8) reported by the DAQiFi CDC device.</summary>
    public const int DaqifiVendorId = 0x04D8;   // 1240

    /// <summary>DAQiFi "Nyquist" product id (0xF794).</summary>
    public const int DaqifiProductId = 0xF794;  // 63380

    /// <summary>
    /// Returns the first attached DAQiFi USB device (matching VID/PID), or null when none is
    /// present. Note: the firmware exposes no per-unit USB iSerial, so multiple units are
    /// indistinguishable here — disambiguate with <c>*IDN?</c> over the link, not USB iSerial.
    /// </summary>
    public static UsbDevice? FindDaqifiDevice(UsbManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        var deviceList = manager.DeviceList;
        if (deviceList == null)
        {
            return null;
        }

        foreach (var device in deviceList.Values)
        {
            if (device.VendorId == DaqifiVendorId && device.ProductId == DaqifiProductId)
            {
                return device;
            }
        }

        return null;
    }

    /// <summary>
    /// Requests USB permission for <paramref name="device"/>, returning true when granted.
    /// Registers a short-lived <see cref="BroadcastReceiver"/> for the permission result and
    /// completes when the system delivers it. Returns immediately when permission already holds.
    /// </summary>
    public static async Task<bool> RequestPermissionAsync(
        Context context, UsbManager manager, UsbDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(device);

        if (manager.HasPermission(device))
        {
            return true;
        }

        var action = $"{context.PackageName}.USB_PERMISSION";
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new UsbPermissionReceiver(action, completion);
        var filter = new IntentFilter(action);

        // Android 13+ requires an explicit exported/not-exported flag on context-registered
        // receivers. This is an app-internal broadcast, so NOT_EXPORTED. The flagged overload
        // binds its parameter as ActivityFlags in .NET-for-Android; the RECEIVER_NOT_EXPORTED
        // value (4) is carried via the ReceiverFlags enum cast.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            context.RegisterReceiver(receiver, filter, (ActivityFlags)ReceiverFlags.NotExported);
        }
        else
        {
            context.RegisterReceiver(receiver, filter);
        }

        try
        {
            var intent = new Intent(action);
            intent.SetPackage(context.PackageName); // keep the broadcast in-app (mutable-PI safety)

            // Mutable so the framework can attach EXTRA_DEVICE / EXTRA_PERMISSION_GRANTED.
            var pendingIntent = PendingIntent.GetBroadcast(context, 0, intent, PendingIntentFlags.Mutable);
            manager.RequestPermission(device, pendingIntent);

            using (cancellationToken.Register(() => completion.TrySetResult(false)))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                context.UnregisterReceiver(receiver);
            }
            catch (Exception ex)
            {
                // Receiver may already be gone; a failure to unregister must not fail the flow.
                AppLogger.Instance.Warning(ex, "Failed to unregister USB permission receiver");
            }
        }
    }

    /// <summary>
    /// Requests permission, builds an <see cref="AndroidUsbStreamTransport"/> + a connected
    /// <see cref="UsbStreamingDevice"/>, registers it with <see cref="ConnectionManager"/>, and
    /// returns it. Returns null (after logging) on any failure — permission denial, connect
    /// failure, or an unexpected exception.
    /// </summary>
    public static async Task<UsbStreamingDevice?> BuildConnectedDeviceAsync(
        Context context,
        UsbManager manager,
        UsbDevice device,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(device);

        try
        {
            var granted = await RequestPermissionAsync(context, manager, device, cancellationToken)
                .ConfigureAwait(false);
            if (!granted)
            {
                AppLogger.Instance.Warning($"USB permission was not granted for {device.DeviceName}");
                return null;
            }

            var name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName!
                : (device.ProductName ?? "DAQiFi USB");

            var transport = new AndroidUsbStreamTransport(manager, device);
            var usbDevice = new UsbStreamingDevice(transport, name);

            // Connect() blocks on Core init; keep it off the UI thread like the WiFi shell does.
            var connected = await Task.Run(usbDevice.Connect, cancellationToken).ConfigureAwait(false);
            if (!connected)
            {
                AppLogger.Instance.Warning($"Failed to connect USB device {name}");
                usbDevice.Disconnect();
                return null;
            }

            ConnectionManager.Instance.RegisterConnectedDevice(usbDevice);
            AppLogger.Instance.Information($"Registered USB device {name} (SN {usbDevice.DeviceSerialNo})");
            return usbDevice;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "Failed to build connected USB device");
            return null;
        }
    }

    /// <summary>
    /// Receives the one-shot USB permission-result broadcast and completes the awaiting task.
    /// </summary>
    private sealed class UsbPermissionReceiver : BroadcastReceiver
    {
        private readonly string _action;
        private readonly TaskCompletionSource<bool> _completion;

        public UsbPermissionReceiver(string action, TaskCompletionSource<bool> completion)
        {
            _action = action;
            _completion = completion;
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != _action)
            {
                return;
            }

            var granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
            _completion.TrySetResult(granted);
        }
    }
}
