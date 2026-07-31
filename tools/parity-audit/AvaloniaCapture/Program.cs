using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Daqifi.Avalonia.Views;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

// Parity-audit screenshot harness for the Avalonia port. Boots the REAL
// Daqifi.Avalonia app headless (Skia backend, no display) via the app's own DI
// bootstrap, then captures faithful PNGs of every desktop pane/drawer and the
// mobile shell in both orientations. DAQIFI_TEST_MODE=1 suppresses modal dialogs and
// skips hardware discovery; DAQIFI_DATA_DIR isolates the DB/logs to a throwaway dir.
//
// Usage:  AvaloniaCapture <output-dir>
// The output dir receives desktop-*.png and mobile-*.png. See ../README.md.

internal static class AvaloniaCapture
{
    private static string _outDir = "";
    private static bool _failed;   // any [FAIL] → non-zero exit so run.sh aborts the pipeline

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
            // "done ->" line. This project pins RuntimeIdentifier=win-x64, so launched from WSL it
            // executes on the WINDOWS .NET runtime through interop, and GetFullPath resolves a
            // Linux-style argument against a Windows root: `/tmp/shots` becomes `C:\tmp\shots`.
            // Echoing the raw argument back made that invisible — the tool reported writing 18 files
            // to /tmp/shots while every byte landed on the Windows filesystem, and `ls /tmp/shots`
            // showed an empty directory (port #74).
            _outDir = Path.GetFullPath(requestedOutDir);
            if (!string.Equals(_outDir, requestedOutDir, StringComparison.Ordinal))
            {
                Console.WriteLine($"[INFO] '{requestedOutDir}' resolved to '{_outDir}'");
                // ASCII only in console literals: this harness is normally run through WSL interop
                // on the Windows console, which mangles non-ASCII (an em-dash here printed as '-').
                Console.WriteLine("[INFO] under WSL a Linux-style path resolves against a Windows " +
                                  "root (win-x64 runtime): look for output there, or pass a " +
                                  "Windows path.");
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
            // Print the FULL exception chain (ToString), not just Message. A bad DAQIFI_DATA_DIR
            // surfaces here as a doubly-wrapped TypeInitializationException (AppDataPaths' static
            // initializer throws inside App's, since the diagnostic is built during type-init) whose
            // own Message is generic ("The type initializer for ... threw"); the actionable message —
            // the env var name and offending value — is on the innermost InvalidOperationException.
            // ToString() unwraps the whole chain so that diagnostic is actually visible.
            Console.WriteLine($"[FAIL] App boot: {ex}");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            CaptureDesktop(desktop.MainWindow);
            CaptureMobile();
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

    // ---- Desktop: the one MainWindow, swept across tabs + drawers via VM props ----
    private static void CaptureDesktop(Window? main)
    {
        if (main is null) { _failed = true; Console.WriteLine("[FAIL] MainWindow null"); return; }
        var vm = main.DataContext;
        if (vm is null) { _failed = true; Console.WriteLine("[FAIL] MainWindow.DataContext null"); return; }

        main.SizeToContent = SizeToContent.Manual;
        main.Width = 1440;
        main.Height = 900;
        if (!main.IsVisible) { main.Show(); }

        var tabs = new[] { "livegraph", "loggeddata", "channels", "devices", "profiles" };
        for (var i = 0; i < tabs.Length; i++)
        {
            Set(vm, "SelectedIndex", i);
            Capture($"desktop-{i + 1}-{tabs[i]}", main);
        }

        Set(vm, "SelectedIndex", 0);
        SweepDrawer(main, vm, "IsAppSettingsOpen", "desktop-6-settings-drawer");
        SweepDrawer(main, vm, "IsNotificationsOpen", "desktop-7-notifications-flyout");
        SweepDrawer(main, vm, "IsLiveGraphSettingsOpen", "desktop-8-livegraph-settings-flyout");
        SweepDrawer(main, vm, "IsLogSummaryOpen", "desktop-9-summary-flyout");
    }

    private static void SweepDrawer(Window main, object vm, string prop, string name)
    {
        if (!Set(vm, prop, true)) { Console.WriteLine($"[SKIP] {name}: no prop {prop}"); return; }
        Capture(name, main);
        Set(vm, prop, false);
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
        Resize(host, 384, 800);
        Capture("mobile-portrait-1-stream", host);
        NavClick(mobile, "NavChannels"); Capture("mobile-portrait-2-channels", host);
        NavClick(mobile, "NavStorage");  Capture("mobile-portrait-3-storage", host);
        NavClick(mobile, "NavProfiles"); Capture("mobile-portrait-4-profiles", host);
        NavClick(mobile, "NavStream");

        // Settings overlay (#11): toggle the named overlay visible + give it its VM.
        var overlay = mobile.FindControl<Control>("SettingsOverlay");
        if (overlay is not null)
        {
            overlay.DataContext ??= new Daqifi.Desktop.ViewModels.SettingsViewModel();
            overlay.IsVisible = true;
            Capture("mobile-portrait-5-settings", host);
            overlay.IsVisible = false;
        }

        // Landscape phone (desktop-style left rail per design intent)
        Resize(host, 820, 360);
        Capture("mobile-landscape-1-stream", host);
        NavClick(mobile, "RailChannels"); Capture("mobile-landscape-2-channels", host);
        NavClick(mobile, "RailStorage");  Capture("mobile-landscape-3-storage", host);
        NavClick(mobile, "RailProfiles"); Capture("mobile-landscape-4-profiles", host);
        NavClick(mobile, "RailStream");
    }

    // ---- helpers ----
    private static void Resize(Window w, int width, int height)
    {
        w.Width = width;
        w.Height = height;
        if (!w.IsVisible) { w.Show(); }
        Pump();
    }

    private static void NavClick(Control root, string name)
    {
        var btn = root.FindControl<Button>(name);
        if (btn is null) { Console.WriteLine($"[SKIP] nav {name}: not found"); return; }
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
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

    private static byte[] Encode(Window w)
    {
        using var buffer = new MemoryStream();
        // Dispose the frame: CaptureRenderedFrame returns a WriteableBitmap, which is
        // IDisposable over a native Skia-backed pixel surface the GC does not reclaim
        // promptly. The settle loop calls this 2-41 times PER SCREEN across 18 screens,
        // so dropping each one piles up hundreds of full-resolution native buffers in a
        // single run.
        using var frame = w.CaptureRenderedFrame();
        frame?.Save(buffer);
        return buffer.ToArray();
    }

    private static (byte[] Frame, bool Settled, int Rounds) SettledFrame(Window w)
    {
        Pump();
        var previous = Encode(w);
        for (var round = 1; round <= SettleMaxRounds; round++)
        {
            Pump();
            var current = Encode(w);
            if (current.Length == previous.Length && current.AsSpan().SequenceEqual(previous))
            {
                return (current, true, round);
            }
            previous = current;
        }
        return (previous, false, SettleMaxRounds);
    }

    private static void Capture(string name, Window w)
    {
        try
        {
            var (encoded, settled, rounds) = SettledFrame(w);
            if (encoded.Length == 0) { _failed = true; Console.WriteLine($"[FAIL] {name}: null frame"); return; }
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
            Console.WriteLine($"[OK]   {name}: {written.Length:N0} bytes, " +
                              $"settled in {rounds} round(s)");
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
