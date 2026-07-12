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
            // @port: replace with the ported MainWindow once its step closes.
            desktop.MainWindow = new global::Avalonia.Controls.Window
            {
                Title = "DAQiFi (Avalonia port scaffold)",
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
