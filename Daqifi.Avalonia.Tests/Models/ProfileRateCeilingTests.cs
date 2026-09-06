using System.ComponentModel;
using System.Text.RegularExpressions;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Models;

/// <summary>
/// Pins that the Profiles editor can express every rate a profile is allowed to hold (issue #274).
///
/// <para>
/// #273 raised the Devices pane's FREQUENCY slider from a literal <c>Maximum="1000"</c> to the
/// board's advertised ceiling — 22000 Hz on the bench Nq1 (fw 3.7.2) — so that pane can now be
/// driven to 5000 Hz. <c>ProfilesPaneViewModel.SaveCurrentSettings</c> copies the live device rate
/// into a new profile unfiltered, from the one <c>CAPTURE FROM CURRENT SETTINGS</c> button, so a
/// saved profile can now carry 5000 too. The edit drawer's own FREQ slider still stated
/// <c>Maximum="1000"</c>, and that combination is a rate the control that edits it cannot
/// represent: the thumb sat pinned at the right-hand end of the track while the read-out beside it
/// (a <c>Mode=OneWay</c> run, reporting the model correctly) said <c>5000 Hz</c>, and the first
/// drag collapsed the profile to at most 1000. Closing the drawer is how an edit is saved, so that
/// downgrade reached the file.
/// </para>
///
/// <para>
/// The fix is <see cref="ProfileDevice.MaxSamplingFrequency"/>: a per-entry ceiling that starts at
/// the conservative default and rises to cover whatever rate the entry takes on. It consults no
/// hardware — a profile is XML, it can name a board that is not connected, and one profile can
/// name several boards with different ceilings — so what it guarantees is narrow and worth stating
/// exactly: a rate already in a profile stays reachable on the slider that edits it.
/// </para>
/// </summary>
public sealed class ProfileRateCeilingTests : IDisposable
{
    private const string DesktopProfilesView = "Daqifi.Avalonia/Daqifi.Desktop/View/ProfilesPane.axaml";
    private const string MobileProfilesView = "Daqifi.Avalonia/Views/Mobile/ProfilesMobileView.axaml";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "daqifi-avalonia-tests", "profile-rate-ceiling-" + Guid.NewGuid().ToString("N"));

    public ProfileRateCeilingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    #region The ceiling covers the rate the profile holds
    /// <summary>
    /// A profile that has never been anywhere near a device still offers the conservative default,
    /// which is the figure the markup used to write down.
    /// </summary>
    [Fact]
    public void A_profile_entry_with_no_rate_yet_offers_the_conservative_default()
    {
        Assert.Equal(1000, ProfileDevice.DefaultMaxSamplingFrequency);
        Assert.Equal(ProfileDevice.DefaultMaxSamplingFrequency, new ProfileDevice().MaxSamplingFrequency);
    }

    /// <summary>
    /// The regression, stated as the property that was violated: whatever rate an entry holds, the
    /// slider bound to it can reach that rate. 5000 is the case from the ticket — captured from a
    /// board advertising 22000 — and 22000 is the top of that board's own range.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(22000)]
    public void Every_rate_a_profile_can_hold_is_reachable_on_the_slider_that_edits_it(int captured)
    {
        var entry = new ProfileDevice { SamplingFrequency = captured };

        Assert.True(
            entry.MaxSamplingFrequency >= entry.SamplingFrequency,
            $"A {captured} Hz profile is offered a ceiling of {entry.MaxSamplingFrequency} Hz.");
        Assert.Equal(Math.Max(captured, ProfileDevice.DefaultMaxSamplingFrequency), entry.MaxSamplingFrequency);
    }

    /// <summary>
    /// And it only ever rises. Recomputing the ceiling from the current rate would ratchet: every
    /// nudge downwards would drag the ceiling down with it and the rate could never be brought
    /// back up — a slider that quietly becomes one-way is a worse control than one that is merely
    /// too short.
    /// </summary>
    [Fact]
    public void Dragging_the_rate_down_does_not_drag_the_ceiling_down_with_it()
    {
        var entry = new ProfileDevice { SamplingFrequency = 5000 };

        entry.SamplingFrequency = 200;

        Assert.Equal(5000, entry.MaxSamplingFrequency);
    }

    /// <summary>
    /// The slider learns about the ceiling through a binding, so a rise that is not announced is a
    /// rise the control never sees.
    /// </summary>
    [Fact]
    public void A_raised_ceiling_announces_itself_so_the_binding_can_follow_it()
    {
        var entry = new ProfileDevice();
        var announced = new List<string?>();
        ((INotifyPropertyChanged)entry).PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        entry.SamplingFrequency = 5000;

        Assert.Contains(nameof(ProfileDevice.MaxSamplingFrequency), announced);
    }

    /// <summary>
    /// The ceiling is derived, never stored: nothing writes it to the profiles file, so a profile
    /// genuinely lowered to 200 Hz and reloaded is offered the default again rather than keeping a
    /// phantom 5000 for the life of the file.
    /// </summary>
    [Fact]
    public void The_ceiling_is_derived_from_the_saved_rate_rather_than_saved_alongside_it()
    {
        var profile = ProfileHolding(5000);
        var manager = NewManager();
        Assert.True(manager.SubscribeProfile(profile));

        Assert.Single(profile.Devices).SamplingFrequency = 200;
        Assert.True(manager.UpdateProfileInXml(profile));

        var reloaded = Assert.Single(Assert.Single(NewManager().LoadProfilesFromXml()).Devices);
        Assert.Equal(200, reloaded.SamplingFrequency);
        Assert.Equal(ProfileDevice.DefaultMaxSamplingFrequency, reloaded.MaxSamplingFrequency);
    }

    /// <summary>
    /// The whole user path, end to end and through the real writer and reader: a profile captured
    /// from a fast board is saved, the app is restarted, and the rate it comes back with is still
    /// reachable on the control the user will edit it with. This is the assertion that would have
    /// caught the ticket.
    /// </summary>
    [Fact]
    public void A_profile_captured_from_a_fast_board_survives_a_restart_still_editable()
    {
        Assert.True(NewManager().SubscribeProfile(ProfileHolding(5000)));

        var reloaded = Assert.Single(Assert.Single(NewManager().LoadProfilesFromXml()).Devices);

        Assert.Equal(5000, reloaded.SamplingFrequency);
        Assert.Equal(5000, reloaded.MaxSamplingFrequency);
    }
    #endregion

    #region The views ask the profile for the ceiling
    /// <summary>
    /// Desktop and mobile are two views over the same profile entry and must not drift: both take
    /// the FREQ slider's upper bound from the entry rather than stating one.
    /// </summary>
    [Theory]
    [InlineData(DesktopProfilesView)]
    [InlineData(MobileProfilesView)]
    public void Both_profiles_views_take_the_entry_ceiling_from_the_profile(string viewPath)
    {
        BindingFacts.AssertBinds(viewPath, "Maximum=\"{Binding MaxSamplingFrequency}\"");

        BindingFacts.AssertExposes(typeof(ProfileDevice), "MaxSamplingFrequency");
    }

    /// <summary>
    /// Stated structurally rather than as a whole-file search for <c>Maximum="1000"</c>, because
    /// one literal in each of these views is correct and stays: the new-profile form's slider
    /// writes a fresh <c>NewProfileFrequency</c> and has no saved rate it could destroy. What must
    /// never come back is the pairing — a slider that edits a profile's stored
    /// <c>SamplingFrequency</c> while writing down its own upper bound.
    /// </summary>
    [Theory]
    [InlineData(DesktopProfilesView)]
    [InlineData(MobileProfilesView)]
    public void No_slider_that_edits_a_saved_rate_writes_down_its_own_ceiling(string viewPath)
    {
        var editors = Sliders(BindingFacts.Source(viewPath))
            .Where(slider => slider.Contains("Value=\"{Binding SamplingFrequency", StringComparison.Ordinal))
            .ToList();

        // Without this the test passes loudly for the wrong reason: rename the bound member and
        // there are no sliders left to be wrong about.
        Assert.NotEmpty(editors);
        Assert.All(editors, slider =>
        {
            Assert.DoesNotContain("Maximum=\"1000\"", slider, StringComparison.Ordinal);
            Assert.Contains("Maximum=\"{Binding MaxSamplingFrequency}\"", slider, StringComparison.Ordinal);
        });
    }
    #endregion

    #region Helpers
    /// <summary>Every <c>&lt;Slider ... /&gt;</c> element in a view, as its own chunk of markup.</summary>
    private static IEnumerable<string> Sliders(string markup) =>
        Regex.Matches(markup, "<Slider\\b[^>]*>", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => match.Value);

    /// <summary>
    /// What <c>CAPTURE FROM CURRENT SETTINGS</c> builds from a device streaming at
    /// <paramref name="rate"/>: the live rate copied into the profile unfiltered.
    /// </summary>
    private static Profile ProfileHolding(int rate) => new()
    {
        Name = "Captured",
        ProfileId = Guid.NewGuid(),
        CreatedOn = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Unspecified),
        Devices =
        [
            new ProfileDevice
            {
                DeviceName = "Nyquist 1",
                DevicePartName = "Nq1",
                MacAddress = "00:11:22:33:44:55",
                DeviceSerialNo = "SERIAL-274",
                SamplingFrequency = rate,
                Channels = [new ProfileChannel { Name = "AI0", Type = "Analog", IsChannelActive = true, SerialNo = "SERIAL-274" }],
            },
        ],
    };

    /// <summary>
    /// A manager over this test's own throwaway profiles file. The production <c>Instance</c> is a
    /// process-wide singleton pointed at the real one.
    /// </summary>
    private LoggingManager NewManager() =>
        new(new UnusedContextFactory(), Path.Combine(_directory, "DAQifiProfilesConfiguration.xml"));

    /// <summary>Profiles are XML, not the database; an attempt to open a context here is a mistake.</summary>
    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException("Profile persistence must not touch the database.");
    }
    #endregion
}
