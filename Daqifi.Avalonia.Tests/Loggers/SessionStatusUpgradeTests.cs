using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// What happens to a <c>DAQiFiDatabase.db</c> written by the SHIPPED app when a build carrying
/// <c>Sessions.Status</c> opens it.
///
/// <para>Every other test of the status column starts from a database this build migrated from
/// nothing, where the column has existed since the file did. That is the one case a real user
/// never has. Their database was written before the column existed, and the question those tests
/// cannot ask is what their existing sessions read as afterwards: <c>Complete</c>, or — because
/// the default landed wrong, or the migration did not run at all — flagged <c>INCOMPLETE
/// IMPORT</c> on a session that imported perfectly well a year ago. A chip appearing on
/// untouched data would be a visible regression that no test on a fresh database can see.</para>
///
/// <para>So these start where the user starts: the schema is built by the app's own migrations
/// stopped at the last one that shipped, rows are written into it by hand as the old build would
/// have, and only then does the new code get the file — through
/// <see cref="DatabaseMigrator.ApplyMigrations"/> and
/// <see cref="LoggingManager.LoadPersistedLoggingSessions"/>, the two things startup actually
/// calls. This repo has been burned twice by database-state bugs (#181, #196); a schema change
/// deserves the upgrade tested and not just the greenfield.</para>
/// </summary>
public sealed class SessionStatusUpgradeTests : IDisposable
{
    /// <summary>
    /// The last migration that shipped before <c>Sessions.Status</c>. Migrating to exactly this
    /// point builds the old schema out of the app's own migration code rather than a hand-written
    /// <c>CREATE TABLE</c> that could drift from it.
    /// </summary>
    private const string LastShippedMigration = "20260415000000_AddSessionSampleCount";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "status-upgrade-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public SessionStatusUpgradeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// The headline: upgrade a database holding an ordinary finished session and it stays an
    /// ordinary finished session — same samples, same count, and no incomplete-import chip.
    /// </summary>
    [Fact]
    public void A_session_written_before_the_status_column_still_reads_as_complete_after_the_upgrade()
    {
        var factory = Factory();
        MigrateToLastShippedSchema(factory);

        // Proves the fixture is honest: without this, a mistake that left the database fully
        // migrated would make the rest of the test pass for the wrong reason.
        Assert.False(SessionsTableHasStatusColumn());

        InsertLegacySession(id: 1, name: "Old Session", sampleCount: 2);
        InsertSampleRow(loggingSessionId: 1);
        InsertSampleRow(loggingSessionId: 1);

        DatabaseMigrator.ApplyMigrations(factory, DatabasePath);
        var loaded = NewManager(factory).LoadPersistedLoggingSessions();

        var session = Assert.Single(loaded);
        Assert.Equal("Old Session", session.Name);
        Assert.Equal(SessionStatus.Complete, session.Status);

        // The whole point. A row that predates the column must not start wearing the chip.
        Assert.False(session.IsIncompleteImport);
        Assert.Equal(string.Empty, session.IncompleteImportTooltip);
        Assert.DoesNotContain("incomplete", session.AccessibilitySummary, StringComparison.OrdinalIgnoreCase);

        // And the upgrade is not allowed to cost the user data.
        Assert.Equal(2, session.SampleCount);
        Assert.Equal(2, SampleCountFor(1));
    }

    /// <summary>
    /// The migration has to actually run on the shipped database, not merely be correct if it did.
    /// <c>PrepareMigration</c> is what startup asks before showing its migration window, and a
    /// pending migration it failed to report is one that never gets applied.
    /// </summary>
    [Fact]
    public void The_status_migration_is_reported_as_pending_and_then_applied_automatically()
    {
        var factory = Factory();
        MigrateToLastShippedSchema(factory);
        InsertLegacySession(id: 1, name: "Old Session", sampleCount: null);

        Assert.True(DatabaseMigrator.PrepareMigration(factory, DatabasePath));

        DatabaseMigrator.ApplyMigrations(factory, DatabasePath);

        Assert.True(SessionsTableHasStatusColumn());
        Assert.False(DatabaseMigrator.PrepareMigration(factory, DatabasePath));
    }

    /// <summary>
    /// A user who upgrades and then goes back to the shipped build. SQLite cannot drop a column
    /// without rebuilding the table, so the reverse of this migration is the one place a downgrade
    /// could quietly take the sessions with it.
    /// </summary>
    [Fact]
    public void The_status_migration_reverses_without_taking_the_sessions_with_it()
    {
        var factory = Factory();
        DatabaseMigrator.ApplyMigrations(factory, DatabasePath);
        InsertLegacySession(id: 1, name: "Old Session", sampleCount: 1, status: (int)SessionStatus.ImportFailed);
        InsertSampleRow(loggingSessionId: 1);

        MigrateToLastShippedSchema(factory);

        Assert.False(SessionsTableHasStatusColumn());
        Assert.Equal("Old Session", ScalarText("SELECT Name FROM Sessions WHERE ID = 1"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 1"));
    }

    #region Helpers

    private TestContextFactory Factory() => new(DatabasePath);

    private LoggingManager NewManager(IDbContextFactory<LoggingContext> factory) =>
        new(factory, Path.Combine(_directory, "DAQifiProfilesConfiguration.xml"));

    /// <summary>
    /// Runs the app's migrations only as far as <see cref="LastShippedMigration"/>, leaving the
    /// database in the exact state the shipped build leaves it. Also used in reverse, to migrate a
    /// fully-migrated database back down.
    /// </summary>
    private void MigrateToLastShippedSchema(IDbContextFactory<LoggingContext> factory)
    {
        using var context = factory.CreateDbContext();
        context.GetService<IMigrator>().Migrate(LastShippedMigration);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// A <c>Sessions</c> row written the way the old build wrote it — raw SQL naming only the
    /// columns that existed then, because the EF model in this build has a <c>Status</c> the old
    /// schema has no room for.
    /// </summary>
    private void InsertLegacySession(int id, string name, long? sampleCount, int? status = null)
    {
        using var connection = new SqliteConnection($"Data source={DatabasePath}");
        connection.Open();

        using var insert = connection.CreateCommand();
        insert.CommandText = status is null
            ? "INSERT INTO Sessions (ID, SessionStart, Name, SampleCount) VALUES ($id, $start, $name, $count)"
            : "INSERT INTO Sessions (ID, SessionStart, Name, SampleCount, Status) VALUES ($id, $start, $name, $count, $status)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$start", "2026-08-01 10:00:00");
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$count", (object?)sampleCount ?? DBNull.Value);
        if (status is not null)
        {
            insert.Parameters.AddWithValue("$status", status.Value);
        }

        insert.ExecuteNonQuery();
    }

    private void InsertSampleRow(int loggingSessionId)
    {
        using var connection = new SqliteConnection($"Data source={DatabasePath}");
        connection.Open();

        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Samples (LoggingSessionID, ChannelName, DeviceName, DeviceSerialNo, Color, Type, Value, TimestampTicks) " +
            "VALUES ($id, 'AI0', 'Nq1', 'SERIAL-A', '#FFD32F2F', 0, 1.0, 0)";
        insert.Parameters.AddWithValue("$id", loggingSessionId);
        insert.ExecuteNonQuery();
    }

    private bool SessionsTableHasStatusColumn() =>
        ScalarLong("SELECT COUNT(*) FROM pragma_table_info('Sessions') WHERE name = 'Status'") > 0;

    private long SampleCountFor(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples.LongCount(s => s.LoggingSessionID == sessionId);
    }

    private long ScalarLong(string sql) => Convert.ToInt64(Scalar(sql));

    private string ScalarText(string sql) => Convert.ToString(Scalar(sql))!;

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// The same SQLite context factory <c>App.Initialize</c> registers in DI, without the
    /// container — as in <see cref="SdCardImportAtomicityTests"/>.
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
