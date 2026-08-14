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
using Avalonia.Threading;
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
            //
            // PendingIntentFlags.Mutable is API 31+, and minSdk is 29, so it cannot be passed
            // unconditionally. Pre-31 needs no flag at all: PendingIntents were mutable by default
            // there, and the immutable/mutable pair only became meaningful (and one of them
            // mandatory) in 31. Passing the 31-only bit on 29/30 happens to be harmless — unknown
            // flag bits are ignored — but relying on that is relying on an accident, and it is the
            // only CA1416 on a code path that actually runs on Android, so it masks real ones.
            // From 31 the flag is also MANDATORY — GetBroadcast throws IllegalArgumentException
            // unless exactly one of Mutable/Immutable is given — so this is not merely a lint fix
            // on that side. Flags are otherwise left alone: no UpdateCurrent/CancelCurrent, matching
            // the behaviour this shipped with.
            var flags = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? PendingIntentFlags.Mutable
                : default;
            var pendingIntent = PendingIntent.GetBroadcast(context, 0, intent, flags);
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

        UsbStreamingDevice? usbDevice = null;
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
            usbDevice = new UsbStreamingDevice(transport, name);

            // Connect() blocks on Core init; keep it off the UI thread like the WiFi shell does.
            global::Android.Util.Log.Info("DaqifiUsb", $"UsbStreamingDevice.Connect starting for {name}…");
            var connected = await Task.Run(usbDevice.Connect, cancellationToken).ConfigureAwait(false);
            global::Android.Util.Log.Info("DaqifiUsb", $"UsbStreamingDevice.Connect => {connected}");
            if (!connected)
            {
                AppLogger.Instance.Warning($"Failed to connect USB device {name}");
                usbDevice.Disconnect();
                usbDevice = null;
                return null;
            }

            // Register on the UI THREAD. RegisterConnectedDevice raises a SYNCHRONOUS
            // ConnectedDevices PropertyChanged whose subscribers (e.g. a live
            // ChannelsPaneViewModel) mutate Avalonia-bound collections — which must
            // happen on the UI thread. We are off it here (the connect ran under
            // Task.Run / ConfigureAwait(false)), so marshal — otherwise Avalonia
            // throws an invalid-thread exception that unwinds into the catch below and
            // misreports a successful connect as a failure (audit #ec1ca58). The desktop
            // path already registers from the UI thread; this restores that invariant.
            await Dispatcher.UIThread.InvokeAsync(
                () => ConnectionManager.Instance.RegisterConnectedDevice(usbDevice));
            AppLogger.Instance.Information($"Registered USB device {name} (SN {usbDevice.DeviceSerialNo})");
            return usbDevice;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "Failed to build connected USB device");
            // Don't leak a connected-but-orphaned device on a late failure: tear down
            // the transport, and unregister (on the UI thread) if it was published.
            if (usbDevice != null)
            {
                try { usbDevice.Disconnect(); } catch { /* best-effort teardown */ }
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () => ConnectionManager.Instance.UnregisterConnectedDevice(usbDevice));
                }
                catch { /* best-effort */ }
            }
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
