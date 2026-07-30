using Avalonia;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

namespace Daqifi.Avalonia;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by the visual designer.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current.Register<MaterialDesignIconProvider>();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
