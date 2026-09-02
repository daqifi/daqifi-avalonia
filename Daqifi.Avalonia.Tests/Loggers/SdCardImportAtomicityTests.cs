using System.Runtime.CompilerServices;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// What an SD card import leaves in the database when it does not finish.
///
/// <para>The import was never atomic: <c>CreateSession</c> commits the <c>Sessions</c> row before
/// a single sample is parsed, and every 1,000-sample batch commits in its own transaction. Nothing
/// recorded that an import had started, so a parser that threw part way through a truncated file
/// left a committed session plus however many batches had landed — and because
/// <c>LoggingManager.LoadPersistedLoggingSessions</c> reloads whatever has samples, that fragment
/// came back at the next launch as an ordinary, complete-looking session. Neither half of that
/// crashed; the database simply said something untrue.</para>
///
/// <para>The other half of the same question is the zero-sample session. The importer deliberately
/// treated an empty log as a legitimate empty session and reported success, while the startup
/// purge deleted zero-sample rows without a word — two components disagreeing about whether such
/// a session exists. These tests pin the single answer: a persisted row always either has samples
/// or is being written right now.</para>
///
/// <para>Everything runs against a real SQLite file, migrated by the app's own migrator, because
/// the behaviour under test IS what is committed to that file.</para>
/// </summary>
public sealed class SdCardImportAtomicityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "sd-import-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    /// <summary>Fixed so nothing in a test depends on the wall clock.</summary>
    private static readonly DateTime SampleBase = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    public SdCardImportAtomicityTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(Factory(), DatabasePath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    #region A failed import

    [Fact]
    public async Task An_import_that_throws_after_committing_samples_keeps_them_and_flags_the_session()
    {
        var importer = new SdCardSessionImporter(Factory());

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportSessionAsync(
            Log("log_truncated.bin", ThrowAfter(SdCardSessionImporter.BatchSize)),
            options: null,
            progress: null,
            ct: CancellationToken.None));

        var persisted = Assert.Single(AllSessions());

        // The samples that did land are kept. The source file is truncated, so re-importing it
        // cannot produce them again — discarding them is the one outcome the user cannot undo.
        Assert.Equal(SdCardSessionImporter.BatchSize, SampleCountFor(persisted.ID));

        // And the session says so, which is the whole fix: before this, the row was
        // indistinguishable from a finished import on the next launch.
        Assert.Equal(SessionStatus.ImportFailed, persisted.Status);
        Assert.True(persisted.IsIncompleteImport);
        Assert.Equal(SdCardSessionImporter.BatchSize, persisted.SampleCount);
    }

    [Fact]
    public async Task An_import_that_throws_before_committing_anything_leaves_no_session_behind()
    {
        var importer = new SdCardSessionImporter(Factory());

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportSessionAsync(
            Log("log_garbage.bin", ThrowAfter(0)),
            options: null,
            progress: null,
            ct: CancellationToken.None));

        // Nothing was written, so there is nothing to preserve and no marker worth showing: the
        // failure dialog is the report, and the database goes back to exactly how it was.
        Assert.Empty(AllSessions());
        Assert.Equal(0, TotalSampleRows());
    }

    /// <summary>
    /// A cancelled import is an unfinished import. The user gets no failure dialog for one — the
    /// view models swallow <see cref="OperationCanceledException"/> — so the flag on the row is
    /// the only thing that says the session is a fragment.
    /// </summary>
    [Fact]
    public async Task A_cancelled_import_flags_the_partial_session_too()
    {
        var importer = new SdCardSessionImporter(Factory());
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportSessionAsync(
            Log("log_cancelled.bin", CancelAfter(SdCardSessionImporter.BatchSize, cts)),
            options: null,
            progress: null,
            ct: cts.Token));

        var persisted = Assert.Single(AllSessions());
        Assert.Equal(SessionStatus.ImportFailed, persisted.Status);
        Assert.Equal(SdCardSessionImporter.BatchSize, SampleCountFor(persisted.ID));
    }

    #endregion

    #region A successful import

    [Fact]
    public async Task A_completed_import_leaves_a_session_that_is_marked_complete()
    {
        var importer = new SdCardSessionImporter(Factory());

        var result = await importer.ImportSessionAsync(
            Log("log_good.bin", Entries(12)),
            options: null,
            progress: null,
            ct: CancellationToken.None);

        Assert.True(result.SessionPersisted);
        Assert.Equal(12, result.SamplesImported);

        var persisted = Assert.Single(AllSessions());
        Assert.Equal(SessionStatus.Complete, persisted.Status);
        Assert.False(persisted.IsIncompleteImport);
        Assert.Equal(12, persisted.SampleCount);
        Assert.Equal(12, SampleCountFor(persisted.ID));
    }

    #endregion

    #region A log with nothing in it

    /// <summary>
    /// The second half of the ticket. A 0-byte log is what an interrupted logging session leaves
    /// on a FAT card, and the importer treats it as a legitimate empty file on purpose — but every
    /// reader of the Sessions table filters zero-sample rows out and the startup purge deletes
    /// them, so persisting one meant reporting a successful import of a session that silently
    /// vanished at the next launch. Now nothing is persisted and the caller is told the count.
    /// </summary>
    [Fact]
    public async Task An_empty_log_persists_no_session_and_reports_no_samples()
    {
        var importer = new SdCardSessionImporter(Factory());

        var result = await importer.ImportSessionAsync(
            Log("log_empty.bin", Entries(0)),
            options: null,
            progress: null,
            ct: CancellationToken.None);

        Assert.Equal(0, result.SamplesImported);
        Assert.False(result.SessionPersisted);
        Assert.Empty(AllSessions());
    }

    #endregion

    #region Overwriting an existing session

    /// <summary>
    /// The overwrite used to delete the session it replaces up front, before anything had been
    /// parsed. Combined with removing the row an empty or immediately-failed import leaves, that
    /// destroyed the user's existing session and put nothing in its place.
    /// </summary>
    [Fact]
    public async Task An_overwrite_that_imports_nothing_leaves_the_existing_session_alone()
    {
        var importer = new SdCardSessionImporter(Factory());
        var options = new ImportOptions { OverwriteExistingSession = true };

        var original = await importer.ImportSessionAsync(
            Log("log_good.bin", Entries(4)), options, progress: null, ct: CancellationToken.None);

        var replaced = await importer.ImportSessionAsync(
            Log("log_good.bin", Entries(0)), options, progress: null, ct: CancellationToken.None);

        Assert.False(replaced.SessionPersisted);

        var survivor = Assert.Single(AllSessions());
        Assert.Equal(original.Session.ID, survivor.ID);
        Assert.Equal(SessionStatus.Complete, survivor.Status);
        Assert.Equal(4, SampleCountFor(survivor.ID));
    }

    [Fact]
    public async Task An_overwrite_that_fails_before_writing_anything_leaves_the_existing_session_alone()
    {
        var importer = new SdCardSessionImporter(Factory());
        var options = new ImportOptions { OverwriteExistingSession = true };

        var original = await importer.ImportSessionAsync(
            Log("log_good.bin", Entries(4)), options, progress: null, ct: CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportSessionAsync(
            Log("log_good.bin", ThrowAfter(0)), options, progress: null, ct: CancellationToken.None));

        var survivor = Assert.Single(AllSessions());
        Assert.Equal(original.Session.ID, survivor.ID);
        Assert.Equal(4, SampleCountFor(survivor.ID));
    }

    /// <summary>And when the replacement IS complete, it replaces — which is the whole option.</summary>
    [Fact]
    public async Task An_overwrite_that_completes_replaces_the_session_it_supersedes()
    {
        var importer = new SdCardSessionImporter(Factory());
        var options = new ImportOptions { OverwriteExistingSession = true };

        var original = await importer.ImportSessionAsync(
            Log("log_good.bin", Entries(4)), options, progress: null, ct: CancellationToken.None);

        var replacement = await importer.ImportSessionAsync(
            Log("log_good.bin", Entries(7)), options, progress: null, ct: CancellationToken.None);

        var survivor = Assert.Single(AllSessions());
        Assert.Equal(replacement.Session.ID, survivor.ID);
        Assert.Equal(7, survivor.SampleCount);

        // Not just the Sessions row: the superseded session's samples go with it, or they become
        // orphans that make a later session ID collide.
        Assert.Equal(0, SampleCountFor(original.Session.ID));
        Assert.Equal(7, TotalSampleRows());
    }

    #endregion

    #region What a reload does with the result

    /// <summary>
    /// The trap the two halves set for each other: flagging a failed import is pointless if the
    /// startup purge then deletes the flag. It does not, because the row it flags has samples —
    /// and the row it would delete is the one no import kept.
    /// </summary>
    [Fact]
    public async Task A_reload_lists_the_flagged_fragment_instead_of_deleting_or_normalising_it()
    {
        var factory = Factory();
        var importer = new SdCardSessionImporter(factory);

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportSessionAsync(
            Log("log_truncated.bin", ThrowAfter(SdCardSessionImporter.BatchSize)),
            options: null,
            progress: null,
            ct: CancellationToken.None));

        var loaded = NewManager(factory).LoadPersistedLoggingSessions();

        var session = Assert.Single(loaded);
        Assert.Equal(SessionStatus.ImportFailed, session.Status);
        Assert.True(session.IsIncompleteImport);
    }

    /// <summary>
    /// The purge's own job, unchanged: a session row with no samples and no import behind it is
    /// litter from a logging session that was started and abandoned, and it still goes.
    /// </summary>
    [Fact]
    public void A_reload_still_purges_an_abandoned_empty_logging_session()
    {
        var factory = Factory();
        InsertSession(factory, id: 7, status: SessionStatus.Complete);

        var loaded = NewManager(factory).LoadPersistedLoggingSessions();

        Assert.Empty(loaded);
        Assert.Empty(AllSessions());
    }

    /// <summary>
    /// An import commits its row before the first sample, so for a moment it is a zero-sample row
    /// — exactly what the purge deletes. That hazard is why the mobile pane got a read-only
    /// snapshot variant; the status test makes it impossible rather than merely avoided by which
    /// method a caller happens to pick.
    /// </summary>
    [Fact]
    public void A_reload_does_not_purge_a_row_an_import_is_still_writing()
    {
        var factory = Factory();
        InsertSession(factory, id: 3, status: SessionStatus.Importing);

        NewManager(factory).LoadPersistedLoggingSessions();

        Assert.Single(AllSessions());
    }

    /// <summary>
    /// The case no catch block can cover: the process was killed mid-import, so nothing ran to
    /// record the failure. A row still marked as importing when the app starts is that, and the
    /// samples it did land must not come back looking like a finished session.
    /// </summary>
    [Fact]
    public void A_reload_flags_a_session_left_mid_import_by_a_process_that_died()
    {
        var factory = Factory();
        InsertSession(factory, id: 4, status: SessionStatus.Importing);
        InsertOrphanSample(loggingSessionId: 4);

        var loaded = NewManager(factory).LoadPersistedLoggingSessions();

        Assert.Equal(SessionStatus.ImportFailed, Assert.Single(loaded).Status);
        Assert.Equal(SessionStatus.ImportFailed, Assert.Single(AllSessions()).Status);
    }

    #endregion

    #region Session IDs

    /// <summary>
    /// The importer used to allocate <c>MAX(Sessions.ID) + 1</c>, the narrow version
    /// <c>LoggingManager</c>'s own comment warns against: an ID orphan rows still reference, whose
    /// reuse makes the composite primary key on <c>SessionDeviceMetadata</c> reject the insert.
    /// Both now go through <see cref="SessionIdAllocator"/>.
    /// </summary>
    [Fact]
    public async Task An_import_does_not_reuse_an_id_that_orphan_sample_rows_still_reference()
    {
        // No Sessions row anywhere, so MAX(Sessions.ID) + 1 would hand out 0 and collide.
        InsertOrphanSample(loggingSessionId: 41);

        var importer = new SdCardSessionImporter(Factory());
        var result = await importer.ImportSessionAsync(
            Log("log_good.bin", Entries(3)),
            options: null,
            progress: null,
            ct: CancellationToken.None);

        Assert.Equal(42, result.Session.ID);
    }

    #endregion

    #region Fixtures

    /// <summary>
    /// A parsed log with no device configuration, so the importer discovers one analog channel
    /// from the first entry and each entry becomes exactly one <c>Samples</c> row — which is what
    /// lets a test say "fail after one committed batch" in units of entries.
    /// </summary>
    private static SdCardLogSession Log(string fileName, IAsyncEnumerable<SdCardLogEntry> samples) =>
        new(fileName, SampleBase, deviceConfig: null, samples);

    private static SdCardLogEntry Entry(int index) =>
        new(SampleBase.AddMilliseconds(index), [index * 0.5], 0u, null);

    private static async IAsyncEnumerable<SdCardLogEntry> Entries(
        int count,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return Entry(i);
            await Task.Yield();
        }
    }

    /// <summary>What a truncated or garbage log does: entries, and then the parser gives up.</summary>
    private static async IAsyncEnumerable<SdCardLogEntry> ThrowAfter(
        int count,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entry in Entries(count, ct))
        {
            yield return entry;
        }

        await Task.Yield();
        throw new InvalidDataException("The log ends part way through a message.");
    }

    /// <summary>The user pressing cancel once some of the file has been read.</summary>
    private static async IAsyncEnumerable<SdCardLogEntry> CancelAfter(
        int count,
        CancellationTokenSource cts,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entry in Entries(count, ct))
        {
            yield return entry;
        }

        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
    }

    #endregion

    #region Database helpers

    private TestContextFactory Factory() => new(DatabasePath);

    private LoggingManager NewManager(IDbContextFactory<LoggingContext> factory) =>
        new(factory, Path.Combine(_directory, "DAQifiProfilesConfiguration.xml"));

    private List<LoggingSession> AllSessions()
    {
        using var context = Factory().CreateDbContext();
        return context.Sessions.AsNoTracking().OrderBy(s => s.ID).ToList();
    }

    private long SampleCountFor(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples.LongCount(s => s.LoggingSessionID == sessionId);
    }

    private long TotalSampleRows()
    {
        using var context = Factory().CreateDbContext();
        return context.Samples.LongCount();
    }

    private static void InsertSession(IDbContextFactory<LoggingContext> factory, int id, SessionStatus status)
    {
        using var context = factory.CreateDbContext();
        context.Sessions.Add(new LoggingSession(id, $"Session_{id}") { Status = status });
        context.SaveChanges();
    }

    /// <summary>
    /// A <c>Samples</c> row whose session does not exist — what a crash, or a delete that ran
    /// without SQLite foreign keys enabled, leaves behind. Written with foreign keys off for the
    /// duration, since the point is to produce a row the schema would normally forbid.
    /// </summary>
    private void InsertOrphanSample(int loggingSessionId)
    {
        using var connection = new SqliteConnection($"Data source={DatabasePath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=OFF";
        pragma.ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Samples (LoggingSessionID, ChannelName, DeviceName, DeviceSerialNo, Color, Type, Value, TimestampTicks) " +
            "VALUES ($id, 'AI0', 'Nq1', 'SERIAL-A', '#FFD32F2F', 0, 1.0, 0)";
        insert.Parameters.AddWithValue("$id", loggingSessionId);
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// The same SQLite context factory <c>App.Initialize</c> registers in DI, without the
    /// container — as in <see cref="DatabaseMigratorUnusableDatabaseTests"/>.
    /// </summary>
    private sealed class TestContextFactory(string databasePath) : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() => new(
            new DbContextOptionsBuilder<LoggingContext>()
                .UseSqlite($"Data source={databasePath}")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);
    }

    #endregion
}
