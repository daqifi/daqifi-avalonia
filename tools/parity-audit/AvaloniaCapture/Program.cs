using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
        "mobile-portrait-1-stream",
        "mobile-portrait-2-channels",
        "mobile-portrait-3-storage",
        "mobile-portrait-4-profiles",
        "mobile-portrait-5-settings",
        "mobile-landscape-1-stream",
        "mobile-landscape-2-channels",
        "mobile-landscape-3-storage",
        "mobile-landscape-4-profiles",
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
            CaptureDesktop(desktop.MainWindow);
            CaptureMobile();
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

        // Never capture after a navigation that did not happen. Set() returning false means the
        // property is gone or read-only, and the window is therefore still showing the PREVIOUS
        // screen — capturing anyway would write that frame under this screen's name, which is
        // worse than not capturing it: a missing screen is caught by VerifyExpectedScreens, and
        // a mislabelled one passes every check this tool has while quietly lying about what the
        // UI looks like. Skipping the capture converts it into the case that IS caught.
        var tabs = new[] { "livegraph", "loggeddata", "channels", "devices", "profiles" };
        for (var i = 0; i < tabs.Length; i++)
        {
            var name = $"desktop-{i + 1}-{tabs[i]}";
            if (!Set(vm, "SelectedIndex", i))
            {
                _failed = true;
                Console.WriteLine($"[SKIP] {name}: SelectedIndex is not writable on the " +
                                  "view-model; not capturing the previous tab under this name");
                continue;
            }
            Capture(name, main);
        }

        if (!Set(vm, "SelectedIndex", 0))
        {
            _failed = true;
            Console.WriteLine("[FAIL] could not return to tab 0 before the drawer sweep; the " +
                              "drawer captures would show the wrong pane behind them");
            return;
        }
        Quiesce(main);
        SweepDrawer(main, vm, "IsAppSettingsOpen", "desktop-6-settings-drawer");
        SweepDrawer(main, vm, "IsNotificationsOpen", "desktop-7-notifications-flyout");
        SweepDrawer(main, vm, "IsLiveGraphSettingsOpen", "desktop-8-livegraph-settings-flyout");
        SweepDrawer(main, vm, "IsLogSummaryOpen", "desktop-9-summary-flyout");
    }

    private static void SweepDrawer(Window main, object vm, string prop, string name)
    {
        if (!Set(vm, prop, true))
        {
            _failed = true;
            Console.WriteLine($"[SKIP] {name}: no writable prop {prop}");
            return;
        }
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
        // The x=1059 flip in that table has the same signature as #201's, which Encode now
        // removes at its source by rendering the tree instead of reading the framebuffer — so
        // this line may well have been masking it rather than fixing it. It stays regardless:
        // the ablation above was not repeated after that change, and "probably redundant now"
        // is not a measurement.
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
        bool settled;
        // Report rather than stack-trace. Every other capture site funnels its failures through
        // [FAIL] + a non-zero exit, and this one is on the same render path (Encode asserts the
        // window painted its whole surface); an uncaught throw from BETWEEN screens would be the
        // one failure in this tool that arrives as a raw trace.
        try { (_, settled, _) = SettledFrame(w); }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] settling between screens: {ex.GetType().Name}: {ex.Message}");
            return;
        }
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
    // desktop window; the phone sizes for the mobile host), and they are what the framebuffer
    // capture produced, so every screen keeps the dimensions already recorded in the baselines.
    // Guarded rather than assumed: an unsized window would otherwise reach RenderTargetBitmap as
    // an ArgumentException from inside Avalonia, with nothing in it naming the capture site.
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
