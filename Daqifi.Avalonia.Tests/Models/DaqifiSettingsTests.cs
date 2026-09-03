using System.Xml;
using System.Xml.Linq;
using Daqifi.Desktop.Models;
using Xunit;

namespace Daqifi.Avalonia.Tests.Models;

/// <summary>
/// Pins <see cref="DaqifiSettings"/>'s recovery contract — #193.
///
/// <para>
/// The bug: recovery was keyed on <c>DAQifiConfiguration.xml</c> being <b>absent</b>, so a file
/// that was present but unparseable was never repaired. <c>XElement.Load</c> threw on it at every
/// launch, and the catch called <c>AppLogger.Error</c>, which captures to Sentry — so one damaged
/// settings file became a fresh crash-report event on every launch for the life of that file,
/// while the user's CSV delimiter silently reverted and stayed reverted.
/// </para>
///
/// <para>
/// So what these tests care about is not "does it parse XML" but the claims that make the
/// permanent condition impossible: an unreadable file is moved aside rather than left in place,
/// its bytes survive that move, a working file exists afterwards, and the <b>second</b> launch
/// finds nothing left to report.
/// </para>
///
/// <para>
/// Each test gets its own temp directory and its own settings instance via the internal
/// path-taking constructor — the production <c>Instance</c> is a process-wide singleton pointed at
/// the real settings file.
/// </para>
/// </summary>
public sealed class DaqifiSettingsTests : IDisposable
{
    /// <summary>The file a power cut part-way through <c>xml.Save</c> leaves behind.</summary>
    private const string TruncatedSettingsXml = "<DAQifiSettings><CsvDelimiter>;";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "DAQifiConfiguration.xml");

    private DaqifiSettings NewSettings() => new(SettingsPath);

    private string[] QuarantinedFiles() =>
        Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.corrupt-*") : [];

    private static string StoredDelimiter(string path) =>
        XElement.Load(path).Element("CsvDelimiter")!.Value;

    public DaqifiSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// A fresh install has no settings file, which is ordinary rather than damaged. Quarantining
    /// here would litter every new installation with <c>.corrupt-</c> files it never cleans up.
    /// </summary>
    [Fact]
    public void A_fresh_install_writes_the_defaults_and_moves_nothing_aside()
    {
        var settings = NewSettings();

        Assert.Equal(",", settings.CsvDelimiter);
        Assert.Equal(",", StoredDelimiter(SettingsPath));
        Assert.Empty(QuarantinedFiles());
    }

    [Fact]
    public void A_stored_delimiter_is_read_back()
    {
        File.WriteAllText(SettingsPath, "<DAQifiSettings><CsvDelimiter>;</CsvDelimiter></DAQifiSettings>");

        Assert.Equal(";", NewSettings().CsvDelimiter);
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>
    /// The core of the ticket. The read has to fail — there is nothing to read — but it must fail
    /// by moving the file aside and writing a good one, because leaving it is what made the next
    /// launch, and every launch after it, do exactly the same thing.
    /// </summary>
    [Fact]
    public void A_damaged_settings_file_is_moved_aside_and_replaced_with_a_working_one()
    {
        File.WriteAllText(SettingsPath, TruncatedSettingsXml);

        var settings = NewSettings();

        Assert.Equal(",", settings.CsvDelimiter);
        // Renamed, never deleted: it is the user's file, and an unparseable one is very often
        // still readable by hand.
        Assert.Equal(TruncatedSettingsXml, File.ReadAllText(Assert.Single(QuarantinedFiles())));
        Assert.Equal(",", StoredDelimiter(SettingsPath));
    }

    /// <summary>
    /// The headline claim: "logs an error and raises a Sentry event on every launch, for ever".
    /// After a recovery there is nothing left to report, because the file the first launch left
    /// behind is readable. A second recovery here would mean the first one had not fixed anything.
    /// </summary>
    [Fact]
    public void A_damaged_settings_file_is_reported_once_and_not_again_on_the_next_launch()
    {
        File.WriteAllText(SettingsPath, TruncatedSettingsXml);

        var firstLaunch = NewSettings();
        var quarantined = Assert.Single(QuarantinedFiles());

        var secondLaunch = NewSettings();

        Assert.Equal(",", firstLaunch.CsvDelimiter);
        Assert.Equal(",", secondLaunch.CsvDelimiter);
        Assert.Equal(quarantined, Assert.Single(QuarantinedFiles()));
    }

    /// <summary>
    /// A well-formed file can still hold a delimiter no exporter should ever see: an empty one
    /// concatenates every CSV column. The UI offers only "," and ";", so nothing in the app can
    /// produce this — but the file is hand-editable and nothing rejected it.
    /// </summary>
    [Fact]
    public void An_empty_delimiter_in_the_file_never_reaches_the_exporter()
    {
        File.WriteAllText(SettingsPath, "<DAQifiSettings><CsvDelimiter></CsvDelimiter></DAQifiSettings>");

        Assert.Equal(",", NewSettings().CsvDelimiter);
    }

    /// <summary>
    /// A delimiter outside the app's own option list is not used either — but the file is
    /// well-formed and is the user's, so it is neither quarantined nor rewritten behind their
    /// back. Choosing a delimiter in Settings is what makes the file valid again.
    /// </summary>
    [Fact]
    public void An_unsupported_delimiter_falls_back_to_the_default_and_leaves_the_file_alone()
    {
        const string handEdited = "<DAQifiSettings><CsvDelimiter>|</CsvDelimiter></DAQifiSettings>";
        File.WriteAllText(SettingsPath, handEdited);

        Assert.Equal(",", NewSettings().CsvDelimiter);
        Assert.Equal(handEdited, File.ReadAllText(SettingsPath));
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>
    /// The distinction the recovery turns on. Content that cannot be parsed is a damaged file; a
    /// file the process cannot reach at all is an environmental condition, and construction must
    /// still succeed with usable defaults rather than throwing out of a static initializer.
    /// </summary>
    [Fact]
    public void A_settings_path_that_cannot_be_reached_leaves_usable_defaults_and_quarantines_nothing()
    {
        // A path that cannot be created on any platform and without touching permissions: its
        // parent is a regular file, so there is no directory to make and nothing to open.
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var settings = new DaqifiSettings(Path.Combine(blocker, "DAQifiConfiguration.xml"));

        Assert.Equal(",", settings.CsvDelimiter);
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>
    /// The same distinction, stated directly on the predicate that draws it. Getting this wrong
    /// destroys the user's file in one direction and leaves #193's permanent failure in place in
    /// the other.
    /// </summary>
    [Theory]
    [InlineData(typeof(XmlException), true)]
    [InlineData(typeof(IOException), false)]
    [InlineData(typeof(UnauthorizedAccessException), false)]
    [InlineData(typeof(FileNotFoundException), false)]
    public void Only_content_failures_count_as_a_damaged_settings_file(Type exceptionType, bool isDamaged)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(isDamaged, DaqifiSettings.IsUnreadableContent(exception));
    }

    /// <summary>
    /// Someone whose delimiter has silently reverted needs to be told why and where their old file
    /// went. Once — the Settings pane repeating it on every open would be noise they cannot
    /// dismiss.
    /// </summary>
    [Fact]
    public void The_recovery_notice_names_the_quarantined_file_and_is_given_out_once()
    {
        File.WriteAllText(SettingsPath, TruncatedSettingsXml);

        var settings = NewSettings();

        var notice = settings.TakeQuarantineNotice();
        Assert.NotNull(notice);
        Assert.Contains(Assert.Single(QuarantinedFiles()), notice);
        Assert.Null(settings.TakeQuarantineNotice());
    }

    /// <summary>
    /// A launch that found nothing wrong has nothing to say, so the pane shows nothing.
    /// </summary>
    [Fact]
    public void A_healthy_settings_file_produces_no_recovery_notice()
    {
        File.WriteAllText(SettingsPath, "<DAQifiSettings><CsvDelimiter>;</CsvDelimiter></DAQifiSettings>");

        Assert.Null(NewSettings().TakeQuarantineNotice());
    }

    // The rename itself — a taken quarantine name, a DIRECTORY on that name, a source that is not
    // there, the bound on the search — is pinned once, in AppDataFileTests, against the one
    // implementation both stores now share. It used to be stated here and again, character for
    // character, in ProfileXmlStoreTests. What stays here is every claim about THIS store: that a
    // damaged settings file reaches the rename at all, and what the user is told afterwards.

    /// <summary>
    /// Moving the damaged file aside and getting a working one back in its place are two outcomes,
    /// and the write can fail on exactly the conditions that damaged the file. Telling the user
    /// settings save normally when nothing on disk holds them is the false reassurance #181 had to
    /// fix; the message has to say which of the two actually happened.
    /// </summary>
    [Fact]
    public void The_recovery_notice_promises_working_settings_only_when_a_replacement_was_written()
    {
        const string quarantined = "/data/DAQifiConfiguration.xml.corrupt-20260902-120000000";

        var written = DaqifiSettings.DescribeQuarantineForUser(quarantined, replacementWritten: true);
        var notWritten = DaqifiSettings.DescribeQuarantineForUser(quarantined, replacementWritten: false);

        Assert.Contains(quarantined, written);
        Assert.Contains(quarantined, notWritten);
        Assert.Contains("has been reset", written);
        Assert.DoesNotContain("could not be written", written);
        Assert.Contains("could not be written", notWritten);
    }

    /// <summary>
    /// The write goes to a temporary file and is renamed over the real one — that is what stops an
    /// interrupted write producing the truncated file this whole ticket starts from. The temporary
    /// file must not survive the write that used it.
    /// </summary>
    [Fact]
    public void A_write_leaves_no_temporary_file_behind()
    {
        var settings = NewSettings();

        settings.CsvDelimiter = ";";

        Assert.Equal(SettingsPath, Assert.Single(Directory.GetFiles(_directory)));
        Assert.Equal(";", StoredDelimiter(SettingsPath));
        Assert.Equal(";", NewSettings().CsvDelimiter);
    }
}
