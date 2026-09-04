using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Exporter;

/// <summary>
/// Pins the export's promise about the file it is overwriting: until an export finishes, the
/// destination the user already had must not change.
///
/// <para>
/// The regression (issue #236): <c>OptimizedLoggingSessionExporter.RunExport</c> opened the
/// destination itself with <c>append: false</c>, which truncates an existing file the instant the
/// stream is created — before a single row is written and before any cancellation checkpoint is
/// reached. Cancelling an export therefore destroyed the complete CSV the user had exported
/// earlier, and <c>ExportDialogViewModel</c> deliberately suppresses the result state on a cancel,
/// so the dialog returned to its configuration form saying nothing at all. Same for a mid-write
/// failure: the destination was already gone by the time the failure surfaced.
/// </para>
///
/// <para>
/// These tests drive the real exporter over a real SQLite database, so they assert about bytes that
/// are actually on disk. Cancellation and failure are injected through the context factory rather
/// than by racing a timer: the source opens a context for its channel list BEFORE the header is
/// written and again for the sample stream AFTER, so firing on the second and later
/// <c>CreateDbContext</c> reproduces "cancelled once the export was already writing" with no timing
/// assumptions and no dependence on how often core reports progress.
/// </para>
/// </summary>
public sealed class ExportAtomicityTests : IDisposable
{
    /// <summary>What a previous, completed export left at the destination. Every assertion below
    /// that the user's file survived is an exact comparison against this.</summary>
    private const string PreviousExport = "Time,Nyquist:SERIAL-0:AI9\n2020-01-01T00:00:00Z,42\n";

    /// <summary>Throwaway root for this test's database and CSVs. Never the real DAQiFi data
    /// directory (see <see cref="TestDataDirectory"/>).</summary>
    private readonly string _root;
    private readonly IDbContextFactory<LoggingContext> _contexts;
    private int _nextSessionId;

    public ExportAtomicityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daqifi-export-atomicity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _contexts = TestDatabase.Contexts(Path.Combine(_root, "DAQiFiDatabase.db"));
        using var context = _contexts.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort — a leftover temp directory must not fail a test */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>
    /// The reported bug, in both of its shapes: the user cancels while the export is still running
    /// and the complete CSV they exported earlier must still be there, byte for byte.
    /// <c>cancelBeforeAnyWork</c> false is the realistic one — the export has already opened the
    /// destination and written its header by the time Cancel is clicked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancelling_leaves_a_previous_export_intact(bool cancelBeforeAnyWork)
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");
        File.WriteAllText(destination, PreviousExport);

        using var cts = new CancellationTokenSource();
        var contexts = cancelBeforeAnyWork ? _contexts : CancelOnceWriting(cts);
        if (cancelBeforeAnyWork) { cts.Cancel(); }

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new OptimizedLoggingSessionExporter(contexts).ExportLoggingSession(
                session, destination, exportRelativeTime: false, new Progress<int>(), cts.Token, 0, 1));

        AssertDestinationUntouched(destination);
    }

    /// <summary>
    /// The same cancel when the destination did NOT already exist. Nothing may be left behind: a
    /// zero-byte or header-only <c>readings.csv</c> sitting next to no error message reads to the
    /// user (and to the next export's pre-flight) as a file that was exported.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancelling_leaves_nothing_behind_when_the_destination_did_not_exist(bool cancelBeforeAnyWork)
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");

        using var cts = new CancellationTokenSource();
        var contexts = cancelBeforeAnyWork ? _contexts : CancelOnceWriting(cts);
        if (cancelBeforeAnyWork) { cts.Cancel(); }

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new OptimizedLoggingSessionExporter(contexts).ExportLoggingSession(
                session, destination, exportRelativeTime: false, new Progress<int>(), cts.Token, 0, 1));

        Assert.False(File.Exists(destination), $"a cancelled export left a file behind: {Present()}");
        AssertNoLeftovers();
    }

    /// <summary>
    /// A failure that lands after rows have started flowing — a transient database or I/O error, the
    /// case <c>TryExportLoggingSession</c> exists to report truthfully. The destination is not the
    /// place to discover it: the previous export must survive, and the caller must be told.
    /// </summary>
    [Fact]
    public void A_failure_once_writing_has_started_leaves_a_previous_export_intact()
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");
        File.WriteAllText(destination, PreviousExport);

        var exporter = new OptimizedLoggingSessionExporter(FailOnceWriting());

        Assert.Throws<InvalidOperationException>(() => exporter.ExportLoggingSession(
            session, destination, exportRelativeTime: false, new Progress<int>(), CancellationToken.None, 0, 1));

        AssertDestinationUntouched(destination);
    }

    /// <summary>As above, with no destination to begin with: a failed export creates no file.</summary>
    [Fact]
    public void A_failure_once_writing_has_started_leaves_nothing_behind()
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");

        var exporter = new OptimizedLoggingSessionExporter(FailOnceWriting());

        Assert.Throws<InvalidOperationException>(() => exporter.ExportLoggingSession(
            session, destination, exportRelativeTime: false, new Progress<int>(), CancellationToken.None, 0, 1));

        Assert.False(File.Exists(destination), $"a failed export left a file behind: {Present()}");
        AssertNoLeftovers();
    }

    /// <summary>
    /// The averaged export is a second entry point into the same write, so it carries the same
    /// promise — asserted here because it reaches the destination through its own public method.
    /// </summary>
    [Fact]
    public void Cancelling_an_averaged_export_leaves_a_previous_export_intact()
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");
        File.WriteAllText(destination, PreviousExport);

        using var cts = new CancellationTokenSource();
        var exporter = new OptimizedLoggingSessionExporter(CancelOnceWriting(cts));

        Assert.ThrowsAny<OperationCanceledException>(() => exporter.ExportAverageSamples(
            session, destination, averageQuantity: 2, exportRelativeTime: false,
            new Progress<int>(), cts.Token, 0, 1));

        AssertDestinationUntouched(destination);
    }

    /// <summary>
    /// The guarantee the mobile export leans on now that it no longer stages its own temp file:
    /// <c>TryExportLoggingSession</c> returns false on a mid-write failure and the destination still
    /// holds the previous export, so a caller that trusts the return value cannot report a success
    /// that clobbered a good file.
    /// </summary>
    [Fact]
    public void The_try_overload_reports_a_mid_export_failure_and_leaves_a_previous_export_intact()
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");
        File.WriteAllText(destination, PreviousExport);

        var exporter = new OptimizedLoggingSessionExporter(FailOnceWriting());

        var exported = exporter.TryExportLoggingSession(
            session, destination, exportRelativeTime: false, new Progress<int>(), CancellationToken.None, 0, 1);

        Assert.False(exported);
        AssertDestinationUntouched(destination);
    }

    /// <summary>Cancelling through the try overload propagates (it is not a "failed export") and
    /// still leaves the previous export alone.</summary>
    [Fact]
    public void Cancelling_the_try_overload_leaves_a_previous_export_intact()
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");
        File.WriteAllText(destination, PreviousExport);

        using var cts = new CancellationTokenSource();
        var exporter = new OptimizedLoggingSessionExporter(CancelOnceWriting(cts));

        Assert.ThrowsAny<OperationCanceledException>(() => exporter.TryExportLoggingSession(
            session, destination, exportRelativeTime: false, new Progress<int>(), cts.Token, 0, 1));

        AssertDestinationUntouched(destination);
    }

    /// <summary>
    /// The ordinary case, so the fix cannot be "never write anything": a completed export replaces
    /// the destination with this run's rows and leaves no staging file next to it.
    /// </summary>
    [Fact]
    public void A_completed_export_replaces_the_destination_and_leaves_nothing_beside_it()
    {
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");
        File.WriteAllText(destination, PreviousExport);

        new OptimizedLoggingSessionExporter(_contexts).ExportLoggingSession(
            session, destination, exportRelativeTime: false, new Progress<int>(), CancellationToken.None, 0, 1);

        var csv = File.ReadAllText(destination);
        Assert.Contains("AI0", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("AI9", csv, StringComparison.Ordinal);
        AssertNoLeftovers();
    }

    /// <summary>
    /// The staging file has to be one the export itself created, never a path it assumed was free.
    /// Staging both truncates and deletes, so a predictable name would let an export destroy a file
    /// the app did not write — this bug again, one filename removed — and would let two exports of
    /// one destination clobber each other's staged rows. A decoy sitting at the obvious staging path
    /// must therefore come through untouched, whether the export finishes or is cancelled.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_export_does_not_touch_a_file_that_merely_looks_like_its_staging_file(bool cancelled)
    {
        const string decoyContent = "someone else's file";
        var session = SeedSession("AI0", 1.25);
        var destination = Path.Combine(_root, "readings.csv");
        var decoyName = "readings.csv.exporting";
        File.WriteAllText(Path.Combine(_root, decoyName), decoyContent);

        using var cts = new CancellationTokenSource();
        var exporter = new OptimizedLoggingSessionExporter(cancelled ? CancelOnceWriting(cts) : _contexts);

        void Export() => exporter.ExportLoggingSession(
            session, destination, exportRelativeTime: false, new Progress<int>(), cts.Token, 0, 1);

        if (cancelled) { Assert.ThrowsAny<OperationCanceledException>(Export); } else { Export(); }

        Assert.Equal(decoyContent, File.ReadAllText(Path.Combine(_root, decoyName)));
        AssertNoLeftovers(decoyName);
    }

    /// <summary>The destination still holds exactly what the previous export put there.</summary>
    private void AssertDestinationUntouched(string destination)
    {
        Assert.True(File.Exists(destination), $"the previous export was deleted: {Present()}");
        Assert.Equal(PreviousExport, File.ReadAllText(destination));
        AssertNoLeftovers();
    }

    /// <summary>No staging file was abandoned beside the destination. Matched by prefix rather than
    /// against a whole-directory listing so the assertion cannot be tripped by SQLite's own
    /// transient <c>-journal</c>/<c>-wal</c> files, which have nothing to do with the export.</summary>
    /// <param name="alsoExpected">A sibling the test itself put there and expects to survive.</param>
    private void AssertNoLeftovers(string? alsoExpected = null)
    {
        var stray = Directory.GetFiles(_root)
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith("readings.csv", StringComparison.Ordinal))
            .Where(name => name != "readings.csv" && name != alsoExpected)
            .ToArray();
        Assert.True(stray.Length == 0, $"the export abandoned {string.Join(", ", stray)}");
    }

    /// <summary>
    /// A context factory that cancels <paramref name="cts"/> from the SECOND context onwards. The
    /// sample source opens its first context to list the session's channels, which core needs before
    /// it writes the header, and opens another for the sample stream afterwards — so this fires only
    /// once the export has committed to writing, with no reliance on timing.
    /// </summary>
    private IDbContextFactory<LoggingContext> CancelOnceWriting(CancellationTokenSource cts) =>
        new HookedContextFactory(_contexts, cts.Cancel);

    /// <summary>As <see cref="CancelOnceWriting"/>, but raises the kind of error a database or disk
    /// can produce part way through a long export.</summary>
    private IDbContextFactory<LoggingContext> FailOnceWriting() =>
        new HookedContextFactory(_contexts, () => throw new InvalidOperationException("simulated mid-export database failure"));

    /// <summary>What is actually sitting in the export folder, for an assertion message that says why.</summary>
    private string Present() =>
        "[" + string.Join(", ", Directory.GetFiles(_root).Select(Path.GetFileName)) + "]";

    /// <summary>Writes one session with two samples into the temp database and returns the detached
    /// row the dialog would hand the exporter.</summary>
    private LoggingSession SeedSession(string channelName, double value)
    {
        // Session ids are assigned by the app, not by SQLite (Sessions.ID has no autoincrement
        // annotation), so the seeder has to supply its own.
        var id = ++_nextSessionId;
        using var context = _contexts.CreateDbContext();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new LoggingSession { ID = id, Name = "Atomicity " + id, SessionStart = start };
        context.Sessions.Add(session);
        context.SaveChanges();

        // Two samples so the averaged export has a full window to fold.
        for (var i = 0; i < 2; i++)
        {
            context.Samples.Add(new DataSample
            {
                LoggingSession = session,
                LoggingSessionID = session.ID,
                DeviceName = "Nyquist",
                DeviceSerialNo = "SERIAL-1",
                ChannelName = channelName,
                Color = "#FFD32F2F",
                Type = ChannelType.Analog,
                TimestampTicks = start.AddSeconds(i).Ticks,
                Value = value + i,
            });
        }

        context.SaveChanges();
        return new LoggingSession { ID = session.ID, Name = session.Name };
    }

    /// <summary>Wraps the real factory and runs <c>onWriting</c> on every context after the first.
    /// Nothing here names a data source: the connection string stays <see cref="TestDatabase"/>'s.</summary>
    private sealed class HookedContextFactory(IDbContextFactory<LoggingContext> inner, Action onWriting)
        : IDbContextFactory<LoggingContext>
    {
        private int _created;

        public LoggingContext CreateDbContext()
        {
            if (Interlocked.Increment(ref _created) > 1) { onWriting(); }
            return inner.CreateDbContext();
        }
    }
}
