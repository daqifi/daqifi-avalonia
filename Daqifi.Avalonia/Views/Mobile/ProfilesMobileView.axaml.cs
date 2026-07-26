// Projected by portomatic from Daqifi.Desktop.View.ProfilesPane over shared VM Daqifi.Desktop.ViewModels.ProfilesPaneViewModel.
//
// SKELETON code-behind for a projected mobile view. The binding contract
// it reproduces is fixed by the projection spec; layout is authored by the
// apply loop from the mobile dialect brief.
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Daqifi.Avalonia.Views.Mobile;

public partial class ProfilesMobileView : UserControl
{
    private INotifyPropertyChanged? _vm;

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
        _vm = DataContext as INotifyPropertyChanged;
        if (_vm is not null) { _vm.PropertyChanged += OnViewModelChanged; }
        ApplyLayout();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // null name = "everything changed"; HasProfiles flips the empty↔populated layout.
        if (e.PropertyName is null or "HasProfiles" or "Profiles") { ApplyLayout(); }
    }

    /// <summary>Landscape → desktop-style bottom status bar + ADD in the bar/empty-state,
    /// header hidden when empty; portrait → ADD in the header, no bottom bar.</summary>
    private void ApplyLayout()
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) { return; }
        var landscape = b.Width > b.Height;
        var hasProfiles = _vm?.GetType().GetProperty("HasProfiles")?.GetValue(_vm) is true;

        BottomBar.IsVisible = landscape;
        HeaderAddButton.IsVisible = !landscape;
        // Header shows in portrait always, and in landscape only when populated (the desktop
        // empty-state has no top header — dropping it lets the centered empty-state own the space).
        HeaderPanel.IsVisible = !landscape || hasProfiles;
    }
}
