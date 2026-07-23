using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Daqifi.Avalonia.Views;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.MaterialDesign;

// Parity-audit screenshot harness for the Avalonia port. Boots the REAL
// Daqifi.Avalonia app headless (Skia backend, no display) via the app's own DI
// bootstrap, then captures faithful PNGs of every desktop pane/drawer and the
// mobile shell in both orientations. DAQIFI_TEST_MODE=1 suppresses modal dialogs
// and uses the per-user data dir.
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
        _outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(_outDir);

        Environment.SetEnvironmentVariable("DAQIFI_TEST_MODE", "1");
        IconProvider.Current.Register<MaterialDesignIconProvider>();

        var desktop = new ClassicDesktopStyleApplicationLifetime
        {
            Args = args,
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        try
        {
            BuildAvaloniaApp().SetupWithLifetime(desktop);
            Console.WriteLine("[OK]   App boot completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] App boot: {ex.GetType().Name}: {ex.Message}");
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

        var host = new Window { SizeToContent = SizeToContent.Manual, SystemDecorations = SystemDecorations.None };
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

    private static void Capture(string name, Window w)
    {
        try
        {
            Pump();
            var frame = w.CaptureRenderedFrame();
            if (frame is null) { _failed = true; Console.WriteLine($"[FAIL] {name}: null frame"); return; }
            var path = Path.Combine(_outDir, name + ".png");
            frame.Save(path);
            Console.WriteLine($"[OK]   {name}: {frame.PixelSize.Width}x{frame.PixelSize.Height}");
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
