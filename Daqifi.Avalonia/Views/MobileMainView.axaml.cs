using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Daqifi.Avalonia.Views.Mobile;
using Daqifi.Desktop.Common.Loggers;
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

    // Standalone, WPF-free notifications VM (like the mobile SettingsViewModel). Shared between the
    // top-bar bell badge and the Notifications overlay list; starts empty (no mobile producer yet, #11).
    private readonly MobileNotificationsViewModel _notifications = new();

    public MobileMainView()
    {
        InitializeComponent();
        // Both the bell badge (in TopCommandBar) and the overlay list bind to the one VM instance.
        TopCommandBar.DataContext = _notifications;
        NotificationsOverlay.DataContext = _notifications;
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
        // Only one full-screen overlay at a time — close the other so keyboard/switch-access
        // navigation can't leave both stacked (their scrims block touch but not Tab focus).
        NotificationsOverlay.IsVisible = false;
        SettingsOverlay.DataContext ??= new SettingsViewModel();
        SettingsOverlay.IsVisible = true;
    }

    private void OnCloseSettings(object? sender, RoutedEventArgs e) =>
        SettingsOverlay.IsVisible = false;

    /// <summary>Opens the notifications overlay (#11) — the mobile analog of the desktop bell +
    /// flyout. The overlay's DataContext (the shared notifications VM) is set in the ctor.</summary>
    private void OnNotifications(object? sender, RoutedEventArgs e)
    {
        // Mutually exclusive with the settings overlay (see OnSettings).
        SettingsOverlay.IsVisible = false;
        NotificationsOverlay.IsVisible = true;
    }

    private void OnCloseNotifications(object? sender, RoutedEventArgs e) =>
        NotificationsOverlay.IsVisible = false;

    /// <summary>Opens a notification's link (e.g. a firmware "learn more") via the platform launcher,
    /// mirroring the desktop flyout's link button. Only http/https schemes are launched (never file:,
    /// intent:, javascript:, …); failures are logged, not swallowed, and never crash the shell.</summary>
    private async void OnNotificationLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) { return; }

        // Restrict to web links — a notification's Link is data, so refuse a malformed URI or any
        // non-http(s) scheme. Distinct messages so a bad link is diagnosable.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            AppLogger.Instance.Warning($"Ignored malformed notification link: {url}");
            return;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            AppLogger.Instance.Warning($"Ignored notification link with unsupported scheme '{uri.Scheme}': {url}");
            return;
        }

        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null)
            {
                AppLogger.Instance.Warning("No platform launcher available to open a notification link.");
                return;
            }
            await launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            // Best-effort: a launch failure must not crash the shell, but log it for diagnosis.
            AppLogger.Instance.Error(ex, $"Failed to open notification link: {url}");
        }
    }

    private void OnStream(object? sender, RoutedEventArgs e) => ShowStream();

    private void OnStorage(object? sender, RoutedEventArgs e)
    {
        // Storage tab = the desktop Logged Data pane's APP LOGS / DEVICE LOGS pivot (#7);
        // StorageMobileView hosts both halves and builds its own DataContexts.
        _storagePage ??= BuildPage("Logged Data", static () => new Mobile.StorageMobileView());
        // Refresh the logged-session list on every visit: the pane is cached (built once),
        // so its ctor reload runs only once — a session logged since the last visit would
        // otherwise stay hidden until a manual Refresh.
        if (_storagePage is Mobile.StorageMobileView storage) { storage.Refresh(); }
        ShowSecondary(_storagePage, 1);
    }

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
