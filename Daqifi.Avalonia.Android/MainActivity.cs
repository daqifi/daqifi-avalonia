using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using Daqifi.Avalonia.Services;

namespace Daqifi.Avalonia.Android;

// NOTE: never write `Android.*`-qualified names inside this namespace —
// the enclosing `Daqifi.Avalonia.Android` namespace shadows the `Android`
// root namespace; unqualified names resolved via the usings above are safe.
[Activity(
    Label = "DAQiFi",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    // SingleTask, not the "standard" default: Avalonia's single-view lifetime owns ONE MainView
    // instance for the process, so a second activity instance builds a second
    // EmbeddableControlRoot that tries to adopt the MainView still parented to the first. That
    // throws InvalidOperationException ("already has a visual parent") on the UI thread and takes
    // the app down — reproduced on a Galaxy A16 / Android 16 by launching a second instance.
    //
    // The default allowed exactly that, and this app hands Android two ways to ask for it: the
    // launcher icon and the streaming foreground-service notification, whose content intent
    // targets this activity. SingleTask makes the "one instance" assumption Avalonia already
    // makes structurally true, so no caller can violate it by passing the wrong intent flags.
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
// Avalonia 12: AvaloniaMainActivity is non-generic and no longer carries the builder hooks —
// the app type and CustomizeAppBuilder moved to AvaloniaAndroidApplication<TApp>. See MainApplication.
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Register the platform discovery scope BEFORE Avalonia loads any
        // view, so the mobile shell's WiFi scan holds the MulticastLock —
        // without it Android power-save-filters the broadcast replies and
        // discovery silently finds nothing.
        //
        // Passing `this` does NOT retain the activity: each constructor promotes its argument to
        // ApplicationContext and keeps only that plus a system service (WifiManager / UsbManager),
        // so leaving these statics set across an activity destroy+recreate cannot pin a dead
        // MainActivity. That also means MainApplication — itself a Context, and constructed first —
        // would work just as well as a home for them; they sit here only because the ordering
        // requirement above is stated against the activity's own view load.
        NetworkDiscoveryScope.Current = new MulticastDiscoveryScope(this);
        // Register the USB (OTG) host connector so the mobile shell can offer a
        // "Connect via USB" affordance (experimental — see Usb/AndroidUsbStreamTransport).
        MobileUsbConnector.Current = new Usb.AndroidUsbConnector(this);
        // Run a foreground service whenever a device is connected, so an acquisition is not
        // torn down when the app leaves the screen. Attached here rather than in
        // MainApplication only for consistency with the registrations above; the coordinator
        // promotes to ApplicationContext and is idempotent across activity recreation.
        ForegroundServiceCoordinator.Attach(this);
        base.OnCreate(savedInstanceState);

        RequestNotificationPermissionIfNeeded();
    }

    /// <summary>
    /// Asks for POST_NOTIFICATIONS on API 33+ so the connected-device notification is visible.
    /// </summary>
    /// <remarks>
    /// Nothing depends on the answer: the foreground service runs and streaming keeps its
    /// background exemption either way, and a denial only hides the notification. It is
    /// requested here rather than at connect time because a Service cannot raise a permission
    /// prompt, and routing one back through the active activity would add lifecycle
    /// bookkeeping for a prompt the user can ignore with no functional loss.
    /// </remarks>
    private void RequestNotificationPermissionIfNeeded()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) { return; }

        const string permission = global::Android.Manifest.Permission.PostNotifications;
        if (CheckSelfPermission(permission) == Permission.Granted) { return; }

        RequestPermissions([permission], 0);
    }
}
