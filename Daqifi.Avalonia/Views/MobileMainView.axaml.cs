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
/// Secondary panes build lazily and DEFENSIVELY: some shared pane ViewModels
/// (ChannelsPaneViewModel, ProfilesPaneViewModel) reach LoggingManager.Instance,
/// which resolves an EF Core DbContext factory from the DESKTOP DI host — a host
/// the mobile bootstrap doesn't start yet. Constructing them therefore throws on
/// mobile today; <see cref="BuildPage"/> catches that and shows a placeholder
/// instead of crashing the app. DeviceLogsViewModel (Storage) only touches the
/// mobile-safe ConnectionManager singleton, so it works now — and it's the SD-card
/// offload pane that the phone→DAQiFi USB transport lights up (WiFi shows the
/// "requires USB" state).
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
    /// Build a projected pane, or a placeholder if its shared ViewModel can't yet
    /// construct on the mobile head. Keeps a mobile-unsafe pane from crashing the
    /// whole app; the pane goes live once the mobile data-layer host lands.
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
            Text = "Coming to mobile — this pane needs the on-device data layer. "
                 + "Streaming and Device Storage work now.",
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
