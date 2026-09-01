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
/// the next launch found the same file and did the same thing. These tests drive that entry point
/// with damaged files.</para>
///
/// <para>The other half of the contract matters just as much: a quarantine MOVES the user's logged
/// data, so it must fire on damaged content and on nothing else. The healthy and zero-byte cases
/// below are the guard on that, and the code discriminates on SQLite's primary result code
/// (<c>SQLITE_CORRUPT</c>/<c>SQLITE_NOTADB</c> only), never on the mere fact that something threw.
/// The environmental codes — busy, locked, read-only, permission denied — cannot be provoked
/// portably from a unit test and are excluded by that filter rather than by a case here.</para>
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
    /// a full disk, or a sync-tool conflict artefact looks like. SQLite reports SQLITE_NOTADB (26).
    /// </summary>
    [Fact]
    public void PrepareMigration_OnAFileThatIsNotADatabase_DoesNotThrow()
    {
        var original = "<!DOCTYPE html><html><body>not your database</body></html>";
        File.WriteAllText(DatabasePath, original);

        // Before the fix this threw SqliteException 26 'file is not a database' out of
        // SeedMigrationHistoryIfNeeded's first sqlite_master query, and the app died on launch.
        var hasPending = DatabaseMigrator.PrepareMigration(Factory(), DatabasePath);

        // The migrations all read as pending because the file in play is now a brand new one:
        // PrepareMigration's own pending-migration probe re-creates DAQiFiDatabase.db (SQLite's
        // default open mode creates), which is exactly the fresh start this is meant to produce.
        Assert.True(hasPending);
        Assert.Equal(original, File.ReadAllText(Assert.Single(QuarantinedFiles())));
    }

    /// <summary>
    /// A database with a valid header whose schema b-tree has been clobbered — the shape real
    /// corruption usually takes, and a different SQLite result code (SQLITE_CORRUPT, 11) from the
    /// case above, so both branches of the filter are covered.
    /// </summary>
    [Fact]
    public void PrepareMigration_OnADatabaseWithACorruptSchemaPage_DoesNotThrow()
    {
        WriteRealDatabase(DatabasePath);
        using (var file = new FileStream(DatabasePath, FileMode.Open, FileAccess.Write))
        {
            file.Seek(100, SeekOrigin.Begin);   // past the 100-byte header, into page 1's b-tree
            file.Write(new byte[512]);
        }

        var hasPending = DatabaseMigrator.PrepareMigration(Factory(), DatabasePath);

        Assert.True(hasPending);
        Assert.Single(QuarantinedFiles());
    }

    /// <summary>
    /// The whole point of moving the file rather than deleting it: a corrupt SQLite file is often
    /// still partly recoverable, and it is the user's data either way.
    /// </summary>
    [Fact]
    public void QuarantineUnusableDatabase_PreservesTheDamagedFileByteForByte()
    {
        var original = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x2A };
        File.WriteAllBytes(DatabasePath, original);

        var quarantinePath = DatabaseMigrator.QuarantineUnusableDatabase(DatabasePath);

        Assert.NotNull(quarantinePath);
        Assert.Equal(original, File.ReadAllBytes(quarantinePath));
        Assert.Equal(quarantinePath, DatabaseMigrator.QuarantinedDatabasePath);
    }

    /// <summary>
    /// The invariant that protects the replacement database: nothing that belonged to the damaged
    /// file is left beside the live path. A stale <c>-wal</c> is not inert — SQLite replays a
    /// journal whose header still checksums, writing the old file's pages into the new one.
    ///
    /// <para>Asserted as "gone from the live path", not "present at the quarantine path", because
    /// on Microsoft.Data.Sqlite 10.0.10 the sidecars are usually removed by SQLite itself when the
    /// readability probe closes its connection; the migrator's own relocation is the backstop for
    /// when they are not.</para>
    /// </summary>
    [Fact]
    public void QuarantineUnusableDatabase_LeavesNoSidecarBesideTheReplacementDatabase()
    {
        File.WriteAllText(DatabasePath, "not a database");
        File.WriteAllText(DatabasePath + "-wal", "stale journal");
        File.WriteAllText(DatabasePath + "-shm", "stale shared memory");

        Assert.NotNull(DatabaseMigrator.QuarantineUnusableDatabase(DatabasePath));

        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
    }

    /// <summary>
    /// The guard that matters most: a quarantine moves the user's logged sessions out of the way,
    /// so a readable database must never trigger one.
    /// </summary>
    [Fact]
    public void QuarantineUnusableDatabase_LeavesAHealthyDatabaseAlone()
    {
        WriteRealDatabase(DatabasePath);
        var before = File.ReadAllBytes(DatabasePath);

        Assert.Null(DatabaseMigrator.QuarantineUnusableDatabase(DatabasePath));

        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>
    /// A zero-byte file is a VALID empty SQLite database, not a corrupt one — it is what an
    /// interrupted first run leaves behind, and the migrations simply populate it. Quarantining it
    /// would be wrong, and would litter a fresh install with .corrupt- files.
    /// </summary>
    [Fact]
    public void QuarantineUnusableDatabase_LeavesAZeroByteFileAlone()
    {
        File.WriteAllBytes(DatabasePath, Array.Empty<byte>());

        Assert.Null(DatabaseMigrator.QuarantineUnusableDatabase(DatabasePath));

        Assert.True(File.Exists(DatabasePath));
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>Nothing to diagnose and nothing to move on a first run.</summary>
    [Fact]
    public void QuarantineUnusableDatabase_DoesNothingWhenThereIsNoDatabaseYet()
    {
        Assert.Null(DatabaseMigrator.QuarantineUnusableDatabase(DatabasePath));
        Assert.Empty(QuarantinedFiles());
    }

    /// <summary>
    /// The end state the user actually gets: startup completes, and the app is left with a working,
    /// fully migrated database rather than a poisoned one it will fail on again tomorrow.
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

    /// <summary>Files the quarantine left behind in the test's own directory.</summary>
    private string[] QuarantinedFiles() =>
        Directory.GetFiles(_directory, "*.corrupt-*");

    /// <summary>A genuine, readable SQLite database with one table and one row in it.</summary>
    private static void WriteRealDatabase(string databasePath)
    {
        using (var connection = new SqliteConnection($"Data source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Samples (Id INTEGER PRIMARY KEY, Value REAL); "
                                  + "INSERT INTO Samples (Value) VALUES (1.5);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
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
