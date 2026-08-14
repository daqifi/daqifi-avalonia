using UIKit;

namespace Daqifi.Avalonia.iOS;

// NOTE: never write `Avalonia.*`-qualified names inside this namespace — the
// enclosing `Daqifi.Avalonia` namespace shadows the `Avalonia` root namespace,
// so `Avalonia.iOS.Foo` binds to `Daqifi.Avalonia.iOS.Foo` and fails to resolve.
// Unqualified names reached through the file-scoped usings above are safe. This
// is the same hazard the Android head documents for `Android.*`.

/// <summary>
/// Process entry point for the iOS head.
/// </summary>
/// <remarks>
/// iOS has no <c>Program.BuildAvaloniaApp</c> equivalent: UIKit owns the run
/// loop, so the builder customization lives on <see cref="AppDelegate"/> rather
/// than here. Passing a null principal class gets the default
/// <c>UIApplication</c>; the delegate is resolved from the type argument.
/// </remarks>
public static class Program
{
    private static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
