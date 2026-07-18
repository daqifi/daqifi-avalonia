using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Daqifi.Avalonia.Services;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.MaterialDesign;

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
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Register the platform discovery scope BEFORE Avalonia loads any
        // view, so the mobile shell's WiFi scan holds the MulticastLock —
        // without it Android power-save-filters the broadcast replies and
        // discovery silently finds nothing.
        NetworkDiscoveryScope.Current = new MulticastDiscoveryScope(this);
        // Register the USB (OTG) host connector so the mobile shell can offer a
        // "Connect via USB" affordance (experimental — see Usb/AndroidUsbStreamTransport).
        MobileUsbConnector.Current = new Usb.AndroidUsbConnector(this);
        base.OnCreate(savedInstanceState);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Register the Material Design icon pack so `i:Icon` (the desktop nav rail's
        // mdi-* glyphs, reused by the mobile landscape shell) renders on Android too.
        IconProvider.Current.Register<MaterialDesignIconProvider>();
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
