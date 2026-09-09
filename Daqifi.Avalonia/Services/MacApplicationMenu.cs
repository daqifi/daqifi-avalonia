using System;
using Avalonia;
using Avalonia.Controls;
using Daqifi.Desktop.Common;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Services;

namespace Daqifi.Avalonia.Services;

/// <summary>
/// Makes the first item of the macOS application menu name DAQiFi instead of the UI framework.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia's macOS backend builds a DEFAULT application menu and attaches it to the
/// <see cref="Application"/>, and that default's first item is the literal string
/// "About Avalonia" wired to Avalonia's own <c>AboutAvaloniaDialog</c> — both strings live in
/// the shipped Avalonia.Native / Avalonia.Dialogs assemblies. Everything else in that menu is
/// already correct, "Hide DAQiFi" included, because those are composed from
/// <see cref="Application.Name"/>. Only the About item names the framework.
/// </para>
/// <para>
/// So this REPLACES one item rather than supplying a menu. Supplying one does not work: by the
/// time an application can run code against its own lifetime the default menu is already
/// attached, fully populated (About, Services, Hide, Hide Others, Show All, Quit) — measured
/// on Avalonia 12.1.1, printed from the live menu, not inferred — so anything that only fills
/// in a MISSING menu is a no-op here. Replacing the item also leaves the other five to the
/// platform, which localises and keyboard-binds them correctly and should keep doing so.
/// </para>
/// <para>
/// This is the third layer of the same macOS identity story as the .app bundle (#108): the
/// bundle's Info.plist names the app in Finder, <see cref="Application.Name"/> names the
/// process in the Dock and the menu bar, and this names it in the application menu. The name
/// is read from <see cref="Application.Name"/> rather than repeated as a literal so the three
/// cannot drift — <c>MacAppBundle.targets</c> already fails the publish if that value
/// disagrees with the bundle name.
/// </para>
/// </remarks>
internal static class MacApplicationMenu
{
    /// <summary>
    /// Replaces the framework's About item with one naming this application. A no-op off macOS,
    /// where no backend synthesises the menu this repairs.
    /// </summary>
    /// <remarks>
    /// Safe to call either side of the first window being shown: the macOS exporter re-reads
    /// the menu model when it changes, so an edit made after the window is up still lands.
    /// Verified both ways rather than assumed.
    /// </remarks>
    public static void Install(Application application)
    {
        if (!OperatingSystem.IsMacOS()) { return; }

        // No identity to advertise, so there is nothing to put in the item. Avalonia would be
        // calling the process "Avalonia Application" too at that point, which is a bigger
        // problem than this menu and one MacAppBundle.targets fails the publish over.
        var appName = application.Name;
        if (string.IsNullOrWhiteSpace(appName)) { return; }

        var menu = NativeMenu.GetMenu(application);
        if (menu is null)
        {
            menu = new NativeMenu();
            NativeMenu.SetMenu(application, menu);
        }

        var about = new NativeMenuItem($"About {appName}");

        // Fire-and-forget is forced: Click is a void-returning event and there is no result to
        // consume. It is safe because ShowAboutAsync cannot fault — its whole body is inside a
        // catch — so this discards a task that is always completed-or-logged, never an
        // unobserved exception.
        about.Click += (_, _) => _ = ShowAboutAsync(appName);

        // macOS puts About first, and so does the default menu being repaired. Matched on the
        // "About" prefix rather than the exact "About Avalonia": the point is to own whatever
        // About item is there, and a version that renames or localises its default should not
        // leave the app with two of them. Anything else in slot 0 (a separator, or a version
        // that stopped adding an About item at all) means ours is simply inserted.
        if (menu.Items.Count > 0 &&
            menu.Items[0] is NativeMenuItem { Header: { } header } &&
            header.StartsWith("About", StringComparison.Ordinal))
        {
            menu.Items[0] = about;
        }
        else
        {
            menu.Items.Insert(0, about);
        }
    }

    private static async Task ShowAboutAsync(string appName)
    {
        try
        {
            // Same resolution as the mobile shell's version text (#126), so the version read out
            // of this dialog, the one on the mobile shell and the Sentry release are one value. A
            // build with no version information says "dev" rather than a plausible "0.0.0".
            var version = AppVersion.Semantic ?? "dev";
#if DEBUG
            // Debug only, matching the mobile shell: #13 recorded that a git SHA is dev noise
            // rather than user-facing content, and it stays useful for build verification here.
            var build = AppVersion.ShortBuildMetadata is { } metadata ? $" ({metadata})" : string.Empty;
#else
            var build = string.Empty;
#endif

            // Awaited inside the try so a fault raised while the dialog is open — an owner
            // window closing under it, say — is logged like any other. Discarding the task at
            // the call site instead would leave that as an unobserved exception. The service
            // finds its own owner window and returns MessageBoxResult.None, rather than
            // throwing, when there is none.
            await new AvaloniaMessageBoxService().ShowAsync(
                $"{appName}\nVersion {version}{build}",
                $"About {appName}",
                MessageBoxButton.OK,
                MessageBoxImage.None);
        }
        catch (Exception ex)
        {
            // A throwing menu handler takes the process down on this backend, and nothing about
            // an About box is worth that.
            AppLogger.Instance.Error(ex, "Error showing the About dialog");
        }
    }
}
