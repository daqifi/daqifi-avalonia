using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// The <c>DropChannelTable</c> migration, applied to real SQLite files rather than asserted about.
///
/// <para>Deleting the vestigial <c>Channel</c> entity (#206) is behaviour-preserving in the C#;
/// the schema half is the part with consequences for databases users already have, and those
/// databases are not all the same shape. The app's own <c>DatabaseMigrator</c> baselines legacy
/// files created by <c>EnsureCreated()</c> at the initial migration and then runs the rest, so a
/// single install can present the table, or not present it, at the moment this migration runs.
/// A <c>DropTable</c> that assumed the table was there would turn the second case into a startup
/// failure with no logged data reachable at all — which is why the migration uses guarded raw SQL,
/// and why the guard is pinned here rather than trusted.</para>
///
/// <para>These tests drive EF's own <see cref="IMigrator"/> over the real migration chain, so they
/// also cover something a hand-written migration can get wrong invisibly: every migration EF
/// applies has its target model built, including the designer snapshots of migrations older than
/// this one, which still name the now-deleted <c>Daqifi.Desktop.Channel.Channel</c> type. If those
/// snapshots could no longer be built, a fresh install would fail on first launch and no
/// compile-time check would have said so.</para>
/// </summary>
public class DropChannelTableMigrationTests : IDisposable
{
    /// <summary>The migration immediately before the one under test — the last state that has the table.</summary>
    private const string BeforeTheDrop = "20260902160000_AddSessionStatus";

    private const string TheDrop = "20260902230000_DropChannelTable";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "dropchannel-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public DropChannelTableMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A brand new install: the whole chain runs in order, the initial migration creates the
    /// table and this one takes it away again, and nothing is left pending afterwards.
    /// </summary>
    [Fact]
    public void OnAFreshDatabase_TheChainAppliesAndLeavesNoChannelTable()
    {
        using var context = CreateContext();
        context.Database.Migrate();

        Assert.False(TableExists("Channel"));
        Assert.True(TableExists("Sessions"));
        Assert.True(TableExists("Samples"));
        Assert.Empty(context.Database.GetPendingMigrations());
        Assert.Contains(TheDrop, context.Database.GetAppliedMigrations());
    }

    /// <summary>
    /// The ordinary upgrade: a database that has the table, with rows in it — what an install
    /// carried forward from an older app version looks like. The table goes; the logged data
    /// beside it does not.
    /// </summary>
    [Fact]
    public void OnADatabaseThatHasTheTable_TheTableIsDroppedAndLoggedDataSurvives()
    {
        MigrateTo(BeforeTheDrop);
        Assert.True(TableExists("Channel"));
        SeedASessionASampleAndAChannelRow();
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM \"Channel\""));

        using (var context = CreateContext())
        {
            context.Database.Migrate();
        }

        Assert.False(TableExists("Channel"));

        using var reopened = CreateContext();
        var session = Assert.Single(reopened.Sessions.AsNoTracking().ToList());
        Assert.Equal("Legacy session", session.Name);
        Assert.Equal(1, reopened.Samples.AsNoTracking().Count());
    }

    /// <summary>
    /// The case the <c>IF EXISTS</c> guard exists for: a database sitting at the pre-drop
    /// migration whose <c>Channel</c> table is already gone — hand-modified, restored from a
    /// partial dump, or baselined out of <c>EnsureCreated()</c> against a model that no longer
    /// declared it. An unguarded <c>DROP TABLE</c> throws here, and the throw travels out of
    /// startup.
    /// </summary>
    [Fact]
    public void OnADatabaseThatNeverHadTheTable_TheMigrationIsANoOpRatherThanAFailure()
    {
        MigrateTo(BeforeTheDrop);
        SeedASessionASampleAndAChannelRow();
        Execute("DROP TABLE \"Channel\"");
        Assert.False(TableExists("Channel"));

        using var context = CreateContext();
        context.Database.Migrate();

        Assert.False(TableExists("Channel"));
        Assert.Empty(context.Database.GetPendingMigrations());
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM \"Sessions\""));
    }

    /// <summary>
    /// Rolling back restores the table's shape — an older build finds the schema it expects — and
    /// deliberately does not pretend to restore rows, because a dropped table's contents are gone.
    /// The assertion on the empty table is the honest half of that claim, not an oversight.
    /// </summary>
    [Fact]
    public void RollingBack_RestoresTheTableShapeButNotItsRows()
    {
        MigrateTo(BeforeTheDrop);
        SeedASessionASampleAndAChannelRow();
        MigrateTo(TheDrop);
        Assert.False(TableExists("Channel"));

        MigrateTo(BeforeTheDrop);

        Assert.True(TableExists("Channel"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM \"Channel\""));
        Assert.Equal(
            new[]
            {
                "ActiveSampleID", "DeviceName", "DeviceSerialNo", "Direction", "HasAdc", "HasValidExpression", "ID",
                "Index", "IsActive", "IsAnalog", "IsBidirectional", "IsDigital", "IsDigitalOn", "IsOutput",
                "IsScalingActive", "IsVisible", "LoggingSessionID", "Name", "OutputValue", "ScaleExpression", "Type",
                "TypeString"
            },
            ChannelColumnNames());
        Assert.Equal(
            new[] { "IX_Channel_ActiveSampleID", "IX_Channel_LoggingSessionID" },
            IndexNamesOn("Channel"));
    }

    /// <summary>
    /// Applying the drop twice — what a retry after an interrupted migration amounts to — is
    /// harmless. EF wraps each migration in a transaction and only records it on success, so the
    /// statement has to be safe to run against a database it has already run against.
    /// </summary>
    [Fact]
    public void ReapplyingTheDrop_IsIdempotent()
    {
        MigrateTo(TheDrop);
        MigrateTo(BeforeTheDrop);
        MigrateTo(TheDrop);
        MigrateTo(BeforeTheDrop);
        MigrateTo(TheDrop);

        Assert.False(TableExists("Channel"));

        using var context = CreateContext();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    #region Fixtures

    /// <summary>
    /// The same SQLite context factory <c>App.Initialize</c> registers in DI, without the
    /// container. The pending-model-changes warning is suppressed for the reason it is suppressed
    /// everywhere else in this repo: the model snapshot carries a pre-existing drift in the
    /// <c>Samples</c> index name that this change deliberately does not touch.
    /// </summary>
    private LoggingContext CreateContext() => new(
        new DbContextOptionsBuilder<LoggingContext>()
            .UseSqlite($"Data source={DatabasePath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    /// <summary>Migrates to a named migration, forwards or backwards, through EF's own migrator.</summary>
    private void MigrateTo(string migrationId)
    {
        using var context = CreateContext();
        context.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(migrationId);
    }

    /// <summary>
    /// One session, one sample, and one <c>Channel</c> row referencing that sample — written with
    /// raw SQL because the entity type this exercises no longer exists in C#, which is the point.
    /// </summary>
    private void SeedASessionASampleAndAChannelRow()
    {
        Execute(
            "INSERT INTO \"Sessions\" (\"ID\", \"SessionStart\", \"Name\", \"SampleCount\", \"Status\") " +
            "VALUES (1, '2026-01-01 00:00:00', 'Legacy session', 1, 0)");
        Execute(
            "INSERT INTO \"Samples\" (\"ID\", \"LoggingSessionID\", \"Value\", \"TimestampTicks\", \"DeviceName\", " +
            "\"ChannelName\", \"DeviceSerialNo\", \"Color\", \"Type\") " +
            "VALUES (1, 1, 1.5, 637000000000000000, 'Nq1', 'AI0', 'SERIAL', '#FF0000', 0)");
        Execute(
            "INSERT INTO \"Channel\" (\"ID\", \"Name\", \"Index\", \"OutputValue\", \"Type\", \"Direction\", " +
            "\"TypeString\", \"ScaleExpression\", \"IsBidirectional\", \"IsOutput\", \"HasAdc\", \"IsActive\", " +
            "\"IsDigital\", \"IsAnalog\", \"IsDigitalOn\", \"IsScalingActive\", \"HasValidExpression\", " +
            "\"ActiveSampleID\", \"IsVisible\", \"DeviceName\", \"DeviceSerialNo\", \"LoggingSessionID\") " +
            "VALUES (1, 'AI0', 0, 0.0, 0, 0, 'Analog', '', 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 1, 'Nq1', 'SERIAL', 1)");
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data source={DatabasePath}");
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    private bool TableExists(string table)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is long count && count > 0;
    }

    private List<string> ChannelColumnNames() => Query("SELECT name FROM pragma_table_info('Channel') ORDER BY name");

    private List<string> IndexNamesOn(string table) =>
        Query($"SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='{table}' AND name NOT LIKE 'sqlite_%' ORDER BY name");

    private List<string> Query(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<string>();
        while (reader.Read()) { values.Add(reader.GetString(0)); }
        return values;
    }

    #endregion
}
