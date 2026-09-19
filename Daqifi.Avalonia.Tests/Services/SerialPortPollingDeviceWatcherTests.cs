using System.Collections.Concurrent;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Services.DeviceWatcher;
using Xunit;

namespace Daqifi.Avalonia.Tests.Services;

/// <summary>
/// Characterisation tests for <see cref="SerialPortPollingDeviceWatcher"/>, the macOS and Linux
/// hotplug backend: the only thing on those platforms that notices a USB device being unplugged.
///
/// <para>
/// Both of its wrong answers cost the user something. A missed removal leaves a device showing
/// as connected after it has gone, so a logging session keeps "running" with nothing arriving. A
/// spurious removal sends <c>ConnectionManager.CheckIfSerialDeviceWasRemoved</c> looking for a
/// device to tear down. The class guards against the second with a generation counter, a
/// reentrancy gate and a keep-the-last-snapshot rule on enumeration failure, and none of those
/// had a test.
/// </para>
///
/// <para>
/// <b>No sleeps.</b> Every test drives the real timer and synchronises on the port provider
/// itself. A poll calls the provider only after passing the reentrancy gate, and the gate is
/// released in the poll's <c>finally</c> — after the event has been raised. So once the provider
/// has been called for the <em>next</em> poll, the previous poll has finished completely,
/// event included. "No event was raised" is asserted only after that has been observed, never
/// after a fixed delay. The one wall-clock value here is the failure timeout on that wait.
/// </para>
/// </summary>
public class SerialPortPollingDeviceWatcherTests
{
    private const string PortA = "/dev/cu.usbmodem1101";
    private const string PortB = "/dev/cu.usbmodem1201";
    private const string PortC = "/dev/cu.usbmodem1301";

    /// <summary>Short enough that polls run back to back; the provider is the clock.</summary>
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(1);

    /// <summary>A cadence no test can reach, for tests that must see no timer poll at all.</summary>
    private static readonly TimeSpan NeverPoll = TimeSpan.FromHours(1);

    #region Test doubles

    /// <summary>
    /// The injectable port table. Counts every read, can be made to throw, and can hold exactly
    /// one read open until the test releases it.
    /// </summary>
    private sealed class PortTable
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

        private readonly object _gate = new();
        private string[]? _current;
        private int _reads;
        private (ManualResetEventSlim Entered, ManualResetEventSlim Release, string[] Answer)? _heldRead;

        public PortTable(params string[] ports) => _current = ports;

        public int Reads
        {
            get { lock (_gate) { return _reads; } }
        }

        /// <summary>What every later read returns.</summary>
        public void Set(params string[] ports)
        {
            lock (_gate) { _current = ports; }
        }

        /// <summary>Every later read throws, as a permissions failure on <c>/dev</c> would.</summary>
        public void Fail()
        {
            lock (_gate) { _current = null; }
        }

        /// <summary>
        /// The next read blocks until <see cref="ManualResetEventSlim.Set"/> is called on the
        /// returned release handle, then answers <paramref name="answer"/>.
        /// </summary>
        public (ManualResetEventSlim Entered, ManualResetEventSlim Release) HoldNextRead(params string[] answer)
        {
            var held = (new ManualResetEventSlim(false), new ManualResetEventSlim(false), answer);
            lock (_gate) { _heldRead = held; }
            return (held.Item1, held.Item2);
        }

        public string[] Read()
        {
            (ManualResetEventSlim Entered, ManualResetEventSlim Release, string[] Answer)? held;
            string[]? answer;
            lock (_gate)
            {
                // Counted BEFORE the snapshot is taken, so a read that shows up in the count
                // after a Set() is guaranteed to see that Set().
                _reads++;
                Monitor.PulseAll(_gate);
                held = _heldRead;
                _heldRead = null;
                answer = _current;
            }

            if (held is { } h)
            {
                h.Entered.Set();
                h.Release.Wait();
                return h.Answer;
            }

            return answer ?? throw new UnauthorizedAccessException("Access to the path '/dev' is denied.");
        }

        /// <summary>
        /// Blocks until two more reads have started than had started when this was called. The
        /// first of them may be a poll that was already running; the second can only begin once
        /// that one has finished (the reentrancy gate is released after the event is raised), so
        /// on return every poll that could have seen the state before this call has completed.
        /// </summary>
        public void WaitForTwoMorePolls()
        {
            lock (_gate)
            {
                var target = _reads + 2;
                var deadline = DateTime.UtcNow + WaitTimeout;
                while (_reads < target)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
                    {
                        if (_reads >= target) { break; }
                        throw new TimeoutException(
                            $"The watcher stopped polling: {_reads} reads, waiting for {target}.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Records what the watcher logs. Minimal and private on purpose; it can fold into the shared
    /// <c>RecordingAppLogger</c> that PR #401 introduces once that merges.
    /// </summary>
    private sealed class RecordingLogger : IAppLogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ConcurrentQueue<(Exception Ex, string Message)> Errors { get; } = new();

        public void Information(string message) { }
        public void Warning(string message) => Warnings.Enqueue(message);
        public void Warning(Exception ex, string message) => Warnings.Enqueue(message);
        public void Error(string message) => Errors.Enqueue((new Exception(message), message));
        public void Error(Exception ex, string message) => Errors.Enqueue((ex, message));
        public void AddBreadcrumb(string category, string message, BreadcrumbLevel level = BreadcrumbLevel.Info) { }
        public void SetDeviceContext(string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }
        public void ClearDeviceContext() { }
        public void Shutdown() { }
    }

    private sealed class Harness : IDisposable
    {
        private int _removals;

        public Harness(PortTable table, TimeSpan? pollInterval = null)
        {
            Table = table;
            Watcher = new SerialPortPollingDeviceWatcher(Logger, pollInterval ?? FastPoll, table.Read);
            Watcher.DeviceRemoved += (sender, _) =>
            {
                Assert.Same(Watcher, sender);
                Interlocked.Increment(ref _removals);
            };
        }

        public PortTable Table { get; }
        public RecordingLogger Logger { get; } = new();
        public SerialPortPollingDeviceWatcher Watcher { get; }
        public int Removals => Volatile.Read(ref _removals);

        public void Dispose() => Watcher.Dispose();
    }

    #endregion

    #region What counts as a removal

    [Fact]
    public void A_port_that_disappears_raises_DeviceRemoved_once_not_on_every_later_poll()
    {
        using var h = new Harness(new PortTable(PortA, PortB));
        h.Watcher.Start();
        h.Table.WaitForTwoMorePolls();
        Assert.Equal(0, h.Removals);

        h.Table.Set(PortA);
        h.Table.WaitForTwoMorePolls();
        h.Table.WaitForTwoMorePolls();

        // The reading that dropped PortB becomes the new baseline, so the polls after it see
        // no change and stay quiet.
        Assert.Equal(1, h.Removals);
    }

    [Fact]
    public void A_port_that_appears_is_not_a_removal()
    {
        using var h = new Harness(new PortTable(PortA));
        h.Watcher.Start();

        h.Table.Set(PortA, PortB);
        h.Table.WaitForTwoMorePolls();

        Assert.Equal(0, h.Removals);
    }

    [Fact]
    public void One_port_swapped_for_another_is_a_removal_even_though_the_count_is_unchanged()
    {
        using var h = new Harness(new PortTable(PortA, PortB));
        h.Watcher.Start();

        h.Table.Set(PortA, PortC);
        h.Table.WaitForTwoMorePolls();

        Assert.Equal(1, h.Removals);
    }

    [Fact]
    public void Port_names_are_compared_ignoring_case()
    {
        using var h = new Harness(new PortTable(PortA));
        h.Watcher.Start();

        h.Table.Set(PortA.ToUpperInvariant());
        h.Table.WaitForTwoMorePolls();

        Assert.Equal(0, h.Removals);
    }

    /// <summary>
    /// The baseline is taken in <c>Start</c>, not by the first poll — which is what lets the
    /// very first poll already report a removal instead of spending itself on a baseline.
    /// </summary>
    [Fact]
    public void Start_reads_the_baseline_synchronously_before_any_poll()
    {
        var table = new PortTable(PortA, PortB);
        using var h = new Harness(table, NeverPoll);

        h.Watcher.Start();

        // Start read the table synchronously, before any timer poll.
        Assert.Equal(1, table.Reads);
    }

    #endregion

    #region Enumeration failures

    [Fact]
    public void A_failed_read_is_not_mistaken_for_every_device_being_unplugged()
    {
        using var h = new Harness(new PortTable(PortA, PortB));
        h.Watcher.Start();

        h.Table.Fail();
        h.Table.WaitForTwoMorePolls();
        Assert.Equal(0, h.Removals);

        // The snapshot from before the outage is still the baseline, so a removal that happened
        // during it is reported on the first good read afterwards.
        h.Table.Set(PortA);
        h.Table.WaitForTwoMorePolls();
        Assert.Equal(1, h.Removals);
    }

    [Fact]
    public void An_enumeration_outage_is_logged_once_and_a_second_outage_is_logged_again()
    {
        using var h = new Harness(new PortTable(PortA));
        h.Watcher.Start();

        h.Table.Fail();
        h.Table.WaitForTwoMorePolls();
        h.Table.WaitForTwoMorePolls();
        Assert.Single(h.Logger.Warnings);

        h.Table.Set(PortA);
        h.Table.WaitForTwoMorePolls();
        h.Table.Fail();
        h.Table.WaitForTwoMorePolls();

        Assert.Equal(2, h.Logger.Warnings.Count);
        Assert.All(h.Logger.Warnings, w => Assert.Contains("enumerate serial ports", w));
        Assert.Empty(h.Logger.Errors);
    }

    /// <summary>
    /// Current behaviour, pinned: with no baseline there is nothing to compare against, so the
    /// first good read only establishes one. A device unplugged while the table was unreadable
    /// from the start is therefore never reported — which is the only answer available, since
    /// the watcher never knew it was there.
    /// </summary>
    [Fact]
    public void When_the_baseline_read_fails_the_first_good_read_becomes_the_baseline_silently()
    {
        var table = new PortTable();
        table.Fail();
        using var h = new Harness(table);

        h.Watcher.Start();
        Assert.Single(h.Logger.Warnings);

        table.Set(PortA);
        table.WaitForTwoMorePolls();
        Assert.Equal(0, h.Removals);

        table.Set();
        table.WaitForTwoMorePolls();
        Assert.Equal(1, h.Removals);
    }

    #endregion

    #region Lifecycle

    [Fact]
    public void Ports_removed_while_stopped_are_not_reported_after_a_restart()
    {
        using var h = new Harness(new PortTable(PortA, PortB));
        h.Watcher.Start();
        h.Table.WaitForTwoMorePolls();

        h.Watcher.Stop();
        h.Table.Set(PortA);
        h.Watcher.Start();
        h.Table.WaitForTwoMorePolls();

        Assert.Equal(0, h.Removals);
    }

    /// <summary>
    /// The race the generation counter exists for. A poll is mid-read when the watcher is
    /// stopped and restarted, the restart takes a fresh baseline, and then the stale read
    /// returns. Its reading belongs to the old cycle and must be thrown away.
    ///
    /// <para>
    /// The stale reading here is a SUPERSET of the new baseline on purpose. A stale reading that
    /// drops ports would be caught twice — once by the in-lock generation check and again by the
    /// re-check just before the event is raised — so removing either check alone could not fail
    /// this test. A superset raises nothing on its own; what it does, if it is not discarded, is
    /// become the baseline, and the next genuine poll then reports the extra port as "removed".
    /// That is a spurious removal only the in-lock check prevents. (The re-check before the
    /// event covers a Stop landing between the lock and the raise, a window a test cannot hold
    /// open without a hook in the production code, so it is not pinned here.)
    /// </para>
    /// </summary>
    [Fact]
    public void A_poll_still_reading_across_a_Stop_and_Start_cannot_poison_the_new_baseline()
    {
        using var h = new Harness(new PortTable(PortA, PortB));
        h.Watcher.Start();

        var (entered, release) = h.Table.HoldNextRead(PortA, PortB, PortC);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "No poll picked up the held read.");

        h.Watcher.Stop();
        h.Watcher.Start();
        release.Set();

        h.Table.WaitForTwoMorePolls();
        h.Table.WaitForTwoMorePolls();
        Assert.Equal(0, h.Removals);

        // And the restarted cycle is live, not wedged behind the stale poll.
        h.Table.Set(PortA);
        h.Table.WaitForTwoMorePolls();
        Assert.Equal(1, h.Removals);
    }

    [Fact]
    public void A_second_Start_is_a_no_op()
    {
        var table = new PortTable(PortA);
        using var h = new Harness(table, NeverPoll);

        h.Watcher.Start();
        h.Watcher.Start();

        Assert.Equal(1, table.Reads);
    }

    [Fact]
    public void Stop_before_Start_and_a_double_Dispose_are_harmless()
    {
        var h = new Harness(new PortTable(PortA), NeverPoll);

        h.Watcher.Stop();
        h.Watcher.Dispose();
        h.Watcher.Dispose();
    }

    [Fact]
    public void Start_after_Dispose_throws()
    {
        var table = new PortTable(PortA);
        var h = new Harness(table, NeverPoll);
        h.Watcher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => h.Watcher.Start());
        Assert.Equal(0, table.Reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_poll_interval_that_is_not_positive_is_rejected(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SerialPortPollingDeviceWatcher(
                new RecordingLogger(), TimeSpan.FromMilliseconds(milliseconds), () => []));

        Assert.Equal("pollInterval", ex.ParamName);
    }

    #endregion

    #region A throwing subscriber

    /// <summary>
    /// An exception escaping a timer callback terminates the process. A subscriber that throws
    /// is logged at Error and the watcher keeps polling — including reporting the next removal.
    /// </summary>
    [Fact]
    public void A_throwing_subscriber_is_logged_and_polling_continues()
    {
        using var h = new Harness(new PortTable(PortA, PortB, PortC));
        var throwOnce = 1;
        h.Watcher.DeviceRemoved += (_, _) =>
        {
            if (Interlocked.Exchange(ref throwOnce, 0) == 1)
            {
                throw new InvalidOperationException("subscriber failed");
            }
        };
        h.Watcher.Start();

        h.Table.Set(PortA, PortB);
        h.Table.WaitForTwoMorePolls();

        var (ex, message) = Assert.Single(h.Logger.Errors);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("Serial port hotplug poll failed.", message);

        h.Table.Set(PortA);
        h.Table.WaitForTwoMorePolls();
        Assert.Equal(2, h.Removals);
    }

    #endregion
}
