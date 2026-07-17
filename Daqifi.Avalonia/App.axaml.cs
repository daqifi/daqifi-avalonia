using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Daqifi.Avalonia;

/// <summary>
/// Avalonia application bootstrap. The ported upstream App logic
/// (Daqifi.Desktop.App: logging, Sentry, migrations, main window wiring)
/// lands here as its apply-plan steps close.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Absorption seam: the ported Daqifi.Desktop.App startup host owns DI, database
            // migration, exception hooks, and MainWindow creation (upstream WPF OnStartup);
            // this bootstrap only hands it the desktop lifetime.
            Daqifi.Desktop.App.Initialize(desktop);
            desktop.Exit += (_, _) => Daqifi.Desktop.App.OnExit();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Mobile heads (Android/iOS, the five-platform goal) boot the
            // navigation shell — the desktop absorption host is desktop-only
            // (serial/WMI transports, firmware/HID services); mobile is WiFi/TCP
            // per the recorded DIV-UI-003 divergence and its UI ports
            // incrementally. InitializeMobile builds the MINIMAL DI the shared
            // pane ViewModels need (an IDbContextFactory<LoggingContext> at the
            // app-private SQLite path) so the projected Channels/Profiles panes
            // construct instead of throwing; MobileMainView then hosts the live
            // stream view plus the projected panes (Storage/Channels/Profiles).
            Daqifi.Desktop.App.InitializeMobile();
            singleView.MainView = new Views.MobileMainView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
