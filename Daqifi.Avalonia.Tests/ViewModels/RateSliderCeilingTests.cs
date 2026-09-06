using Daqifi.Core.Device;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins that the Devices pane's FREQUENCY slider offers the rate range the attached board says it
/// has, rather than a number written into the markup (issue #270).
///
/// <para>
/// Both device views carried a literal <c>Maximum="1000"</c>. That 1000 is Core's board-table
/// bootstrap value, correct only until the device describes itself: the bench Nq1 (fw 3.7.2)
/// publishes a schema-2 capability document stating <c>sample_rate_range_hz.max = 22000</c>, and
/// Core's <c>InitializeAsync</c> raises <c>MaxSamplingRate</c> to that during connect. The wrapper's
/// rate guard and Core's own setter both validate against that same figure, so the slider was the
/// one thing in the chain that could not ask for a rate the hardware supports.
/// </para>
///
/// <para>
/// The view half of these facts is deliberately textual, through <see cref="BindingFacts"/> —
/// neither device view declares an <c>x:DataType</c>, so their bindings are resolved by reflection
/// at runtime and a missing or misspelled member fails silently while both heads build green.
/// </para>
///
/// <para>
/// Scope: only the two views whose slider drives a connected device's live rate. The Profiles
/// pane's sliders edit a <c>ProfileDevice</c> — an <c>ObservableObject</c> loaded from XML with no
/// device behind it, which can name a board that is not connected and can appear several times in
/// one profile with different ceilings — so there is no device there to read a bound from. Their
/// separate problem, that the editor could not express a rate the profile already held, is
/// <see cref="Daqifi.Avalonia.Tests.Models.ProfileRateCeilingTests"/> (issue #274). A profile's
/// rate is clamped to the real ceiling when it is applied (PR #255), so nothing unsafe reaches the
/// hardware from either.
/// </para>
/// </summary>
public class RateSliderCeilingTests
{
    /// <summary>Repo-relative paths of the two views whose slider sets a connected device's rate.</summary>
    private const string DesktopDevicesView =
        "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/DevicesPanePrototype.axaml";

    private const string MobileDevicesView =
        "Daqifi.Avalonia/Views/Mobile/DevicesMobileView.axaml";

    #region The slider's bound comes from the device
    /// <summary>
    /// Desktop and mobile are two views over one view model and must not drift: both take the
    /// slider's upper bound from <c>MaxFrequencyHz</c>, which reads the selected device's
    /// advertised maximum.
    /// </summary>
    [Theory]
    [InlineData(DesktopDevicesView)]
    [InlineData(MobileDevicesView)]
    public void The_devices_pane_takes_its_rate_ceiling_from_the_device(string viewPath)
    {
        BindingFacts.AssertBinds(viewPath, "Maximum=\"{Binding MaxFrequencyHz}\"");

        BindingFacts.AssertExposes(typeof(DevicesPaneViewModel), "MaxFrequencyHz");
    }

    /// <summary>
    /// And no longer states one itself. A literal ceiling here is the bug: it is right for exactly
    /// one board, silently wrong for every other, and nothing downstream can tell that the rate the
    /// user wanted was never offered.
    /// </summary>
    [Theory]
    [InlineData(DesktopDevicesView)]
    [InlineData(MobileDevicesView)]
    public void Neither_devices_view_writes_a_rate_ceiling_of_its_own(string viewPath)
    {
        Assert.DoesNotContain("Maximum=\"1000\"", BindingFacts.Source(viewPath), StringComparison.Ordinal);
    }
    #endregion

    #region The bound the slider offers is the bound the device keeps
    /// <summary>
    /// The point of reading the ceiling from the device is that the top of the slider is reachable:
    /// the highest rate the control can produce must be a rate the wrapper stores unchanged, or the
    /// user is back to asking for something that silently becomes something else.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(7746)]
    [InlineData(22000)]
    public void The_top_of_the_advertised_range_is_a_rate_the_device_stores_unchanged(int advertised)
    {
        var device = DeviceAdvertising(advertised);

        Assert.Equal(advertised, device.MaxStreamingFrequency);

        device.StreamingFrequency = device.MaxStreamingFrequency;

        Assert.Equal(advertised, device.StreamingFrequency);
    }

    /// <summary>
    /// <c>MaxSamplingRate</c> is a mutable, unvalidated property on Core's capabilities, so a
    /// malformed or unread document can leave it at zero or below. The advertised ceiling is
    /// floored to 1 the way Core floors it — a slider bound of 0 would sit under its own
    /// <c>Minimum</c> of 1 and describe a range with nothing in it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void An_unusable_advertised_ceiling_still_leaves_one_hertz_offerable(int advertised)
    {
        var device = DeviceAdvertising(advertised);

        Assert.Equal(1, device.MaxStreamingFrequency);

        device.StreamingFrequency = device.MaxStreamingFrequency;

        Assert.Equal(1, device.StreamingFrequency);
    }

    /// <summary>
    /// The ceiling follows the device rather than the session. Core seeds it from the board table
    /// and replaces it when the capability document arrives, so the same wrapper reports a
    /// different bound before and after — which is exactly what the slider has to track.
    /// </summary>
    [Fact]
    public void The_advertised_ceiling_moves_when_the_device_re_describes_itself()
    {
        var device = DeviceAdvertising(1000);
        Assert.Equal(1000, device.MaxStreamingFrequency);

        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 22000 };

        Assert.Equal(22000, device.MaxStreamingFrequency);

        // And the rate the widened slider can now reach is one the device keeps.
        device.StreamingFrequency = 5000;
        Assert.Equal(5000, device.StreamingFrequency);
    }
    #endregion

    #region Helpers
    /// <summary>
    /// A concrete wrapper advertising <paramref name="maxSamplingRate"/>. Nothing here connects or
    /// sends: both properties under test read <c>Metadata.Capabilities</c> and no transport.
    /// </summary>
    private static SerialStreamingDevice DeviceAdvertising(int maxSamplingRate)
    {
        var device = new SerialStreamingDevice("COM-TEST-270");
        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = maxSamplingRate };
        return device;
    }
    #endregion
}
