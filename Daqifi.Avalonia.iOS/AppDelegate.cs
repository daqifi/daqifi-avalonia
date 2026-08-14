using Avalonia;
using Avalonia.iOS;
using Foundation;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

namespace Daqifi.Avalonia.iOS;

// NOTE: see Program.cs — `Avalonia.*`-qualified names must not be written inside
// this namespace; the usings above are the safe path.

/// <summary>
/// Hosts Avalonia's app-builder customization for the iOS head.
/// </summary>
/// <remarks>
/// Unlike Android — where Avalonia 12 moved the builder hooks off the activity onto
/// <c>AvaloniaAndroidApplication&lt;TApp&gt;</c> — iOS keeps them on the delegate:
/// <c>AvaloniaAppDelegate&lt;TApp&gt;</c> is still generic and still declares
/// <c>CustomizeAppBuilder</c> (verified against the Avalonia.iOS 12.1.1 assembly).
/// So this one type is the iOS analogue of BOTH Android files.
/// <para>
/// No <c>NetworkDiscoveryScope.Current</c> registration here, deliberately. That hook
/// exists to hold an OS resource for the duration of a discovery sweep, and its one
/// implementation is Android's <c>MulticastLock</c> — iOS has no equivalent to hold.
/// iOS gates the same traffic through the Local Network *privacy permission* instead
/// (Info.plist <c>NSLocalNetworkUsageDescription</c>), which is granted by the user
/// once rather than acquired per sweep. Leaving it unset gets the interface's no-op.
/// </para>
/// <para>
/// <c>MobileUsbConnector.Current</c> is likewise left unset: iOS has no USB host path,
/// per recorded divergence DIV-UI-003. <c>IsAvailable =&gt; Current is not null</c> makes
/// the mobile shell hide the affordance for free.
/// </para>
/// </remarks>
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Register the Material Design icon pack so `i:Icon` (the mdi-* glyphs the
        // mobile landscape shell reuses from the desktop nav rail) renders on iOS.
        IconProvider.Current.Register<MaterialDesignIconProvider>();
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
