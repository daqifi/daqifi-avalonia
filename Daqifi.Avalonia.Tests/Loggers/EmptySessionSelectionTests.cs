using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #262: opening a logging session that holds no plottable samples left the Logged Data pane
/// saying <c>No session selected</c>, directly under a session list where that very session was
/// highlighted.
///
/// <para>The cause is one boolean asked to carry two meanings. <see cref="DatabaseLogger"/> sets
/// <c>HasSessionData = false</c> both when nothing is open and when an empty session is open, and the
/// view had nothing else to read, so it rendered the "nothing is open" card for both.
/// <c>CurrentSession</c> already separated them — null in the first case, the opened session in the
/// second — and the fix is a named <c>IsSessionOpen</c> the view can bind instead of inferring
/// selection from <c>HasSessionData</c>.</para>
///
/// <para>What this file pins is the SESSION-SIDE half: that a session with nothing to draw is a real,
/// reachable state, and by which routes. Two things put the other half out of reach here, and both are
/// worth writing down because each looks surmountable until it is tried:</para>
/// <list type="bullet">
/// <item>The gate itself is XAML. Views in this repo carry no <c>x:DataType</c>, so <c>IsVisible</c>
/// bindings resolve by reflection at run time and no test in this project can see them — the same
/// limitation <c>EmptyLoggedPlotFrameTests</c> records for #251. The before/after evidence on the PR
/// is a render, for exactly that reason.</item>
/// <item>The view-model state behind it is out of reach too, and NOT merely by the csproj's
/// library-code-only policy. <c>DisplayLoggingSession</c> and <c>ClearPlot</c> reach shared state
/// through <c>Dispatcher.UIThread.Invoke</c>. Outside a running Avalonia app that dispatcher binds to
/// whichever thread touches it FIRST and is never pumped, so the same three tests that pass in
/// isolation deadlock the whole run when another class got there first: measured here, the suite went
/// from 11 s to a testhost blocked indefinitely at 0.6% CPU, reproducible by pairing this class with
/// anything under <c>Tests/Device</c>. There is no ordering a test can assert, so the tests were
/// removed rather than left as a suite-wide hang waiting for a scheduling change.</item>
/// </list>
///
/// <para>Every test below passes against the unchanged code, and that IS the finding rather than a
/// gap: the view model already distinguished the two states correctly and only the view conflated
/// them, so there is no failing unit test to write for this fix.</para>
/// </summary>
public sealed class EmptySessionSelectionTests : IDisposable
{
    private const int SessionId = 1;

    /// <summary>An ordinary timestamp, for the rows that are meant to be plottable.</summary>
    private static readonly long HealthyTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "empty-session-" + Guid.NewGuid().ToString("N"));

    private readonly SilentLogger _logger = new();

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public EmptySessionSelectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// The case the issue names: a session row with no sample rows at all. It is the condition the
    /// issue points at (<c>LoadInitialSession(id).IsEmpty</c>) and it is the one a reader will try
    /// first, so it is pinned even though the route to it is the harder half — see the remark.
    /// </summary>
    /// <remarks>
    /// Getting a session in this shape IN FRONT OF A USER is another matter, and looking for the way
    /// in is what turned up the test below. Every path traced from here defends against it:
    /// <c>LoggingManager.OnActiveChanged</c> adds a finished session to the bound list only when it
    /// recorded samples; <c>LoadPersistedLoggingSessions</c> DELETES sample-less non-importing rows at
    /// startup and then returns only sessions that have samples; and the SD importer reports
    /// <c>SessionPersisted = false</c> for a log with no samples, which is what the import callers
    /// gate their <c>LoggingSessions.Add</c> on. No live route to a truly sample-less row in the list
    /// was found — which does not make the branch dead, because the next test reaches it another way.
    /// </remarks>
    [Fact]
    public void A_session_with_no_samples_at_all_loads_as_empty()
    {
        SeedSession();

        var load = Repository().LoadInitialSession(SessionId);

        Assert.True(load.IsEmpty);
        Assert.Equal(0, load.TotalSampleCount);
        Assert.Null(load.FirstTime);
    }

    /// <summary>
    /// The second route, which the issue does not mention and which writing this file surfaced: a
    /// session whose stored ticks are ALL outside the range a date can represent is skipped down to
    /// empty by #237's filter and reaches the identical branch.
    ///
    /// <para>This is the route that matters, because it is the one that survives a restart. Such a
    /// session has real sample rows, so it passes both of the guards the test above describes — the
    /// startup purge keeps it and the "has samples" filter lists it — and it is on screen, clickable,
    /// on every launch. Its provenance is the same argument <c>SessionTimestampRangeTests</c> makes
    /// for #237 and no stronger: no current write path produces such a tick value, but SQLite does not
    /// re-validate rows already in a file, so an older store can hold one.</para>
    /// </summary>
    [Fact]
    public void A_session_whose_every_sample_is_unreadable_also_loads_as_empty()
    {
        SeedSession();
        SeedRow(long.MinValue);
        SeedRow(long.MaxValue);

        var load = Repository().LoadInitialSession(SessionId);

        Assert.True(load.IsEmpty);
        Assert.Equal(0, load.TotalSampleCount);

        // The rows are still there — this is a populated session by every other measure, which is
        // why nothing upstream of the plot treats it as empty.
        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM Samples"));
    }

    /// <summary>
    /// The control. Without it the two assertions above would also pass against a repository that
    /// called every session empty.
    /// </summary>
    [Fact]
    public void A_session_with_one_readable_sample_does_not_load_as_empty()
    {
        SeedSession();
        SeedRow(HealthyTicks);

        var load = Repository().LoadInitialSession(SessionId);

        Assert.False(load.IsEmpty);
        Assert.Equal(1, load.TotalSampleCount);
    }

    #region Helpers

    private SessionDataRepository Repository() => new(TestDatabase.Contexts(DatabasePath), _logger);

    private void SeedSession()
    {
        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(DatabasePath), DatabasePath);

        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO Sessions (ID, SessionStart, Name) VALUES ($id, $start, 'Session')";
        insert.Parameters.AddWithValue("$id", SessionId);
        insert.Parameters.AddWithValue("$start", "2026-01-01 00:00:00");
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// A sample row written by hand so the tick value goes in exactly as given — the app's own write
    /// paths all start from a <see cref="DateTime"/> and cannot produce the unreadable ones.
    /// </summary>
    private void SeedRow(long ticks)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Samples (LoggingSessionID, ChannelName, DeviceName, DeviceSerialNo, Color, Type, Value, TimestampTicks) " +
            "VALUES ($session, 'AI0', 'Nq1', 'SERIAL-A', '#FFD32F2F', 0, 1.0, $ticks)";
        insert.Parameters.AddWithValue("$session", SessionId);
        insert.Parameters.AddWithValue("$ticks", ticks);
        insert.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        return (long)query.ExecuteScalar()!;
    }

    /// <summary>
    /// Keeps the repository's #237 warning off the real application log. Nothing here asserts on it —
    /// <c>SessionTimestampRangeTests</c> owns that — so this only needs to swallow.
    /// </summary>
    private sealed class SilentLogger : IAppLogger
    {
        public void Information(string message) { }

        public void Warning(string message) { }

        public void Warning(Exception ex, string message) { }

        public void Error(string message) { }

        public void Error(Exception ex, string message) { }

        public void AddBreadcrumb(
            string category,
            string message,
            Daqifi.Desktop.Common.Loggers.BreadcrumbLevel level = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel.Info) { }

        public void SetDeviceContext(string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

        public void ClearDeviceContext() { }

        public void Shutdown() { }
    }

    #endregion
}
