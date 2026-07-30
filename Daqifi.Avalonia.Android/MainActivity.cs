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
        // Registered here rather than in MainApplication alongside the icon provider because both
        // need an Android Context, and OnCreate is the first point one exists. Neither RETAINS the
        // activity: each constructor promotes its argument to ApplicationContext and keeps only
        // that plus a system service (WifiManager / UsbManager), so leaving these statics set
        // across an activity destroy+recreate cannot pin a dead MainActivity.
        NetworkDiscoveryScope.Current = new MulticastDiscoveryScope(this);
        // Register the USB (OTG) host connector so the mobile shell can offer a
        // "Connect via USB" affordance (experimental — see Usb/AndroidUsbStreamTransport).
        MobileUsbConnector.Current = new Usb.AndroidUsbConnector(this);
        base.OnCreate(savedInstanceState);
    }
}
