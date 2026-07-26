// Projected by portomatic from Daqifi.Desktop.View.ProfilesPane over shared VM Daqifi.Desktop.ViewModels.ProfilesPaneViewModel.
//
// SKELETON code-behind for a projected mobile view. The binding contract
// it reproduces is fixed by the projection spec; layout is authored by the
// apply loop from the mobile dialect brief.
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Daqifi.Desktop.ViewModels;

namespace Daqifi.Avalonia.Views.Mobile;

public partial class ProfilesMobileView : UserControl
{
    private ProfilesPaneViewModel? _vm;

    public ProfilesMobileView()
    {
        InitializeComponent();
        // Orientation-adaptive Profiles pane (#13): portrait keeps the single-column
        // touch flow; landscape mimics the desktop ProfilesPane — a bottom status bar
        // (count + active/logging + ADD PROFILE), the ADD action relocated out of the
        // header into that bar, and (empty) the top header hidden so it doesn't collide
        // with the centered empty-state. Mirrors the SizeChanged→Bounds orientation
        // pattern used by MobileMainView/ChannelsMobileView. Re-runs on HasProfiles
        // changes too, since the empty↔populated header rule is orientation-AND-state
        // dependent.
        SizeChanged += (_, _) => ApplyLayout();
        DataContextChanged += (_, _) => HookViewModel();
        HookViewModel();
    }

    private void HookViewModel()
    {
        if (_vm is not null) { _vm.PropertyChanged -= OnViewModelChanged; }
        _vm = DataContext as ProfilesPaneViewModel;
        if (_vm is not null) { _vm.PropertyChanged += OnViewModelChanged; }
        ApplyLayout();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Null OR empty PropertyName = "everything changed" (the INotifyPropertyChanged
        // convention); HasProfiles flips the empty↔populated layout; IsDrawerOpen gates the
        // bottom bar and header so their ADD can't re-fire over the open form.
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is "HasProfiles" or "Profiles" or "IsDrawerOpen")
        {
            ApplyLayout();
        }
    }

    /// <summary>Landscape → desktop-style bottom status bar + ADD in the bar,
    /// header hidden when empty; portrait → ADD in the header, no bottom bar.</summary>
    private void ApplyLayout()
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) { return; }
        var landscape = b.Width > b.Height;
        var hasProfiles = _vm?.HasProfiles == true;
        var drawerOpen = _vm?.IsDrawerOpen == true;

        // Both the landscape bottom bar and the portrait header carry a "+ ADD PROFILE" that
        // re-fires OpenNewDrawer — which silently discards the in-progress form (or abandons a
        // pending EDIT without persisting it). On desktop the drawer is a full-pane overlay that
        // covers the whole pane, so re-firing is physically unreachable. Reproduce that here by
        // hiding BOTH the bottom bar and the header whenever the drawer is open: the fixed Row-0
        // header would otherwise stay pinned on-screen the entire time the drawer is open, and a
        // landscape→portrait rotation mid-EDIT would re-expose the header ADD. HeaderAddButton's
        // own !landscape gate still applies (inside the header) for the drawer-closed case.
        BottomBar.IsVisible = landscape && !drawerOpen;
        HeaderAddButton.IsVisible = !landscape;
        // Header shows in portrait always, and in landscape only when populated (the desktop
        // empty-state has no top header — dropping it lets the centered empty-state own the space);
        // hidden while the drawer is open (see above) so its ADD can't re-fire over the form.
        HeaderPanel.IsVisible = (!landscape || hasProfiles) && !drawerOpen;
    }
}
