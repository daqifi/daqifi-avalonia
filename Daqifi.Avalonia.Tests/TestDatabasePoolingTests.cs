using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// The guard on <see cref="TestDatabase"/>'s one job.
///
/// <para>Pooling is what exposes a test to the process-global
/// <see cref="SqliteConnection.ClearAllPools"/> that the app's own <c>DatabaseMigrator</c> calls
/// on almost every path these tests drive — see the note on <see cref="TestDatabase"/> for the
/// window it opens and the <c>ObjectDisposedException</c> it produces in an unrelated test.</para>
///
/// <para>These two assertions are deterministic, which is the point. The failure they prevent is
/// not: the reporter saw it once in 32 full-suite runs, and it has since been chased for 348 more
/// runs on that machine without appearing once (issue #210 records the counts). So RUNNING it is
/// not a check on this — the suite is equally green pooled or unpooled, and would stay green for
/// months with the race wide open. Asserting on the connection string is.</para>
/// </summary>
public sealed class TestDatabasePoolingTests
{
    private const string AnyPath = "/tmp/daqifi-avalonia-tests/does-not-need-to-exist.db";

    /// <summary>The raw-ADO path: fixtures that write rows the EF model has no room for.</summary>
    [Fact]
    public void The_raw_connection_string_disables_pooling()
    {
        var connectionString = new SqliteConnectionStringBuilder(TestDatabase.ConnectionString(AnyPath));

        Assert.False(connectionString.Pooling);
        Assert.Equal(AnyPath, connectionString.DataSource);
    }

    /// <summary>
    /// The EF path, which is the bulk of them — asserted on the context the factory actually
    /// hands out rather than on the options, so wrapping or rebuilding the options cannot lose it.
    /// </summary>
    [Fact]
    public void The_context_factory_disables_pooling()
    {
        using var context = TestDatabase.Contexts(AnyPath).CreateDbContext();

        var connectionString = new SqliteConnectionStringBuilder(context.Database.GetConnectionString());

        Assert.False(connectionString.Pooling);
        Assert.Equal(AnyPath, connectionString.DataSource);
    }
}
