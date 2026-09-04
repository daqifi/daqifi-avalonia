using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OxyPlot;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Opening a saved session whose <c>Samples.TimestampTicks</c> holds a value no <see cref="DateTime"/>
/// can represent (issue #237).
///
/// <para>The column is a plain SQLite <c>INTEGER</c> with no <c>CHECK</c>, so it can hold anything a
/// <see cref="long"/> can, while <c>new DateTime(long)</c> accepts only
/// <c>0 .. DateTime.MaxValue.Ticks</c>. <see cref="SessionDataRepository"/> converted the stored value
/// at two sites without checking it.</para>
///
/// <para>Running these against the unchanged code turned up a failure mode the issue explicitly ruled
/// out ("there is no silent-wrong-render variant — <c>new DateTime</c> either succeeds or throws").
/// There is one, and which of the two you get depends on WHICH END of the range the damaged row sits
/// at, because both sites convert the session's MINIMUM tick value and nothing else:</para>
/// <list type="bullet">
/// <item>a row BELOW the range (negative) is the session minimum, so it is the value converted — that
/// throws <see cref="ArgumentOutOfRangeException"/> out of the load and the session will not open;</item>
/// <item>a row ABOVE the range (e.g. <c>long.MaxValue</c>) is never the minimum when any healthy row
/// exists, so nothing throws. It is plotted, at a delta-time of about 27,000 years, which stretches the
/// X axis so far that the real data collapses into a single vertical line at the origin. The session
/// "loads", and is unreadable. See <see cref="A_sample_above_the_range_is_silently_plotted_27000_years_out"/>.</item>
/// </list>
///
/// <para>The fix skips unreadable rows rather than clamping them, because a clamped origin would shift
/// every delta-time on the plot — trading a crash for a quietly wrong graph, which is the failure the
/// second bullet describes. The rest of the session loads and one warning names it.</para>
///
/// <para>Reachability is the same argument issue #231 makes for <c>Samples.Color</c> and no stronger:
/// no current write path can produce such a value (every producer goes through a <see cref="DateTime"/>),
/// but SQLite does not re-validate rows already sitting in a database file, so a store written before
/// the current schema can hold one. <see cref="A_session_database_can_hold_a_tick_value_no_date_can_represent"/>
/// builds such a file rather than assuming it.</para>
///
/// <para>Thirteen of the seventeen cases here fail against the unchanged code: ten throw
/// <see cref="ArgumentOutOfRangeException"/> out of the load, and three fail on an assertion having
/// thrown nothing at all — the silent variant above. Measured by running this file against the merge
/// base, not by reasoning about which ones ought to fail.</para>
/// </summary>
public sealed class SessionTimestampRangeTests : IDisposable
{
    /// <summary>
    /// Highest tick value <c>new DateTime(long)</c> accepts. The low end needs no constant: it is
    /// <c>0</c>, and <see cref="The_edges_of_the_representable_range_are_kept"/> writes it as such.
    /// </summary>
    private static readonly long HighestReadableTicks = DateTime.MaxValue.Ticks;

    /// <summary>An ordinary, healthy timestamp for the rows that are meant to survive.</summary>
    private static readonly long HealthyTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private const int SessionId = 1;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "ticks-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingLogger _logger = new();

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public SessionTimestampRangeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    #region The reachability argument

    /// <summary>
    /// The premise everything else rests on, demonstrated rather than asserted: the column takes any
    /// <see cref="long"/>, including values <c>new DateTime</c> rejects, and reads them back verbatim.
    /// </summary>
    [Fact]
    public void A_session_database_can_hold_a_tick_value_no_date_can_represent()
    {
        Seed(HealthyTicks, long.MinValue, long.MaxValue);

        Assert.Equal(long.MinValue, ScalarLong("SELECT MIN(TimestampTicks) FROM Samples"));
        Assert.Equal(long.MaxValue, ScalarLong("SELECT MAX(TimestampTicks) FROM Samples"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DateTime(long.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DateTime(long.MaxValue));
    }

    #endregion

    #region Phase 1 — LoadInitialSession

    /// <summary>
    /// The headline for the low end: one negative row must not stop the session opening. The row is
    /// dropped and the healthy samples are still there.
    /// </summary>
    [Fact]
    public void A_negative_sample_does_not_abort_the_session_load()
    {
        Seed(-1L, HealthyTicks, HealthyTicks + 10_000L);

        var load = Repository().LoadInitialSession(SessionId);

        Assert.Equal(2, Assert.Single(load.Points).Value.Count);
        Assert.Equal(new DateTime(HealthyTicks), load.FirstTime);
    }

    /// <summary>
    /// The headline for the high end, and the failure mode the issue said did not exist. With any
    /// healthy row present, an above-range row is not the session minimum, so nothing converts it and
    /// nothing throws — it is simply plotted. Unfixed, this fails on the delta-time assertion with
    /// <c>858434381285477.6 ms</c>, about 27,000 years past the origin, which stretches the X axis far
    /// enough that the real data collapses to a vertical line at zero.
    /// </summary>
    /// <remarks>
    /// This is the one that made "skip" the right remedy rather than "clamp". Clamping the row's own
    /// timestamp still leaves the point in the wrong place, and clamping the ORIGIN instead would push
    /// every healthy point out there together.
    /// </remarks>
    [Fact]
    public void A_sample_above_the_range_is_silently_plotted_27000_years_out()
    {
        Seed(HealthyTicks, HealthyTicks + 10_000L, long.MaxValue);

        var points = Assert.Single(Repository().LoadInitialSession(SessionId).Points).Value;

        Assert.All(points, p => Assert.True(p.X < 1_000.0, $"delta-time {p.X} ms is not from a healthy row"));
        Assert.Equal(2, points.Count);
    }

    /// <summary>
    /// The magnitude of the damage does not matter, only which side of the boundary it falls on — so
    /// each extreme is paired with a row exactly ONE tick past <see cref="DateTime.MaxValue"/>, which
    /// is the value an off-by-one in the guard would let through.
    /// </summary>
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void Rows_outside_the_range_are_dropped_at_the_boundary_and_at_the_extremes(long hostileTicks)
    {
        Seed(hostileTicks, HealthyTicks);
        SeedRow(HighestReadableTicks + 1);

        var load = Repository().LoadInitialSession(SessionId);

        Assert.Equal(new DateTime(HealthyTicks), load.FirstTime);
        Assert.Equal(1, load.TotalSampleCount);
        Assert.Single(Assert.Single(load.Points).Value);
    }

    /// <summary>
    /// The other half of the guard, and the reason it is a range test and not a "positive?" test:
    /// zero ticks IS a date — <see cref="DateTime.MinValue"/> — and so is
    /// <c>DateTime.MaxValue.Ticks</c>. Neither may be swept up with the damaged rows.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    public void The_edges_of_the_representable_range_are_kept(long edgeTicks)
    {
        Seed(edgeTicks, HighestReadableTicks);

        var load = Repository().LoadInitialSession(SessionId);

        Assert.Equal(2, load.TotalSampleCount);
        Assert.Equal(new DateTime(edgeTicks), load.FirstTime);
        Assert.Empty(_logger.Warnings);
    }

    /// <summary>
    /// Channel discovery keys off the session's first timestamp, so a damaged row that sorts below
    /// every healthy one would otherwise BE that timestamp — and only the channel on the damaged row
    /// would be discovered. Every other channel's samples are then dropped, because Phase 1 seeds a
    /// point list only for the channels it discovered. Losing three channels out of four is a data
    /// loss the crash was hiding.
    /// </summary>
    [Fact]
    public void A_damaged_row_does_not_shrink_the_discovered_channel_set()
    {
        SeedSession();
        SeedRow(-1L, channelName: "AI3");
        SeedRow(HealthyTicks, channelName: "AI0");
        SeedRow(HealthyTicks, channelName: "AI1");
        SeedRow(HealthyTicks, channelName: "AI2");

        var channels = Repository().LoadInitialSession(SessionId).Channels;

        Assert.Equal(["AI0", "AI1", "AI2"], channels.Select(c => c.ChannelName));
    }

    /// <summary>
    /// A skipped sample is a dropped data point, so it may not be silent. One warning per load, naming
    /// the session, is the trail the user's log needs to explain a shorter graph.
    /// </summary>
    [Fact]
    public void Skipping_a_damaged_row_is_reported_once_and_names_the_session()
    {
        Seed(-1L, HealthyTicks, long.MaxValue);

        Repository().LoadInitialSession(SessionId);

        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains($"Session {SessionId}", warning);
    }

    /// <summary>
    /// And the mirror of it: a healthy session must not learn to cry wolf.
    /// </summary>
    [Fact]
    public void A_healthy_session_is_not_warned_about()
    {
        Seed(HealthyTicks, HealthyTicks + 10_000L);

        Repository().LoadInitialSession(SessionId);

        Assert.Empty(_logger.Warnings);
    }

    /// <summary>
    /// A session with nothing readable left must come back empty rather than throw — it is the same
    /// answer the loader already gives for a session with no samples at all, and the caller already
    /// handles it.
    /// </summary>
    [Fact]
    public void A_session_of_nothing_but_damaged_rows_loads_as_empty()
    {
        Seed(long.MinValue, -1L, long.MaxValue);

        var load = Repository().LoadInitialSession(SessionId);

        Assert.True(load.IsEmpty);
        Assert.Null(load.FirstTime);
        Assert.Equal(0, load.TotalSampleCount);
    }

    /// <summary>
    /// The sample count decides whether the caller runs the expensive Phase-2 load, so it has to count
    /// rows that will actually be drawn — a session of a million unreadable rows must not be treated
    /// as a million-point session.
    /// </summary>
    [Fact]
    public void The_sample_count_covers_only_rows_that_can_be_drawn()
    {
        Seed(-1L, HealthyTicks, HealthyTicks + 10_000L, long.MaxValue);

        Assert.Equal(2, Repository().LoadInitialSession(SessionId).TotalSampleCount);
    }

    #endregion

    #region Phase 2 — LoadSampledData

    /// <summary>
    /// The second site the issue names. Phase 2 takes its time origin from <c>MIN(TimestampTicks)</c>
    /// read straight out of SQL, so a negative row is the value it converts, and the full-range load
    /// throws where Phase 1 threw.
    /// </summary>
    [Fact]
    public void A_negative_sample_does_not_abort_the_full_range_load()
    {
        Seed(-1L, HealthyTicks, HealthyTicks + 10_000L);

        Assert.Equal(new DateTime(HealthyTicks), Repository().LoadSampledData(SessionId, 1, Seeded()));
    }

    /// <summary>
    /// The high end at the second site, which fails the same silent way as Phase 1: the origin is
    /// fine, so nothing throws, and the damaged row is plotted 27,000 years out.
    /// </summary>
    [Fact]
    public void A_sample_above_the_range_is_dropped_from_the_full_range_load()
    {
        Seed(HealthyTicks, HealthyTicks + 10_000L, long.MaxValue);

        var points = Seeded();
        Repository().LoadSampledData(SessionId, 1, points);

        Assert.All(Assert.Single(points).Value,
            p => Assert.True(p.X < 1_000.0, $"delta-time {p.X} ms is not from a healthy row"));
    }

    /// <summary>
    /// Both ends at once — the case that also removes the <c>minTicks + i * tickStep</c> overflow the
    /// issue notes, since after the range check both operands are bounded by
    /// <c>DateTime.MaxValue.Ticks</c> and the sum cannot leave <see cref="long"/>.
    /// </summary>
    [Fact]
    public void A_full_range_load_survives_damage_at_both_ends()
    {
        Seed(long.MinValue, HealthyTicks, HealthyTicks + 10_000L, long.MaxValue);

        var points = Seeded();

        Assert.Equal(new DateTime(HealthyTicks), Repository().LoadSampledData(SessionId, 1, points));
        Assert.All(Assert.Single(points).Value, p => Assert.True(p.X < 1_000.0, $"delta-time {p.X} ms"));
    }

    /// <summary>
    /// A session left with one readable timestamp has no time range to sample across, which is the
    /// existing "degenerate session" answer (null) rather than a new failure.
    /// </summary>
    [Fact]
    public void A_full_range_load_of_one_readable_timestamp_reports_no_range()
    {
        Seed(long.MinValue, HealthyTicks, long.MaxValue);

        Assert.Null(Repository().LoadSampledData(SessionId, 1, Seeded()));
    }

    #endregion

    #region The single-timestamp fallback

    /// <summary>
    /// The one path on which a skipped row could still reach the plot, found by review rather than by
    /// the issue. Skipping it from the two loads is not enough on its own, because BOTH viewers have a
    /// third path: when a session is past <see cref="SessionDataRepository.INITIAL_LOAD_POINTS"/> and
    /// <see cref="SessionDataRepository.LoadSampledData"/> finds no time range to sample across, they
    /// fall back to <see cref="SessionDataRepository.LoadSingleTickValueSpread"/>, which draws each
    /// channel as a MIN..MAX vertical segment aggregated over the whole session. That aggregation was
    /// keyed on the session id alone, so a damaged row's VALUE was still the segment's extreme — the
    /// row skipped everywhere else, back on the graph.
    /// </summary>
    /// <remarks>
    /// <para>This fix made the path MORE reachable, which is why it belongs in this PR rather than a
    /// follow-up: excluding unreadable rows from the bounds is exactly what makes a damaged session's
    /// readable rows share one timestamp, and therefore what sends the caller down this branch.</para>
    /// <para>The chain is exercised for real — over the cap, no time range, then the fallback — rather
    /// than by calling the fallback directly, because "the fallback is reached" is half the claim. The
    /// 100,001 rows go in with one recursive-CTE insert, so the test still runs in well under a second.
    /// The two callers themselves (<c>DatabaseLogger</c>, <c>LoggedSessionsMobileViewModel</c>) are the
    /// only part not covered here; both make exactly these three calls in this order.</para>
    /// </remarks>
    [Fact]
    public void The_single_timestamp_fallback_does_not_bring_a_skipped_row_back_as_a_channel_extreme()
    {
        SeedSession();
        SeedManyRows(SessionDataRepository.INITIAL_LOAD_POINTS + 1, HealthyTicks, value: 1.0);
        SeedRow(long.MaxValue, value: 999_999.0);

        var repository = Repository();
        var load = repository.LoadInitialSession(SessionId);

        // The caller's gate: without this the fallback is never reached and the test proves nothing.
        Assert.True(load.TotalSampleCount > SessionDataRepository.INITIAL_LOAD_POINTS);

        // And the branch: every readable row shares one timestamp, so there is no range to sample.
        Assert.Null(repository.LoadSampledData(SessionId, load.Channels.Count, Seeded()));

        var spread = SessionDataRepository.LoadSingleTickValueSpread(
            TestDatabase.Contexts(DatabasePath), SessionId, [("SERIAL-A", "AI0")]);

        // One point, not a segment: the channel's value never changes once the damaged row is out.
        var point = Assert.Single(Assert.Single(spread).Value);
        Assert.Equal(1.0, point.Y);
    }

    #endregion

    #region Helpers

    private SessionDataRepository Repository() => new(TestDatabase.Contexts(DatabasePath), _logger);

    /// <summary>One pre-seeded point list for "AI0", the shape <c>LoadSampledData</c> expects.</summary>
    private static Dictionary<(string deviceSerial, string channelName), List<DataPoint>> Seeded() =>
        new() { [("SERIAL-A", "AI0")] = [] };

    private void Seed(params long[] tickValues)
    {
        SeedSession();
        foreach (var ticks in tickValues)
        {
            SeedRow(ticks);
        }
    }

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
    /// A sample row written by hand, so the tick value goes in exactly as given. The app's own write
    /// paths all start from a <see cref="DateTime"/> and so cannot produce these values — which is the
    /// point: the damaged row comes from an older store, not from this build.
    /// </summary>
    private void SeedRow(long ticks, string channelName = "AI0", double value = 1.0)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Samples (LoggingSessionID, ChannelName, DeviceName, DeviceSerialNo, Color, Type, Value, TimestampTicks) " +
            "VALUES ($session, $channel, 'Nq1', 'SERIAL-A', '#FFD32F2F', 0, $value, $ticks)";
        insert.Parameters.AddWithValue("$session", SessionId);
        insert.Parameters.AddWithValue("$channel", channelName);
        insert.Parameters.AddWithValue("$value", value);
        insert.Parameters.AddWithValue("$ticks", ticks);
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// <paramref name="count"/> identical "AI0" rows at one timestamp, inserted by a recursive CTE in a
    /// single statement. Needed only by the fallback test, which has to get a session past
    /// <see cref="SessionDataRepository.INITIAL_LOAD_POINTS"/> — a hundred thousand round trips would
    /// have made that test too slow to keep, and this takes well under a second.
    /// </summary>
    private void SeedManyRows(int count, long ticks, double value)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Samples (LoggingSessionID, ChannelName, DeviceName, DeviceSerialNo, Color, Type, Value, TimestampTicks) " +
            "SELECT $session, 'AI0', 'Nq1', 'SERIAL-A', '#FFD32F2F', 0, $value, $ticks " +
            "FROM (WITH RECURSIVE rows(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM rows WHERE i < $count) SELECT i FROM rows)";
        insert.Parameters.AddWithValue("$session", SessionId);
        insert.Parameters.AddWithValue("$value", value);
        insert.Parameters.AddWithValue("$ticks", ticks);
        insert.Parameters.AddWithValue("$count", count);
        insert.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Keeps the warnings so a test can assert on them, and off the real application log.</summary>
    private sealed class RecordingLogger : IAppLogger
    {
        internal List<string> Warnings { get; } = [];

        public void Information(string message) { }

        public void Warning(string message) => Warnings.Add(message);

        public void Warning(Exception ex, string message) => Warnings.Add(message);

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
