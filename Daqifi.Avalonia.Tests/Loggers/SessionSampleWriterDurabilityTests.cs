using System.Diagnostics;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// <see cref="SessionSampleWriter"/> — the one path every logged sample takes to disk.
///
/// <para>Everything the user keeps from a logging run goes through this class: <c>DatabaseLogger</c>
/// hands it each <see cref="DataSample"/> and delegates its whole buffer surface
/// (<c>ClearBuffer</c>/<c>DiscardPendingBatch</c>/<c>WaitForIdle</c>/<c>SuspendConsumer</c>/
/// <c>ResumeConsumer</c>) straight through. It is also, alone among the write path, a hand-rolled
/// durability machine: a batch whose commit fails is retained outside the buffer and retried, the
/// retries are backed off exponentially, a waiting <c>WaitForIdle</c> caller overrides that backoff,
/// and a delete-all purge can ask for the retained batch to be dropped. None of that had a test.
/// The class even carries three <c>internal</c> members whose XML docs say they are "exposed for
/// tests" — <see cref="SessionSampleWriter.PendingRetryCount"/>,
/// <see cref="SessionSampleWriter.PollsUntilRetry"/> and
/// <see cref="SessionSampleWriter.IsConsumerThreadAlive"/> — and nothing in the suite referenced any
/// of them, so the seams were there and the tests were not.</para>
///
/// <para>What is at stake if any of it breaks is not an exception the user would see: it is samples
/// that quietly never land (a retained batch abandoned), the same samples landing twice (a batch
/// cleared before its commit proved), a <c>SampleCount</c> persisted from a <c>WaitForIdle</c> that
/// reported idle while rows were still stranded, or a purged database repopulated from a batch that
/// outlived it. So these tests are written against outcomes — rows in the database, attempts against
/// it, what reached the log — rather than against the fields that produce them.</para>
///
/// <para>The failure injected is a <see cref="IDbContextFactory{TContext}.CreateDbContext"/> that
/// throws, which is what a locked or unwritable data directory does and the same seam
/// <c>DeleteAllSessionsRecoveryTests</c> uses. It lands inside the consumer's <c>try</c>, so the
/// production frames under test are the real ones.</para>
///
/// <para>The consumer polls every 100ms, so several of these tests are necessarily wall-clock bound.
/// They are written to assert on relative outcomes (attempts in a window versus attempts in an equal
/// quiet window) rather than absolute timings wherever a relative form was available.</para>
/// </summary>
public sealed class SessionSampleWriterDurabilityTests : IDisposable
{
    /// <summary>The one session every sample in this fixture belongs to.</summary>
    private const int SessionId = 1;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "sample-writer-" + Guid.NewGuid().ToString("N"));

    private readonly FailableContexts _contexts;
    private readonly RecordingLogger _appLogger = new();
    private long _nextTick = DateTime.UtcNow.Ticks;

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public SessionSampleWriterDurabilityTests()
    {
        Directory.CreateDirectory(_directory);
        _contexts = new FailableContexts(DatabasePath);

        using (var context = Verification())
        {
            context.Database.EnsureCreated();

            // Samples carry a required foreign key to their session, so one has to exist before
            // any of them can be committed.
            context.Sessions.Add(new LoggingSession
            {
                ID = SessionId,
                Name = "Sample writer fixture",
                SessionStart = DateTime.UtcNow
            });
            context.SaveChanges();
        }
    }

    public void Dispose()
    {
        // Nothing to unpool first: TestDatabase's connections are not pooled, so each one has
        // already released its handle on the file by the time it is disposed.
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort — a leftover temp directory must not fail a test */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>
    /// The baseline the rest of this file stands on: a sample handed to the producer is on disk,
    /// with its value intact, once <see cref="SessionSampleWriter.WaitForIdle"/> says the buffer has
    /// drained. Everything below is a variation on this sentence failing to hold.
    /// </summary>
    [Fact]
    public void A_buffered_sample_reaches_the_database()
    {
        using var writer = NewWriter();

        writer.Add(Sample(1.5));
        writer.Add(Sample(2.5));

        writer.WaitForIdle(TimeSpan.FromSeconds(10));

        using var context = Verification();
        Assert.Equal([1.5, 2.5], context.Samples.OrderBy(sample => sample.Value).Select(sample => sample.Value).ToList());
    }

    /// <summary>
    /// The durability contract in one test: a batch whose commit fails is not lost, and when the
    /// database comes back it lands EXACTLY once.
    ///
    /// <para>Both halves matter and they pull in opposite directions. Clearing the batch before the
    /// commit is proved loses the samples outright; clearing it after a commit that actually
    /// succeeded but whose disposal then threw would write every row twice — a session showing
    /// double the samples it recorded, each timestamp duplicated. The production code threads that
    /// needle by clearing inside the <c>using</c>, immediately after <c>transaction.Commit()</c>,
    /// and this pins the outcome rather than the placement.</para>
    /// </summary>
    [Fact]
    public void A_batch_whose_commit_fails_is_retried_and_lands_exactly_once()
    {
        _contexts.FailCreates = true;
        using var writer = NewWriter();

        writer.Add(Sample(1));
        writer.Add(Sample(2));
        writer.Add(Sample(3));

        // Drained out of the buffer and stranded in the consumer's own batch — invisible to the
        // buffer count, which is exactly why the writer tracks it separately.
        Assert.True(
            WaitUntil(() => writer.PendingRetryCount == 3, TimeSpan.FromSeconds(15)),
            $"the failed batch was never retained (PendingRetryCount={writer.PendingRetryCount})");
        Assert.Equal(0, CountSamples());

        _contexts.FailCreates = false;
        writer.WaitForIdle(TimeSpan.FromSeconds(15));

        Assert.Equal(0, writer.PendingRetryCount);
        Assert.Equal(3, CountSamples());
    }

    /// <summary>
    /// <see cref="SessionSampleWriter.WaitForIdle"/> must not report idle while a failed batch is
    /// still held, because its caller acts on the answer: <c>LoggingManager</c> follows it with a
    /// <c>SELECT COUNT(*)</c> and persists the result as the session's <c>SampleCount</c>. A
    /// premature return there writes an undercount that the null-only backfill never repairs — the
    /// session list shows the wrong number for that run forever.
    ///
    /// <para>A stranded batch lives outside <c>_buffer</c>, so a drained-buffer test alone would
    /// pass while the rows were still in the air. What this asserts is that the call consumed its
    /// whole timeout rather than returning on an empty buffer.</para>
    /// </summary>
    [Fact]
    public void WaitForIdle_does_not_report_idle_while_a_failed_batch_is_stranded()
    {
        _contexts.FailCreates = true;
        using var writer = NewWriter();

        writer.Add(Sample(1));
        Assert.True(
            WaitUntil(() => writer.PendingRetryCount > 0, TimeSpan.FromSeconds(15)),
            "the failed batch was never retained");

        var stopwatch = Stopwatch.StartNew();
        writer.WaitForIdle(TimeSpan.FromMilliseconds(600));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(550),
            $"WaitForIdle returned after {stopwatch.ElapsedMilliseconds}ms with rows still unwritten");
        Assert.True(writer.PendingRetryCount > 0, "the batch was committed against a failing database");
        Assert.Equal(0, CountSamples());
    }

    /// <summary>
    /// The retry backoff is bounded at ~5s per attempt, which is longer than the timeouts its own
    /// callers use — so a database that recovers while <see cref="SessionSampleWriter.WaitForIdle"/>
    /// is waiting would otherwise sit out the cooldown and let the caller time out anyway, persisting
    /// the undercount the previous test describes. The writer answers that by ignoring the cooldown
    /// entirely while a caller is waiting.
    ///
    /// <para>Asserted as a rate, not a duration: the same wall-clock window is measured twice
    /// against a permanently failing database — once with nobody waiting, once inside
    /// <c>WaitForIdle</c> — and only the second may re-attempt. Comparing the two windows makes the
    /// test independent of how fast the machine happens to be, which an absolute deadline would
    /// not be.</para>
    /// </summary>
    [Fact]
    public void WaitForIdle_overrides_the_retry_backoff_so_a_recovered_database_is_not_left_waiting()
    {
        _contexts.FailCreates = true;
        using var writer = NewWriter();

        writer.Add(Sample(1));

        // Five failed attempts puts the cooldown at 16 polls (~1.6s), comfortably longer than
        // either measurement window below.
        Assert.True(
            WaitUntil(() => _contexts.CreateCount >= 5, TimeSpan.FromSeconds(20)),
            $"the batch was not retried (attempts={_contexts.CreateCount})");

        var beforeQuietWindow = _contexts.CreateCount;
        Thread.Sleep(1000);
        var quietAttempts = _contexts.CreateCount - beforeQuietWindow;

        var beforeExpeditedWindow = _contexts.CreateCount;
        writer.WaitForIdle(TimeSpan.FromMilliseconds(1000));
        var expeditedAttempts = _contexts.CreateCount - beforeExpeditedWindow;

        Assert.True(
            expeditedAttempts >= quietAttempts + 2,
            $"the backoff was not overridden: {quietAttempts} attempt(s) while nobody waited, " +
            $"{expeditedAttempts} while WaitForIdle did");
    }

    /// <summary>
    /// The delete-all purge's half of the contract. <c>ClearBuffer</c> empties the producer buffer,
    /// but a batch stranded by a failed commit lives in the consumer's own list and survives it —
    /// so without <c>DiscardPendingBatch</c> the purge would wipe the database, recreate the schema,
    /// and then have the consumer helpfully re-insert rows belonging to sessions the user just
    /// deleted, into a database whose session rows are gone.
    ///
    /// <para>The drop is asynchronous — the consumer honors it the next time it passes the suspend
    /// gate, before any insert — so what is pinned is that the batch is gone by the time the writer
    /// is idle again, and that nothing of it reached the database even though the database was
    /// healed first.</para>
    /// </summary>
    [Fact]
    public void DiscardPendingBatch_drops_a_stranded_batch_so_a_purge_cannot_repopulate_the_database()
    {
        _contexts.FailCreates = true;
        using var writer = NewWriter();

        writer.Add(Sample(1));
        writer.Add(Sample(2));
        Assert.True(
            WaitUntil(() => writer.PendingRetryCount == 2, TimeSpan.FromSeconds(15)),
            $"the failed batch was never retained (PendingRetryCount={writer.PendingRetryCount})");

        // Exactly the sequence LoggingSessionListViewModel's purge issues.
        writer.SuspendConsumer();
        writer.ClearBuffer();
        writer.DiscardPendingBatch();

        // The database is healthy again from here on — so a batch that was NOT dropped would
        // succeed, which is what makes this test able to fail.
        _contexts.FailCreates = false;
        writer.ResumeConsumer();

        Assert.True(
            WaitUntil(() => writer.PendingRetryCount == 0, TimeSpan.FromSeconds(10)),
            "the stranded batch was never dropped");

        writer.WaitForIdle(TimeSpan.FromSeconds(5));
        Assert.Equal(0, CountSamples());
    }

    /// <summary>
    /// The other half of the purge: samples still in the producer buffer when the user deletes
    /// everything must not arrive afterwards. Suspending first is what makes the clear meaningful —
    /// the consumer drains every 100ms otherwise — and it is the order the purge uses.
    /// </summary>
    [Fact]
    public void ClearBuffer_under_suspension_drops_samples_that_never_reached_the_database()
    {
        using var writer = NewWriter();

        writer.SuspendConsumer();

        writer.Add(Sample(1));
        writer.Add(Sample(2));
        writer.Add(Sample(3));
        writer.ClearBuffer();

        writer.ResumeConsumer();
        writer.WaitForIdle(TimeSpan.FromSeconds(5));

        Assert.Equal(0, CountSamples());
        Assert.Equal(0, writer.PendingRetryCount);
    }

    /// <summary>
    /// A persistently failing database must not flood the log — and, because <c>Error</c> also
    /// reports to Sentry, must not flood Sentry — at the consumer's ~10Hz poll rate. Only the first
    /// failure of a streak is reported, and the recovery is reported once when the batch finally
    /// commits, so a support log shows "it broke, then it came back" rather than several hundred
    /// identical lines.
    ///
    /// <para>The recovery line is the part that could rot silently: it is emitted on the
    /// proven-commit path, inside the <c>using</c>, so that a context or transaction whose disposal
    /// throws cannot swallow it and leave the streak looking unresolved.</para>
    /// </summary>
    [Fact]
    public void A_streak_of_failures_is_reported_once_and_so_is_the_recovery()
    {
        _contexts.FailCreates = true;
        using var writer = NewWriter();

        writer.Add(Sample(1));
        Assert.True(
            WaitUntil(() => _contexts.CreateCount >= 4, TimeSpan.FromSeconds(20)),
            $"the batch was not retried (attempts={_contexts.CreateCount})");

        Assert.Single(_appLogger.Errors);

        _contexts.FailCreates = false;
        writer.WaitForIdle(TimeSpan.FromSeconds(15));

        Assert.Equal(1, CountSamples());
        Assert.Single(_appLogger.Errors);
        Assert.Contains("recovered", Assert.Single(_appLogger.Informations), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shutdown. The consumer thread must actually exit — it holds a database connection and the
    /// app's data directory, and a survivor would keep writing into a database the next run is
    /// migrating — and a producer that outlives the writer must not throw, because
    /// <c>DatabaseLogger.Log</c> is called from the device message thread and cannot know that
    /// shutdown has already run.
    /// </summary>
    [Fact]
    public void Disposing_stops_the_consumer_and_leaves_the_producer_harmless()
    {
        var writer = NewWriter();

        writer.Add(Sample(1));
        writer.WaitForIdle(TimeSpan.FromSeconds(10));
        Assert.Equal(1, CountSamples());

        writer.Dispose();

        Assert.False(writer.IsConsumerThreadAlive, "the consumer thread outlived Dispose");

        // Both are things the app does on the way down: a late sample from the device thread, and a
        // second Dispose from a disposal path that runs twice.
        writer.Add(Sample(2));
        writer.Dispose();

        Thread.Sleep(300);
        Assert.Equal(1, CountSamples());
    }

    /// <summary>
    /// CHARACTERIZATION, NOT APPROVAL. This pins behavior the writer has today that a user would
    /// call a bug, so that a fix has something to change and cannot land by accident.
    ///
    /// <para>"Never abandon the batch" is the right answer for the failures the class was written
    /// against — a full disk, a file another process has open, an unwritable folder — because those
    /// end and the rows are still wanted when they do. It is the wrong answer for a row the database
    /// will reject every time it is offered, and the consumer cannot tell the two apart: it retains
    /// the batch, backs off to one attempt every ~5s, and drains each new sample onto the same
    /// doomed list. So ONE bad row means nothing from that session ever reaches disk, the retained
    /// batch grows without bound for as long as the session runs, and the log carries a single line
    /// about it — the log-once streak rule, correct in itself, ensures the silence afterwards.</para>
    ///
    /// <para>The row used here is a sample whose <see cref="DataSample.Value"/> is
    /// <see cref="double.NaN"/>, which Microsoft.Data.Sqlite refuses outright ("Cannot store 'NaN'
    /// values."). Measured alongside it: infinities and <see cref="double.MaxValue"/> store without
    /// complaint, so NaN is specifically the value that does this. The app already holds a policy
    /// that non-finite values must not propagate —
    /// <c>AbstractChannel.ActiveSample</c> checks <c>double.IsFinite</c> and disables scaling rather
    /// than pass one on, "so Infinity/NaN never reaches the live plot or exported data" — but that
    /// check sits on the user-scaling-expression path only. The unscaled device path
    /// (<c>AnalogChannel.GetScaledValue</c> → Core's calibration formula) has no equivalent, and
    /// neither does the writer.</para>
    ///
    /// <para>Whichever way it is fixed — refusing non-finite values at the producer, isolating the
    /// offending row after repeated failure, or bounding the retained batch — this test should
    /// change, and its replacement should assert that the five good samples land. It is here so
    /// that the change is deliberate.</para>
    /// </summary>
    [Fact]
    public void A_row_the_database_will_never_accept_wedges_every_later_sample_in_the_session()
    {
        // The database is healthy throughout: the failure is the row, not the storage.
        using var writer = NewWriter();

        writer.Add(Sample(double.NaN));
        Assert.True(
            WaitUntil(() => _appLogger.Errors.Count == 1, TimeSpan.FromSeconds(15)),
            "the rejected row was never reported");
        Assert.Contains("retaining the batch", _appLogger.Errors[0], StringComparison.OrdinalIgnoreCase);

        // Five perfectly ordinary samples, offered after the batch is already doomed.
        for (var value = 1; value <= 5; value++)
        {
            writer.Add(Sample(value));
        }

        Assert.True(
            WaitUntil(() => writer.PendingRetryCount == 6, TimeSpan.FromSeconds(15)),
            $"the good samples were not drained onto the doomed batch (PendingRetryCount={writer.PendingRetryCount})");

        writer.WaitForIdle(TimeSpan.FromSeconds(2));

        // The whole session's data, held in memory and never written, with no further diagnostics.
        Assert.Equal(0, CountSamples());
        Assert.Equal(6, writer.PendingRetryCount);
        Assert.Single(_appLogger.Errors);
    }

    #region Fixture
    private SessionSampleWriter NewWriter() => new(_contexts, _appLogger);

    /// <summary>
    /// A sample belonging to the fixture's session. Timestamps ascend so a test can order by them
    /// without relying on insertion order.
    /// </summary>
    private DataSample Sample(double value) => new()
    {
        LoggingSessionID = SessionId,
        Value = value,
        TimestampTicks = Interlocked.Increment(ref _nextTick),
        DeviceName = "Test Device",
        ChannelName = "AI0",
        DeviceSerialNo = "SN0001",
        Color = "#FF2196F3"
    };

    /// <summary>
    /// A context that bypasses <see cref="FailableContexts"/> entirely, so reading the database back
    /// neither counts as an attempt nor trips the injected failure.
    /// </summary>
    private LoggingContext Verification() => new(TestDatabase.Options(DatabasePath));

    private int CountSamples()
    {
        using var context = Verification();
        return context.Samples.Count();
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) { return true; }
            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>
    /// The suite's usual SQLite context factory plus a switch that makes <c>CreateDbContext</c> fail
    /// the way a locked or unwritable data directory does, and a count of how many times the
    /// consumer has tried — which is how the backoff is observed without reading its own field.
    /// </summary>
    private sealed class FailableContexts(string databasePath) : IDbContextFactory<LoggingContext>
    {
        private int _createCount;
        private volatile bool _failCreates;

        /// <summary>Attempts made against the database, successful or not. Read from the test thread.</summary>
        internal int CreateCount => Volatile.Read(ref _createCount);

        /// <summary>While set, every attempt fails. Written from the test thread, read by the consumer.</summary>
        internal bool FailCreates
        {
            get => _failCreates;
            set => _failCreates = value;
        }

        public LoggingContext CreateDbContext()
        {
            Interlocked.Increment(ref _createCount);

            if (_failCreates)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            return new LoggingContext(TestDatabase.Options(databasePath));
        }
    }

    /// <summary>
    /// Captures the consumer thread's diagnostics instead of writing them to the real log, and keeps
    /// them readable from the test thread.
    /// </summary>
    private sealed class RecordingLogger : IAppLogger
    {
        private readonly Lock _gate = new();
        private readonly List<string> _errors = [];
        private readonly List<string> _informations = [];

        internal IReadOnlyList<string> Errors
        {
            get { lock (_gate) { return [.. _errors]; } }
        }

        internal IReadOnlyList<string> Informations
        {
            get { lock (_gate) { return [.. _informations]; } }
        }

        public void Information(string message)
        {
            lock (_gate) { _informations.Add(message); }
        }

        public void Warning(string message) { }

        public void Warning(Exception ex, string message) { }

        public void Error(string message)
        {
            lock (_gate) { _errors.Add(message); }
        }

        public void Error(Exception ex, string message)
        {
            lock (_gate) { _errors.Add(message); }
        }

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
