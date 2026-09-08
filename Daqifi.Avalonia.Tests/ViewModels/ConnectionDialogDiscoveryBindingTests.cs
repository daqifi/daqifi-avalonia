using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the connection dialog's discovery bindings to the <b>attributes</b> they feed.
///
/// <para>
/// The half these tests used to carry — that the member named by each binding still exists on the
/// type the markup meets — is now the XAML compiler's, because <c>ConnectionDialog.axaml</c> declares
/// <c>x:DataType</c> and <c>x:CompileBindings="True"</c> (issue #323). A renamed, moved or deleted
/// member is a build error there, for every binding in the view at once rather than only the pinned
/// ones, and inside each <c>DataTemplate</c> against the item type rather than the view model. That
/// is strictly stronger than a substring search and it is why these theories are no longer the thing
/// standing between #317's kind of rewrite and a silently blank dialog.
/// </para>
///
/// <para>
/// What the compiler still does <b>not</b> check is which attribute a binding feeds: bound to
/// <c>Tag</c> instead of <c>ItemsSource</c>, or swapped between <c>Text</c> and <c>IsVisible</c> —
/// which is how the two error messages differ from each other — it type-checks and compiles just as
/// happily. That is the residual gap these anchored literals cover, and the reason they are kept
/// rather than deleted as redundant (issue #318 asked for the names; the attribute anchor is what
/// survives #323).
/// </para>
///
/// <para>
/// <see cref="The_view_declares_its_data_type"/> is the guard on the guard: nothing else in the build
/// notices <c>x:CompileBindings</c> being dropped, and dropping it would put every binding in the file
/// back on reflection with all of the above still green.
/// </para>
/// </summary>
public class ConnectionDialogDiscoveryBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/ConnectionDialog.axaml";

    /// <summary>
    /// The declarations that make every other binding in the view a compile-time fact.
    ///
    /// <para>
    /// Removing <c>x:CompileBindings="True"</c> silently downgrades all 37 bindings in the file back
    /// to runtime reflection, and desktop, iOS and every test in this assembly still pass — so this is
    /// the only thing in the build that notices.
    /// </para>
    ///
    /// <para>
    /// Asserted by <b>parsing</b> the markup rather than searching it, because the substring form of
    /// this guard does not hold. Measured on this view: delete <c>x:CompileBindings="True"</c> from the
    /// <c>Window</c> tag while the same literal survives in the prose comment a few lines below that
    /// explains the attribute, and a whole-file <c>Assert.Contains</c> still passes while a
    /// deliberately dead binding builds with <c>0 Error(s)</c>. XML forbids commenting an attribute out
    /// in place, but nothing stops the literal existing elsewhere in a file that discusses it at
    /// length. <see cref="BindingFacts.AssertRootDeclares"/> asks the parsed root element for the
    /// attribute, so only the real declaration counts (Qodo round 2 on PR #325).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:ConnectionDialogViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_view_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Each <c>DataTemplate</c>'s own item scope, pinned to <b>the list it belongs to</b>. Inside a
    /// template the <c>DataContext</c> is the item, not the view model, so a template that loses its
    /// <c>x:DataType</c> resolves against an inherited scope — the escape hatch #323 exists to remove.
    ///
    /// <para>
    /// The list is named rather than the type merely being looked for somewhere in the file: the three
    /// scopes as a set survive exchanging any two of them, so an any-of assertion would pass a WiFi list
    /// rendering itself as serial devices. The compiler happens to reject that swap today, but only
    /// because these device types expose different members — see
    /// <see cref="BindingFacts.AssertTemplateScopedTo"/> for the measurement and why it is not something
    /// to lean on.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DeviceList", "wifiDevice:DaqifiStreamingDevice")]
    [InlineData("SerialList", "serialDevice:SerialStreamingDevice")]
    [InlineData("HidList", "firmware:HeldBootloader")]
    public void Each_item_template_declares_its_own_scope(string listName, string itemType) =>
        BindingFacts.AssertTemplateScopedTo(View, listName, itemType);

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
        // The first half is the load-bearing one now: that the binding still feeds THIS attribute,
        // which the compiler has no opinion about. The second is kept as a cheap restatement of what
        // compiled bindings already require of the member — public, instance, readable — so that a
        // future decision to drop x:CompileBindings does not also silently drop this check.
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

            // A null or empty PropertyName is INotifyPropertyChanged's "all properties changed"
            // convention, and refreshes the gate's binding just as well as naming it. The app reads it
            // that way too (DeviceTileViewModel, ProfilesMobileView, DeviceLogsViewModel), so accepting
            // it here is what keeps this an assertion about the contract rather than about
            // [NotifyPropertyChangedFor] being the implementation of it.
            Assert.Contains(raised, name => name == gate || string.IsNullOrEmpty(name));
        }
        finally
        {
            viewModel.Close();
        }
    }
}
