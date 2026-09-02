using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Startup's handling of a <c>DAQiFiDatabase.db</c> that cannot be read.
///
/// <para><c>DatabaseMigrator.PrepareMigration</c> is the first thing on the desktop launch path to
/// touch the database file, and every read it makes assumed the file was a SQLite database. When it
/// was not, the <see cref="SqliteException"/> travelled straight out of <c>App.Initialize</c> and
/// <c>OnFrameworkInitializationCompleted</c> and killed the process before a window existed — and
/// the next launch found the same file and did the same thing. These tests drive the real entry
/// points with damaged files.</para>
///
/// <para>The other half of the contract matters just as much: a quarantine MOVES the user's logged
/// data, so it must fire on damaged content and on nothing else. The healthy and zero-byte cases
/// below are the guard on that. The code discriminates on SQLite's primary result code
/// (<c>SQLITE_CORRUPT</c>/<c>SQLITE_NOTADB</c> only), never on the mere fact that something threw;
/// the environmental codes — busy, locked, read-only, permission denied — are excluded by that
/// filter rather than by a case here, because none can be provoked portably from a unit test.</para>
/// </summary>
public class DatabaseMigratorUnusableDatabaseTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "migrator-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public DatabaseMigratorUnusableDatabaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A file whose header is not SQLite's at all — what a truncated copy, a half-written file from
    /// a full disk, or a sync-tool conflict artefact looks like. SQLite reports SQLITE_NOTADB (26)
    /// from the migrator's very first <c>sqlite_master</c> query.
    /// </summary>
    [Fact]
    public void PrepareMigration_OnAFileThatIsNotADatabase_DoesNotThrow()
    {
        const string original = "<!DOCTYPE html><html><body>not your database</body></html>";
        File.WriteAllText(DatabasePath, original);

        var hasPending = DatabaseMigrator.PrepareMigration(Factory(), DatabasePath);

        // Every migration reads as pending because the file in play is now a brand new one:
        // the pending-migration probe re-creates DAQiFiDatabase.db (SQLite's default open mode
        // creates), which is exactly the fresh start this is meant to produce.
        Assert.True(hasPending);
        Assert.Equal(original, File.ReadAllText(Assert.Single(QuarantinedFiles())));
    }

    /// <summary>
    /// The case a cheap pre-flight probe cannot see, and the reason the recovery hangs off the read
    /// that actually fails rather than off a probe: a database whose header and schema catalog (page
    /// 1) are intact but whose remaining pages are overwritten. <c>SELECT COUNT(*) FROM
    /// sqlite_master</c> answers happily; the very next table read — here EF's own
    /// <c>__EFMigrationsHistory</c> lookup inside the pending-migration check — throws
    /// SQLITE_CORRUPT (11).
    /// </summary>
    [Fact]
    public void PrepareMigration_OnADatabaseCorruptPastItsHeader_DoesNotThrow()
    {
        var factory = Factory();
        WriteMigratedDatabase(factory, DatabasePath);
        CorruptEveryPageAfterTheFirst(DatabasePath);

        var hasPending = DatabaseMigrator.PrepareMigration(factory, DatabasePath);

        Assert.True(hasPending);
        Assert.Single(QuarantinedFiles());
    }

    /// <summary>
    /// The whole point of moving the file rather than deleting it: a corrupt SQLite file is often
    /// still partly recoverable, and it is the user's data either way.
    /// </summary>
    [Fact]
    public void PrepareMigration_PreservesTheDamagedFileByteForByte()
    {
        var original = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x2A };
        File.WriteAllBytes(DatabasePath, original);

        DatabaseMigrator.PrepareMigration(Factory(), DatabasePath);

        var quarantinePath = Assert.Single(QuarantinedFiles());
        Assert.Equal(original, File.ReadAllBytes(quarantinePath));
        Assert.Equal(quarantinePath, DatabaseMigrator.QuarantinedDatabasePath);
    }

    /// <summary>
    /// The invariant that protects the replacement database: nothing belonging to the damaged file
    /// is left beside the live path. A stale journal is not inert — SQLite rolls back a hot
    /// <c>-journal</c> and replays a <c>-wal</c> whose header still checksums, either way writing
    /// the old file's pages into the new one. <c>-journal</c> is the one this app can actually
    /// produce: nothing here sets <c>journal_mode</c>, so SQLite's default DELETE mode applies.
    /// </summary>
    [Fact]
    public void PrepareMigration_LeavesNoJournalBesideTheReplacementDatabase()
    {
        File.WriteAllText(DatabasePath, "not a database");
        File.WriteAllText(DatabasePath + "-journal", "stale rollback journal");
        File.WriteAllText(DatabasePath + "-wal", "stale write-ahead log");
        File.WriteAllText(DatabasePath + "-shm", "stale shared memory");

        DatabaseMigrator.PrepareMigration(Factory(), DatabasePath);

        Assert.False(File.Exists(DatabasePath + "-journal"));
        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
    }

    /// <summary>
    /// The guard that matters most: a quarantine moves the user's logged sessions out of the way, so
    /// a readable database must never trigger one — and its rows must still be there afterwards.
    /// </summary>
    [Fact]
    public void PrepareMigration_LeavesAHealthyDatabaseAlone()
    {
        var factory = Factory();
        WriteMigratedDatabase(factory, DatabasePath);

        Assert.False(DatabaseMigrator.PrepareMigration(factory, DatabasePath));

        Assert.Empty(QuarantinedFiles());
        using var context = factory.CreateDbContext();
        Assert.Equal("Session_0", Assert.Single(context.Sessions.ToList()).Name);
    }

    /// <summary>
    /// A zero-byte file is a VALID empty SQLite database, not a corrupt one — it is what an
    /// interrupted first run leaves behind, and the migrations simply populate it. Quarantining it
    /// would be wrong, and would litter a fresh install with .corrupt- files.
    /// </summary>
    [Fact]
    public void PrepareMigration_LeavesAZeroByteFileAlone()
    {
        File.WriteAllBytes(DatabasePath, Array.Empty<byte>());

        Assert.True(DatabaseMigrator.PrepareMigration(Factory(), DatabasePath));

        Assert.True(File.Exists(DatabasePath));
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>Nothing to diagnose and nothing to move on a first run.</summary>
    [Fact]
    public void PrepareMigration_QuarantinesNothingWhenThereIsNoDatabaseYet()
    {
        Assert.True(DatabaseMigrator.PrepareMigration(Factory(), DatabasePath));
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>
    /// The end state the user actually gets on the desktop path: startup completes, and the app is
    /// left with a working, fully migrated database rather than a poisoned one it will fail on again
    /// tomorrow.
    /// </summary>
    [Fact]
    public void AfterAQuarantine_MigrationRebuildsAUsableDatabase()
    {
        File.WriteAllText(DatabasePath, "not a database");

        var factory = Factory();
        Assert.True(DatabaseMigrator.PrepareMigration(factory, DatabasePath));
        DatabaseMigrator.ApplyMigrations(factory, DatabasePath);

        using var context = factory.CreateDbContext();
        Assert.Empty(context.Database.GetPendingMigrations());
        Assert.Empty(context.Sessions.ToList());
    }

    /// <summary>
    /// What the user is actually shown. The message has to name the file it preserved — an empty
    /// Logged Data pane otherwise reads as deleted data — and it must only claim a working
    /// replacement once one has been migrated, because moving the damaged file aside and rebuilding
    /// are separate outcomes.
    /// </summary>
    [Fact]
    public void AfterAQuarantine_TheUserIsToldWhereTheOldDatabaseWentAndThatTheNewOneWorks()
    {
        File.WriteAllText(DatabasePath, "not a database");

        var factory = Factory();
        Assert.True(DatabaseMigrator.PrepareMigration(factory, DatabasePath));
        DatabaseMigrator.ApplyMigrations(factory, DatabasePath);

        var message = DatabaseMigrator.DescribeQuarantineForUser();
        Assert.NotNull(message);
        Assert.Contains(Assert.Single(QuarantinedFiles()), message);
        Assert.Contains("is in use", message);
    }

    /// <summary>
    /// The mobile heads never call PrepareMigration — there is no MigrationStatusWindow to drive —
    /// so they get the same recovery through their own entry point. Before it, a damaged database
    /// did not crash the phone app; it booted with every DB-backed pane broken, on that launch and
    /// every later one, because nothing moved the bad file aside.
    /// </summary>
    [Fact]
    public void MigrateWithCorruptionRecovery_RebuildsFromADamagedDatabase()
    {
        File.WriteAllText(DatabasePath, "not a database");

        var factory = Factory();
        DatabaseMigrator.MigrateWithCorruptionRecovery(factory, DatabasePath);

        Assert.Single(QuarantinedFiles());
        using var context = factory.CreateDbContext();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    /// <summary>Files a quarantine left behind in the test's own directory.</summary>
    private string[] QuarantinedFiles() => Directory.GetFiles(_directory, "*.corrupt-*");

    /// <summary>
    /// A genuine, fully migrated DAQiFi database — the app's own schema, through the app's own
    /// migrator — with one session row in it, so a test can tell "left alone" from "rebuilt".
    /// </summary>
    private static void WriteMigratedDatabase(IDbContextFactory<LoggingContext> factory, string databasePath)
    {
        DatabaseMigrator.ApplyMigrations(factory, databasePath);

        using (var context = factory.CreateDbContext())
        {
            context.Sessions.Add(new LoggingSession(0, "Session_0"));
            context.SaveChanges();
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Overwrites everything after page 1, leaving the file header and the schema catalog readable.
    /// SQLite then answers schema questions normally and fails SQLITE_CORRUPT on the first read of
    /// any actual table.
    /// </summary>
    private static void CorruptEveryPageAfterTheFirst(string databasePath)
    {
        SqliteConnection.ClearAllPools();

        using var file = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite);

        // Bytes 16-17 of the header are the page size, big-endian, with 1 meaning 65536.
        var header = new byte[18];
        file.ReadExactly(header);
        var pageSize = (header[16] << 8) | header[17];
        if (pageSize == 1) { pageSize = 65536; }

        Assert.True(file.Length > pageSize, "the fixture database is a single page, so there is nothing past it to damage");

        var junk = new byte[file.Length - pageSize];
        Array.Fill(junk, (byte)0xA5);
        file.Seek(pageSize, SeekOrigin.Begin);
        file.Write(junk);
    }

    private TestContextFactory Factory() => new(DatabasePath);

    /// <summary>
    /// The same SQLite context factory <c>App.Initialize</c> registers in DI, without the container.
    /// </summary>
    private sealed class TestContextFactory(string databasePath) : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() => new(
            new DbContextOptionsBuilder<LoggingContext>()
                .UseSqlite($"Data source={databasePath}")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);
    }
}
