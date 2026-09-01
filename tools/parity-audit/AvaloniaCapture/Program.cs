using System.Globalization;
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
        Quiesce(main);
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
        // The one-pixel flips are sub-perceptual and still exactly the class of difference a
        // visual gate must not manufacture: indistinguishable from a real one-pixel regression,
        // and enough to fail a byte-identity baseline half the time.
        Quiesce(main);
    }

    // Drive the window to a still frame and throw the frame away. Same settle loop the capture
    // path uses; the point is only the side effect of pumping until nothing moves.
    private static void Quiesce(Window w)
    {
        var (_, settled, _) = SettledFrame(w);
        if (!settled)
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

    private static byte[] Encode(Window w)
    {
        using var buffer = new MemoryStream();
        // Dispose the frame. CaptureRenderedFrame allocates a NEW caller-owned
        // WriteableBitmap per call (headless copies out of the locked framebuffer), and
        // the settle loop calls this 2-41 times per screen rather than once.
        //
        // Hygiene, not a crash fix: the pixel store is an UnmanagedBlob whose ctor calls
        // GC.AddMemoryPressure, so undisposed frames are GC-visible, provoke collection,
        // and are freed by finalizers. Dropping them is not an unbounded native leak, and
        // the pre-settle code did not dispose either. Deterministic release is still
        // right — it just keeps peak transient pressure (~5 MB per 1440x900 frame) off
        // the finalizer queue.
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
            Thread.Sleep(SettleSampleInterval);
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
            // InvariantCulture, not the ambient one: "N0" under a French locale emits a
            // NON-BREAKING SPACE (U+00A0) as the group separator, which is exactly the
            // non-ASCII the WSL/Windows console mangles — and it would also make this
            // line's grouping vary by machine, which run-to-run log comparison relies on
            // not doing.
            var size = written.Length.ToString("N0", CultureInfo.InvariantCulture);
            Console.WriteLine($"[OK]   {name}: {size} bytes, settled in {rounds} round(s)");
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
