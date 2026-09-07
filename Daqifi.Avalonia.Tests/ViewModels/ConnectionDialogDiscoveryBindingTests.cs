using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the connection dialog's discovery bindings to the members that back them.
///
/// <para>
/// <c>ConnectionDialog.axaml</c> declares no <c>x:DataType</c>, so every binding on it is resolved by
/// reflection at runtime. Renaming, moving or deleting one of these members fails <b>silently</b> —
/// the tile list renders empty, or the "Scanning for USB devices…" overlay never appears or never
/// leaves — while both heads still build green and every other test still passes.
/// </para>
///
/// <para>
/// Added with the move of the dialog's discovery onto Core's <c>ContinuousDeviceFinder</c>, which
/// rewrote everything behind these members and touched none of their names. That is exactly the
/// change that would have been caught by nothing: the loop that fills these collections and sets
/// these messages was replaced wholesale, and the compiler has no view of whether the markup still
/// finds them.
/// </para>
///
/// <para>
/// Every expected binding is written with the attribute it feeds. <see cref="BindingFacts.AssertBinds"/>
/// is a substring match over the raw markup, so a bare <c>"{Binding AvailableWiFiDevices}"</c> keeps
/// passing after the binding is moved to a different attribute or a different control — including into
/// a <c>DataTemplate</c>, where it would rebind to the item rather than to the view model. Naming the
/// attribute costs nothing and closes that gap (issue #318).
/// </para>
/// </summary>
public class ConnectionDialogDiscoveryBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/ConnectionDialog.axaml";

    /// <summary>
    /// The three tabs' device lists, and the three members that gate each tab's animated
    /// "Scanning…" overlay.
    ///
    /// <para>
    /// WiFi and USB gate their overlay on the computed <c>Is*DiscoveryScanning</c> rather than on
    /// <c>HasNo*Devices</c>, so the overlay stops claiming to scan once discovery has given up
    /// (issue #290). The Firmware tab has no give-up state to report — the HID bootloader watcher is
    /// app-global and never stops — so it gates on <see cref="ConnectionDialogViewModel.HasNoHidDevices"/>
    /// directly. That asymmetry is deliberate; what matters here is that each tab's overlay is still
    /// wired to a member that exists.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ItemsSource=\"{Binding AvailableWiFiDevices}\"", nameof(ConnectionDialogViewModel.AvailableWiFiDevices))]
    [InlineData("ItemsSource=\"{Binding AvailableSerialDevices}\"", nameof(ConnectionDialogViewModel.AvailableSerialDevices))]
    [InlineData("ItemsSource=\"{Binding AvailableHidDevices}\"", nameof(ConnectionDialogViewModel.AvailableHidDevices))]
    [InlineData("IsVisible=\"{Binding IsWiFiDiscoveryScanning}\"", nameof(ConnectionDialogViewModel.IsWiFiDiscoveryScanning))]
    [InlineData("IsVisible=\"{Binding IsSerialDiscoveryScanning}\"", nameof(ConnectionDialogViewModel.IsSerialDiscoveryScanning))]
    [InlineData("IsVisible=\"{Binding HasNoHidDevices}\"", nameof(ConnectionDialogViewModel.HasNoHidDevices))]
    [InlineData("Text=\"{Binding WiFiDiscoveryError}\"", nameof(ConnectionDialogViewModel.WiFiDiscoveryError))]
    [InlineData("Text=\"{Binding SerialDiscoveryError}\"", nameof(ConnectionDialogViewModel.SerialDiscoveryError))]
    public void The_discovery_bindings_resolve_against_the_view_model(string binding, string memberName)
    {
        // Both halves, because either alone passes while the screen is broken: the markup still names
        // it on the attribute it is supposed to feed, and the runtime type still exposes it as
        // something a binding can read.
        BindingFacts.AssertBinds(View, binding);
        BindingFacts.AssertExposes(typeof(ConnectionDialogViewModel), memberName);
    }

    /// <summary>
    /// The error messages are shown through a converter on the same member the text binds to, so the
    /// visibility binding has to survive a rename too — a message set to a non-null string that no
    /// longer toggles anything visible is the same bug as no message at all.
    /// </summary>
    [Theory]
    [InlineData("IsVisible=\"{Binding WiFiDiscoveryError, Converter={StaticResource NotNullToVis}}\"")]
    [InlineData("IsVisible=\"{Binding SerialDiscoveryError, Converter={StaticResource NotNullToVis}}\"")]
    public void The_give_up_message_still_controls_its_own_visibility(string binding) =>
        BindingFacts.AssertBinds(View, binding);
}

/// <summary>
/// Pins the third leg of the same contract: that the two computed overlay gates are actually
/// re-raised when the state they are computed from changes.
///
/// <para>
/// <c>IsWiFiDiscoveryScanning</c> and <c>IsSerialDiscoveryScanning</c> are expressions over two
/// members each, and depend entirely on four <c>[NotifyPropertyChangedFor]</c> attributes to reach
/// the screen. Drop one and nothing above catches it: the markup still names the gate, the type
/// still exposes it, and the binding still resolves — the overlay simply keeps animating "Scanning
/// for USB devices…" over a discovery that has already given up, which is the exact defect
/// issue #290 was filed for. The wiring is correct today; these assertions are what keep it so.
/// </para>
///
/// <para>
/// Asserted through the observable contract rather than by looking for the attributes, so hand-written
/// notification would satisfy it too. In the <c>ConnectionManager</c> singleton collection because the
/// view model's constructor subscribes to that singleton; nothing here starts discovery, opens a port
/// or touches a socket.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class ConnectionDialogScanningOverlayRefreshTests
{
    [Theory]
    [InlineData(
        nameof(ConnectionDialogViewModel.HasNoWiFiDevices), false,
        nameof(ConnectionDialogViewModel.IsWiFiDiscoveryScanning))]
    [InlineData(
        nameof(ConnectionDialogViewModel.WiFiDiscoveryError), "WiFi discovery gave up.",
        nameof(ConnectionDialogViewModel.IsWiFiDiscoveryScanning))]
    [InlineData(
        nameof(ConnectionDialogViewModel.HasNoSerialDevices), false,
        nameof(ConnectionDialogViewModel.IsSerialDiscoveryScanning))]
    [InlineData(
        nameof(ConnectionDialogViewModel.SerialDiscoveryError), "USB discovery gave up.",
        nameof(ConnectionDialogViewModel.IsSerialDiscoveryScanning))]
    public void Changing_what_an_overlay_gate_is_computed_from_re_raises_the_gate(
        string sourceMember, object newValue, string gate)
    {
        var viewModel = new ConnectionDialogViewModel(null!, null);
        try
        {
            var raised = new List<string?>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            var source = typeof(ConnectionDialogViewModel).GetProperty(sourceMember);
            Assert.NotNull(source);
            source.SetValue(viewModel, newValue);

            Assert.Contains(gate, raised);
        }
        finally
        {
            viewModel.Close();
        }
    }
}
