using System.Xml.Linq;
using Daqifi.Desktop.Helpers;
using Xunit;

namespace Daqifi.Avalonia.Tests.Helpers;

/// <summary>
/// The one suite for <see cref="AppDataFile"/> — the rules the settings store (#193) and the
/// profiles store (#184) used to state, and test, twice each.
///
/// <para>
/// Both stores keep their own end-to-end tests, which prove they still <b>reach</b> these rules
/// through a real recovery and a real save. What lives here is the seam: the claims that need a
/// collision, an exhausted name space or a mid-write failure arranged deliberately, none of which
/// can be provoked from outside a store whose quarantine name is <c>DateTime.UtcNow</c>.
/// </para>
///
/// <para>
/// Two copies of a rule are two chances to get it wrong, and both went wrong at least once:
/// the profiles copy shipped with <c>overwrite: true</c> and destroyed an earlier quarantine
/// (#197), and both copies keyed the retry on <c>File.Exists</c>, which is <c>false</c> for a
/// directory (#198). Every one of those regressions is pinned below, once.
/// </para>
/// </summary>
public sealed class AppDataFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "appdatafile-" + Guid.NewGuid().ToString("N"));

    private string Source => Path.Combine(_directory, "DAQifiConfiguration.xml");

    private string Preferred => Path.Combine(_directory, "DAQifiConfiguration.xml.corrupt-20260902-120000000");

    private string[] TemporaryFiles() => Directory.GetFiles(_directory, "*.tmp");

    public AppDataFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    #region MoveAside
    /// <summary>
    /// The claim #197 exists for: a quarantine must never destroy an earlier quarantine. Two app
    /// instances share the data directory and both can reach recovery on the same file inside the
    /// same millisecond, so the timestamp callers put in the name is not on its own a guarantee.
    /// </summary>
    [Fact]
    public void Moving_a_damaged_file_aside_never_overwrites_an_earlier_one()
    {
        const string earlier = "the file an earlier recovery moved aside";
        File.WriteAllText(Preferred, earlier);
        File.WriteAllText(Source, "damaged");

        var moved = AppDataFile.MoveAside(Source, Preferred);

        Assert.Equal(Preferred + "-1", moved);
        Assert.Equal(earlier, File.ReadAllText(Preferred));
        Assert.Equal("damaged", File.ReadAllText(moved!));
        Assert.False(File.Exists(Source));
    }

    /// <summary>
    /// Giving up on a taken name is not an option, unlike the database quarantine: abandoning the
    /// move leaves the damaged file at its real name, which is the exact permanent failure #184
    /// and #193 exist to end. So the next name is tried, and the next.
    /// </summary>
    [Fact]
    public void Moving_a_damaged_file_aside_keeps_taking_the_next_name_until_one_is_free()
    {
        File.WriteAllText(Preferred, "first");
        File.WriteAllText(Preferred + "-1", "second");
        File.WriteAllText(Source, "third");

        var moved = AppDataFile.MoveAside(Source, Preferred);

        Assert.Equal(Preferred + "-2", moved);
        Assert.Equal("first", File.ReadAllText(Preferred));
        Assert.Equal("second", File.ReadAllText(Preferred + "-1"));
        Assert.Equal("third", File.ReadAllText(moved!));
    }

    /// <summary>
    /// A directory sitting on the preferred name is a taken name like any other — #198.
    /// </summary>
    /// <remarks>
    /// <c>File.Exists</c> is <c>false</c> for a directory while <c>Path.Exists</c> is <c>true</c>,
    /// and <c>File.Move</c> onto a directory throws <see cref="IOException"/> — so keying the retry
    /// on <c>File.Exists</c> sends this to the giving-up catch, abandons the quarantine, and leaves
    /// the damaged file at its real name. Both stores had that gap; there is now one place to have
    /// it in.
    /// </remarks>
    [Fact]
    public void A_directory_on_the_preferred_name_is_stepped_over_like_any_other_taken_name()
    {
        Directory.CreateDirectory(Preferred);
        File.WriteAllText(Path.Combine(Preferred, "inside.txt"), "not ours to touch");
        File.WriteAllText(Source, "damaged");

        var moved = AppDataFile.MoveAside(Source, Preferred);

        Assert.Equal(Preferred + "-1", moved);
        Assert.Equal("damaged", File.ReadAllText(moved!));
        Assert.False(File.Exists(Source));
        Assert.Equal("not ours to touch", File.ReadAllText(Path.Combine(Preferred, "inside.txt")));
    }

    /// <summary>
    /// A file that is not there cannot be moved, and the caller has to be told so rather than
    /// handed a path nothing was written to — <c>ProfileXmlStore.Open</c> turns that <c>null</c>
    /// into the refusal that keeps a later save from renaming over the damaged file.
    /// </summary>
    [Fact]
    public void Moving_a_file_that_is_not_there_reports_failure()
    {
        Assert.Null(AppDataFile.MoveAside(Source, Path.Combine(_directory, "target")));
    }

    /// <summary>
    /// The search for a free name is bounded, and running out is a failure reported as one rather
    /// than an endless loop or a silent overwrite. The damaged file stays exactly where it was, so
    /// the caller's refusal to write over it still means something.
    /// </summary>
    /// <remarks>
    /// Neither store pinned the bound before the two copies were collapsed into one, so nothing
    /// stopped a consolidation from quietly changing it. This test was written against the
    /// unchanged <c>DaqifiSettings.MoveFileAside</c> and passed there first.
    /// </remarks>
    [Fact]
    public void The_search_for_a_free_name_is_bounded_and_reports_failure_when_it_runs_out()
    {
        File.WriteAllText(Source, "damaged");
        File.WriteAllText(Preferred, "taken");
        for (var i = 1; i < 100; i++) { File.WriteAllText($"{Preferred}-{i}", "taken"); }

        Assert.Null(AppDataFile.MoveAside(Source, Preferred));
        Assert.Equal("damaged", File.ReadAllText(Source));
        Assert.Equal(100, Directory.GetFiles(_directory, "*.corrupt-*").Length);
    }

    /// <summary>
    /// The other side of the bound: the hundredth name is tried, not skipped. Pins the loop's
    /// off-by-one, which a rewrite of the bound would otherwise be free to move.
    /// </summary>
    [Fact]
    public void The_hundredth_name_is_still_tried()
    {
        File.WriteAllText(Source, "damaged");
        File.WriteAllText(Preferred, "taken");
        for (var i = 1; i < 99; i++) { File.WriteAllText($"{Preferred}-{i}", "taken"); }

        Assert.Equal($"{Preferred}-99", AppDataFile.MoveAside(Source, Preferred));
        Assert.False(File.Exists(Source));
    }
    #endregion

    #region WriteAtomically
    /// <summary>
    /// The write is never given the real file: it is handed a temporary path, and only a completed
    /// write is renamed over the destination. That is what stops an interrupted write leaving the
    /// truncated file both #184 and #193 start from.
    /// </summary>
    [Fact]
    public void A_write_goes_to_a_temporary_file_and_is_renamed_over_the_destination()
    {
        string? written = null;

        AppDataFile.WriteAtomically(Source, path =>
        {
            written = path;
            File.WriteAllText(path, "new");
        });

        Assert.NotEqual(Source, written);
        Assert.Equal("new", File.ReadAllText(Source));
        Assert.Empty(TemporaryFiles());
    }

    /// <summary>An existing destination is replaced whole, not merged or appended to.</summary>
    [Fact]
    public void A_write_replaces_an_existing_destination()
    {
        File.WriteAllText(Source, "old");

        AppDataFile.WriteAtomically(Source, path => File.WriteAllText(path, "new"));

        Assert.Equal("new", File.ReadAllText(Source));
        Assert.Empty(TemporaryFiles());
    }

    /// <summary>
    /// A write that fails part-way through must leave the destination untouched, clean up after
    /// itself, and let the original failure through: it is the one the caller reports.
    /// </summary>
    /// <remarks>
    /// This claim was unpinned at BOTH stores, and could not be pinned at either — their writers
    /// are <see cref="XDocument.Save(string)"/> against a document the test supplies, which does
    /// not fail once the directory is writable. Extracting the seam is what made it testable, and
    /// a half-written temporary file left in the data directory is not hypothetical: it is the
    /// shape of the damaged file these stores recover from.
    /// </remarks>
    [Fact]
    public void A_failed_write_leaves_the_destination_alone_removes_the_temporary_file_and_rethrows()
    {
        File.WriteAllText(Source, "old");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            AppDataFile.WriteAtomically(Source, path =>
            {
                File.WriteAllText(path, "half a doc");
                throw new InvalidOperationException("the disk filled up");
            }));

        Assert.Equal("the disk filled up", failure.Message);
        Assert.Equal("old", File.ReadAllText(Source));
        Assert.Empty(TemporaryFiles());
    }

    /// <summary>
    /// Two writes of the same destination never share a temporary name. A fixed <c>.tmp</c> would
    /// let two app instances truncate, move or delete each other's half-written file — which is the
    /// corruption this whole mechanism exists to prevent, reintroduced by the prevention.
    /// </summary>
    [Fact]
    public void Two_writes_of_the_same_destination_use_different_temporary_names()
    {
        var names = new List<string>();
        void Record(string path) { names.Add(path); File.WriteAllText(path, "x"); }

        AppDataFile.WriteAtomically(Source, Record);
        AppDataFile.WriteAtomically(Source, Record);

        Assert.Equal(2, names.Count);
        Assert.NotEqual(names[0], names[1]);
        Assert.All(names, name => Assert.StartsWith(Source, name, StringComparison.Ordinal));
    }
    #endregion
}
