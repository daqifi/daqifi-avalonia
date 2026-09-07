using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.View;
using Daqifi.Desktop.ViewModels;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

// Parity-audit screenshot harness for the Avalonia port. Boots the REAL
// Daqifi.Avalonia app headless (Skia backend, no display) via the app's own DI
// bootstrap, then captures faithful PNGs of every desktop pane/drawer, the
// mobile shell in both orientations, and the modal dialogs. DAQIFI_TEST_MODE=1
// suppresses modal dialogs and skips hardware discovery; DAQIFI_DATA_DIR isolates
// the DB/logs to a throwaway dir.
//
// Usage:  AvaloniaCapture <output-dir>
// The output dir receives desktop-*.png, mobile-*.png and dialog-*.png. See ../README.md.

internal static class AvaloniaCapture
{
    private static string _outDir = "";
    private static bool _failed;   // any [FAIL] → non-zero exit so run.sh aborts the pipeline

    // Every screen this harness is contracted to produce. Checked at the end of the run, and
    // that check is the point: without it a MISSING screen is the quietest possible failure.
    // Several capture sites can decline to fire without anything going wrong on the surface —
    // SweepDrawer prints [SKIP] when a VM property is gone, NavClick prints [SKIP] when a named
    // button is gone, and the mobile settings overlay was simply skipped in silence when the
    // named control was absent — and none of those set the failure flag. A screen dropped that
    // way then disappears from every downstream comparison too: the run-to-run determinism
    // check compares whatever both runs contain, so a consistently absent screen reads as a
    // clean "17/17 byte-identical" rather than as an incomplete capture. A gate that quietly
    // shrinks its own scope is worse than no gate, because it is believed.
    //
    // Adding a screen means adding its name here. That is deliberate: this list is the
    // contract, and the failure it produces when the two drift apart is the whole feature.
    private static readonly string[] ExpectedScreens =
    [
        "desktop-1-livegraph",
        "desktop-2-loggeddata",
        "desktop-3-channels",
        "desktop-4-devices",
        "desktop-5-profiles",
        "desktop-6-settings-drawer",
        "desktop-7-notifications-flyout",
        "desktop-8-livegraph-settings-flyout",
        "desktop-9-summary-flyout",
        // The same nine desktop screens at the window's own declared minimum size
        // (MainWindow.axaml: MinWidth=720 MinHeight=480), which is the size a user can
        // actually drag the window down to and the size at which clipping and overlap
        // happen. Read off the window rather than restated here — see MinimumWindowSize.
        "desktop-min-1-livegraph",
        "desktop-min-2-loggeddata",
        "desktop-min-3-channels",
        "desktop-min-4-devices",
        "desktop-min-5-profiles",
        "desktop-min-6-settings-drawer",
        "desktop-min-7-notifications-flyout",
        "desktop-min-8-livegraph-settings-flyout",
        "desktop-min-9-summary-flyout",
        "mobile-portrait-1-stream",
        "mobile-portrait-2-channels",
        "mobile-portrait-3-storage",
        "mobile-portrait-4-profiles",
        "mobile-portrait-5-settings",
        "mobile-landscape-1-stream",
        "mobile-landscape-2-channels",
        "mobile-landscape-3-storage",
        "mobile-landscape-4-profiles",
        // Dialogs. One name per seeded state, defined in DialogScreens() — keep the two lists
        // in step, which VerifyExpectedScreens enforces in both directions.
        "dialog-connect-wifi-scanning",
        "dialog-connect-usb-idle",
        "dialog-connect-usb-error",
        "dialog-connect-manual-usb-error",
        "dialog-export-configure",
        "dialog-export-failed",
        "dialog-firmware-uploading",
    ];

    // Names actually written, recorded by Capture only after the file is confirmed non-empty.
    private static readonly HashSet<string> _captured = new(StringComparer.Ordinal);

    [STAThread]
    public static void Main(string[] args)
    {
        var requestedOutDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");

        var desktop = new ClassicDesktopStyleApplicationLifetime
        {
            Args = args,
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        try
        {
            // Setup lives inside the try so any path error (a bad output-dir arg, GetFullPath on a
            // malformed value) reports the same "[FAIL]" + exit-1 controlled failure as a boot crash,
            // instead of an uncaught throw that bypasses the harness's diagnostic in a CI/tooling run.
            // That is why the resolution below is HERE and not up beside the argument parse: on a
            // malformed path GetFullPath throws, and outside this block it would take the process
            // down with a raw stack trace instead of the harness's diagnostic.
            //
            // Resolve ONCE and use the resolved path for everything after — including the closing
            // "done ->" line. run.sh drives this harness with the WINDOWS dotnet when it runs from
            // WSL (the WPF leg it runs alongside requires Windows at all), so there it executes on
            // the Windows .NET runtime through interop and GetFullPath resolves a Linux-style
            // argument against a Windows root: `/tmp/shots` becomes `C:\tmp\shots`.
            // The project itself is RID-less and runs natively on macOS/Linux, where no such
            // rewrite happens — but the WSL path is still live, so this stays.
            // Echoing the raw argument back made that invisible — the tool reported writing 18 files
            // to /tmp/shots while every byte landed on the Windows filesystem, and `ls /tmp/shots`
            // showed an empty directory (port #74).
            _outDir = Path.GetFullPath(requestedOutDir);
            if (!string.Equals(_outDir, requestedOutDir, StringComparison.Ordinal))
            {
                Console.WriteLine($"[INFO] '{requestedOutDir}' resolved to '{_outDir}'");
                // Only explain the WSL trap when this actually IS it: a POSIX-absolute argument
                // that came back non-POSIX. GetFullPath also rewrites the string for ordinary
                // normalization (a relative path, "..", trailing or forward slashes), and blaming
                // those on the Windows-root rewrite would send someone hunting for output on the
                // wrong filesystem when the resolved path is perfectly correct.
                //
                // ASCII only in console literals: this harness is normally run through WSL interop
                // on the Windows console, which mangles non-ASCII (an em-dash here printed as '-').
                if (requestedOutDir.StartsWith('/') && !_outDir.StartsWith('/'))
                {
                    Console.WriteLine("[INFO] this process is on the Windows runtime (run.sh " +
                                      "drives the Windows dotnet from WSL), so a Linux-style " +
                                      "path resolves against a Windows root: look for output " +
                                      "there, or pass a Windows path.");
                }
            }
            Console.WriteLine($"[INFO] output directory: {_outDir}");

            Directory.CreateDirectory(_outDir);

            Environment.SetEnvironmentVariable("DAQIFI_TEST_MODE", "1");
            // Isolate all app data (DB + logs) into a throwaway dir under the run's own output dir, so
            // a capture never reads or migrates the developer's real DAQiFiDatabase.db (#18:
            // DAQIFI_DATA_DIR override). Kept under _outDir (rather than the system temp) because it's
            // always a valid path on every host the harness runs on — notably WSL, where
            // Path.GetTempPath() returns a Windows path the Linux runtime can't resolve. Must be set
            // before the app bootstrap first touches AppDataPaths below. Already absolute: _outDir is
            // resolved at the top, and the app requires an absolute override to place the DB
            // unambiguously rather than against whatever cwd it inherits.
            Environment.SetEnvironmentVariable(
                "DAQIFI_DATA_DIR", Path.Combine(_outDir, ".appdata"));
            IconProvider.Current.Register<MaterialDesignIconProvider>();

            BuildAvaloniaApp().SetupWithLifetime(desktop);
            Console.WriteLine("[OK]   App boot completed");
        }
        catch (Exception ex)
        {
            // Print the FULL exception chain (ToString), not just Message. A bad DAQIFI_DATA_DIR now
            // arrives as a plain InvalidOperationException from App.Initialize's
            // AppDataPaths.ThrowIfDataDirectoryUnusable gate, so the actionable message — the env var
            // name and the offending value — is already on TOP (#127; before that it was a
            // doubly-wrapped TypeInitializationException whose own Message said only "The type
            // initializer for ... threw"). ToString() is still what prints the underlying filesystem
            // error beneath it, and it is what keeps every OTHER boot failure legible here.
            Console.WriteLine($"[FAIL] App boot: {ex}");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            RequireThemePinnedDark();
            CaptureDesktop(desktop.MainWindow);
            CaptureMobile();
            // LAST, and deliberately so. Every dialog here builds a real view-model, and those
            // constructors touch process-global state the other two phases read (the connection
            // manager's duplicate-device handler, its firmware-in-progress event). Running the
            // dialogs after both existing phases is what keeps the eighteen committed screens a
            // function of exactly the sequence that produced their baselines — the run order is
            // part of what a byte-identity manifest is measuring.
            CaptureDialogs();
            VerifyExpectedScreens();
        }
        finally
        {
            // Deterministic teardown: fire desktop.Exit so the app's Exit-wired
            // cleanup (e.g. AppLogger shutdown) runs, instead of leaving it to
            // process exit. We drive the lifetime on this thread (never Start), so
            // Shutdown can be called directly; pump once to let Exit handlers run.
            try { desktop.Shutdown(); Pump(); }
            catch (Exception ex) { Console.WriteLine($"[WARN] shutdown: {ex.Message}"); }
        }

        Environment.ExitCode = _failed ? 1 : 0;
        Console.WriteLine($"done -> {_outDir}");
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Daqifi.Avalonia.App>()
            .UseSkia()
            // Avalonia 12 decoupled text shaping from the renderer, so an explicit UseSkia() no
            // longer brings a shaper with it. Without this the capture renders without shaped text —
            // which a build cannot catch, and which would silently corrupt every parity montage this
            // tool exists to produce. The desktop head is unaffected: UsePlatformDetect wires it up.
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    /// <summary>
    /// Fails the run unless the app is still pinned to the Dark theme variant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every hash in the committed manifest is a DARK rendering, and nothing else in this repo
    /// says so. The pin is one attribute — <c>RequestedThemeVariant="Dark"</c> on
    /// <c>App.axaml</c>'s <c>Application</c> — and deleting it does not break the build, does not
    /// fail a test, and does not change how the app looks on a dark-mode desktop. It changes how
    /// the app looks on a LIGHT-mode one, because the variant then follows the platform.
    /// </para>
    /// <para>
    /// Measured on this harness (#253), which runs on a headless platform that reports
    /// <c>Light</c>: with the pin removed the capture is byte-identical to one taken with
    /// <c>ThemeVariant.Light</c> forced, and <b>10 of the 25 screens the manifest then listed
    /// changed</b> — the two flyouts with stock inputs in them, three dialogs, and five mobile
    /// screens. Those are the surfaces built from Fluent's own control chrome rather than from
    /// the app's <c>DesignTokens.axaml</c> brushes, and Fluent's chrome is the half of the app
    /// that has a light variant. The app's own tokens have none: <c>DesignTokens.axaml</c> ships
    /// <c>Dark</c> and <c>Default</c> carrying identical dark values and no <c>Light</c>
    /// dictionary at all, so the result is a hybrid — light controls on dark panels — that no
    /// user can reach while the pin is in place, and that every user on a light-mode desktop
    /// would get the moment it is not.
    /// </para>
    /// <para>
    /// So this is the whole of what a light-theme check can honestly assert here, and it is
    /// worth more than ten more PNGs would be: baselining that hybrid would freeze an
    /// unreachable rendering as "correct", while this catches the one edit that makes it
    /// reachable, for no screens and no run time. If the app ever grows a real light variant —
    /// a <c>Light</c> dictionary and a way for a user to select it — this is the assert to
    /// replace with light captures, and the ten screens above are the ones that will move.
    /// </para>
    /// </remarks>
    private static void RequireThemePinnedDark()
    {
        var app = Application.Current;
        if (app is null)
        {
            _failed = true;
            Console.WriteLine("[FAIL] theme: Application.Current is null, so the variant every " +
                              "screen below is captured in cannot be established");
            return;
        }

        var requested = app.RequestedThemeVariant;
        var actual = app.ActualThemeVariant;
        // Reported for context rather than asserted: it is a property of the HOST, and the whole
        // point of the pin is that the app ignores it. Headless answers Light, which is why a
        // dropped pin shows up here at all.
        var platform = app.PlatformSettings?.GetColorValues().ThemeVariant.ToString() ?? "(unknown)";
        Console.WriteLine($"[INFO] theme: RequestedThemeVariant={Describe(requested)}, " +
                          $"ActualThemeVariant={Describe(actual)}, platform reports {platform}");

        if (Equals(requested, ThemeVariant.Dark) && Equals(actual, ThemeVariant.Dark))
        {
            Console.WriteLine("[OK]   theme pinned to Dark; every screen below is a Dark rendering");
            return;
        }

        _failed = true;
        Console.WriteLine(
            $"[FAIL] theme: the app is not pinned to Dark (requested {Describe(requested)}, " +
            $"actual {Describe(actual)}). App.axaml sets RequestedThemeVariant=\"Dark\" and the " +
            "committed baselines are all Dark renderings, so either that pin was removed - in " +
            "which case the app now follows the host, and on a light-mode host 10 of these " +
            "screens render Fluent's light control chrome over the app's dark panels - or the " +
            "app grew a real light variant, in which case this check and the baselines both " +
            "need rethinking. Neither is a harness problem.");
    }

    // ThemeVariant.Default renders as an empty string, which reads as a missing value in a log
    // line rather than as the answer it is.
    private static string Describe(ThemeVariant? variant) =>
        variant?.ToString() is { Length: > 0 } text ? text : "Default";

    // The size every desktop screen has been captured at since this harness existed. Not the
    // app's own default (the view-model's Width/Height binding supplies that) - a fixed capture
    // size is what makes the bytes reproducible.
    private static readonly (int Width, int Height) DesktopSize = (1440, 900);

    // ---- Desktop: the one MainWindow, swept across tabs + drawers via VM props ----
    private static void CaptureDesktop(Window? main)
    {
        if (main is null) { _failed = true; Console.WriteLine("[FAIL] MainWindow null"); return; }
        var vm = main.DataContext;
        if (vm is null) { _failed = true; Console.WriteLine("[FAIL] MainWindow.DataContext null"); return; }

        main.SizeToContent = SizeToContent.Manual;
        SweepDesktop(main, vm, "desktop", DesktopSize);

        // The same sweep again at the window's own declared minimum (#253). Until this existed a
        // green visual gate said "the dark theme at 1440x900 did not change", which is a much
        // narrower claim than the gate looks like it is making: clipping and overlap happen at
        // the SMALL end, and nothing here had ever rendered the app there. The minimum is the
        // one other size the app itself names, so it is the one other size worth pinning - any
        // other number would be the harness inventing a viewport and gating its own choice.
        var minimum = MinimumWindowSize(main);
        if (minimum is null) { return; }
        SweepDesktop(main, vm, "desktop-min", minimum.Value);

        // Leave the window as the later phases have always found it. Nothing after this captures
        // MainWindow, but the mobile and dialog phases run against a process this one has been
        // mutating, and "put it back" is cheaper than establishing that they do not care.
        Resize(main, DesktopSize.Width, DesktopSize.Height);
    }

    /// <summary>
    /// The minimum window size, read off the window rather than restated here.
    /// </summary>
    /// <remarks>
    /// <c>MainWindow.axaml</c> declares <c>MinWidth="720" MinHeight="480"</c>, and that
    /// declaration is the definition of "minimum window size" for this app - a real platform
    /// window will not go below it, and every layout in the app has to hold there. Reading it
    /// back off the window keeps the capture and the constraint from drifting apart: change the
    /// AXAML and these screens re-render at the new size, which the baseline check then reports
    /// as a change, which is exactly right. Restating 720x480 in this file would let the two
    /// disagree silently, and a capture at a size the app does not name would gate nothing but
    /// the harness's own opinion.
    /// <para>
    /// Avalonia's default for both is 0, not NaN, so an app that declares no minimum arrives
    /// here as 0x0 and fails loudly instead of being captured at a fabricated size.
    /// </para>
    /// </remarks>
    private static (int Width, int Height)? MinimumWindowSize(Window main)
    {
        var width = main.MinWidth;
        var height = main.MinHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
        {
            _failed = true;
            Console.WriteLine(
                $"[FAIL] MainWindow declares no usable minimum size (MinWidth={width}, " +
                $"MinHeight={height}), so there is no minimum-size screen to capture. Either " +
                "MinWidth/MinHeight were removed from MainWindow.axaml - in which case the app " +
                "no longer has a minimum and these baselines should go with it - or they moved " +
                "somewhere this cannot see.");
            return null;
        }
        Console.WriteLine($"[INFO] minimum window size, read from MainWindow: " +
                          $"{(int)width}x{(int)height}");
        return ((int)width, (int)height);
    }

    /// <summary>
    /// Captures the five panes and the four drawers at one window size, named
    /// <c><paramref name="prefix"/>-1-livegraph</c> … <c><paramref name="prefix"/>-9-summary-flyout</c>.
    /// </summary>
    private static void SweepDesktop(Window main, object vm, string prefix, (int Width, int Height) size)
    {
        main.Width = size.Width;
        main.Height = size.Height;
        if (!main.IsVisible) { main.Show(); }

        // The end state every screen in this sweep is taken against. Resolved once here, after
        // the window is up so the SplitView's template exists to be read, and threaded through
        // every Capture and Quiesce below - see DesktopPaneAtRest.
        var paneAtRest = DesktopPaneAtRest(main, prefix);

        // A resize relayouts the whole tree, so the first capture of a sweep starts from motion
        // unless something waits for it - the same reason SweepDrawer quiesces after a close.
        Quiesce(main, paneAtRest);

        // Never capture after a navigation that did not happen. Set() returning false means the
        // property is gone or read-only, and the window is therefore still showing the PREVIOUS
        // screen — capturing anyway would write that frame under this screen's name, which is
        // worse than not capturing it: a missing screen is caught by VerifyExpectedScreens, and
        // a mislabelled one passes every check this tool has while quietly lying about what the
        // UI looks like. Skipping the capture converts it into the case that IS caught.
        var tabs = new[] { "livegraph", "loggeddata", "channels", "devices", "profiles" };
        for (var i = 0; i < tabs.Length; i++)
        {
            var name = $"{prefix}-{i + 1}-{tabs[i]}";
            if (!Set(vm, "SelectedIndex", i))
            {
                _failed = true;
                Console.WriteLine($"[SKIP] {name}: SelectedIndex is not writable on the " +
                                  "view-model; not capturing the previous tab under this name");
                continue;
            }
            Capture(name, main, paneAtRest);
        }

        if (!Set(vm, "SelectedIndex", 0))
        {
            _failed = true;
            Console.WriteLine("[FAIL] could not return to tab 0 before the drawer sweep; the " +
                              "drawer captures would show the wrong pane behind them");
            return;
        }
        Quiesce(main, paneAtRest);
        SweepDrawer(main, vm, "IsAppSettingsOpen",
                    $"{prefix}-6-settings-drawer", paneAtRest);
        SweepDrawer(main, vm, "IsNotificationsOpen",
                    $"{prefix}-7-notifications-flyout", paneAtRest);
        SweepDrawer(main, vm, "IsLiveGraphSettingsOpen",
                    $"{prefix}-8-livegraph-settings-flyout", paneAtRest);
        SweepDrawer(main, vm, "IsLogSummaryOpen",
                    $"{prefix}-9-summary-flyout", paneAtRest);
    }

    private static void SweepDrawer(
        Window main, object vm, string prop, string name, EndState? paneAtRest)
    {
        if (!Set(vm, prop, true))
        {
            _failed = true;
            Console.WriteLine($"[SKIP] {name}: no writable prop {prop}");
            return;
        }
        Capture(name, main, paneAtRest);
        Set(vm, prop, false);
        // Let the CLOSE finish before the next drawer opens. Set() pumps a fixed number of
        // times, which is enough to get the new content on screen but nowhere near enough to
        // finish an animation — and the last three drawers here are not three panels, they are
        // ONE SplitView (MainWindow.axaml binds IsPaneOpen to BoolOr of the three VM flags), so
        // whatever state a half-finished close left the pane in is the state the next open
        // starts from, and the error accumulates across the sweep.
        //
        // This line and SettleSampleInterval are two DIFFERENT fixes and both are load-bearing.
        // Ablated on macOS, repeat runs of the same binary at the same commit:
        //
        //   neither         (8 runs)   desktop-9-summary-flyout flipped between two encodings,
        //                              4 each way — 6 pixels differing by 1 down the pane's left
        //                              edge column (x=1059, the 1440-380 boundary)
        //   interval only   (10 runs)  desktop-9 holds, but desktop-7-notifications-flyout — the
        //                              FIRST user of that same SplitView — flips instead
        //   both            (12 runs)  18/18 screens byte-identical
        //
        // (This line alone was not measured separately; the interval is what stops the mid-fade
        // case described on SettleSampleInterval, so it is not optional regardless.)
        //
        // The x=1059 flip in that table has the same signature as #201's, which Encode now
        // removes at its source by rendering the tree instead of reading the framebuffer — so
        // this line may well have been masking it rather than fixing it. It stays regardless:
        // the ablation above was not repeated after that change, and "probably redundant now"
        // is not a measurement.
        //
        // The one-pixel flips are sub-perceptual and still exactly the class of difference a
        // visual gate must not manufacture: indistinguishable from a real one-pixel regression,
        // and enough to fail a byte-identity baseline half the time.
        Quiesce(main, paneAtRest);
    }

    // Drive the window to a still frame and throw the frame away. Same settle loop the capture
    // path uses; the point is only the side effect of pumping until nothing moves — and, when
    // the caller hands over an end state, until that end state holds.
    private static void Quiesce(Window w, EndState? endState = null)
    {
        bool settled;
        string? violation;
        // Report rather than stack-trace. Every other capture site funnels its failures through
        // [FAIL] + a non-zero exit, and this one is on the same render path (Encode asserts the
        // window painted its whole surface); an uncaught throw from BETWEEN screens would be the
        // one failure in this tool that arrives as a raw trace.
        try { (_, settled, _, violation) = SettledFrame(w, endState); }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] settling between screens: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (violation != null)
        {
            // Not fatal HERE, deliberately: nothing was saved, and the capture that follows
            // carries the same end state, so it will refuse to save a frame taken from this
            // state and will fail by name. Saying it here as well is what turns "screen X
            // failed" into "the pane had already not finished closing before screen X started".
            Console.WriteLine($"[WARN] between screens: {endState?.What} did not reach its end " +
                              $"state within {SettleMaxRounds} settle rounds - {violation}");
        }
        else if (!settled)
        {
            // Not fatal — nothing was saved. But a window that will not stop moving between
            // screens means the NEXT capture starts from a moving state, so say so rather
            // than letting it turn into an unexplained difference in someone's diff.
            Console.WriteLine($"[WARN] window still changing after {SettleMaxRounds} settle " +
                              "rounds between screens; the next capture starts from motion");
        }
    }

    // ---- Mobile: MobileMainView hosted at phone portrait + landscape sizes ----
    private static void CaptureMobile()
    {
        Daqifi.Desktop.App.InitializeMobile();   // idempotent; DI already up from desktop boot

        MobileMainView mobile;
        // Log the full exception (type + message + stack): a MobileMainView ctor
        // failure aborts the entire mobile capture, so the stack is the diagnostic.
        try { mobile = new MobileMainView(); }
        catch (Exception ex) { _failed = true; Console.WriteLine($"[FAIL] MobileMainView ctor: {ex}"); return; }

        var host = new Window { SizeToContent = SizeToContent.Manual, WindowDecorations = WindowDecorations.None };
        host.Content = mobile;

        // Sizes are the Samsung Galaxy A16's real *logical* content area (physical
        // 1080x2340 @ density 450 -> 2.8125x -> 384x832 logical, minus the status bar
        // and gesture/nav bar). Rendering at the true device size is what makes
        // "below the fold / clipped" issues (e.g. #15) reproduce headless instead of
        // silently fitting in an oversized viewport.
        // Portrait phone
        // Every capture below is gated on the navigation that selects it. NavClick returning
        // false means the named button is gone and the view is still on the PREVIOUS pane, so
        // capturing anyway would save that pane under this one's name — a duplicate, mislabelled
        // screenshot that satisfies the completeness check and every byte comparison downstream,
        // while being a picture of the wrong screen. That is the one failure a visual gate must
        // never produce. Skipping the capture turns it into a missing screen, which
        // VerifyExpectedScreens does catch.
        //
        // The bare NavClicks that return to Stream are gated too, for the same reason one step
        // removed: they set up the state the NEXT capture is taken from.
        Resize(host, 384, 800);
        Capture("mobile-portrait-1-stream", host);
        if (NavClick(mobile, "NavChannels")) { Capture("mobile-portrait-2-channels", host); }
        if (NavClick(mobile, "NavStorage"))  { Capture("mobile-portrait-3-storage", host); }
        if (NavClick(mobile, "NavProfiles")) { Capture("mobile-portrait-4-profiles", host); }

        // Back to Stream. This one is not followed by a capture of its own, but the two that
        // come after it — the settings overlay and the landscape Stream pane — are both taken
        // with it as their backdrop, so a failure here mislabels them rather than itself.
        var onStream = NavClick(mobile, "NavStream");

        // Settings overlay (#11): toggle the named overlay visible + give it its VM.
        var overlay = mobile.FindControl<Control>("SettingsOverlay");
        if (overlay is null)
        {
            _failed = true;
            Console.WriteLine("[SKIP] mobile-portrait-5-settings: no SettingsOverlay control");
        }
        else if (!onStream)
        {
            Console.WriteLine("[SKIP] mobile-portrait-5-settings: the view is not on Stream, so " +
                              "the overlay would be captured over the wrong pane");
        }
        else
        {
            overlay.DataContext ??= new Daqifi.Desktop.ViewModels.SettingsViewModel();
            overlay.IsVisible = true;
            Capture("mobile-portrait-5-settings", host);
            overlay.IsVisible = false;
        }

        // Landscape phone (desktop-style left rail per design intent)
        Resize(host, 820, 360);
        if (onStream)
        {
            Capture("mobile-landscape-1-stream", host);
        }
        else
        {
            Console.WriteLine("[SKIP] mobile-landscape-1-stream: the view is not on Stream");
        }
        if (NavClick(mobile, "RailChannels")) { Capture("mobile-landscape-2-channels", host); }
        if (NavClick(mobile, "RailStorage"))  { Capture("mobile-landscape-3-storage", host); }
        if (NavClick(mobile, "RailProfiles")) { Capture("mobile-landscape-4-profiles", host); }
        NavClick(mobile, "RailStream");
    }

    // ---- Dialogs: the Windows IDialogService shows, built by hand and seeded ----
    //
    // WHY THIS PHASE EXISTS (#213). XAML in this repo is not compile-checked: the views carry no
    // x:DataType, so every binding resolves by reflection at run time. A green build therefore
    // cannot tell you whether a TextBlock renders, whether its binding resolves, or whether a
    // StaticResource brush exists — and dialogs were the one family of surfaces this harness
    // never constructed, so nothing else could tell you either. Three changes in a row shipped
    // with an explicit "I did not render this dialog" disclosure before this existed.
    //
    // WHAT A SCENARIO MUST NOT DO. Nothing here may start device discovery. The dialog view-models
    // expose it as a separate call (ConnectionDialogViewModel.StartConnectionFinders), never from a
    // constructor, so building one is inert — and it must stay that way: a real SerialDeviceFinder
    // opens every DAQiFi VID/PID COM port on the machine, which would both render a real device's
    // serial number into a screen that is supposed to be byte-reproducible and interfere with a
    // board another process is using. Seed the bound collections directly instead, as these do.
    // (Constructing a SerialStreamingDevice is safe: its SerialPort is constructed, never opened.)
    private static void CaptureDialogs()
    {
        foreach (var screen in DialogScreens())
        {
            Window dialog;
            try
            {
                dialog = screen.Build();
            }
            catch (Exception ex)
            {
                // Log the whole exception: a view-model constructor that throws here is usually a
                // missing DI registration or a renamed member, and the stack is the diagnostic.
                _failed = true;
                Console.WriteLine($"[FAIL] {screen.Name}: building the dialog threw: {ex}");
                continue;
            }

            try
            {
                ShowForCapture(dialog, screen.Size);

                // Both gated, and neither followed by a capture when it fails, for the reason
                // NavClick is gated: a dialog left on the wrong tab, or one still animating, would
                // be saved under this screen's name. A missing screen is caught by
                // VerifyExpectedScreens; a mislabelled one passes every check this tool has.
                if (screen.Prepare != null && !screen.Prepare(dialog)) { continue; }
                if (!SettleIndeterminateProgress(dialog, screen)) { continue; }

                Quiesce(dialog);
                screen.Inspect?.Invoke(dialog);
                Capture(screen.Name, dialog);
            }
            catch (Exception ex)
            {
                _failed = true;
                Console.WriteLine($"[FAIL] {screen.Name}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Close through the window's own Closing handler rather than dropping the
                // reference, so each dialog's real teardown runs — ConnectionDialog's, for one,
                // calls the view-model's Close(). Exercising it is free coverage; skipping it
                // would leave this harness the only caller in the app that does not.
                try { dialog.Close(); Pump(); }
                catch (Exception ex) { Console.WriteLine($"[WARN] closing {screen.Name}: {ex.Message}"); }
            }
        }
    }

    /// <summary>One dialog state worth gating.</summary>
    private sealed class DialogScreen
    {
        /// <summary>Screen name, also the PNG filename. Must appear in <c>ExpectedScreens</c>.</summary>
        public required string Name { get; init; }

        /// <summary>
        /// Builds the window and seeds its view-model. Runs AFTER the app bootstrap, because the
        /// real dialog view-models resolve services out of the DI container.
        /// </summary>
        public required Func<Window> Build { get; init; }

        /// <summary>
        /// Capture size. Required, and it should be the size the window's own AXAML declares.
        /// </summary>
        /// <remarks>
        /// Not optional, because the obvious alternative — let a <c>SizeToContent</c> dialog size
        /// itself and shoot whatever layout produced — is measurably NOT what the app shows.
        /// <c>DuplicateDeviceDialog</c> declares <c>MinWidth="480" MinHeight="280"</c>; headless,
        /// its <c>Bounds</c>, <c>ClientSize</c>, <c>DesiredSize</c> and <c>Width</c>/<c>Height</c>
        /// all come back 460x248, i.e. under its own stated minimum, because nothing in the
        /// headless windowing stack applies a window's min/max the way a real platform window does.
        /// A capture at that size would be a picture of a dialog no user has. So the autosizing
        /// alert family (<c>ErrorDialog</c>, <c>SuccessDialog</c>, <c>DuplicateDeviceDialog</c>,
        /// <c>MessageDialog</c>) is deliberately NOT covered here yet — see README, "Dialogs".
        /// </remarks>
        public required (int Width, int Height) Size { get; init; }

        /// <summary>
        /// State that needs a realized visual tree — selecting a tab, reaching a templated part.
        /// Runs after <c>Show()</c>. Returns false to skip the capture rather than save the wrong
        /// screen under this name.
        /// </summary>
        public Func<Window, bool>? Prepare { get; init; }

        /// <summary>
        /// Opt in to freezing indeterminate progress animations instead of failing on them. See
        /// <see cref="SettleIndeterminateProgress"/>: prefer seeding the state that hides the
        /// animation, and reach for this only when the animation IS the state under test.
        /// </summary>
        public bool FreezeIndeterminateProgress { get; init; }

        /// <summary>
        /// Reads the state under test back out of the visual tree, separately from the pixels.
        /// </summary>
        public Action<Window>? Inspect { get; init; }
    }

    // Fixed, invented device identity, shared by every scenario that needs one. Literals rather
    // than anything discovered or generated: these strings are rendered into the PNG, so a value
    // that varied by host or by run would put the difference straight into the baseline.
    private const string SeedPortName = "COM7";
    private const string SeedDeviceName = "DAQiFi Nq1";
    private const string SeedSerialNumber = "1024";
    private const string SeedFirmwareVersion = "1.0.1.24";

    private static SerialStreamingDevice SeedSerialDevice() =>
        new(SeedPortName, SeedDeviceName, SeedSerialNumber, SeedFirmwareVersion);

    /// <summary>
    /// The connect dialog with one discovered USB device seeded into the list. Shared by the two
    /// USB scenarios so the only difference between their pixels is the error message.
    /// </summary>
    private static ConnectionDialog ConnectDialogWithOneSerialDevice(string? serialConnectError)
    {
        // The parameterless constructor is the one the app uses; it resolves IDialogService out of
        // the container, which is up because this phase runs after SetupWithLifetime.
        var vm = new ConnectionDialogViewModel();
        vm.AvailableSerialDevices.Add(SeedSerialDevice());
        // Hides the "Scanning for USB devices…" overlay — see SettleIndeterminateProgress. This is
        // the state a user is in whenever a device has been found, so it is the honest way to get a
        // still frame here rather than a workaround.
        vm.HasNoSerialDevices = false;
        vm.SerialConnectError = serialConnectError;
        return new ConnectionDialog { DataContext = vm };
    }

    // The connect dialog's own declared size (ConnectionDialog.axaml: Width 560, Height 500).
    private static readonly (int Width, int Height) ConnectDialogSize = (560, 500);

    private const string SerialConnectErrorText =
        "Could not connect to 'DAQiFi Nq1'. The device may be in use by another application " +
        "or not responding.";

    private const string ManualPortErrorText =
        "COM99 is not a serial port on this system.";

    private static IEnumerable<DialogScreen> DialogScreens()
    {
        // The empty state, which is what the dialog looks like every time it opens. The indeterminate
        // "Scanning…" bar IS this screen, so it is the one scenario that freezes rather than seeds.
        yield return new DialogScreen
        {
            Name = "dialog-connect-wifi-scanning",
            Size = ConnectDialogSize,
            Build = () => new ConnectionDialog { DataContext = new ConnectionDialogViewModel() },
            Prepare = w => SelectTab(w, "dialog-connect-wifi-scanning", "WiFi"),
            FreezeIndeterminateProgress = true,
        };

        yield return new DialogScreen
        {
            Name = "dialog-connect-usb-idle",
            Size = ConnectDialogSize,
            Build = () => ConnectDialogWithOneSerialDevice(serialConnectError: null),
            Prepare = w => SelectTab(w, "dialog-connect-usb-idle", "USB"),
        };

        // The pair that earns its place: with SerialConnectError null the error row collapses to
        // zero height, so comparing these two shows whether the message costs layout when absent.
        yield return new DialogScreen
        {
            Name = "dialog-connect-usb-error",
            Size = ConnectDialogSize,
            Build = () => ConnectDialogWithOneSerialDevice(SerialConnectErrorText),
            Prepare = w => SelectTab(w, "dialog-connect-usb-error", "USB"),
            Inspect = w => RequireRenderedText(
                w, "dialog-connect-usb-error", "SerialConnectError", SerialConnectErrorText),
        };

        yield return new DialogScreen
        {
            Name = "dialog-connect-manual-usb-error",
            Size = ConnectDialogSize,
            Build = () =>
            {
                var vm = new ConnectionDialogViewModel();
                // Order matters: OnManualPortNameChanged clears a stale error when the port name is
                // edited, so setting the name second would wipe the message this screen is about.
                vm.ManualPortName = "COM99";
                vm.ManualPortError = ManualPortErrorText;
                return new ConnectionDialog { DataContext = vm };
            },
            Prepare = w => SelectTab(w, "dialog-connect-manual-usb-error", "Manual USB"),
            Inspect = w => RequireRenderedText(
                w, "dialog-connect-manual-usb-error", "ManualPortError", ManualPortErrorText),
        };

        // A second dialog, and a second Window class, so this phase is a mechanism rather than one
        // dialog's special case. Its two states are picked by IsConfiguring / IsExportComplete, so
        // between them they show that a scenario can drive a view-model state machine and not just
        // set a string.
        yield return new DialogScreen
        {
            Name = "dialog-export-configure",
            Size = ExportDialogSize,
            Build = () => new ExportDialog { DataContext = SeedExportViewModel() },
        };

        // The result state, and specifically the FAILED one: it swaps the icon, the message and two
        // of the three buttons off a single bool, which is a lot of rendering to take on trust.
        yield return new DialogScreen
        {
            Name = "dialog-export-failed",
            Size = ExportDialogSize,
            Build = () =>
            {
                var vm = SeedExportViewModel();
                vm.ExportSucceeded = false;
                vm.ExportResultMessage = "Export failed: the destination folder is not writable.";
                vm.IsExportComplete = true;
                return new ExportDialog { DataContext = vm };
            },
        };

        // The bootloader dialog mid-flash. This is the one state in the app that a user could get
        // stuck in: the scrim covers every control the dialog has, including its own Cancel button,
        // and until issue #241 nothing under it offered a way out — a stalled flash meant killing
        // the app. The screen exists to gate the control that fixes that, which no other check in
        // the repo can see: the button's only reference is a reflection binding in AXAML, so a
        // rename would leave the build green and the scrim inescapable again.
        yield return new DialogScreen
        {
            Name = "dialog-firmware-uploading",
            Size = FirmwareDialogSize,
            Build = () =>
            {
                // The download service is stubbed because the real one is the only collaborator a
                // bare construction reaches: the constructor kicks off LoadFirmwareOptionsAsync,
                // which queries the firmware release feed. A capture must not depend on the
                // network, and a version string fetched at capture time would land in the baseline.
                // The update service comes from the container and is never called — no scenario
                // starts a flash, and starting one is on the destructive denylist besides.
                var vm = new FirmwareDialogViewModel(
                    hidDeviceName: "DAQiFi Bootloader",
                    firmwareDownloadService: new OfflineFirmwareDownloadService())
                {
                    UploadFirmwareProgress = 42,
                    IsFirmwareUploading = true,
                };
                return new FirmwareDialog { DataContext = vm };
            },
            Inspect = w => RequireRenderedText(
                w, "dialog-firmware-uploading", "FirmwareCancelUpload", "Cancel Upload"),
        };
    }

    // The firmware dialog's own declared size (FirmwareDialog.axaml: Width 500, Height 380).
    private static readonly (int Width, int Height) FirmwareDialogSize = (500, 380);

    /// <summary>
    /// A firmware download service that reports nothing to download. Every member returns the
    /// "no release" answer rather than throwing, so the dialog's best-effort option load finishes
    /// quietly and the harness never opens a socket.
    /// </summary>
    private sealed class OfflineFirmwareDownloadService : IFirmwareDownloadService
    {
        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<(string, string)?>(null);

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }
    }

    // The export dialog's own declared size (ExportDialog.axaml: Width 560, Height 390).
    private static readonly (int Width, int Height) ExportDialogSize = (560, 390);

    /// <summary>
    /// The export dialog's real view-model over a fixed session id, with a fixed destination path.
    /// </summary>
    /// <remarks>
    /// The path is a literal rather than anything resolved from the environment because it is
    /// RENDERED: a real user directory would put the capturing machine's home directory into the
    /// PNG and the baseline would then be per-developer. Nothing here reads the session out of the
    /// database — the id is only carried until an export is started, which no scenario does.
    /// </remarks>
    private static ExportDialogViewModel SeedExportViewModel() =>
        new(sessionId: 1) { ExportFilePath = "/Users/daqifi/Documents/session-1.csv" };

    // ---- dialog helpers ----

    // SizeToContent.Manual first, mirroring CaptureDesktop: several of these dialogs autosize by
    // default, and an autosizing window ignores an assigned Width/Height.
    private static void ShowForCapture(Window w, (int Width, int Height) size)
    {
        w.SizeToContent = SizeToContent.Manual;
        w.Width = size.Width;
        w.Height = size.Height;
        w.Show();
        Pump();
    }

    /// <summary>
    /// Selects the tab whose header is <paramref name="header"/>, or fails the run.
    /// </summary>
    /// <remarks>
    /// By header rather than by index. An index is silently wrong the moment a tab is inserted
    /// before it — and the wrong tab saved under the right filename is the one failure a visual
    /// gate must never produce, because it passes every downstream check while being a picture of
    /// something else. Matching on the header makes selecting and asserting the same act.
    /// The connect dialog's TabControl has no <c>x:Name</c>, hence the visual-tree search.
    /// </remarks>
    private static bool SelectTab(Window w, string screenName, string header)
    {
        var tabs = w.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is null)
        {
            _failed = true;
            Console.WriteLine($"[SKIP] {screenName}: no TabControl in the dialog's visual tree");
            return false;
        }

        var items = tabs.Items.OfType<TabItem>().ToArray();
        var match = items.FirstOrDefault(t => string.Equals(t.Header as string, header, StringComparison.Ordinal));
        if (match is null)
        {
            _failed = true;
            var found = items.Length == 0
                ? "none"
                : string.Join(", ", items.Select(t => $"'{t.Header}'"));
            Console.WriteLine($"[SKIP] {screenName}: no tab with header '{header}' (found: {found}); " +
                              "not capturing the previously selected tab under this name");
            return false;
        }

        tabs.SelectedItem = match;
        Pump();
        return true;
    }

    /// <summary>
    /// Deals with indeterminate <c>ProgressBar</c>s before the settle loop meets them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the trap that makes a naive dialog capture fail, and the failure does not point at
    /// its cause. An indeterminate progress bar never stops animating, so <c>SettledFrame</c> burns
    /// all 40 rounds and <c>Capture</c> correctly refuses to save a moving frame — the run reports
    /// "still changing after 40 settle rounds" and says nothing about a progress bar. The connect
    /// dialog puts one behind a "Scanning…" overlay on three of its five tabs, so it is the
    /// DEFAULT state of the dialog most likely to be captured next.
    /// </para>
    /// <para>
    /// Two remedies, and the choice is per scenario rather than global on purpose. The settle rule
    /// is load-bearing — CI has caught a real six-pixel race with it (#201) — so it is never
    /// loosened for everything.
    /// </para>
    /// <list type="number">
    /// <item>PREFERRED: seed the state that hides the animation, the way
    /// <see cref="ConnectDialogWithOneSerialDevice"/> puts a device in the list and clears
    /// <c>HasNoSerialDevices</c>. Nothing is faked — that is a state the app really has.</item>
    /// <item><c>FreezeIndeterminateProgress</c>, for a screen whose whole subject is the scanning
    /// state. A frozen bar renders as its empty track rather than as a moving pill, so the PNG is
    /// not what a user sees at any given instant; the [INFO] line below records that, so the empty
    /// track is never read as a rendering bug.</item>
    /// </list>
    /// <para>
    /// Anything else fails LOUDLY and by name, which is the point: the next person to add a dialog
    /// gets told what is moving instead of spending an hour on a settle-loop timeout.
    /// </para>
    /// </remarks>
    private static bool SettleIndeterminateProgress(Window w, DialogScreen screen)
    {
        // Only the bars actually on screen. IsEffectivelyVisible, not IsVisible: a TabControl
        // realizes one tab's content at a time, and a collapsed overlay's bar animates nothing.
        var live = w.GetVisualDescendants()
                    .OfType<ProgressBar>()
                    .Where(p => p.IsIndeterminate && p.IsEffectivelyVisible)
                    .ToArray();
        if (live.Length == 0) { return true; }

        if (!screen.FreezeIndeterminateProgress)
        {
            _failed = true;
            Console.WriteLine(
                $"[SKIP] {screen.Name}: {live.Length} indeterminate ProgressBar(s) are visible " +
                $"({DescribeBars(live)}). An indeterminate bar never stops animating, so the " +
                "settle loop would burn all its rounds and the capture would be refused as a " +
                "moving frame. Either seed the state that hides it (preferred - e.g. add a device " +
                "so HasNoSerialDevices goes false), or set FreezeIndeterminateProgress on this " +
                "scenario if the scanning state is what you are photographing.");
            return false;
        }

        foreach (var bar in live)
        {
            bar.IsIndeterminate = false;
        }
        Pump();
        Console.WriteLine(
            $"[INFO] {screen.Name}: froze {live.Length} indeterminate ProgressBar(s) " +
            $"({DescribeBars(live)}); each renders as its empty track, not as the moving " +
            "indicator a user sees.");
        return true;
    }

    private static string DescribeBars(IEnumerable<ProgressBar> bars) =>
        string.Join("; ", bars.Select(b => $"{b.Bounds.Width}x{b.Bounds.Height} at {b.Bounds.Position}"));

    /// <summary>
    /// Asserts that the element carrying <paramref name="automationId"/> is on screen, has non-zero
    /// bounds, and holds exactly <paramref name="expected"/> — and prints what it found either way.
    /// </summary>
    /// <remarks>
    /// The picture alone is not proof. A TextBlock whose binding silently failed to resolve and one
    /// that rendered an empty string are indistinguishable in a screenshot, and with no
    /// <c>x:DataType</c> anywhere in these views the first case is exactly what a renamed view-model
    /// member produces. So read the element back out of the tree and say so in the log, separately
    /// from the pixels. <c>AutomationProperties.AutomationId</c> rather than <c>x:Name</c> because
    /// these views already carry ids on the interesting elements, for the UI automation to use.
    /// </remarks>
    private static void RequireRenderedText(Window w, string screenName, string automationId, string expected)
    {
        var matches = w.GetVisualDescendants()
                       .OfType<Control>()
                       .Where(c => AutomationProperties.GetAutomationId(c) == automationId)
                       .ToArray();
        if (matches.Length == 0)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {screenName}: no element with AutomationId '{automationId}' " +
                              "in the visual tree");
            return;
        }
        // Ambiguity is a failure, not a first-match. An id that appears twice means this assert is
        // silently checking whichever one the tree walk reached first, which is exactly the kind of
        // "passes while measuring the wrong thing" the rest of this harness refuses to do.
        if (matches.Length > 1)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {screenName}: AutomationId '{automationId}' is on " +
                              $"{matches.Length} elements ({string.Join(", ", matches.Select(m => m.GetType().Name))}); " +
                              "an assert on it would be checking an arbitrary one of them");
            return;
        }
        var control = matches[0];

        var text = control switch
        {
            TextBlock block => block.Text,
            TextBox box => box.Text,
            ContentControl content => content.Content?.ToString(),
            _ => null,
        };
        var foreground = (control as TextBlock)?.Foreground
                         ?? (control as TemplatedControl)?.Foreground;

        Console.WriteLine($"[INFO] {screenName}: {automationId} is a {control.GetType().Name}, " +
                          $"IsVisible={control.IsEffectivelyVisible} Bounds={control.Bounds} " +
                          $"Foreground={foreground?.ToString() ?? "(none)"} Text=\"{text}\"");

        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {screenName}: {automationId} occupies no space on screen, so " +
                              "the capture does not show it");
            return;
        }
        if (!string.Equals(text, expected, StringComparison.Ordinal))
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {screenName}: {automationId} reads \"{text}\" but the scenario " +
                              $"seeded \"{expected}\" - the binding did not resolve to it");
        }
    }

    // ---- helpers ----
    private static void Resize(Window w, int width, int height)
    {
        w.Width = width;
        w.Height = height;
        if (!w.IsVisible) { w.Show(); }
        Pump();
    }

    // Returns whether the navigation actually happened, and fails the run when it did not.
    // Callers MUST gate their capture on this: the view is still on the previous pane, so a
    // capture taken anyway is a picture of the wrong screen filed under the right name.
    private static bool NavClick(Control root, string name)
    {
        var btn = root.FindControl<Button>(name);
        if (btn is null)
        {
            _failed = true;
            Console.WriteLine($"[SKIP] nav {name}: button not found; the pane it selects is not " +
                              "reachable, so nothing is captured for it");
            return false;
        }
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        return true;
    }

    private static bool Set(object target, string prop, object? value)
    {
        var p = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite) { return false; }
        p.SetValue(target, value);
        Pump();
        return true;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // A fixed number of pumps does NOT guarantee a settled frame: Avalonia transitions are
    // time-based, so whether a fade-in has finished when the shutter opens is a race. That race
    // silently corrupts the whole point of this tool. Measured on the Storage pane: two runs of
    // the SAME binary at the SAME commit differed on 65.6% of pixels (mean channel delta 10.3),
    // because one run caught the pane at ~50% opacity mid-fade and the other caught it settled.
    // Diffed against another Avalonia version that reads as a catastrophic visual regression.
    //
    // So instead of pumping a fixed number of times and hoping, pump until two consecutive frames
    // are byte-identical. Comparing encoded PNGs is exact and needs no pixel-buffer access.
    private const int SettleMaxRounds = 40;

    // ...and let REAL TIME pass between the two frames being compared, which is the half that
    // was missing. "Two consecutive samples are identical" only means nothing changed BETWEEN
    // THOSE SAMPLES. Avalonia's animation clock advances with wall time, not with pump count,
    // so two samples taken microseconds apart are identical for a transition sitting at 20%
    // opacity just as surely as for one that finished — and the loop stops on the first pair,
    // saving a half-drawn frame while reporting "settled in 1 round".
    //
    // Found on macOS by running the harness eight times and hashing: one run's
    // mobile-portrait-3-storage differed from the other seven on 81% of its pixels (249,600 of
    // 307,200). That is not a subtle artifact, it is the whole pane at the wrong opacity, and it
    // is the same shape as the 65.6% false positive above — which was diagnosed as a race and
    // then fixed with a settle loop that could still lose that race. Spacing the samples is what
    // makes them evidence: every Avalonia transition in this app is far longer than this
    // interval, so an unfinished one is guaranteed to move between two samples.
    //
    // 50 ms because it must exceed a frame and stay well under the shortest transition. The cost
    // is bounded and small: a settling screen takes 2-4 rounds (~100-200 ms), and only a screen
    // that never settles — which fails the capture rather than saving it — pays the full 2 s.
    private static readonly TimeSpan SettleSampleInterval = TimeSpan.FromMilliseconds(50);

    // ...and require the agreement to HOLD, which is the half that was missing after that.
    //
    // Spacing the samples makes "nothing changed" evidence only while the thing that could be
    // moving moves faster than a pixel per interval. An EASED transition does not, at its end:
    // the last 1% of a cubic ease-out crawls, so one 50 ms gap can pass with every pixel
    // rounding to the same value, and the loop declares victory on a tree that is still moving.
    //
    // Measured on this harness (#253), and it is not subtle. The three right-hand drawers are
    // one SplitView whose pane opens by animating PART_PaneRoot's width to OpenPaneLength=380.
    // Instrumented immediately after Capture returned, the pane's Bounds came back
    // `340, 0, 380, 447` on most runs and `344, 0, 376, 447` on others — settled, saved, and
    // four pixels short of open. That is ~1% of the animation, invisible as motion between two
    // samples and glaring in a byte comparison: 7,063 pixels differ, because a pane four pixels
    // narrower re-lays out every glyph inside it. It flipped roughly one run in three.
    //
    // Three samples rather than two, because the failure needs the crawl to look still TWICE in
    // a row across 100 ms rather than once across 50 ms. Cheap — one extra interval per screen
    // that settles first time — and it is the same argument the interval itself rests on, taken
    // one step further: two samples agreeing is not evidence unless something could have changed
    // between them, and at the tail of an ease, something could not.
    //
    // ...and that is where raising the constant stops working, which is why EndState below
    // exists and why this stayed at 3. Measured over 51 --determinism runs (#278): with three
    // samples the SplitView pane still saved 1-2 px short of OpenPaneLength in 5 runs, 9.8%,
    // and the rate went UP under CPU load — 2 of 39 idle, 3 of 12 loaded. That is the argument
    // against a fourth sample and against any fifth after it: under load what gets delayed is
    // the animation's own tick, so consecutive encodes match BECAUSE NOTHING WAS SCHEDULED
    // between them. Stillness gets cheaper to fake exactly when the host is busiest, and no
    // finite number of 50 ms samples bounds a scheduler stall. This constant is a good rule for
    // motion the harness cannot predict; it was never going to be one for motion it can.
    private const int SettleStableSamples = 3;

    /// <summary>
    /// A fact the app itself declares, that a capture must be able to READ BACK before its frame
    /// is kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything above this point infers "the transition finished" from "the pixels stopped
    /// changing". That inference has no right answer to check against, so it can only ever be
    /// made rarer, never sound — see the note on <see cref="SettleStableSamples"/>. Where a
    /// transition's end state is DECLARED, the harness does not have to infer anything: it can
    /// read the number the app committed to and compare.
    /// </para>
    /// <para>
    /// This is deliberately not a fallback and not a tolerance. Acceptance in
    /// <see cref="SettledFrame"/> is stillness AND this, so it is strictly stronger than the
    /// rule it joins; the wait is bounded by the <see cref="SettleMaxRounds"/> budget that was
    /// already there, so no new constant is introduced and there is nothing to tune. A frame
    /// that is still but not at the end state is not saved with a warning — it costs more
    /// rounds, and if the budget runs out the run FAILS by name with both numbers in it.
    /// </para>
    /// </remarks>
    private sealed class EndState
    {
        /// <summary>What is being waited for. Appears in the log line and the failure.</summary>
        public required string What { get; init; }

        /// <summary>
        /// Null once the end state holds; otherwise what the tree says instead, phrased so the
        /// failure names the observed value and the declared one.
        /// </summary>
        public required Func<string?> Violation { get; init; }
    }

    // Avalonia's SplitView template part that carries the pane, and the thing that actually
    // animates: its Width is driven by a DoubleTransition from 0 to OpenPaneLength.
    private const string PaneRootPart = "PART_PaneRoot";

    /// <summary>
    /// MainWindow's drawer pane is at the width the app declares for its current state — open at
    /// <c>OpenPaneLength</c>, or closed — rather than somewhere on the way there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one end state in the capture set that is knowable, and it is the one that has
    /// actually been recorded wrong. <c>MainWindow.axaml</c> hosts all three right-hand flyouts in
    /// ONE <c>SplitView</c> with <c>OpenPaneLength="380"</c>, so at 720 wide the open pane's left
    /// edge is 720-380 = 340 exactly. Instrumented across 51 runs (#278), the flaky captures sat
    /// at 341-342: settled, saved, and 1-2 px short — which re-lays out every centred glyph in the
    /// pane and moves 1.1-2.0% of the image, worst channel delta 222. #277 found the same defect at
    /// 4 px and made it smaller by requiring a third stable sample. It did not close it, and no
    /// count of samples can.
    /// </para>
    /// <para>
    /// Resolved once per sweep and closed over, so the per-round check is two property reads
    /// rather than a visual-tree walk. Ambiguity is a failure rather than a first-match, for the
    /// reason <see cref="RequireRenderedText"/> gives: a guard quietly checking an arbitrary one
    /// of two candidates is worse than no guard, because it is believed.
    /// </para>
    /// </remarks>
    private static EndState? DesktopPaneAtRest(Window main, string prefix)
    {
        var views = main.GetVisualDescendants().OfType<SplitView>().ToArray();
        if (views.Length != 1)
        {
            _failed = true;
            Console.WriteLine(
                $"[FAIL] {prefix} drawer end state: MainWindow's visual tree holds {views.Length} " +
                "SplitView(s), and this check is written for the one MainWindow.axaml declares. " +
                "With none, the three right-hand flyout screens are captured with nothing " +
                "watching their open animation; with several, the check would be reading an " +
                "arbitrary one of them.");
            return null;
        }

        var view = views[0];
        var panes = view.GetVisualDescendants()
                        .OfType<Control>()
                        .Where(c => string.Equals(c.Name, PaneRootPart, StringComparison.Ordinal))
                        .ToArray();
        if (panes.Length != 1)
        {
            _failed = true;
            Console.WriteLine(
                $"[FAIL] {prefix} drawer end state: the SplitView template holds " +
                $"{panes.Length} controls named '{PaneRootPart}'. That part is what animates " +
                "open, so without exactly one of it there is nothing to check the drawer " +
                "captures against.");
            return null;
        }

        var pane = panes[0];
        Console.WriteLine(
            $"[INFO] {prefix} drawer end state: {PaneRootPart} must be {view.OpenPaneLength} " +
            $"wide when the pane is open and {ClosedPaneLength(view)} when it is closed " +
            $"(OpenPaneLength={view.OpenPaneLength}, DisplayMode={view.DisplayMode}), read off " +
            "the SplitView rather than restated here");

        return new EndState
        {
            What = $"the SplitView drawer pane ({PaneRootPart})",
            Violation = () =>
            {
                var expected = view.IsPaneOpen ? view.OpenPaneLength : ClosedPaneLength(view);
                var actual = pane.Bounds.Width;
                // Bounds, not Width: Bounds is what layout gave the pane and therefore what the
                // pixels show, which is the thing a baseline records.
                //
                // Exact equality, with no epsilon, on purpose. A tolerance here would be the
                // same kind of dial as a sample count, and the whole point of reading a
                // DECLARED number back is that the comparison has a right answer — the pane is
                // laid out to OpenPaneLength exactly once the transition has run, which is what
                // every good frame in 51 measured runs shows (340 = 720 - 380, to the pixel).
                // NaN on either side falls through to a violation, which is correct: NaN is not
                // a width the app declares, and an unmeasured pane must not read as at rest.
                if (actual == expected) { return null; }
                return $"it is {actual} wide (Bounds={pane.Bounds}) but the pane is " +
                       (view.IsPaneOpen
                            ? $"OPEN, and MainWindow.axaml declares OpenPaneLength={expected}"
                            : $"CLOSED, which for DisplayMode={view.DisplayMode} is {expected}");
            },
        };
    }

    // What "closed" means for the pane's width, taken from Avalonia's own rule rather than
    // assumed: the two compact modes leave CompactPaneLength of the pane on screen, Inline and
    // Overlay collapse it to nothing. MainWindow uses Overlay, so this is 0 there today - but
    // reading it off DisplayMode is what stops this check quietly asserting the wrong number if
    // that attribute ever changes.
    private static double ClosedPaneLength(SplitView view) => view.DisplayMode switch
    {
        SplitViewDisplayMode.CompactInline or SplitViewDisplayMode.CompactOverlay
            => view.CompactPaneLength,
        _ => 0,
    };

    // 96 DPI, matching the headless framebuffer this capture path replaced, so the PNG's pHYs
    // chunk — and therefore its bytes — are unchanged for every screen whose pixels are.
    private static readonly Vector Dpi = new(96, 96);

    // A capture must be a function of the VISUAL TREE. It used to be a function of the tree AND
    // of how the surface happened to get that way, which is a different thing and is the hole
    // the settle loop above cannot see through.
    //
    // CaptureRenderedFrame() reads the headless FRAMEBUFFER: one persistent surface that the
    // compositor updates in place, a dirty region at a time. What is in it therefore depends on
    // the whole sequence of partial redraws that built it. The settle loop can only prove the
    // surface has STOPPED CHANGING — and a mosaic assembled by one sequence of redraws is every
    // bit as still as the same picture assembled by another. Both are settled. They are not the
    // same bytes.
    //
    // That distinction is the third defect this settle loop has been asked to absorb, and the
    // first one that is not about the tree still moving (#179's two were: a pane saved mid-fade,
    // and the shared SplitView reopened mid-close). Measured, on the capture set this harness
    // produces (#201):
    //
    //   With the SplitView pane open, the column immediately to its left — x=1059, the
    //   1440-minus-380 edge — is covered by more than one partial redraw, and 8-bit quantisation
    //   between passes leaves +/-1 wherever a content divider crosses that column. Exactly six
    //   pixels on each of the three flyout screens, always the same six, mixed sign, sub-
    //   perceptual, and enough to fail byte identity. On GitHub's macos-latest it flipped ~1
    //   capture run in 5 (7 of 30 across four jobs); on an M-series Mac it never flipped in 40.
    //   Waiting LONGER made it worse and produced a third encoding, because more time means more
    //   redraws, not fewer — which is why no settle rule and no timeout could have fixed it.
    //
    // So stop photographing the framebuffer and render the tree instead. RenderTargetBitmap
    // starts blank and is drawn exactly once, so the bytes returned here are a pure function of
    // what the window currently looks like. Measured against the previous path on the baseline
    // Mac: 15 of the 18 screens come out BYTE-IDENTICAL, and the only pixels that move are those
    // 18 — each one onto the value the rest of its own line already had.
    //
    // (Quiesce and SettleSampleInterval stay. They fix a different failure — a tree that is
    // genuinely still in motion — which this does nothing about.)
    private static byte[] Encode(Window w)
    {
        var size = RenderSize(w);

        using var rendered = new RenderTargetBitmap(size, Dpi);
        rendered.Render(w);

        // RenderTargetBitmap is premultiplied and starts fully transparent; the framebuffer it
        // replaced was opaque. Copying into an Opaque bitmap is what keeps the PNG three-channel,
        // and that is not cosmetic — it is what leaves 15 of the 18 committed baseline hashes
        // untouched, so the baseline diff for this change shows only the pixels the change is
        // about. It is sound only while the tree paints every pixel, because premultiplied colour
        // with its alpha dropped is DARKENED: an unpainted region would arrive as a plausible
        // dark patch rather than as an error. Checked below rather than assumed.
        using var opaque = new WriteableBitmap(size, Dpi, PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using (var locked = opaque.Lock())
        {
            rendered.CopyPixels(new PixelRect(size), locked.Address,
                                locked.RowBytes * locked.Size.Height, locked.RowBytes);
            RequireFullyOpaque(locked, size);
        }

        using var buffer = new MemoryStream();
        opaque.Save(buffer);
        return buffer.ToArray();
    }

    // The size to render at. These are the values this harness itself set (1440x900 for the
    // desktop window; the phone sizes for the mobile host; each dialog's own declared size), and
    // they are what the framebuffer capture produced, so every screen keeps the dimensions already
    // recorded in the baselines. Guarded rather than assumed: an unsized window would otherwise
    // reach RenderTargetBitmap as an ArgumentException from inside Avalonia, with nothing in it
    // naming the capture site.
    //
    // Every capture site here sizes its window explicitly, and that is a rule rather than an
    // accident — see the note on DialogScreen.Size about the SizeToContent dialogs.
    private static PixelSize RenderSize(Window w)
    {
        if (!double.IsFinite(w.Width) || !double.IsFinite(w.Height) || w.Width < 1 || w.Height < 1)
        {
            throw new InvalidOperationException(
                $"window has no usable size to capture ({w.Width} x {w.Height}); every capture " +
                "site must size the window before the shutter opens");
        }
        return new PixelSize((int)w.Width, (int)w.Height);
    }

    // Reads only the alpha byte of every pixel. Four bytes per pixel and alpha last are
    // Rgba8888's layout, which is the format Encode just constructed the target with; RowBytes
    // rather than Width*4 as the row stride, because the platform is free to pad.
    private static unsafe void RequireFullyOpaque(ILockedFramebuffer locked, PixelSize size)
    {
        for (var y = 0; y < size.Height; y++)
        {
            // (nint) on the row offset, not int: at these sizes it cannot overflow, but a
            // silently wrapped offset here would read someone else's memory rather than fail.
            var row = new ReadOnlySpan<byte>(
                (byte*)locked.Address + ((nint)y * locked.RowBytes), size.Width * 4);
            for (var i = 3; i < row.Length; i += 4)
            {
                if (row[i] != byte.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"pixel ({i / 4},{y}) rendered with alpha {row[i]}: the window left part of " +
                        "its surface unpainted, and saving premultiplied colour as opaque would " +
                        "darken it into something that looks like a real screenshot");
                }
            }
        }
    }

    private static (byte[] Frame, bool Settled, int Rounds, string? Violation) SettledFrame(
        Window w, EndState? endState = null)
    {
        Pump();
        var previous = Encode(w);
        // Consecutive samples that matched the one before them. The frame is accepted once
        // SettleStableSamples frames in a row are identical, i.e. after the agreement has
        // survived more than one sample interval — see the note on SettleStableSamples.
        var agreed = 1;
        for (var round = 1; round <= SettleMaxRounds; round++)
        {
            Thread.Sleep(SettleSampleInterval);
            Pump();
            var current = Encode(w);
            if (current.Length == previous.Length && current.AsSpan().SequenceEqual(previous))
            {
                if (++agreed >= SettleStableSamples)
                {
                    // Stillness is necessary and NOT sufficient — see the note on EndState. The
                    // end state is read here rather than one statement later, because nothing
                    // pumps between Encode and this line, so what it reports is a property of
                    // the very frame in `current` and not of some later state of the tree.
                    if (endState?.Violation() is null) { return (current, true, round, null); }

                    // Still enough, wrong state: keep going on the SAME budget. This is the
                    // whole change — the loop now has something to wait FOR, so a stalled
                    // animation costs rounds instead of silently producing a baseline.
                }
            }
            else
            {
                // Any change restarts the count, including one that undoes an earlier change:
                // the question is whether the tree moved at all since the frame being kept.
                agreed = 1;
            }
            previous = current;
        }
        // Out of rounds. Report the end state as it stands, so the caller can say which of the
        // two failures this was: a tree that never stopped moving, or one that stopped in a
        // state the app does not declare.
        return (previous, false, SettleMaxRounds, endState?.Violation());
    }

    private static void Capture(string name, Window w, EndState? endState = null)
    {
        try
        {
            var (encoded, settled, rounds, violation) = SettledFrame(w, endState);
            if (encoded.Length == 0) { _failed = true; Console.WriteLine($"[FAIL] {name}: null frame"); return; }
            if (violation != null)
            {
                // The frame is still, and wrong. Before this check it was saved — and a saved
                // wrong frame is a candidate baseline, which is strictly worse than a failed run.
                _failed = true;
                Console.WriteLine($"[FAIL] {name}: {endState?.What} never reached its end state " +
                                  $"within {SettleMaxRounds} settle rounds - {violation}. Not " +
                                  "saving: this frame is a picture of a transition that had not " +
                                  "finished, and it is indistinguishable from a layout regression.");
                return;
            }
            if (!settled)
            {
                // Never save a frame we know is still moving — that is the silent-corruption case.
                _failed = true;
                Console.WriteLine($"[FAIL] {name}: still changing after {SettleMaxRounds} settle " +
                                  "rounds; capture would be non-deterministic");
                return;
            }
            var path = Path.Combine(_outDir, name + ".png");
            File.WriteAllBytes(path, encoded);

            // Confirm the write instead of inferring it from "the call did not throw". The harness
            // reporting [OK] for captures that wrote zero bytes is precisely the failure this tool
            // must never have — it exists to be trusted about what the UI looked like.
            var written = new FileInfo(path);
            if (!written.Exists || written.Length == 0)
            {
                _failed = true;
                Console.WriteLine($"[FAIL] {name}: wrote {path} but it is " +
                                  (written.Exists ? "empty" : "missing"));
                return;
            }
            // InvariantCulture, not the ambient one: "N0" under a French locale emits a
            // NON-BREAKING SPACE (U+00A0) as the group separator, which is exactly the
            // non-ASCII the WSL/Windows console mangles — and it would also make this
            // line's grouping vary by machine, which run-to-run log comparison relies on
            // not doing.
            var size = written.Length.ToString("N0", CultureInfo.InvariantCulture);
            _captured.Add(name);
            Console.WriteLine($"[OK]   {name}: {size} bytes, settled in {rounds} round(s)");
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Turn "a screen is missing" from a silent shrink into a failed run. Reports BOTH
    // directions: a missing screen is the dangerous case, and an unexpected one means the
    // capture set and ExpectedScreens have drifted, which is the same bug seen from the other
    // side — a downstream comparison would treat the new name as an extra rather than as
    // coverage nobody approved.
    private static void VerifyExpectedScreens()
    {
        var missing = ExpectedScreens.Where(name => !_captured.Contains(name)).ToArray();
        var unexpected = _captured.Except(ExpectedScreens, StringComparer.Ordinal)
                                  .OrderBy(name => name, StringComparer.Ordinal).ToArray();

        if (missing.Length > 0)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {missing.Length} expected screen(s) were not captured: " +
                              string.Join(", ", missing) +
                              ". A [SKIP] or [FAIL] above says why; the run is incomplete, and an " +
                              "incomplete set must not be compared as if it were whole.");
        }
        if (unexpected.Length > 0)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {unexpected.Length} screen(s) were captured that " +
                              "ExpectedScreens does not list: " + string.Join(", ", unexpected) +
                              ". Add them there so the completeness check covers them.");
        }
        if (missing.Length == 0 && unexpected.Length == 0)
        {
            Console.WriteLine($"[OK]   all {ExpectedScreens.Length} expected screens captured");
        }
    }
}
