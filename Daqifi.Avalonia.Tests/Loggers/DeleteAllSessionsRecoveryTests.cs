using System.Collections.ObjectModel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// What happens when "Delete All Sessions" gets part-way and stops.
///
/// <para>The purge deleted <c>DAQiFiDatabase.db</c> and its sidecars first and only then asked EF to
/// rebuild the schema, so anything that failed after the first delete — a second process, a scanner
/// or Finder holding a sidecar, a full disk, a read-only folder — left the app with a
/// STRUCTURALLY VALID BUT TABLE-LESS database and no way back. The failure was swallowed to the log,
/// the session list still showed every session, and the next flip of the logging toggle ran
/// <c>SELECT MAX(...) FROM Sessions</c> against that file from inside a UI property setter and took
/// the process down with <c>no such table: Sessions</c>.</para>
///
/// <para>These are the two halves of that, tested separately: the purge must never be able to
/// produce the table-less state, and a start that fails must not be able to reach the dispatcher.
/// This is a different fault from <see cref="DatabaseMigratorUnusableDatabaseTests"/>, whose file is
/// DAMAGED; this one's file is perfectly readable and simply has no tables in it, which SQLite is
/// entirely happy about.</para>
/// </summary>
public class DeleteAllSessionsRecoveryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "deleteall-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public DeleteAllSessionsRecoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The crash itself. A zero-byte file is a perfectly valid, perfectly empty SQLite database —
    /// which is exactly what an interrupted purge leaves at the database path — and starting a
    /// logging session against it threw <c>SqliteException: no such table: Sessions</c> out of a
    /// property setter, through the toggle's two-way binding write, into the dispatcher, and out of
    /// the process.
    /// </summary>
    [Fact]
    public void StartingASession_AgainstATablelessDatabase_DoesNotThrow()
    {
        File.WriteAllBytes(DatabasePath, []);

        var manager = new LoggingManager(Factory()) { CurrentMode = LoggingMode.Stream };

        manager.Active = true;

        // Reported rather than thrown, and specific enough to act on — the caller shows this
        // sentence and puts the toggle back to OFF.
        Assert.NotNull(manager.SessionStartFailure);
    }

    /// <summary>
    /// The guard on the above: <see cref="LoggingManager.SessionStartFailure"/> is what the toggle
    /// reads to decide whether to revert, so a session that DID start must never leave it set.
    /// </summary>
    [Fact]
    public void StartingASession_AgainstAWorkingDatabase_ReportsNoFailure()
    {
        var factory = Factory();
        SeedDatabaseWithOneSession(factory, DatabasePath);

        var manager = new LoggingManager(factory) { CurrentMode = LoggingMode.Stream };

        manager.Active = true;

        Assert.Null(manager.SessionStartFailure);
        Assert.NotNull(manager.Session);
    }

    /// <summary>
    /// The state that produced the crash. When the rebuild cannot run, the user's database must
    /// still be there, still readable and still holding their sessions — the purge failed, so
    /// nothing should have been destroyed by it.
    /// </summary>
    [Fact]
    public async Task DeleteAll_WhenTheSchemaCannotBeRebuilt_LeavesTheDatabaseIntact()
    {
        var factory = new FailableContextFactory(DatabasePath);
        SeedDatabaseWithOneSession(factory, DatabasePath);

        var host = new FakeHost(new LoggingSession(0, "Session_0"));
        var listViewModel = new LoggingSessionListViewModel(host, () => factory, DatabasePath, new SilentLogger());

        factory.FailNextCreate = true;
        await listViewModel.DeleteAllSessionsAsync();

        factory.FailNextCreate = false;
        SqliteConnection.ClearAllPools();
        using var context = factory.CreateDbContext();
        Assert.Equal("Session_0", Assert.Single(context.Sessions.ToList()).Name);

        // Put BACK, not merely preserved somewhere: a failed purge must leave the directory as it
        // found it, or the next launch opens an empty database and the sessions look deleted anyway.
        Assert.Empty(RelocatedFiles());
    }

    /// <summary>
    /// The half of the report that has nothing to do with crashing: the pane went on listing
    /// sessions whose data the purge had already destroyed, and the only trace of the failure was a
    /// line in a log file the user will never open.
    /// </summary>
    [Fact]
    public async Task DeleteAll_WhenTheSchemaCannotBeRebuilt_KeepsTheListAndTellsTheUser()
    {
        var factory = new FailableContextFactory(DatabasePath);
        SeedDatabaseWithOneSession(factory, DatabasePath);

        var host = new FakeHost(new LoggingSession(0, "Session_0"));
        var listViewModel = new LoggingSessionListViewModel(host, () => factory, DatabasePath, new SilentLogger());

        factory.FailNextCreate = true;
        await listViewModel.DeleteAllSessionsAsync();

        Assert.Single(host.LoggingSessions);
        Assert.NotEmpty(host.MessagesShown);
    }

    /// <summary>The ordinary case, which must keep working: everything goes, the schema comes back.</summary>
    [Fact]
    public async Task DeleteAll_OnTheHappyPath_EmptiesTheDatabaseAndLeavesItUsable()
    {
        var factory = new FailableContextFactory(DatabasePath);
        SeedDatabaseWithOneSession(factory, DatabasePath);

        var host = new FakeHost(new LoggingSession(0, "Session_0"));
        var listViewModel = new LoggingSessionListViewModel(host, () => factory, DatabasePath, new SilentLogger());

        await listViewModel.DeleteAllSessionsAsync();

        Assert.Empty(host.LoggingSessions);
        Assert.Empty(host.MessagesShown);
        using var context = factory.CreateDbContext();
        Assert.Empty(context.Sessions.ToList());
        Assert.Empty(context.Database.GetPendingMigrations());

        // The user asked for this data to be destroyed, so a successful purge deletes what it moved
        // aside — the one place this differs from the corruption quarantine, which keeps its file.
        Assert.Empty(RelocatedFiles());
    }

    /// <summary>
    /// The sidecar the old purge did not know about. Nothing in this app sets <c>journal_mode</c>,
    /// so SQLite runs in its default DELETE mode and the file it writes is <c>-journal</c> — which
    /// the delete list (<c>-wal</c>, <c>-shm</c>) missed, leaving a rollback journal beside the
    /// freshly created database for SQLite to replay the deleted sessions back out of.
    /// </summary>
    [Fact]
    public async Task DeleteAll_RemovesTheRollbackJournalTheOldPurgeLeftBehind()
    {
        var factory = new FailableContextFactory(DatabasePath);
        SeedDatabaseWithOneSession(factory, DatabasePath);
        File.WriteAllText(DatabasePath + "-journal", "stale rollback journal");

        var host = new FakeHost(new LoggingSession(0, "Session_0"));
        var listViewModel = new LoggingSessionListViewModel(host, () => factory, DatabasePath, new SilentLogger());

        await listViewModel.DeleteAllSessionsAsync();

        Assert.False(File.Exists(DatabasePath + "-journal"));
        Assert.Empty(RelocatedFiles());
    }

    /// <summary>Files a purge moved aside and has not cleaned up, in the test's own directory.</summary>
    private string[] RelocatedFiles() => Directory.GetFiles(_directory, "*.deleted-*");

    /// <summary>
    /// A migrated database with one session in it, through the app's own migrator, so a test can
    /// tell "left alone" from "wiped".
    /// </summary>
    private static void SeedDatabaseWithOneSession(IDbContextFactory<LoggingContext> factory, string databasePath)
    {
        DatabaseMigrator.ApplyMigrations(factory, databasePath);

        using (var context = factory.CreateDbContext())
        {
            context.Sessions.Add(new LoggingSession(0, "Session_0"));
            context.SaveChanges();
        }

        SqliteConnection.ClearAllPools();
    }

    private FailableContextFactory Factory() => new(DatabasePath);

    /// <summary>
    /// The same SQLite context factory <c>App.Initialize</c> registers in DI, plus a switch that
    /// makes the next <c>CreateDbContext</c> fail the way a locked or unwritable data directory
    /// does — which is the only way to reach the half-finished purge without a second process.
    /// </summary>
    private sealed class FailableContextFactory(string databasePath) : IDbContextFactory<LoggingContext>
    {
        internal bool FailNextCreate { get; set; }

        public LoggingContext CreateDbContext()
        {
            if (FailNextCreate)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            return new LoggingContext(
                new DbContextOptionsBuilder<LoggingContext>()
                    .UseSqlite($"Data source={databasePath}")
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options);
        }
    }

    /// <summary>
    /// The host seam, recording what the list view model asked it to do. Everything the purge routes
    /// through the plot/consumer members is a no-op here; what the tests read back is the session
    /// collection and the dialogs.
    /// </summary>
    private sealed class FakeHost : ILoggingSessionListHost
    {
        internal FakeHost(params LoggingSession[] sessions) => LoggingSessions = new ObservableCollection<LoggingSession>(sessions);

        internal List<string> MessagesShown { get; } = [];

        public ObservableCollection<LoggingSession> LoggingSessions { get; }

        public LoggingSession SelectedLoggingSession { set { } }

        public bool IsLoggedDataBusy { set { } }

        public string LoggedDataBusyReason { set { } }

        public bool IsLoggingActive => false;

        public void NotifyLoggingSessionsChanged() { }

        public void DisplaySessionOnPlot(LoggingSession session) { }

        public void DeleteSessionFromDatabase(LoggingSession session) { }

        public void ClearPlot() { }

        public void SuspendConsumer() { }

        public void ResumeConsumer() { }

        public void ClearBuffer() { }

        public void DiscardPendingBatch() { }

        public Task ShowExportDialogForSessionAsync(int sessionId) => Task.CompletedTask;

        public Task ShowExportDialogForSessionsAsync(IReadOnlyList<LoggingSession> sessions) => Task.CompletedTask;

        // The delete-all confirmation. Answering "yes" is what puts the purge under test.
        public Task<bool> ShowConfirmAsync(string title, string message, string affirmativeLabel, bool isDestructive)
            => Task.FromResult(true);

        public Task ShowMessageAsync(string title, string message)
        {
            MessagesShown.Add($"{title}: {message}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Swallows the diagnostics so a deliberate failure does not write to the real log.</summary>
    private sealed class SilentLogger : IAppLogger
    {
        public void Information(string message) { }

        public void Warning(string message) { }

        public void Warning(Exception ex, string message) { }

        public void Error(string message) { }

        public void Error(Exception ex, string message) { }

        public void AddBreadcrumb(string category, string message, Daqifi.Desktop.Common.Loggers.BreadcrumbLevel level = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel.Info) { }

        public void SetDeviceContext(string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

        public void ClearDeviceContext() { }

        public void Shutdown() { }
    }
}
