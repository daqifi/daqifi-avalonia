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
/// rewrote everything behind these six members and touched none of their names. That is exactly the
/// change that would have been caught by nothing: the loop that fills these collections and sets
/// these messages was replaced wholesale, and the compiler has no view of whether the markup still
/// finds them.
/// </para>
/// </summary>
public class ConnectionDialogDiscoveryBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/ConnectionDialog.axaml";

    /// <summary>
    /// The two device lists the tabs render, and the four members that decide what the tab says when
    /// a list is empty: the animated overlay's gate and the give-up message, per transport.
    /// </summary>
    [Theory]
    [InlineData("{Binding AvailableWiFiDevices}", nameof(ConnectionDialogViewModel.AvailableWiFiDevices))]
    [InlineData("{Binding AvailableSerialDevices}", nameof(ConnectionDialogViewModel.AvailableSerialDevices))]
    [InlineData("{Binding IsWiFiDiscoveryScanning}", nameof(ConnectionDialogViewModel.IsWiFiDiscoveryScanning))]
    [InlineData("{Binding IsSerialDiscoveryScanning}", nameof(ConnectionDialogViewModel.IsSerialDiscoveryScanning))]
    [InlineData("{Binding WiFiDiscoveryError}", nameof(ConnectionDialogViewModel.WiFiDiscoveryError))]
    [InlineData("{Binding SerialDiscoveryError}", nameof(ConnectionDialogViewModel.SerialDiscoveryError))]
    public void The_discovery_bindings_resolve_against_the_view_model(string binding, string memberName)
    {
        // Both halves, because either alone passes while the screen is broken: the markup still names
        // it, and the runtime type still exposes it.
        BindingFacts.AssertBinds(View, binding);
        BindingFacts.AssertExposes(typeof(ConnectionDialogViewModel), memberName);
    }

    /// <summary>
    /// The error messages are shown through a converter on the same member the text binds to, so the
    /// visibility binding has to survive a rename too — a message set to a non-null string that no
    /// longer toggles anything visible is the same bug as no message at all.
    /// </summary>
    [Theory]
    [InlineData("{Binding WiFiDiscoveryError, Converter={StaticResource NotNullToVis}}")]
    [InlineData("{Binding SerialDiscoveryError, Converter={StaticResource NotNullToVis}}")]
    public void The_give_up_message_still_controls_its_own_visibility(string binding) =>
        BindingFacts.AssertBinds(View, binding);
}
