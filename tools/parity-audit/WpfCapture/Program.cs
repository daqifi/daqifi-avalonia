using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfCapture;

// Parity-audit reference harness for the ORIGINAL WPF app (daqifi-desktop).
// Runs the real Daqifi.Desktop.App (its OnStartup builds DI + shows the
// MetroWindow), then drives SelectedIndex / flyout booleans on the VM and
// captures each state off-screen via RenderTargetBitmap. DAQIFI_TEST_MODE=1
// suppresses modal dialogs and uses the per-user data dir.
//
// Usage:  WpfCapture <output-dir>
// The output dir receives wpf-*.png. See ../README.md.
internal static class Program
{
    private static string _outDir = "";
    private static bool _failed;   // any [FAIL] → non-zero exit so run.sh aborts the pipeline

    [STAThread]
    public static void Main(string[] args)
    {
        _outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(_outDir);

        Environment.SetEnvironmentVariable("DAQIFI_TEST_MODE", "1");

        var app = new Daqifi.Desktop.App();
        app.InitializeComponent();   // load App.xaml (MahApps theme dictionaries + a DesignTokens shim, see Resources/)
        app.Startup += (_, _) =>
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { CaptureAll(app); }
                catch (Exception ex) { _failed = true; Console.WriteLine("[FAIL] capture: " + ex.Message); }
                finally { app.Shutdown(); }
            }), DispatcherPriority.ApplicationIdle);

        app.Run();
        Environment.ExitCode = _failed ? 1 : 0;
        Console.WriteLine($"done -> {_outDir}");
    }

    private static void CaptureAll(Application app)
    {
        var main = app.MainWindow;
        if (main is null) { _failed = true; Console.WriteLine("[FAIL] MainWindow null"); return; }

        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -4000; main.Top = 0;
        main.Width = 1440; main.Height = 900;
        main.WindowState = WindowState.Normal;
        Settle(main);

        var vm = main.DataContext;
        if (vm is null) { _failed = true; Console.WriteLine("[FAIL] DataContext null"); return; }

        // FlyoutWidth = Width - SidePanelWidth, and Width defaults to 800 until the
        // window-size binding propagates. Setting it directly ensures the flyout width
        // matches the real window (avoids a stale narrow flyout capture).
        Set(vm, "Width", 1440);
        Set(vm, "Height", 900);

        var tabs = new[] { "livegraph", "loggeddata", "channels", "devices", "profiles" };
        for (var i = 0; i < tabs.Length; i++)
        {
            Set(vm, "SelectedIndex", i);
            Settle(main);
            Snap(main, $"wpf-{i + 1}-{tabs[i]}");
        }

        Set(vm, "SelectedIndex", 0);
        Settle(main);
        Drawer(main, vm, "IsAppSettingsOpen", "wpf-6-settings-drawer");
        Drawer(main, vm, "IsNotificationsOpen", "wpf-7-notifications-flyout");
        Drawer(main, vm, "IsLiveGraphSettingsOpen", "wpf-8-livegraph-settings-flyout");
        Drawer(main, vm, "IsLogSummaryOpen", "wpf-9-summary-flyout");
    }

    private static void Drawer(Window main, object vm, string prop, string name)
    {
        if (!Set(vm, prop, true)) { Console.WriteLine($"[SKIP] {name}: no prop {prop}"); return; }
        Settle(main);
        Snap(main, name);
        Set(vm, prop, false);
        Settle(main);
    }

    private static bool Set(object target, string prop, object value)
    {
        var p = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite) { return false; }
        p.SetValue(target, value);
        return true;
    }

    // Flush layout + let bindings/OxyPlot render settle across a few dispatcher passes.
    private static void Settle(Window w)
    {
        for (var i = 0; i < 4; i++)
        {
            w.UpdateLayout();
            w.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            w.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
    }

    private static void Snap(Window w, string name)
    {
        try
        {
            var width = (int)Math.Ceiling(w.ActualWidth);
            var height = (int)Math.Ceiling(w.ActualHeight);
            if (width <= 0 || height <= 0) { _failed = true; Console.WriteLine($"[FAIL] {name}: size 0"); return; }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(w);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(_outDir, name + ".png"));
            enc.Save(fs);
            Console.WriteLine($"[OK]   {name}: {width}x{height}");
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
