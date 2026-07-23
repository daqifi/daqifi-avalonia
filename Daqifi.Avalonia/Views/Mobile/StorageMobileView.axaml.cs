using Avalonia.Controls;
using Avalonia.Interactivity;
using Daqifi.Desktop.ViewModels;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Storage tab pivot host (#7): APP LOGS (locally-logged sessions) / DEVICE LOGS
/// (SD-card offload). Each half owns its own DataContext; the SD half is built
/// lazily on first open, mirroring the shell's lazy pane pattern.
/// </summary>
public partial class StorageMobileView : UserControl
{
    public StorageMobileView()
    {
        InitializeComponent();
        AppLogsHost.DataContext = new LoggedSessionsMobileViewModel();
    }

    private void OnAppLogs(object? sender, RoutedEventArgs e)
    {
        AppLogsHost.IsVisible = true;
        DeviceLogsHost.IsVisible = false;
    }

    private void OnDeviceLogs(object? sender, RoutedEventArgs e)
    {
        // Lazily build the SD pane (its DeviceLogsViewModel touches the device layer).
        DeviceLogsHost.Content ??=
            new DeviceLogsMobileView { DataContext = new DeviceLogsViewModel() };
        AppLogsHost.IsVisible = false;
        DeviceLogsHost.IsVisible = true;
    }
}
