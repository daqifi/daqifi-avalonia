using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Renaming a logging session — the write behind the editable name box on the Logged Data pane.
///
/// <para>Until now this write lived in <c>LoggedDataPanePrototype.axaml.cs</c>: a markup
/// code-behind that resolved <c>IDbContextFactory&lt;LoggingContext&gt;</c> off
/// <c>App.ServiceProvider</c>, opened a <c>DbContext</c>, found the row and saved it. It was the
/// only <c>.axaml.cs</c> in the repo that referenced EF Core or the service locator at all, and
/// nothing in this project can construct a view, so the rename was the one session write with no
/// reachable test. Moving it to <see cref="LoggingManager"/> — which already owns the sessions this
/// pane lists and the context factory they live in — is what makes these rows possible.</para>
///
/// <para>These tests are characterization, not regression: the behaviour is unchanged by the move,
/// so none of them can fail against <c>origin/main</c> — they name a member that does not exist
/// there, which is "does not compile", not "0 failed". What shows they bite is the mutation run
/// recorded on the PR, where each row is killed by exactly the mutation its name claims.</para>
/// </summary>
public sealed class SessionRenamePersistenceTests : IDisposable
{
    private const int SessionId = 1;

    /// <summary>The name the row is seeded with, so every assertion below moves it off a value it
    /// already held — an assertion that only ever sees the default proves nothing.</summary>
    private const string SeededName = "Session";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "session-rename-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    /// <summary>A throwaway profiles file, so nothing here can reach the developer's real one.</summary>
    private string ProfilePath => Path.Combine(_directory, "DAQifiProfilesConfiguration.xml");

    public SessionRenamePersistenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>The headline: what the user typed is in the database afterwards.</summary>
    [Fact]
    public async Task A_typed_name_reaches_the_sessions_row()
    {
        SeedSession();

        await Manager().PersistSessionNameAsync(SessionId, "Bench run 3");

        Assert.Equal("Bench run 3", StoredName());
    }

    /// <summary>
    /// The blank case, which is not merely cosmetic: the pane sends <c>null</c> rather than an empty
    /// string so that <c>LoggingSession.Name</c>'s getter renders "Session {ID}" again on reload.
    /// Storing <c>""</c> instead would leave the row displaying an empty label forever.
    /// </summary>
    [Fact]
    public async Task Clearing_the_box_stores_null_so_the_row_goes_back_to_its_default_label()
    {
        SeedSession();

        await Manager().PersistSessionNameAsync(SessionId, null);

        Assert.Null(StoredName());
    }

    /// <summary>
    /// A debounced write can land after its session was deleted — the pane's 250 ms delay is long
    /// enough for a Delete to complete in between. That has to be a no-op rather than a throw, or
    /// the race surfaces to the user as a failed rename on a session that is already gone.
    /// </summary>
    [Fact]
    public async Task Renaming_a_session_that_is_no_longer_there_changes_nothing_and_does_not_throw()
    {
        SeedSession();

        await Manager().PersistSessionNameAsync(SessionId + 99, "Ghost");

        Assert.Equal(SeededName, StoredName());
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM Sessions"));
    }

    /// <summary>
    /// The debounce contract seen from this side: the caller cancels the previous write on every
    /// keystroke, and a cancelled write must not reach the row. Without this, a fast typist's
    /// superseded keystrokes could still be racing toward the database behind the final one.
    /// </summary>
    [Fact]
    public async Task A_cancelled_write_leaves_the_row_alone()
    {
        SeedSession();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Manager().PersistSessionNameAsync(SessionId, "Superseded", cancelled.Token));

        Assert.Equal(SeededName, StoredName());
    }

    #region Helpers

    private LoggingManager Manager() => new(TestDatabase.Contexts(DatabasePath), ProfilePath);

    private void SeedSession()
    {
        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(DatabasePath), DatabasePath);

        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO Sessions (ID, SessionStart, Name) VALUES ($id, $start, $name)";
        insert.Parameters.AddWithValue("$id", SessionId);
        insert.Parameters.AddWithValue("$start", "2026-01-01 00:00:00");
        insert.Parameters.AddWithValue("$name", SeededName);
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Read straight out of SQLite rather than back through EF, so a change that only ever existed
    /// in a change-tracker cannot pass this.
    /// </summary>
    private string? StoredName()
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT Name FROM Sessions WHERE ID = $id";
        query.Parameters.AddWithValue("$id", SessionId);
        return query.ExecuteScalar() as string;
    }

    private long ScalarLong(string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        return (long)query.ExecuteScalar()!;
    }

    #endregion
}
