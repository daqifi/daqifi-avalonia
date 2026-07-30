using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

namespace Daqifi.Avalonia.Android;

// NOTE: never write `Android.*`-qualified names inside this namespace — the enclosing
// `Daqifi.Avalonia.Android` namespace shadows the `Android` root namespace; unqualified
// names resolved via the usings above are safe.

/// <summary>
/// Hosts Avalonia's app-builder customization for the Android head.
/// </summary>
/// <remarks>
/// Avalonia 12 moved the builder hooks off the activity: <c>AvaloniaMainActivity</c> is now
/// non-generic and declares only <c>OnResume</c>/<c>OnDestroy</c>, while
/// <c>CreateAppBuilder</c>/<c>CustomizeAppBuilder</c> live on
/// <see cref="AvaloniaAndroidApplication{TApp}"/> (verified by reflection against
/// Avalonia.Android 12.0.5). The application object also outlives any single activity, which is
/// the correct scope for process-wide registrations like the icon provider — registering it per
/// activity would re-run on every recreation.
/// <para>
/// Activity-scoped registrations stay in <see cref="MainActivity"/>: they capture the Activity as
/// their Android <c>Context</c> and must not be bound to the longer-lived application object.
/// </para>
/// </remarks>
[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Register the Material Design icon pack so `i:Icon` (the desktop nav rail's mdi-* glyphs,
        // reused by the mobile landscape shell) renders on Android too.
        IconProvider.Current.Register<MaterialDesignIconProvider>();
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
