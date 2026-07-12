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

        base.OnFrameworkInitializationCompleted();
    }
}
