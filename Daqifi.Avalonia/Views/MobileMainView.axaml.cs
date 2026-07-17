using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Daqifi.Avalonia.Views.Mobile;
using Daqifi.Desktop.ViewModels;

namespace Daqifi.Avalonia.Views;

/// <summary>
/// Mobile navigation shell: a bottom tab bar over the live streaming experience
/// plus the mobile panes portomatic projected from the desktop views + shared
/// ViewModels (Storage/Channels/Profiles). The Stream tab is the validated live
/// view, kept ALWAYS attached (IsVisible-toggled, never Content-swapped) so
/// switching tabs never tears down the device connection or its render timer.
///
/// Secondary panes build lazily. Their shared ViewModels (ChannelsPaneViewModel,
/// ProfilesPaneViewModel) reach LoggingManager.Instance, which resolves an EF Core
/// DbContext factory from App.ServiceProvider — now stood up on mobile by
/// App.InitializeMobile() (a minimal SQLite factory at the app-private path). So
/// they construct on mobile. <see cref="BuildPage"/> still wraps construction
/// defensively — if the data layer failed to initialize (e.g. an EF/SQLite runtime
/// issue on an unusual device), the pane shows a placeholder rather than crashing
/// the whole app; the Stream tab needs no data layer and is unaffected.
/// DeviceLogsViewModel (Storage) only touches the mobile-safe ConnectionManager and
/// is the SD-card offload pane the phone→DAQiFi USB transport lights up (WiFi shows
/// the "requires USB" state).
/// </summary>
public partial class MobileMainView : UserControl
{
    private Control? _storagePage;
    private Control? _channelsPage;
    private Control? _profilesPage;
    private Button[] _navButtons = Array.Empty<Button>();

    public MobileMainView()
    {
        InitializeComponent();
        _navButtons = [NavStream, NavStorage, NavChannels, NavProfiles];
        ShowStream();
    }

    private void ShowStream()
    {
        StreamView.IsVisible = true;
        SecondaryHost.IsVisible = false;
        SetActive(NavStream);
    }

    private void ShowSecondary(Control page, Button nav)
    {
        SecondaryHost.Content = page;
        StreamView.IsVisible = false;
        SecondaryHost.IsVisible = true;
        SetActive(nav);
    }

    private void OnStream(object? sender, RoutedEventArgs e) => ShowStream();

    private void OnStorage(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            _storagePage ??= BuildPage(
                "Device Storage",
                static () => new DeviceLogsMobileView { DataContext = new DeviceLogsViewModel() }),
            NavStorage);

    private void OnChannels(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            _channelsPage ??= BuildPage(
                "Channels",
                static () => new ChannelsMobileView { DataContext = new ChannelsPaneViewModel() }),
            NavChannels);

    private void OnProfiles(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            _profilesPage ??= BuildPage(
                "Profiles",
                static () => new ProfilesMobileView { DataContext = new ProfilesPaneViewModel() }),
            NavProfiles);

    /// <summary>
    /// Build a projected pane, or a placeholder if its shared ViewModel fails to
    /// construct (e.g. the mobile data layer didn't initialize). A safety net so a
    /// pane can never crash the whole app.
    /// </summary>
    private static Control BuildPage(string title, Func<Control> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            return BuildPlaceholder(title, ex);
        }
    }

    private static Control BuildPlaceholder(string title, Exception ex)
    {
        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(24),
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "This pane is temporarily unavailable — the on-device data layer "
                 + "didn't initialize. Streaming and Device Storage are unaffected.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 320,
        });
        return panel;
    }

    private void SetActive(Button active)
    {
        foreach (var b in _navButtons)
        {
            b.Classes.Set("active", ReferenceEquals(b, active));
        }
    }
}
