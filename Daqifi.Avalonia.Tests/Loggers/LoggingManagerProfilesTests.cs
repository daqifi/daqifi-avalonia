using System.Collections.ObjectModel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins <see cref="LoggingManager"/>'s half of #184: what the Profiles pane is showing must never
/// disagree with what is on disk.
///
/// <para>
/// Three separate ways it used to. A load cleared <c>SubscribedProfiles</c> <b>before</b>
/// attempting the parse, so one damaged file emptied the pane while the file still held every
/// profile. A save added the profile to the collection whether or not the write succeeded, so a
/// profile that was never persisted looked saved until the next launch removed it. And a delete
/// removed the profile from the collection <b>before</b> saving, so a failed write dropped it from
/// the pane while leaving it in the file, to reappear at the next launch. All three are silent:
/// nothing is shown to the user at any point.
/// </para>
///
/// <para>
/// Each test builds its own manager over its own temp path via the internal test constructor —
/// the production <c>Instance</c> is a process-wide singleton pointed at the real profiles file.
/// </para>
/// </summary>
public sealed class LoggingManagerProfilesTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "manager-profiles-" + Guid.NewGuid().ToString("N"));

    private string ProfilePath => Path.Combine(_directory, "DAQifiProfilesConfiguration.xml");

    /// <summary>
    /// A path that cannot be written, on every platform and without touching permissions: its
    /// parent "directory" is a regular file, so <c>Directory.CreateDirectory</c> throws.
    /// </summary>
    private string UnwritablePath()
    {
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, string.Empty);
        return Path.Combine(blocker, "DAQifiProfilesConfiguration.xml");
    }

    public LoggingManagerProfilesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Profile persistence is XML, not the database. Any attempt to open a context here is a
    /// mistake worth failing on rather than quietly satisfying.
    /// </summary>
    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException("Profile persistence must not touch the database.");
    }

    private LoggingManager NewManager(string? profilePath = null) =>
        new(new UnusedContextFactory(), profilePath ?? ProfilePath);

    private static Profile NewProfile(string name = "Bench") => new()
    {
        Name = name,
        ProfileId = Guid.NewGuid(),
        CreatedOn = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
        Devices =
        [
            new ProfileDevice
            {
                DeviceName = "Nyquist 1",
                DevicePartName = "Nq1",
                MacAddress = "00:11:22:33:44:55",
                DeviceSerialNo = "SERIAL-A",
                SamplingFrequency = 1000,
                Channels = [new ProfileChannel { Name = "AI0", Type = "Analog", IsChannelActive = true, SerialNo = "SERIAL-A" }],
            },
        ],
    };

    /// <summary>
    /// The regression. The file is damaged while the app is running; the next load must leave the
    /// pane alone rather than emptying it, because it has nothing to put there in its place.
    /// </summary>
    [Fact]
    public void A_damaged_file_does_not_empty_the_profiles_already_on_display()
    {
        var manager = NewManager();
        Assert.True(manager.SubscribeProfile(NewProfile()));

        File.WriteAllText(ProfilePath, "<Profiles><Profile><Name>Ben");
        var loaded = manager.LoadProfilesFromXml();

        Assert.Equal("Bench", Assert.Single(manager.SubscribedProfiles).Name);
        Assert.Equal("Bench", Assert.Single(loaded).Name);
    }

    [Fact]
    public void A_saved_profile_survives_a_reload()
    {
        var profile = NewProfile();
        Assert.True(NewManager().SubscribeProfile(profile));

        var reloaded = Assert.Single(NewManager().LoadProfilesFromXml());

        Assert.Equal(profile.Name, reloaded.Name);
        Assert.Equal(profile.ProfileId, reloaded.ProfileId);
        Assert.Equal("SERIAL-A", Assert.Single(reloaded.Devices).DeviceSerialNo);
        Assert.Equal("AI0", Assert.Single(Assert.Single(reloaded.Devices).Channels).Name);
    }

    /// <summary>
    /// The write fails, so the pane must not show the profile as saved. It used to: the failure
    /// was swallowed into a log line and the profile was added to the collection regardless, so it
    /// sat there looking saved until the next launch quietly removed it.
    /// </summary>
    [Fact]
    public void A_profile_that_could_not_be_written_is_not_shown_as_saved()
    {
        var manager = NewManager(UnwritablePath());

        Assert.False(manager.SubscribeProfile(NewProfile()));

        Assert.Empty(manager.SubscribedProfiles);
    }

    /// <summary>
    /// The same failure mirrored onto delete. The removal from the collection used to happen
    /// inside the XML writer, before the save, so a failed write dropped the profile from the pane
    /// while leaving it in the file.
    /// </summary>
    [Fact]
    public void A_profile_that_could_not_be_deleted_stays_on_display()
    {
        var manager = NewManager(UnwritablePath());
        var profile = NewProfile();
        manager.SubscribedProfiles.Add(profile);

        Assert.False(manager.UnsubscribeProfile(profile));

        Assert.Same(profile, Assert.Single(manager.SubscribedProfiles));
    }

    [Fact]
    public void A_deleted_profile_leaves_both_the_pane_and_the_file()
    {
        var manager = NewManager();
        var profile = NewProfile();
        Assert.True(manager.SubscribeProfile(profile));

        Assert.True(manager.UnsubscribeProfile(profile));

        Assert.Empty(manager.SubscribedProfiles);
        Assert.Empty(NewManager().LoadProfilesFromXml());
    }

    /// <summary>
    /// After a recovery the file is empty while the pane still holds the user's profiles, so an
    /// edit finds nothing to update. Refusing there would be the permanent failure again in a new
    /// costume; writing the profile back is how the file heals.
    /// </summary>
    [Fact]
    public void Editing_a_profile_that_is_missing_from_the_file_writes_it_back()
    {
        var manager = NewManager();
        var profile = NewProfile();
        Assert.True(manager.SubscribeProfile(profile));
        File.WriteAllText(ProfilePath, "<Profiles />");

        Assert.True(manager.UpdateProfileInXml(profile));

        Assert.Equal(profile.ProfileId, Assert.Single(NewManager().LoadProfilesFromXml()).ProfileId);
    }

    [Fact]
    public void An_edited_profile_replaces_the_one_in_the_file_rather_than_joining_it()
    {
        var manager = NewManager();
        var profile = NewProfile();
        Assert.True(manager.SubscribeProfile(profile));

        profile.Name = "Renamed";
        profile.Devices = [];
        Assert.True(manager.UpdateProfileInXml(profile));

        var reloaded = Assert.Single(NewManager().LoadProfilesFromXml());
        Assert.Equal("Renamed", reloaded.Name);
        Assert.Empty(reloaded.Devices);
    }

    /// <summary>
    /// End to end, the sentence from the ticket: "profiles can never be saved again for the life
    /// of that file". They can now — the damaged file is moved aside on the way past.
    /// </summary>
    [Fact]
    public void A_profile_saves_again_after_the_file_was_damaged()
    {
        var manager = NewManager();
        File.WriteAllText(ProfilePath, "<Profiles><Profile><Name>Ben");
        manager.LoadProfilesFromXml();

        Assert.True(manager.SubscribeProfile(NewProfile("After the crash")));

        Assert.Equal("After the crash", Assert.Single(NewManager().LoadProfilesFromXml()).Name);
    }

    /// <summary>
    /// And the user is told, rather than left to wonder where their profiles went. Once.
    /// </summary>
    [Fact]
    public void The_pane_is_told_once_that_the_damaged_file_was_moved_aside()
    {
        var manager = NewManager();
        File.WriteAllText(ProfilePath, "<Profiles><Profile><Name>Ben");

        manager.LoadProfilesFromXml();

        var notice = manager.TakeProfileRecoveryNotice();
        Assert.NotNull(notice);
        Assert.Contains(Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*")), notice);
        Assert.Null(manager.TakeProfileRecoveryNotice());
    }

    /// <summary>
    /// A clean run must not leave recovery litter beside the profiles file.
    /// </summary>
    [Fact]
    public void An_ordinary_save_moves_nothing_aside()
    {
        Assert.True(NewManager().SubscribeProfile(NewProfile()));

        Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));
        Assert.Equal(ProfilePath, Assert.Single(Directory.GetFiles(_directory)));
    }
}
