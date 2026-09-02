using System.Xml;
using System.Xml.Linq;
using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins <see cref="ProfileXmlStore"/>'s recovery contract — the half of #184 that is about the
/// file rather than the collection.
///
/// <para>
/// The bug was that every recovery path keyed on the profiles file being <b>absent</b>, so a file
/// that was present but unparseable was never repaired: the read threw, and so did every
/// subsequent write, for the life of that file. What these tests care about is therefore not "does
/// it parse XML" but the three claims that make the permanent failure impossible: an unreadable
/// file is moved aside rather than left in place, its bytes survive that move, and writing works
/// again afterwards.
/// </para>
///
/// <para>
/// Each test gets its own temp directory. The assembly already redirects <c>DAQIFI_DATA_DIR</c>
/// away from the developer's real data directory (see <see cref="TestDataDirectory"/>), but the
/// point of giving the store its path is that these tests never depend on that at all.
/// </para>
/// </summary>
public sealed class ProfileXmlStoreTests : IDisposable
{
    private static readonly Guid KnownProfileId = new("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    /// <summary>
    /// One profile, written the way the app writes it. Hand-written rather than produced by the
    /// writer under test, so that a change to the writer cannot quietly redefine what the reader
    /// is expected to accept.
    /// </summary>
    private const string OneProfileXml = """
        <Profiles>
          <Profile>
            <Name>Bench</Name>
            <ProfileID>3f2504e0-4f89-11d3-9a0c-0305e82c3301</ProfileID>
            <CreatedOn>2026-01-02T03:04:05</CreatedOn>
            <Devices>
              <Device>
                <DeviceName>Nyquist 1</DeviceName>
                <DevicePartNumber>Nq1</DevicePartNumber>
                <MACAddress>00:11:22:33:44:55</MACAddress>
                <DeviceSerialNo>SERIAL-A</DeviceSerialNo>
                <Channels>
                  <Channel>
                    <Name>AI0</Name>
                    <Type>Analog</Type>
                    <IsActive>true</IsActive>
                  </Channel>
                </Channels>
                <SamplingFrequency>1000</SamplingFrequency>
              </Device>
            </Devices>
          </Profile>
        </Profiles>
        """;

    /// <summary>
    /// The same profile, followed by one with no <c>&lt;Devices&gt;</c> element at all — an older
    /// or hand-edited file. Both must load.
    /// </summary>
    private const string ProfileWithNoDevicesXml = """
        <Profiles>
          <Profile>
            <Name>Bench</Name>
            <ProfileID>3f2504e0-4f89-11d3-9a0c-0305e82c3301</ProfileID>
            <CreatedOn>2026-01-02T03:04:05</CreatedOn>
            <Devices />
          </Profile>
          <Profile>
            <Name>Deviceless</Name>
            <ProfileID>6ba7b810-9dad-11d1-80b4-00c04fd430c8</ProfileID>
            <CreatedOn>2026-01-02T03:04:05</CreatedOn>
          </Profile>
        </Profiles>
        """;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "profile-store-" + Guid.NewGuid().ToString("N"));

    private string ProfilePath => Path.Combine(_directory, "DAQifiProfilesConfiguration.xml");

    private ProfileXmlStore NewStore() => new(ProfilePath);

    private string[] QuarantinedFiles() =>
        Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.corrupt-*") : [];

    public ProfileXmlStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// A fresh install has no profiles file, which is ordinary rather than damaged. Quarantining
    /// here would litter every new installation with <c>.corrupt-</c> files it then never cleans up.
    /// </summary>
    [Fact]
    public void A_fresh_install_reads_no_profiles_and_moves_nothing_aside()
    {
        Assert.True(NewStore().TryLoad(out var profiles));

        Assert.Empty(profiles);
        Assert.Empty(QuarantinedFiles());
    }

    [Fact]
    public void A_profiles_file_is_read_into_the_model()
    {
        File.WriteAllText(ProfilePath, OneProfileXml);

        Assert.True(NewStore().TryLoad(out var profiles));

        var profile = Assert.Single(profiles);
        Assert.Equal("Bench", profile.Name);
        Assert.Equal(KnownProfileId, profile.ProfileId);
        var device = Assert.Single(profile.Devices);
        Assert.Equal("SERIAL-A", device.DeviceSerialNo);
        Assert.Equal(1000, device.SamplingFrequency);
        Assert.Equal("AI0", Assert.Single(device.Channels).Name);
    }

    /// <summary>
    /// A container the writer may legitimately have left out is not a damaged file. Passing the
    /// resulting null sequence to <c>ObservableCollection</c> threw, and this reader treats a throw
    /// as damage — so one profile with no <c>&lt;Devices&gt;</c> would have quarantined every other
    /// profile in an otherwise perfectly readable file.
    /// </summary>
    [Fact]
    public void A_profile_with_no_devices_loads_without_condemning_the_rest_of_the_file()
    {
        File.WriteAllText(ProfilePath, ProfileWithNoDevicesXml);

        Assert.True(NewStore().TryLoad(out var profiles));

        Assert.Equal(["Bench", "Deviceless"], profiles.Select(profile => profile.Name));
        Assert.All(profiles, profile => Assert.Empty(profile.Devices));
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>
    /// The file a power cut during <c>doc.Save</c> leaves behind. The read has to fail — there is
    /// nothing to return — but it must fail by moving the file aside, because leaving it is what
    /// made every later write throw too.
    /// </summary>
    [Fact]
    public void A_malformed_file_is_moved_aside_and_the_read_reports_failure()
    {
        const string truncated = "<Profiles><Profile><Name>Ben";
        File.WriteAllText(ProfilePath, truncated);

        Assert.False(NewStore().TryLoad(out var profiles));

        Assert.Empty(profiles);
        Assert.False(File.Exists(ProfilePath));
        // Renamed, never deleted: a truncated file still holds most of what the user had, and an
        // unparseable one is often still readable by hand.
        Assert.Equal(truncated, File.ReadAllText(Assert.Single(QuarantinedFiles())));
    }

    /// <summary>
    /// Well-formed XML whose values are not what the reader casts them to — a hand-edit, or a
    /// file from something else entirely. It is just as unloadable as malformed XML, and leaving
    /// it in place strands the user in the same permanent failure.
    /// </summary>
    [Fact]
    public void A_file_whose_values_cannot_be_read_is_moved_aside_too()
    {
        File.WriteAllText(ProfilePath, OneProfileXml.Replace(
            "<SamplingFrequency>1000</SamplingFrequency>",
            "<SamplingFrequency>not a number</SamplingFrequency>"));

        Assert.False(NewStore().TryLoad(out var profiles));

        Assert.Empty(profiles);
        Assert.Single(QuarantinedFiles());
    }

    /// <summary>
    /// The headline claim of the ticket: "profiles can never be saved again for the life of that
    /// file". After a recovery they can.
    /// </summary>
    [Fact]
    public void Saving_works_again_after_a_damaged_file_has_been_recovered()
    {
        File.WriteAllText(ProfilePath, "<Profiles><Profile><Name>Ben");
        var store = NewStore();
        Assert.False(store.TryLoad(out _));

        var document = store.Open();
        document.Root!.Add(XElement.Parse("<Profile><Name>New</Name></Profile>"));
        store.Save(document);

        Assert.Equal("New", XDocument.Load(ProfilePath).Descendants("Profile").Single().Element("Name")!.Value);
    }

    /// <summary>
    /// A writer takes a document and appends to it; it never has to interpret the profiles already
    /// in the file, so it is not in a position to declare them unreadable. Destroying them on its
    /// judgement would be a worse bug than the one being fixed.
    /// </summary>
    [Fact]
    public void Opening_for_a_write_does_not_move_aside_a_file_it_merely_cannot_interpret()
    {
        var unreadable = OneProfileXml.Replace(
            "<SamplingFrequency>1000</SamplingFrequency>",
            "<SamplingFrequency>not a number</SamplingFrequency>");
        File.WriteAllText(ProfilePath, unreadable);

        var document = NewStore().Open();

        Assert.Single(document.Descendants("Profile"));
        Assert.Empty(QuarantinedFiles());
        Assert.Equal(unreadable, File.ReadAllText(ProfilePath));
    }

    /// <summary>
    /// The write goes to a temporary file and is renamed over the real one — that is what stops an
    /// interrupted write producing the truncated file this whole ticket starts from. The temporary
    /// file must not survive the write that used it.
    /// </summary>
    [Fact]
    public void A_write_leaves_no_temporary_file_behind()
    {
        var store = NewStore();
        store.Save(XDocument.Parse(OneProfileXml));

        Assert.Equal(ProfilePath, Assert.Single(Directory.GetFiles(_directory)));
        Assert.True(store.TryLoad(out var profiles));
        Assert.Single(profiles);
    }

    /// <summary>
    /// The distinction the recovery turns on. Content the reader cannot interpret is a damaged
    /// file; a file the process cannot reach right now — locked by another process, on a volume
    /// that just went away, behind a permission problem — is a perfectly good file, and treating
    /// it as damaged would destroy profiles over a transient condition.
    /// </summary>
    [Theory]
    [InlineData(typeof(XmlException), true)]
    [InlineData(typeof(FormatException), true)]
    [InlineData(typeof(ArgumentNullException), true)]
    [InlineData(typeof(OverflowException), true)]
    [InlineData(typeof(InvalidCastException), true)]
    [InlineData(typeof(IOException), false)]
    [InlineData(typeof(UnauthorizedAccessException), false)]
    [InlineData(typeof(FileNotFoundException), false)]
    public void Only_content_failures_count_as_a_damaged_file(Type exceptionType, bool isDamaged)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(isDamaged, ProfileXmlStore.IsUnreadableContent(exception));
    }

    /// <summary>
    /// Someone whose profiles pane has gone empty needs to be told why and where the old file
    /// went. Once — repeating it on every drawer open would be noise they cannot dismiss.
    /// </summary>
    [Fact]
    public void The_recovery_notice_names_the_file_and_is_given_out_once()
    {
        File.WriteAllText(ProfilePath, "<Profiles><Profile><Name>Ben");
        var store = NewStore();
        Assert.Null(store.TakeQuarantineNotice());

        Assert.False(store.TryLoad(out _));

        var notice = store.TakeQuarantineNotice();
        Assert.NotNull(notice);
        Assert.Contains(Assert.Single(QuarantinedFiles()), notice);
        Assert.Null(store.TakeQuarantineNotice());
    }

    /// <summary>
    /// The claim #197 exists for, and the one nothing pinned when #192 shipped: a quarantine must
    /// never destroy an earlier quarantine.
    /// </summary>
    /// <remarks>
    /// The quarantine existed to keep the user's damaged-but-often-hand-recoverable profiles.
    /// Moving the second damaged file onto the first one's name throws away the only copy of the
    /// thing the rename was for, and reports success while doing it. Two app instances share one
    /// data directory and both can reach recovery on the same file inside the same millisecond, so
    /// the timestamp in the name is not on its own the protection the doc comment claimed it was.
    /// <para>
    /// Driven through <c>MoveFileAside</c> with an explicit destination rather than through a
    /// recovery, because the production name is <c>DateTime.UtcNow</c> to the millisecond and a
    /// collision cannot be arranged from outside. Same seam, for the same reason, as
    /// <c>DaqifiSettingsTests.Moving_a_damaged_file_aside_never_overwrites_an_earlier_one</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Moving_a_damaged_file_aside_never_overwrites_an_earlier_one()
    {
        const string earlier = "<Profiles><Profile><Name>the file an earlier recovery moved aside";
        const string later = "<Profiles><Profile><Name>Ben";
        var taken = Path.Combine(_directory, "DAQifiProfilesConfiguration.xml.corrupt-20260902-120000000");
        File.WriteAllText(taken, earlier);
        File.WriteAllText(ProfilePath, later);

        var moved = ProfileXmlStore.MoveFileAside(ProfilePath, taken);

        Assert.Equal(taken + "-1", moved);
        Assert.Equal(earlier, File.ReadAllText(taken));
        Assert.Equal(later, File.ReadAllText(moved!));
        Assert.False(File.Exists(ProfilePath));
    }

    /// <summary>
    /// Giving up on a taken name is not an option here, unlike the database quarantine: abandoning
    /// the move leaves the damaged file at <c>FilePath</c>, and that is the exact permanent failure
    /// #184 existed to end. So the next name is tried, and the next.
    /// </summary>
    [Fact]
    public void Moving_a_damaged_file_aside_keeps_taking_the_next_name_until_one_is_free()
    {
        var taken = Path.Combine(_directory, "DAQifiProfilesConfiguration.xml.corrupt-20260902-120000000");
        File.WriteAllText(taken, "first");
        File.WriteAllText(taken + "-1", "second");
        File.WriteAllText(ProfilePath, "third");

        var moved = ProfileXmlStore.MoveFileAside(ProfilePath, taken);

        Assert.Equal(taken + "-2", moved);
        Assert.Equal("first", File.ReadAllText(taken));
        Assert.Equal("second", File.ReadAllText(taken + "-1"));
        Assert.Equal("third", File.ReadAllText(moved!));
    }

    /// <summary>
    /// A directory sitting on the preferred name is a taken name like any other. It has to be
    /// stepped over rather than treated as an unrecoverable failure: giving up here would leave the
    /// damaged file at <c>FilePath</c> and disable saving, which is the failure this whole type
    /// exists to end.
    /// </summary>
    [Fact]
    public void A_directory_on_the_preferred_name_is_stepped_over_like_any_other_taken_name()
    {
        const string damaged = "<Profiles><Profile><Name>Ben";
        var taken = Path.Combine(_directory, "DAQifiProfilesConfiguration.xml.corrupt-20260902-120000000");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "inside.txt"), "not ours to touch");
        File.WriteAllText(ProfilePath, damaged);

        var moved = ProfileXmlStore.MoveFileAside(ProfilePath, taken);

        Assert.Equal(taken + "-1", moved);
        Assert.Equal(damaged, File.ReadAllText(moved!));
        Assert.Equal("not ours to touch", File.ReadAllText(Path.Combine(taken, "inside.txt")));
    }

    /// <summary>
    /// A file that is not there cannot be moved, and the caller has to be told so rather than
    /// handed a path nothing was written to — <see cref="ProfileXmlStore.Open"/> turns that
    /// <c>null</c> into the refusal that keeps a later <c>Save</c> from renaming over the damaged
    /// file.
    /// </summary>
    [Fact]
    public void Moving_a_file_that_is_not_there_reports_failure()
    {
        Assert.Null(ProfileXmlStore.MoveFileAside(ProfilePath, Path.Combine(_directory, "target")));
    }

    /// <summary>
    /// The same claim stated the way a user would: two damaged files, two quarantines, both sets of
    /// bytes still on disk. Complements the seam test above rather than replacing it — this one
    /// exercises the real recovery path but relies on the clock for its two names, so it is the
    /// seam test that actually pins the collision.
    /// </summary>
    [Fact]
    public void A_second_damaged_file_is_quarantined_alongside_the_first_not_over_it()
    {
        const string first = "<Profiles><Profile><Name>Bench";
        const string second = "<Profiles><Profile><Name>Rig";

        File.WriteAllText(ProfilePath, first);
        Assert.False(NewStore().TryLoad(out _));
        File.WriteAllText(ProfilePath, second);
        Assert.False(NewStore().TryLoad(out _));

        Assert.Equal([first, second], QuarantinedFiles().Order().Select(File.ReadAllText));
    }
}
