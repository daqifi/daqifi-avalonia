using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Assembly-wide policy: every SQLite connection this suite opens FOR ITSELF is UNPOOLED, and this
/// is the only place that decides so.
///
/// <para>xUnit runs distinct test classes in parallel in one process, and
/// <see cref="SqliteConnection.ClearAllPools"/> is process-global — it is not scoped to a
/// connection string, a pool, or the calling class. The suite reaches it constantly: the app's
/// own <see cref="DatabaseMigrator"/> calls it five times on the paths these tests drive, so it
/// fires on essentially every SQLite-backed test whether or not a test asks for it.</para>
///
/// <para>That is only survivable because nothing here is pooled. The race it would otherwise
/// leave open lives inside Microsoft.Data.Sqlite: <c>SqliteConnectionInternal.Activate</c> sets
/// <c>_active = true</c> and only THEN points its <see cref="WeakReference{T}"/> at the opening
/// connection, while <c>Leaked</c> is <c>_active &amp;&amp; the weak reference is empty</c>.
/// Between those two writes a connection being handed to an opening caller reads as leaked, and a
/// concurrent <c>ClearAllPools()</c> — which marks every connection <c>DoNotPool()</c> and then
/// reclaims the leaked ones — disposes its <c>sqlite3</c> handle out from under the caller. The
/// caller's next statement throws <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c> in a test
/// that has nothing to do with whoever cleared the pools (issue #210).</para>
///
/// <para>An unpooled connection is never entered into a pool's bookkeeping at all
/// (<c>SqliteConnectionPoolGroup.IsNonPooled</c> hands out a <c>SqliteConnectionInternal</c> with
/// no pool), so <c>Clear()</c> cannot see it and the window does not exist. It also removes the
/// reason the fixtures used to clear pools themselves: an unpooled connection releases its file
/// handle when it is disposed, so a temp directory can be deleted without dropping a pool first.
/// Pooling buys a test nothing — every database here is a throwaway file used by one test.</para>
///
/// <para>The one exception is deliberate and already contained. <c>DeviceRefusalCrashTests</c> does
/// not build a connection string at all — it stands the real app host up with
/// <c>App.InitializeMobile()</c> and uses the <c>IDbContextFactory</c> that registers, which is
/// production's own pooled <c>Data source=</c> (<c>App.cs</c>). Those connections stay pooled
/// because they are the shipping app's, so that class remains exposed to the window described
/// above; <c>AppHostCollection</c>'s <c>DisableParallelization</c> is what keeps it out of the way,
/// and it is scoped to exactly that one class. Do not "simplify" it away — <c>App.ServiceProvider</c>
/// is static, so an app host cannot be made per-test.</para>
///
/// <para>Pinned by <c>TestDatabasePoolingTests</c>, which is what fails if a future fixture
/// grows its own connection string instead of coming through here.</para>
/// </summary>
internal static class TestDatabase
{
    /// <summary>The connection string for a throwaway test database at <paramref name="databasePath"/>.</summary>
    internal static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();

    /// <summary>
    /// The same SQLite context factory <c>App.Initialize</c> registers in DI, without the
    /// container — and without pooling, per the note above.
    /// </summary>
    internal static IDbContextFactory<LoggingContext> Contexts(string databasePath) =>
        new TestContextFactory(databasePath);

    /// <summary>
    /// The options behind <see cref="Contexts"/>, for the one fixture that needs to wrap the
    /// factory rather than use it (<c>DeleteAllSessionsRecoveryTests.FailableContextFactory</c>).
    /// </summary>
    internal static DbContextOptions<LoggingContext> Options(string databasePath) =>
        new DbContextOptionsBuilder<LoggingContext>()
            .UseSqlite(ConnectionString(databasePath))
            // A test that migrates a database deliberately stops part way through the migration
            // list, which EF reports as a model that has moved on since the last migration.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    private sealed class TestContextFactory(string databasePath) : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() => new(Options(databasePath));
    }
}
