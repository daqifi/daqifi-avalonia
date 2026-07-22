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
/// Orientation-adaptive mobile shell. LANDSCAPE renders the desktop's left nav
/// rail + content (~1:1 with the desktop app); PORTRAIT falls back to a bottom
/// tab bar. The content area is SHARED between both layouts (Grid col 1, row 0) —
/// only the nav chrome swaps, so the live Stream view and the lazily-built panes
/// are never re-parented or torn down on rotation.
///
/// The Stream tab stays ALWAYS attached (IsVisible-toggled) so switching panes
/// never disposes its ViewModel / drops the connection. Secondary panes build
/// lazily + defensively: some shared pane ViewModels need the mobile data-layer
/// host (App.InitializeMobile); if one fails to construct, a placeholder shows
/// instead of crashing (only the Stream tab needs no data layer).
/// </summary>
public partial class MobileMainView : UserControl
{
    private Control? _storagePage;
    private Control? _channelsPage;
    private Control? _profilesPage;
    private Button[] _navButtons = Array.Empty<Button>();
    private Button[] _railButtons = Array.Empty<Button>();

    public MobileMainView()
    {
        InitializeComponent();
        _navButtons = [NavStream, NavStorage, NavChannels, NavProfiles];
        _railButtons = [RailStream, RailStorage, RailChannels, RailProfiles];
        ShowStream();
        SizeChanged += (_, _) => UpdateOrientation();
        UpdateOrientation();
    }

    /// <summary>Landscape → desktop-style left rail; portrait → bottom nav.</summary>
    private void UpdateOrientation()
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) { return; }
        var landscape = b.Width > b.Height;
        SideNav.IsVisible = landscape;
        BottomNav.IsVisible = !landscape;
    }

    private void ShowStream()
    {
        StreamView.IsVisible = true;
        SecondaryHost.IsVisible = false;
        SetActive(0);
    }

    private void ShowSecondary(Control page, int index)
    {
        SecondaryHost.Content = page;
        StreamView.IsVisible = false;
        SecondaryHost.IsVisible = true;
        SetActive(index);
    }

    /// <summary>Opens the DAQiFi support page via the platform launcher (the
    /// desktop top bar's help button; Process.Start on desktop → LaunchUriAsync
    /// cross-platform).</summary>
    private async void OnHelp(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(new Uri("https://www.daqifi.com/support"));
            }
        }
        catch
        {
            // best-effort: a missing browser / launcher must not crash the shell
        }
    }

    /// <summary>Opens the settings overlay (#11) — the mobile analog of the desktop
    /// settings drawer. Binds a standalone <see cref="SettingsViewModel"/> (backed by
    /// DaqifiSettings.Instance, no DaqifiViewModel host needed).</summary>
    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        SettingsOverlay.DataContext ??= new SettingsViewModel();
        SettingsOverlay.IsVisible = true;
    }

    private void OnCloseSettings(object? sender, RoutedEventArgs e) =>
        SettingsOverlay.IsVisible = false;

    private void OnStream(object? sender, RoutedEventArgs e) => ShowStream();

    private void OnStorage(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            // Storage tab = the desktop Logged Data pane's APP LOGS / DEVICE LOGS
            // pivot (#7); StorageMobileView hosts both halves. It builds its own
            // DataContexts, so no factory DataContext is set here.
            _storagePage ??= BuildPage(
                "Logged Data",
                static () => new Mobile.StorageMobileView()),
            1);

    private void OnChannels(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            _channelsPage ??= BuildPage(
                "Channels",
                static () => new ChannelsMobileView { DataContext = new ChannelsPaneViewModel() }),
            2);

    private void OnProfiles(object? sender, RoutedEventArgs e) =>
        ShowSecondary(
            _profilesPage ??= BuildPage(
                "Profiles",
                static () => new ProfilesMobileView { DataContext = new ProfilesPaneViewModel() }),
            3);

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

    /// <summary>Highlight the active pane in BOTH nav chromes (bottom bar + rail).</summary>
    private void SetActive(int index)
    {
        for (var i = 0; i < _navButtons.Length; i++)
        {
            _navButtons[i].Classes.Set("active", i == index);
        }
        for (var i = 0; i < _railButtons.Length; i++)
        {
            _railButtons[i].Classes.Set("active", i == index);
        }
    }
}
