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
}
