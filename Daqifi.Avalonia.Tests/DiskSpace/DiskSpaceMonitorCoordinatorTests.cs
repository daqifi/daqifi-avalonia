using Daqifi.Desktop.DiskSpace;
using Xunit;

namespace Daqifi.Avalonia.Tests.DiskSpace;

/// <summary>
/// Characterisation tests for <see cref="DiskSpaceMonitorCoordinator"/> — the class that turns
/// <see cref="DiskSpaceMonitor"/>'s classification into consequences: whether the logging toggle
/// is allowed to turn on, which dialog the user sees, and whether a running session is stopped.
///
/// <para>
/// <see cref="DiskSpaceMonitorTests"/> already pins the monitor's thresholds. Nothing pinned what
/// happens next, and that half is where the user's data is decided: a critical reading that did
/// not reach <see cref="IDiskSpaceMonitorHost.StopLogging"/> keeps a session writing into a full
/// volume, and a pre-session level mapped to the wrong decision either blocks logging on a disk
/// with room or starts it on one without.
/// </para>
///
/// <para>
/// The monitor and host are hand-rolled doubles, so every test runs synchronously with no timer
/// and no dispatcher.
/// </para>
/// </summary>
public class DiskSpaceMonitorCoordinatorTests
{
    private static long Mb(long megabytes) => megabytes * 1024 * 1024;

    #region The pre-logging gate

    /// <summary>
    /// The level → decision table, including the dialog each one shows. The decisions are
    /// compared by reference because <see cref="DiskSpaceStartDecision"/> exposes exactly three
    /// singletons, and <c>DaqifiViewModel</c> reads <c>CanStart</c> to revert the toggle and
    /// <c>SuppressInitialWarning</c> to avoid warning twice. <c>Warning</c> is in the table on
    /// purpose: the pre-session check can return it, and it must warn, not pass silently.
    /// </summary>
    [Theory]
    [InlineData(DiskSpaceLevel.Ok, "Allowed", null)]
    [InlineData(DiskSpaceLevel.PreSessionWarning, "AllowedWithWarning", "Low Disk Space Warning")]
    [InlineData(DiskSpaceLevel.Warning, "AllowedWithWarning", "Low Disk Space Warning")]
    [InlineData(DiskSpaceLevel.Critical, "Blocked", "Cannot Start Logging")]
    public void EvaluateStartLogging_maps_each_level_to_its_decision_and_dialog(
        DiskSpaceLevel level, string expectedDecision, string? expectedTitle)
    {
        var (coordinator, monitor, host, _) = Build();
        monitor.PreLoggingResult = new DiskSpaceCheckResult(Mb(1000), level);

        var decision = coordinator.EvaluateStartLogging();

        var expected = expectedDecision switch
        {
            "Allowed" => DiskSpaceStartDecision.Allowed,
            "AllowedWithWarning" => DiskSpaceStartDecision.AllowedWithWarning,
            _ => DiskSpaceStartDecision.Blocked,
        };
        Assert.Same(expected, decision);
        string[] expectedCalls = expectedTitle is null ? [] : [$"Dialog:{expectedTitle}"];
        Assert.Equal(expectedCalls, host.Titles);
    }

    /// <summary>
    /// The pre-session gate is advisory only: a critical reading refuses the start but must not
    /// call <see cref="IDiskSpaceMonitorHost.StopLogging"/> — nothing is running yet, and the
    /// host's <c>StopLogging</c> writes <c>IsLogging = false</c> from inside the setter that is
    /// asking. It must not start monitoring either; the caller does that once it has decided.
    /// </summary>
    [Fact]
    public void EvaluateStartLogging_on_a_critical_disk_neither_stops_logging_nor_starts_monitoring()
    {
        var (coordinator, monitor, host, _) = Build();
        monitor.PreLoggingResult = new DiskSpaceCheckResult(Mb(10), DiskSpaceLevel.Critical);

        coordinator.EvaluateStartLogging();

        Assert.Equal(0, host.StopLoggingCalls);
        Assert.Empty(monitor.StartCalls);
    }

    /// <summary>
    /// The dialogs quote whole megabytes, truncated rather than rounded: 49.9 MB free reads as
    /// "49 MB". Pinned because it is the only number the user is shown and it is below the
    /// 50 MB threshold that produced the block, so a rounding change would show "50 MB" on a
    /// dialog refusing to start because space is under 50 MB.
    /// </summary>
    [Fact]
    public void EvaluateStartLogging_quotes_available_space_in_truncated_megabytes()
    {
        var (coordinator, monitor, host, _) = Build();
        monitor.PreLoggingResult = new DiskSpaceCheckResult(Mb(50) - 1, DiskSpaceLevel.Critical);

        coordinator.EvaluateStartLogging();

        var message = Assert.Single(host.Messages);
        Assert.StartsWith("Only 49 MB of disk space remaining.", message);
    }

    #endregion

    #region Events raised while logging

    /// <summary>
    /// The critical threshold during a session stops logging, logs a warning, and then tells the
    /// user — in that order. Stopping first matters: the dialog is fire-and-forget, and the
    /// session has to stop writing whether or not a dialog is ever shown.
    /// </summary>
    [Fact]
    public void A_critical_reading_while_logging_stops_logging_before_showing_the_dialog()
    {
        var (coordinator, monitor, host, logger) = Build();

        monitor.RaiseCritical(Mb(40));

        Assert.Equal(["StopLogging", "Dialog:Logging Stopped — Disk Space Critical"], host.Titles);
        Assert.Contains("40 MB", Assert.Single(host.Messages));
        Assert.Equal(["Disk space critical (40 MB) — automatically stopping logging"], logger.Warnings);
        GC.KeepAlive(coordinator);
    }

    /// <summary>
    /// If the host's stop throws, the exception leaves the handler and the dialog is not shown.
    /// That is today's behaviour, pinned rather than endorsed: the monitor's tick catches it, and
    /// production <c>StopLogging</c> only posts to the dispatcher, so it does not reach the user.
    /// A change that reorders or wraps these calls should have to change this test on purpose.
    /// </summary>
    [Fact]
    public void A_critical_reading_whose_stop_throws_shows_no_dialog()
    {
        var (coordinator, monitor, host, _) = Build();
        host.StopLoggingThrows = new InvalidOperationException("stop refused");

        var thrown = Assert.Throws<InvalidOperationException>(() => monitor.RaiseCritical(Mb(40)));

        Assert.Equal("stop refused", thrown.Message);
        Assert.Equal(["StopLogging"], host.Titles);
        GC.KeepAlive(coordinator);
    }

    /// <summary>
    /// A low-space warning during a session is a dialog and nothing else: logging carries on, and
    /// the message names the 50 MB hard-stop threshold the monitor enforces.
    /// </summary>
    [Fact]
    public void A_low_space_warning_while_logging_shows_a_dialog_and_does_not_stop_logging()
    {
        var (coordinator, monitor, host, logger) = Build();

        monitor.RaiseLow(Mb(90));

        Assert.Equal(["Dialog:Low Disk Space Warning"], host.Titles);
        var message = Assert.Single(host.Messages);
        Assert.StartsWith("Only 90 MB of disk space remaining.", message);
        Assert.Contains("below 50 MB", message);
        Assert.Empty(logger.Warnings);
        GC.KeepAlive(coordinator);
    }

    #endregion

    #region Forwarding and lifetime

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartMonitoring_passes_the_suppress_flag_through(bool suppress)
    {
        var (coordinator, monitor, _, _) = Build();

        coordinator.StartMonitoring(suppress);

        Assert.Equal([suppress], monitor.StartCalls);
    }

    [Fact]
    public void StopMonitoring_stops_the_monitor()
    {
        var (coordinator, monitor, _, _) = Build();

        coordinator.StopMonitoring();

        Assert.Equal(1, monitor.StopCalls);
    }

    /// <summary>
    /// After <see cref="DiskSpaceMonitorCoordinator.Dispose"/> the coordinator is off both events,
    /// so a tick already in flight on the monitor's timer thread cannot stop a session or open a
    /// dialog on a torn-down view model. The monitor itself is disposed exactly once, however many
    /// times the coordinator is.
    /// </summary>
    [Fact]
    public void Dispose_unsubscribes_from_both_events_and_disposes_the_monitor_once()
    {
        var (coordinator, monitor, host, _) = Build();

        coordinator.Dispose();
        coordinator.Dispose();
        monitor.RaiseCritical(Mb(10));
        monitor.RaiseLow(Mb(90));

        Assert.Equal(1, monitor.DisposeCalls);
        Assert.Empty(host.Titles);
        Assert.Equal(0, monitor.CriticalSubscribers);
        Assert.Equal(0, monitor.LowSubscribers);
    }

    #endregion

    #region Test doubles

    private static (DiskSpaceMonitorCoordinator, FakeMonitor, RecordingHost, RecordingAppLogger) Build()
    {
        var monitor = new FakeMonitor();
        var host = new RecordingHost();
        var logger = new RecordingAppLogger();
        return (new DiskSpaceMonitorCoordinator(host, monitor, logger), monitor, host, logger);
    }

    private sealed class FakeMonitor : IDiskSpaceMonitor
    {
        private EventHandler<DiskSpaceEventArgs>? _low;
        private EventHandler<DiskSpaceEventArgs>? _critical;

        public DiskSpaceCheckResult PreLoggingResult { get; set; } = new(Mb(1000), DiskSpaceLevel.Ok);
        public List<bool> StartCalls { get; } = [];
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int LowSubscribers => _low?.GetInvocationList().Length ?? 0;
        public int CriticalSubscribers => _critical?.GetInvocationList().Length ?? 0;

        public event EventHandler<DiskSpaceEventArgs> LowSpaceWarning
        {
            add => _low += value;
            remove => _low -= value;
        }

        public event EventHandler<DiskSpaceEventArgs> CriticalSpaceReached
        {
            add => _critical += value;
            remove => _critical -= value;
        }

        public bool IsMonitoring => false;

        public DiskSpaceCheckResult CheckPreLoggingSpace() => PreLoggingResult;

        public void StartMonitoring(bool suppressInitialWarning = false) => StartCalls.Add(suppressInitialWarning);

        public void StopMonitoring() => StopCalls++;

        public void Dispose() => DisposeCalls++;

        public void RaiseLow(long bytes) => _low?.Invoke(this, new DiskSpaceEventArgs(bytes, DiskSpaceLevel.Warning));

        public void RaiseCritical(long bytes) => _critical?.Invoke(this, new DiskSpaceEventArgs(bytes, DiskSpaceLevel.Critical));
    }

    /// <summary>
    /// Records host calls in order. Dialog tasks never complete, which is what the production
    /// dialog looks like until the user clicks OK — so any path that awaited one would hang here.
    /// </summary>
    private sealed class RecordingHost : IDiskSpaceMonitorHost
    {
        public List<string> Titles { get; } = [];
        public List<string> Messages { get; } = [];
        public int StopLoggingCalls { get; private set; }
        public Exception? StopLoggingThrows { get; set; }

        public void StopLogging()
        {
            StopLoggingCalls++;
            Titles.Add("StopLogging");
            if (StopLoggingThrows is not null)
            {
                throw StopLoggingThrows;
            }
        }

        public Task ShowDiskSpaceMessageAsync(string title, string message)
        {
            Titles.Add($"Dialog:{title}");
            Messages.Add(message);
            return new TaskCompletionSource().Task;
        }
    }

    #endregion
}
