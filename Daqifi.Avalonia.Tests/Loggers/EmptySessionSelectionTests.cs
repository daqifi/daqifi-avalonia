using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #262: opening a logging session that holds no samples left the Logged Data pane saying
/// <c>No session selected</c>, directly under a session list where that very session was highlighted.
///
/// <para>The cause is a single boolean asked to carry two meanings. <see cref="DatabaseLogger"/> sets
/// <c>HasSessionData = false</c> both when nothing is open and when an empty session is open, and the
/// view had nothing else to read, so it rendered the "nothing is open" card for both.
/// <c>CurrentSession</c> already separates them — it is null in the first case and the opened session
/// in the second — so the fix is the view reading that instead of inferring selection from
/// <c>HasSessionData</c>.</para>
///
/// <para>The gate itself is XAML and cannot be asserted here: views in this repo carry no
/// <c>x:DataType</c>, so <c>IsVisible</c> bindings resolve by reflection at run time (the same
/// limitation <c>EmptyLoggedPlotFrameTests</c> records for #251). What these tests hold is the
/// view-model invariant the new gate rests on, in both directions:</para>
/// <list type="bullet">
/// <item>the empty-session branch of <c>DisplayLoggingSession</c> returns early, and must leave
/// <c>CurrentSession</c> pointing at the session it was asked to open — nulling it there would look
/// like tidying and would silently restore the bug, because the pane would go back to having no way
/// to tell the two states apart;</item>
/// <item><c>ClearPlot</c> must keep nulling it, or the pane would claim a session is open after the
/// last one is deleted.</item>
/// </list>
///
/// <para><b>Run against the unchanged code, every test in this file passes.</b> That is the finding,
/// not a gap: the view model already distinguished the two states correctly and only the view was
/// wrong, so there is no failing unit test to write for the fix itself — the before/after evidence is
/// rendered, and these pin the state the render depends on.</para>
///
/// <para>The reachability section below is why the state is worth pinning at all. "Zero samples" is
/// reachable two ways, not one, and the second was not in the issue: a session whose rows all carry a
/// tick value no <see cref="DateTime"/> can represent (#237) is dropped to empty by
/// <see cref="SessionDataRepository.LoadInitialSession"/> as well, so a session that visibly reports
/// thousands of samples in the list can still land in this branch.</para>
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

    #region How a session ends up with nothing to plot

    /// <summary>
    /// The case the issue names: a session row with no sample rows at all — a run stopped before the
    /// first sample landed, or an SD import that wrote the session record and no data.
    /// </summary>
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
    /// The second route, which the issue does not mention and which running this file surfaced: a
    /// session with rows whose stored ticks are all outside the range a date can represent is skipped
    /// down to empty by #237's filter, and reaches the identical branch. It matters because such a
    /// session is NOT visibly empty anywhere else — the row count is real, so the list can advertise a
    /// sample count while the pane has nothing to draw.
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

        // The rows are still there — this is a full session by every other measure.
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

    #endregion

    #region What the pane is left holding

    /// <summary>
    /// The invariant the fix binds to. An empty session is still an OPEN session: the early return in
    /// <c>DisplayLoggingSession</c>'s empty branch happens after <c>CurrentSession</c> is assigned, so
    /// the pane can name the session it is showing nothing for, and the session-list highlight has
    /// something in the pane that agrees with it.
    /// </summary>
    [Fact]
    public void An_empty_session_stays_open_on_the_plot_with_no_data()
    {
        SeedSession();
        var session = new LoggingSession(SessionId, "Session");

        using var logger = Logger();
        logger.DisplayLoggingSession(session);

        Assert.Same(session, logger.CurrentSession);
        Assert.True(logger.IsSessionOpen);
        Assert.False(logger.HasSessionData);
        Assert.Equal(0, logger.CurrentSessionSampleCount);
        Assert.Empty(logger.PlotModel.Series);
    }

    /// <summary>
    /// The other direction, and the one that keeps "no session selected" honest: clearing really does
    /// close the session. Deleting the last session goes through here.
    /// </summary>
    [Fact]
    public void Clearing_the_plot_closes_the_session()
    {
        SeedSession();

        using var logger = Logger();
        logger.DisplayLoggingSession(new LoggingSession(SessionId, "Session"));
        logger.ClearPlot();

        Assert.Null(logger.CurrentSession);
        Assert.False(logger.IsSessionOpen);
        Assert.False(logger.HasSessionData);
    }

    /// <summary>
    /// A session that does hold samples is open AND has data, so the two flags are not silently the
    /// same thing — which is what would make the new gate indistinguishable from the old one.
    /// </summary>
    [Fact]
    public void A_session_with_samples_is_open_and_has_data()
    {
        SeedSession();
        SeedRow(HealthyTicks);
        SeedRow(HealthyTicks + 10_000L);

        using var logger = Logger();
        logger.DisplayLoggingSession(new LoggingSession(SessionId, "Session"));

        Assert.True(logger.IsSessionOpen);
        Assert.True(logger.HasSessionData);
        Assert.NotEmpty(logger.PlotModel.Series);
    }

    #endregion

    #region Helpers

    private SessionDataRepository Repository() => new(TestDatabase.Contexts(DatabasePath), _logger);

    /// <summary>
    /// A real <see cref="DatabaseLogger"/> over the throwaway database. It reaches shared state through
    /// <c>Dispatcher.UIThread.Invoke</c>, which runs inline outside a running Avalonia app, so no
    /// headless harness is needed — but it also starts the sample-writer thread and two dispatcher
    /// timers, hence the <c>using</c> at every call site.
    /// </summary>
    private DatabaseLogger Logger() => new(TestDatabase.Contexts(DatabasePath));

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
