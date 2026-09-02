using System.Globalization;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the export dialog's promise that selecting N sessions produces N files.
///
/// <para>
/// The regression: <c>ResolveExportTargets</c> named each destination
/// <c>{sanitized session name}.csv</c> with no check that the name was already taken, and
/// <c>OptimizedLoggingSessionExporter.RunExport</c> opens its <c>StreamWriter</c> with
/// <c>append: false</c>. Two selected sessions sharing a name therefore resolved to ONE path, the
/// second truncating the first — and because both writes succeeded, the dialog reported "Export
/// complete". The user was told the export worked, one CSV was missing, and the survivor held only
/// the later session's rows. Nothing threw, nothing was logged, and nothing about the result was
/// recoverable from.
/// </para>
///
/// <para>
/// End-to-end on purpose. Everything below the substituted session-name source is the production
/// path: the dialog's own export command, the real target resolution and pre-flight, the real
/// <see cref="Daqifi.Desktop.Exporter.OptimizedLoggingSessionExporter"/>, and a real SQLite
/// database — so the assertions are about files that actually exist on disk with the rows they
/// should hold, not about a naming helper in isolation. Only the name source is substituted, and
/// only because <c>LoggingManager.Instance</c> is a lazy singleton that resolves
/// <c>App.ServiceProvider</c> and so cannot be touched without an app host.
/// </para>
/// </summary>
public sealed class ExportFileNameCollisionTests : IDisposable
{
    /// <summary>Throwaway root for this test's database and exported CSVs. Never the real DAQiFi
    /// data directory (see <see cref="TestDataDirectory"/>).</summary>
    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private int _nextSessionId;

    public ExportFileNameCollisionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daqifi-export-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _contexts = new SqliteContextFactory(Path.Combine(_root, "DAQiFiDatabase.db"));
        using var context = _contexts.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections, which keeps a handle on the file; drop them
        // before deleting so the cleanup is not a no-op on Windows.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort — a leftover temp directory must not fail a test */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>
    /// The three ways two selected sessions end up wanting the same file, all named by issue #186:
    /// the user renamed both rows to the same thing; the same SD card log was imported twice so both
    /// rows carry the importer's generated name; and — the case a set keyed on the RAW name would
    /// miss — two genuinely different names that collide only after sanitization, because <c>/</c>
    /// is not a legal file-name character and becomes <c>_</c>.
    /// </summary>
    [Theory]
    [InlineData("Test", "Test", "Test.csv", "Test (2).csv")]
    [InlineData("SD Import - LOG_001", "SD Import - LOG_001", "SD Import - LOG_001.csv", "SD Import - LOG_001 (2).csv")]
    [InlineData("a/b", "a_b", "a_b.csv", "a_b (2).csv")]
    public async Task Two_sessions_with_colliding_names_each_get_their_own_csv(
        string firstName, string secondName, string firstFile, string secondFile)
    {
        // Distinct channel names as well as distinct values, so a file can be attributed to exactly
        // one session by its header alone — independent of how the CSV formats a double.
        var first = SeedSession(firstName, "AI0", 1.25);
        var second = SeedSession(secondName, "AI1", 2.5);
        var destination = Path.Combine(_root, "export");

        var viewModel = new ExportDialogViewModel(_contexts, new[] { first, second });
        await viewModel.ExportToDirectoryAsync(destination);

        // The dialog reported success before this fix too — that is what made the loss silent, and
        // it must still be the truth afterwards.
        Assert.True(viewModel.ExportSucceeded, viewModel.ExportResultMessage);

        var firstPath = Path.Combine(destination, firstFile);
        var secondPath = Path.Combine(destination, secondFile);
        Assert.True(File.Exists(firstPath), $"'{firstName}' was not exported: {firstFile} is missing. Present: {Present(destination)}");
        Assert.True(File.Exists(secondPath), $"'{secondName}' was not exported: {secondFile} is missing. Present: {Present(destination)}");

        var firstCsv = await File.ReadAllTextAsync(firstPath);
        var secondCsv = await File.ReadAllTextAsync(secondPath);

        // Each file holds ITS session's channel and value, and not the other's. Asserting the
        // absence matters as much as the presence: the truncating write left one file that existed,
        // was non-empty, and contained the WRONG session's rows.
        Assert.Contains("AI0", firstCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("AI1", firstCsv, StringComparison.Ordinal);
        Assert.Contains(1.25.ToString(CultureInfo.InvariantCulture), firstCsv, StringComparison.Ordinal);

        Assert.Contains("AI1", secondCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("AI0", secondCsv, StringComparison.Ordinal);
        Assert.Contains(2.5.ToString(CultureInfo.InvariantCulture), secondCsv, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two sessions with blank stored names must still get a file each, and neither may be called
    /// <c>.csv</c>. This one passed before the fix as well — <see cref="LoggingSession.Name"/>
    /// substitutes <c>"Session {ID}"</c> for a blank name, so the two were already distinct by the
    /// time the exporter saw them. It is here so that fallback cannot be "simplified" away without
    /// something failing, since nothing downstream would notice two blanks collapsing into one.
    /// </summary>
    [Fact]
    public async Task Sessions_with_blank_names_still_get_a_file_each()
    {
        var first = SeedSession("   ", "AI0", 1.25);
        var second = SeedSession(string.Empty, "AI1", 2.5);
        var destination = Path.Combine(_root, "export");

        var viewModel = new ExportDialogViewModel(_contexts, new[] { first, second });
        await viewModel.ExportToDirectoryAsync(destination);

        Assert.True(viewModel.ExportSucceeded, viewModel.ExportResultMessage);
        Assert.Equal(2, Directory.GetFiles(destination).Length);
        Assert.True(File.Exists(Path.Combine(destination, $"Session {first.ID}.csv")), Present(destination));
        Assert.True(File.Exists(Path.Combine(destination, $"Session {second.ID}.csv")), Present(destination));
    }

    /// <summary>
    /// The ordinary case, so the fix cannot be "disambiguate everything": distinct names keep the
    /// exact file names they always had, with no " (2)" suffix anywhere.
    /// </summary>
    [Fact]
    public async Task Distinctly_named_sessions_keep_their_own_names()
    {
        var first = SeedSession("Morning run", "AI0", 1.25);
        var second = SeedSession("Afternoon run", "AI1", 2.5);
        var destination = Path.Combine(_root, "export");

        var viewModel = new ExportDialogViewModel(_contexts, new[] { first, second });
        await viewModel.ExportToDirectoryAsync(destination);

        Assert.True(viewModel.ExportSucceeded, viewModel.ExportResultMessage);
        Assert.Equal(
            new[] { "Afternoon run.csv", "Morning run.csv" },
            Directory.GetFiles(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Two spellings of the same word — <c>Café</c> composed (U+00E9) and decomposed (e + U+0301) —
    /// are one file on macOS, whose volumes resolve canonically equivalent names to the same entry,
    /// but two distinct UTF-16 strings to an ordinal comparer. Left unhandled that is this whole
    /// bug again, one encoding removed.
    ///
    /// <para>
    /// Asserted against <see cref="ExportFileNamer"/> directly rather than by exporting and looking
    /// for the files: the file-existence form of this check only fails on a normalization-insensitive
    /// volume, so it would pass on CI's Linux runner with the fix reverted and pin nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void Canonically_equivalent_names_are_treated_as_a_collision()
    {
        // Spelled with escapes, not literal characters: the two forms are indistinguishable in an
        // editor, and a tool that normalized this file would quietly gut the test.
        const string composedName = "Caf\u00E9";     // é as a single code point
        const string decomposedName = "Cafe\u0301";  // e followed by a combining acute
        var namer = new ExportFileNamer();

        var composed = namer.NextName(composedName, 1);
        var decomposed = namer.NextName(decomposedName, 2);

        Assert.Equal(composedName, composed);
        Assert.NotEqual(composed, decomposed);
        // The written name keeps the caller's own spelling — only the collision key is normalized.
        Assert.Equal(decomposedName + " (2)", decomposed);
    }

    /// <summary>
    /// The compatibility-equivalent pair the normalization deliberately does NOT fold: no file
    /// system treats <c>ﬁ</c> (U+FB01) and <c>fi</c> as one name, so disambiguating them would
    /// suffix a file that was never in conflict.
    /// </summary>
    [Fact]
    public void Compatibility_equivalent_names_are_left_alone()
    {
        var namer = new ExportFileNamer();

        Assert.Equal("\uFB01le", namer.NextName("\uFB01le", 1));
        Assert.Equal("file", namer.NextName("file", 2));
    }

    /// <summary>Writes one session with a single sample into the temp database and returns the
    /// detached row the Logged Data list would hand the dialog (id + name).</summary>
    private LoggingSession SeedSession(string name, string channelName, double value)
    {
        // Session ids are assigned by the app, not by SQLite (Sessions.ID has no autoincrement
        // annotation), so the seeder has to supply its own.
        var id = ++_nextSessionId;
        using var context = _contexts.CreateDbContext();
        var session = new LoggingSession { ID = id, Name = name, SessionStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        context.Sessions.Add(session);
        context.SaveChanges();

        context.Samples.Add(new DataSample
        {
            LoggingSession = session,
            LoggingSessionID = session.ID,
            DeviceName = "Nyquist",
            DeviceSerialNo = "SERIAL-1",
            ChannelName = channelName,
            Color = "#FFD32F2F",
            Type = ChannelType.Analog,
            TimestampTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            Value = value,
        });
        context.SaveChanges();

        return new LoggingSession { ID = session.ID, Name = name };
    }

    /// <summary>What actually landed in the destination, for an assertion message that says why.</summary>
    private static string Present(string directory) =>
        Directory.Exists(directory)
            ? "[" + string.Join(", ", Directory.GetFiles(directory).Select(Path.GetFileName)) + "]"
            : "(destination directory does not exist)";

    /// <summary>The production <see cref="LoggingContext"/> over a throwaway SQLite file, standing
    /// in for the factory the app resolves from its DI container.</summary>
    private sealed class SqliteContextFactory : IDbContextFactory<LoggingContext>
    {
        private readonly DbContextOptions<LoggingContext> _options;

        internal SqliteContextFactory(string databasePath)
        {
            _options = new DbContextOptionsBuilder<LoggingContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
        }

        public LoggingContext CreateDbContext() => new(_options);
    }
}
