using System.Collections.Concurrent;
using Daqifi.Desktop.DiskSpace;
using Xunit;

namespace Daqifi.Avalonia.Tests.DiskSpace;

/// <summary>
/// Characterisation tests for <see cref="DiskSpaceMonitor"/> — the class that decides, from a
/// raw byte count, whether a logging session may start and whether one already running gets
/// killed.
///
/// <para>
/// It is worth pinning because both of its wrong answers are expensive and neither announces
/// itself. Classify too generously and a session keeps writing samples into
/// <c>DAQiFiDatabase.db</c> until the volume fills; SQLite's failure at that point lands in the
/// middle of a write, and what the user loses is not the last row but the session they were
/// recording. Classify too harshly and the app refuses to start a session on a disk that has
/// hundreds of megabytes free, with a dialog that quotes a number the user can see is wrong.
/// The whole judgement is four comparisons against three constants, and nothing downstream
/// re-checks it.
/// </para>
///
/// <para>
/// These tests pin what the code DOES today, quirks included. Two of the behaviours asserted
/// below look like defects rather than decisions —
/// <see cref="IsMonitoring_still_reports_true_after_the_critical_hard_stop"/> and
/// <see cref="StartMonitoring_after_Dispose_starts_a_timer_that_Dispose_will_not_stop"/> — and
/// they are asserted as they are, not as they arguably should be, so that a later fix has to
/// change a test on purpose instead of changing behaviour by accident. Each says so at its own
/// comment.
/// </para>
///
/// <para>
/// Thresholds are written as literal megabyte counts rather than as the class's own constants.
/// Expressing the input in terms of the constant under test would move the input whenever the
/// constant moved, and the test could then never fail — the point here is that 50, 100 and 500
/// MB are the documented, user-visible numbers, so a change to any of them should have to be
/// made twice.
/// </para>
/// </summary>
public class DiskSpaceMonitorTests
{
    private const string MonitoredPath = "/tmp/daqifi-disk-space-tests";

    /// <summary>Generous enough for a loaded CI box; the first timer tick is due immediately.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to watch for a tick that should NOT happen. The monitor's period is 15 s, so a
    /// timer that was wrongly (re)started fires within milliseconds and a timer that was left
    /// alone cannot fire inside this window either way.
    /// </summary>
    private static readonly TimeSpan NoFurtherProbeWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to watch for an event that should NOT be raised, measured from the point the
    /// tick's lock is free. Everything between the free-space probe returning and the event
    /// being raised is a comparison and a lock acquisition — no I/O — so a tick that was going
    /// to raise has raised long before this expires.
    /// </summary>
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromSeconds(1);

    private static long Mb(long megabytes) => megabytes * 1024 * 1024;

    #region Test doubles

    /// <summary>
    /// The injectable free-space provider, recording every probe. Counting probes is what makes
    /// the negative assertions below real: "no event was raised" is only worth asserting once
    /// something proves the monitor actually looked at the disk.
    /// </summary>
    private sealed class FreeSpaceProbe
    {
        private readonly Func<string, long> _answer;
        private readonly ManualResetEventSlim _probed = new(false);
        private int _count;

        public FreeSpaceProbe(long availableBytes) : this(_ => availableBytes)
        {
        }

        public FreeSpaceProbe(Func<string, long> answer) => _answer = answer;

        public int Count => Volatile.Read(ref _count);

        public string? LastPath { get; private set; }

        public long Probe(string path)
        {
            LastPath = path;
            Interlocked.Increment(ref _count);
            _probed.Set();
            return _answer(path);
        }

        /// <summary>Blocks until the monitor has asked about the disk at least once.</summary>
        public void WaitForFirstProbe() =>
            Assert.True(_probed.Wait(EventTimeout), "the monitor never probed the disk");

        /// <summary>True if a further probe arrived inside <paramref name="window"/>.</summary>
        public bool WaitForAnotherProbe(int probesSoFar, TimeSpan window)
        {
            var deadline = DateTime.UtcNow + window;
            while (DateTime.UtcNow < deadline)
            {
                if (Count > probesSoFar)
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return Count > probesSoFar;
        }
    }

    /// <summary>Collects threshold events off the monitor's timer thread.</summary>
    private sealed class EventSink
    {
        private readonly ConcurrentQueue<DiskSpaceEventArgs> _events = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void Handle(object? sender, DiskSpaceEventArgs e)
        {
            _events.Enqueue(e);
            _signal.Release();
        }

        /// <summary>
        /// Events seen so far. Only asserted on once the tick's decision is known to have been
        /// made: either the OTHER sink's <see cref="WaitForOne"/> has returned (the tick's switch
        /// raises at most one event and then breaks), or the other sink has already spent a full
        /// <see cref="SilenceWindow"/> waiting.
        /// </summary>
        public int Count => _events.Count;

        public DiskSpaceEventArgs WaitForOne()
        {
            Assert.True(_signal.Wait(EventTimeout), "expected a disk-space event that never arrived");
            Assert.True(_events.TryDequeue(out var raised));
            return raised!;
        }

        /// <summary>
        /// True if an event arrives inside <paramref name="window"/>. Used for the assertions
        /// that require silence, so that they wait for the event they claim is absent rather
        /// than sampling a counter the instant after the probe was entered.
        /// </summary>
        public bool AnyWithin(TimeSpan window) => _signal.Wait(window);
    }

    /// <summary>
    /// A monitor wired to <paramref name="probe"/>, plus sinks already subscribed to both events.
    /// </summary>
    private static (DiskSpaceMonitor Monitor, EventSink Warnings, EventSink Criticals) Monitored(FreeSpaceProbe probe)
    {
        var monitor = new DiskSpaceMonitor(MonitoredPath, probe.Probe);
        var warnings = new EventSink();
        var criticals = new EventSink();
        monitor.LowSpaceWarning += warnings.Handle;
        monitor.CriticalSpaceReached += criticals.Handle;
        return (monitor, warnings, criticals);
    }

    /// <summary>
    /// Waits for the monitor's first tick to have been taken and acted on, without disturbing
    /// the decision that tick is in the middle of making.
    /// <para>
    /// The probe signals on ENTRY, so its return is not on its own evidence that the tick has
    /// finished — the classification and the event decision happen after the provider returns.
    /// Calling <see cref="DiskSpaceMonitor.StartMonitoring"/> on an already-running monitor is a
    /// bare acquire-and-release of the very lock the tick raises its events under, and its
    /// already-running early return mutates nothing, so it is a barrier with no side effect.
    /// </para>
    /// <para>
    /// <see cref="DiskSpaceMonitor.StopMonitoring"/> takes that same lock and would look like
    /// the natural barrier, but it must NOT be used as one: it resets <c>_warningRaised</c> on
    /// the way through. A stop that won the lock against a tick still on its way to it would
    /// un-suppress that tick, and the suppression test would intermittently observe the very
    /// warning it asserts is absent.
    /// </para>
    /// </summary>
    private static void SettleFirstTick(DiskSpaceMonitor monitor, FreeSpaceProbe probe)
    {
        probe.WaitForFirstProbe();
        monitor.StartMonitoring();
    }

    #endregion

    #region Construction

    [Fact]
    public void Constructor_rejects_a_null_path()
    {
        var thrown = Assert.Throws<ArgumentNullException>(
            () => { _ = new DiskSpaceMonitor(null!, _ => 0L); });

        Assert.Equal("monitoredPath", thrown.ParamName);
    }

    [Fact]
    public void Constructor_rejects_a_null_free_space_provider()
    {
        var thrown = Assert.Throws<ArgumentNullException>(
            () => { _ = new DiskSpaceMonitor(MonitoredPath, null!); });

        Assert.Equal("getAvailableFreeSpace", thrown.ParamName);
    }

    [Fact]
    public void A_new_monitor_is_not_monitoring()
    {
        using var monitor = new DiskSpaceMonitor(MonitoredPath, _ => Mb(1000));

        Assert.False(monitor.IsMonitoring);
    }

    #endregion

    #region The thresholds

    /// <summary>
    /// The gate evaluated before a session starts. Its extra band — below 500 MB but not yet
    /// low — is the only warning a user gets while they can still do something about it
    /// cheaply, so it has to be the band that is actually checked.
    /// </summary>
    [Theory]
    // Nothing left, and the nonsensical answer a failing filesystem can report: both are the
    // hard stop, because the alternative is to keep writing.
    [InlineData(0L, DiskSpaceLevel.Critical)]
    [InlineData(-1L, DiskSpaceLevel.Critical)]
    // 50 MB is Critical's exclusive upper bound: a disk sitting exactly on it is Warning, not
    // Critical. This pair is what pins `<` rather than `<=`.
    [InlineData(50L * 1024 * 1024 - 1, DiskSpaceLevel.Critical)]
    [InlineData(50L * 1024 * 1024, DiskSpaceLevel.Warning)]
    [InlineData(100L * 1024 * 1024 - 1, DiskSpaceLevel.Warning)]
    [InlineData(100L * 1024 * 1024, DiskSpaceLevel.PreSessionWarning)]
    [InlineData(500L * 1024 * 1024 - 1, DiskSpaceLevel.PreSessionWarning)]
    [InlineData(500L * 1024 * 1024, DiskSpaceLevel.Ok)]
    [InlineData(long.MaxValue, DiskSpaceLevel.Ok)]
    public void ClassifyLevel_before_a_session_warns_below_500_MB(long availableBytes, DiskSpaceLevel expected)
    {
        Assert.Equal(expected, DiskSpaceMonitor.ClassifyLevel(availableBytes, preSession: true));
    }

    /// <summary>
    /// The same three constants, evaluated during a session. The 500 MB band is deliberately
    /// NOT a warning here: it would fire on the first tick of most sessions on a working
    /// laptop, and a warning that common is a warning nobody reads by the time it matters.
    /// </summary>
    [Theory]
    [InlineData(0L, DiskSpaceLevel.Critical)]
    [InlineData(-1L, DiskSpaceLevel.Critical)]
    [InlineData(50L * 1024 * 1024 - 1, DiskSpaceLevel.Critical)]
    [InlineData(50L * 1024 * 1024, DiskSpaceLevel.Warning)]
    [InlineData(100L * 1024 * 1024 - 1, DiskSpaceLevel.Warning)]
    // The two rows that differ from the pre-session gate: mid-band space is simply Ok in session.
    [InlineData(100L * 1024 * 1024, DiskSpaceLevel.Ok)]
    [InlineData(500L * 1024 * 1024 - 1, DiskSpaceLevel.Ok)]
    [InlineData(500L * 1024 * 1024, DiskSpaceLevel.Ok)]
    [InlineData(long.MaxValue, DiskSpaceLevel.Ok)]
    public void ClassifyLevel_during_a_session_warns_only_below_100_MB(long availableBytes, DiskSpaceLevel expected)
    {
        Assert.Equal(expected, DiskSpaceMonitor.ClassifyLevel(availableBytes, preSession: false));
    }

    #endregion

    #region The pre-logging gate

    [Fact]
    public void CheckPreLoggingSpace_reports_the_probed_bytes_and_the_pre_session_level()
    {
        using var monitor = new DiskSpaceMonitor(MonitoredPath, _ => Mb(300));

        var result = monitor.CheckPreLoggingSpace();

        Assert.Equal(Mb(300), result.AvailableBytes);
        Assert.Equal(300, result.AvailableMegabytes);
        // 300 MB is inside the pre-session band, so starting a session here warns — where the
        // same 300 MB mid-session would not.
        Assert.Equal(DiskSpaceLevel.PreSessionWarning, result.Level);
    }

    [Fact]
    public void CheckPreLoggingSpace_asks_about_the_monitored_path()
    {
        var probe = new FreeSpaceProbe(Mb(1000));
        using var monitor = new DiskSpaceMonitor(MonitoredPath, probe.Probe);

        monitor.CheckPreLoggingSpace();

        Assert.Equal(MonitoredPath, probe.LastPath);
        Assert.Equal(1, probe.Count);
    }

    /// <summary>
    /// The megabyte figure is integer division, so it truncates toward zero. It is the number
    /// quoted in every disk-space dialog, and it under-reports by up to a megabyte: a disk with
    /// 49.9 MB free is reported as 49.
    /// </summary>
    [Fact]
    public void CheckPreLoggingSpace_truncates_megabytes_toward_zero()
    {
        using var monitor = new DiskSpaceMonitor(MonitoredPath, _ => Mb(300) + 1024 * 1023);

        Assert.Equal(300, monitor.CheckPreLoggingSpace().AvailableMegabytes);
    }

    /// <summary>
    /// A probe that throws — an unreadable mount point, a path with no drive root — is treated
    /// as unlimited space rather than as an empty disk. The choice is deliberate and stated in
    /// the code: the check exists to protect a session, and failing it closed would block
    /// logging outright on a machine whose disk is fine.
    /// </summary>
    [Fact]
    public void CheckPreLoggingSpace_treats_a_failed_probe_as_unlimited_space()
    {
        using var monitor = new DiskSpaceMonitor(
            MonitoredPath, _ => throw new IOException("drive not ready"));

        var result = monitor.CheckPreLoggingSpace();

        Assert.Equal(long.MaxValue, result.AvailableBytes);
        Assert.Equal(DiskSpaceLevel.Ok, result.Level);
    }

    [Fact]
    public void CheckPreLoggingSpace_does_not_start_monitoring()
    {
        using var monitor = new DiskSpaceMonitor(MonitoredPath, _ => Mb(10));

        monitor.CheckPreLoggingSpace();

        Assert.False(monitor.IsMonitoring);
    }

    #endregion

    #region Monitoring an active session

    [Fact]
    public void StartMonitoring_probes_immediately_and_reports_monitoring()
    {
        var probe = new FreeSpaceProbe(Mb(1000));
        using var monitor = new DiskSpaceMonitor(MonitoredPath, probe.Probe);

        monitor.StartMonitoring();

        Assert.True(monitor.IsMonitoring);
        probe.WaitForFirstProbe();
        Assert.Equal(MonitoredPath, probe.LastPath);
    }

    [Fact]
    public void StartMonitoring_warns_below_100_MB()
    {
        var (monitor, warnings, criticals) = Monitored(new FreeSpaceProbe(Mb(80)));
        using (monitor)
        {
            monitor.StartMonitoring();

            var warning = warnings.WaitForOne();

            Assert.Equal(Mb(80), warning.AvailableBytes);
            Assert.Equal(80, warning.AvailableMegabytes);
            Assert.Equal(DiskSpaceLevel.Warning, warning.Level);
            Assert.Equal(0, criticals.Count);
        }
    }

    /// <summary>
    /// Below 50 MB the session is stopped, not warned about. The two events are mutually
    /// exclusive: a critical tick that also raised the warning would put a "logging may be
    /// stopped" dialog in front of the user at the moment it already had been.
    /// </summary>
    [Fact]
    public void StartMonitoring_raises_critical_below_50_MB_and_not_the_warning()
    {
        var (monitor, warnings, criticals) = Monitored(new FreeSpaceProbe(Mb(10)));
        using (monitor)
        {
            monitor.StartMonitoring();

            var critical = criticals.WaitForOne();

            Assert.Equal(Mb(10), critical.AvailableBytes);
            Assert.Equal(10, critical.AvailableMegabytes);
            Assert.Equal(DiskSpaceLevel.Critical, critical.Level);
            Assert.Equal(0, warnings.Count);
        }
    }

    /// <summary>
    /// 300 MB warns at the pre-logging gate and must stay silent once a session is running, or
    /// every long session on an ordinary laptop would open a dialog on its first tick.
    /// <para>
    /// What this pins is the OUTCOME — that mid-band space raises nothing during a session — not
    /// which of the two mechanisms produces it. The tick classifies with <c>preSession: false</c>
    /// AND its switch handles only <c>Critical</c> and <c>Warning</c>, so either alone would keep
    /// this quiet. The test fails if the switch ever grows a <c>PreSessionWarning</c> case, or if
    /// the warning threshold moves up past 300 MB.
    /// </para>
    /// </summary>
    [Fact]
    public void Monitoring_stays_silent_in_the_pre_session_band()
    {
        var probe = new FreeSpaceProbe(Mb(300));
        var (monitor, warnings, criticals) = Monitored(probe);
        using (monitor)
        {
            monitor.StartMonitoring();
            SettleFirstTick(monitor, probe);

            Assert.False(warnings.AnyWithin(SilenceWindow));
            Assert.Equal(0, criticals.Count);
        }
    }

    /// <summary>
    /// The flag the coordinator passes when the pre-logging gate has already shown a low-space
    /// dialog, so the user is not told the same thing twice inside a second.
    /// <para>
    /// The silence is followed by a positive control on the same monitor and the same 80 MB
    /// reading: re-armed with the flag clear, it must warn. Without that, "no warning arrived"
    /// would also be satisfied by a monitor whose events were never wired up at all.
    /// </para>
    /// </summary>
    [Fact]
    public void StartMonitoring_with_the_warning_suppressed_does_not_warn()
    {
        var probe = new FreeSpaceProbe(Mb(80));
        var (monitor, warnings, criticals) = Monitored(probe);
        using (monitor)
        {
            monitor.StartMonitoring(suppressInitialWarning: true);
            SettleFirstTick(monitor, probe);

            Assert.False(warnings.AnyWithin(SilenceWindow));
            Assert.Equal(0, criticals.Count);

            // Same monitor, same 80 MB, flag clear: the warning this test just showed absent is
            // reachable, so its absence above was the suppression and not a dead subscription.
            monitor.StopMonitoring();
            monitor.StartMonitoring(suppressInitialWarning: false);

            Assert.Equal(DiskSpaceLevel.Warning, warnings.WaitForOne().Level);
        }
    }

    /// <summary>
    /// Each call to <see cref="DiskSpaceMonitor.StartMonitoring"/> re-arms the one warning it
    /// allows, so a second session on a still-low disk warns again rather than inheriting the
    /// first session's spent warning.
    /// <para>
    /// The complementary half of that rule — that a single session warns only once, however
    /// many ticks stay in the band — is not asserted here: the monitor's period is 15 seconds,
    /// and there is no seam that lets a test reach the second tick sooner.
    /// </para>
    /// </summary>
    [Fact]
    public void Each_monitoring_session_re_arms_the_warning()
    {
        var (monitor, warnings, _) = Monitored(new FreeSpaceProbe(Mb(80)));
        using (monitor)
        {
            monitor.StartMonitoring();
            Assert.Equal(DiskSpaceLevel.Warning, warnings.WaitForOne().Level);
            monitor.StopMonitoring();

            monitor.StartMonitoring();

            Assert.Equal(DiskSpaceLevel.Warning, warnings.WaitForOne().Level);
        }
    }

    /// <summary>
    /// Starting an already-running monitor is ignored. Without the guard the field would be
    /// overwritten with a second timer and the first would tick on, unreferenced and
    /// undisposable, for the life of the process — and every subsequent tick would arrive twice.
    /// </summary>
    [Fact]
    public void StartMonitoring_while_already_monitoring_does_not_start_a_second_timer()
    {
        var probe = new FreeSpaceProbe(Mb(1000));
        using var monitor = new DiskSpaceMonitor(MonitoredPath, probe.Probe);

        monitor.StartMonitoring();
        probe.WaitForFirstProbe();

        monitor.StartMonitoring();

        // A second timer would be due immediately; the existing one is not due for 15 s.
        Assert.False(probe.WaitForAnotherProbe(probesSoFar: 1, NoFurtherProbeWindow));
    }

    [Fact]
    public void StopMonitoring_before_starting_is_a_no_op()
    {
        using var monitor = new DiskSpaceMonitor(MonitoredPath, _ => Mb(1000));

        monitor.StopMonitoring();

        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public void StopMonitoring_ends_monitoring()
    {
        var probe = new FreeSpaceProbe(Mb(1000));
        using var monitor = new DiskSpaceMonitor(MonitoredPath, probe.Probe);
        monitor.StartMonitoring();
        probe.WaitForFirstProbe();

        monitor.StopMonitoring();

        Assert.False(monitor.IsMonitoring);
    }

    /// <summary>
    /// A probe that throws mid-session must not take the process down. The tick runs on a timer
    /// thread, so an escaping exception is unhandled — it would kill the app during logging,
    /// which is the one thing the disk-space monitor exists to avoid doing.
    /// </summary>
    [Fact]
    public void A_failed_probe_during_monitoring_is_swallowed()
    {
        var probe = new FreeSpaceProbe(_ => throw new IOException("drive went away"));
        var (monitor, warnings, criticals) = Monitored(probe);
        using (monitor)
        {
            monitor.StartMonitoring();
            SettleFirstTick(monitor, probe);

            Assert.False(warnings.AnyWithin(SilenceWindow));
            Assert.Equal(0, criticals.Count);
        }
    }

    #endregion

    #region Pinned quirks

    /// <summary>
    /// PINNED, NOT ENDORSED. Reaching the critical threshold stops the timer but leaves the
    /// field set, so the monitor goes on reporting <c>IsMonitoring == true</c> while it is in
    /// fact dead. Asserted as it behaves today; see issue #208.
    /// </summary>
    [Fact]
    public void IsMonitoring_still_reports_true_after_the_critical_hard_stop()
    {
        var (monitor, _, criticals) = Monitored(new FreeSpaceProbe(Mb(10)));
        using (monitor)
        {
            monitor.StartMonitoring();
            criticals.WaitForOne();

            Assert.True(monitor.IsMonitoring);
        }
    }

    /// <summary>
    /// PINNED, NOT ENDORSED. The consequence of the above: because the field is still set,
    /// <see cref="DiskSpaceMonitor.StartMonitoring"/> takes its already-running early return and
    /// silently does nothing. A caller that trusted <c>IsMonitoring</c> and tried to resume
    /// would get no monitoring at all; only a <c>StopMonitoring</c> first makes it restartable.
    /// </summary>
    [Fact]
    public void StartMonitoring_after_the_critical_hard_stop_does_not_resume_probing()
    {
        var probe = new FreeSpaceProbe(Mb(10));
        var (monitor, _, criticals) = Monitored(probe);
        using (monitor)
        {
            monitor.StartMonitoring();
            criticals.WaitForOne();
            var probesAtHardStop = probe.Count;

            monitor.StartMonitoring();

            Assert.False(probe.WaitForAnotherProbe(probesAtHardStop, NoFurtherProbeWindow));
        }
    }

    [Fact]
    public void Dispose_stops_monitoring()
    {
        var probe = new FreeSpaceProbe(Mb(1000));
        var monitor = new DiskSpaceMonitor(MonitoredPath, probe.Probe);
        monitor.StartMonitoring();
        probe.WaitForFirstProbe();

        monitor.Dispose();

        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public void Dispose_twice_is_safe()
    {
        var monitor = new DiskSpaceMonitor(MonitoredPath, _ => Mb(1000));

        monitor.Dispose();
        monitor.Dispose();

        Assert.False(monitor.IsMonitoring);
    }

    /// <summary>
    /// PINNED, NOT ENDORSED. Nothing guards <see cref="DiskSpaceMonitor.StartMonitoring"/>
    /// against a disposed instance, so it starts a fresh timer — and since <c>Dispose</c> has
    /// already latched itself, a second <c>Dispose</c> returns without stopping it. The timer
    /// then ticks every 15 seconds for the life of the process. Asserted as it behaves today;
    /// see issue #208.
    /// </summary>
    [Fact]
    public void StartMonitoring_after_Dispose_starts_a_timer_that_Dispose_will_not_stop()
    {
        var probe = new FreeSpaceProbe(Mb(1000));
        var monitor = new DiskSpaceMonitor(MonitoredPath, probe.Probe);
        monitor.Dispose();

        monitor.StartMonitoring();
        probe.WaitForFirstProbe();

        Assert.True(monitor.IsMonitoring);
        monitor.Dispose();
        Assert.True(monitor.IsMonitoring);

        // Not part of the assertion: stop the resurrected timer by hand so this test does not
        // leave one ticking for the rest of the run.
        monitor.StopMonitoring();
    }

    #endregion

    #region The real free-space provider

    /// <summary>
    /// The public constructor's default provider, which every production caller gets: resolve
    /// the path's drive root and read its available space. Exercised against the temp directory
    /// — read-only, and nowhere near the app's data directory — because the provider is
    /// otherwise reachable only through <c>DriveInfo</c> and would go entirely unexercised.
    /// </summary>
    [Fact]
    public void The_default_provider_reads_the_real_drive()
    {
        using var monitor = new DiskSpaceMonitor(Path.GetTempPath());

        var result = monitor.CheckPreLoggingSpace();

        // A real reading, not the long.MaxValue the catch block substitutes when the probe
        // throws. Zero is deliberately inside the accepted range: a correctly queried full
        // volume reports zero, and these tests classify zero as Critical rather than as an
        // error, so requiring a positive number would fail a CI box with a full temp volume
        // even though the provider had behaved perfectly.
        Assert.InRange(result.AvailableBytes, 0, long.MaxValue - 1);
    }

    #endregion
}
