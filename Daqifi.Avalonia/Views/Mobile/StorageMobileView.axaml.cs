using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Daqifi.Desktop.Common.Loggers;
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
        // Lazily build the SD pane. Its DeviceLogsViewModel touches the device layer,
        // which can throw if that layer isn't ready — so fall back to a placeholder
        // rather than letting this click handler crash the app (mirrors the shell's
        // defensive BuildPage pattern).
        if (DeviceLogsHost.Content is null)
        {
            try
            {
                DeviceLogsHost.Content = new DeviceLogsMobileView { DataContext = new DeviceLogsViewModel() };
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "Mobile: failed to build the Device Logs pane");
                DeviceLogsHost.Content = new TextBlock
                {
                    Text = "Device logs are unavailable right now.",
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
        }
        AppLogsHost.IsVisible = false;
        DeviceLogsHost.IsVisible = true;
    }
}
