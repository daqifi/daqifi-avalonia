using Daqifi.Core.Communication.Transport;
using Daqifi.Desktop.Device.Firmware;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Characterization tests for <see cref="BootloaderHoldService"/> — the component that decides whether a
/// firmware flash can proceed at all. It had no direct coverage before this file.
///
/// The hold exists because Windows USB selective-suspend wedges a sitting PIC32 bootloader
/// (daqifi-nyquist-firmware#568): the service opens the exclusive HID handle and keeps an interrupt-IN
/// read permanently pending so the link never goes idle. Three of its behaviours are load-bearing and
/// none of them is obvious from the outside:
///
/// <list type="bullet">
/// <item><c>ReleaseAsync</c> is the real watcher-to-flasher transition, and it must actually close the
/// handle. <c>BootloaderWatcher.PrepareFlashAsync</c> calls it on the target hold so the flasher's own
/// transport can open that path; left open, the exclusive handle locks the flasher out of the very
/// device it was asked to flash.</item>
/// <item><c>HoldDropped</c> must fire when the device vanishes from under the read, and must NOT fire on
/// a stop we asked for. A spurious drop tells the watcher a bootloader disappeared; a missed one leaves a
/// dead hold advertising itself as live.</item>
/// <item>Disposal must close the owned transport deterministically. Each hold news up its own transport
/// (<c>App.cs</c> wires the watcher's factory that way, deliberately NOT the flasher's DI singleton), so
/// nothing else will ever close it.</item>
/// </list>
///
/// <para>
/// <b><c>PauseForFlashAsync</c> has no production caller</b> — verified across every tracked source file;
/// only the interface, its implementation and tests mention it. Its own doc comments describe a
/// warm-handle hand-off in which the flasher reuses the held transport, and that is not the design the
/// app ships: each hold owns a separate transport and the watcher releases rather than pauses. The pause
/// tests below therefore pin the method's API contract as it stands today and make no claim about a live
/// flash path; the surrounding production comments are recorded as stale, not endorsed. Raised by Qodo on
/// PR #416 and filed as issue #417.
/// </para>
///
/// Everything here runs against a fake <see cref="IHidTransport"/> — no hardware, no HID stack.
/// </summary>
public class BootloaderHoldServiceTests
{
    private const string DevicePath = "hid://bootloader-a";
    private const string DeviceName = "DAQiFi Bootloader";

    /// <summary>Short enough that several keep-alive reads happen inside a test, long enough not to spin.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(20);

    private readonly FakeHidTransport _transport = new();
    private readonly RecordingAppLogger _logger = new();

    private BootloaderHoldService CreateService(string? devicePath = DevicePath) =>
        new(_transport, _logger, ReadTimeout, devicePath, DeviceName);

    #region Establishing the hold

    /// <summary>
    /// With a device path the hold targets that exact bootloader by path. Identical bootloaders share
    /// VID/PID and carry no serial, so the path is the only thing that tells two of them apart — a
    /// VID/PID connect here would hold the wrong device.
    /// </summary>
    [Fact]
    public async Task A_hold_with_a_device_path_connects_by_path_not_by_vid_pid()
    {
        using var service = CreateService();

        await service.BeginHoldAsync();

        Assert.Equal([DevicePath], _transport.ConnectByPathCalls);
        Assert.Empty(_transport.ConnectCalls);
        Assert.True(service.IsHolding);
    }

    /// <summary>
    /// With no device path the hold falls back to the first VID/PID match — the single-device behaviour.
    /// The identifiers are the PIC32 HID bootloader's, taken from Core's finder defaults.
    /// </summary>
    [Fact]
    public async Task A_hold_with_no_device_path_connects_to_the_first_bootloader_by_vid_pid()
    {
        using var service = CreateService(devicePath: null);

        await service.BeginHoldAsync();

        Assert.Empty(_transport.ConnectByPathCalls);
        var connect = Assert.Single(_transport.ConnectCalls);
        Assert.Equal(0x04D8, connect.VendorId);
        Assert.Equal(0x003C, connect.ProductId);
        Assert.Null(connect.SerialNumber);
        Assert.True(service.IsHolding);
    }

    /// <summary>
    /// Once held, the keep-alive keeps re-issuing reads. A single read that times out and is never
    /// re-issued would leave the link idle, which is exactly the state that wedges the bootloader.
    /// </summary>
    [Fact]
    public async Task Holding_keeps_re_issuing_the_keep_alive_read()
    {
        using var service = CreateService();

        await service.BeginHoldAsync();

        await _transport.WaitForReadsAsync(3);
        Assert.True(_transport.ReadCount >= 3);
    }

    /// <summary>
    /// The hold is best-effort: a device that is not there (already flashed back into application mode,
    /// or the open refused) leaves the service un-held and logs, rather than throwing into the flash path.
    /// The flasher's own connect-and-retry still covers a device that shows up later.
    /// </summary>
    [Fact]
    public async Task A_refused_open_leaves_the_service_un_held_instead_of_throwing()
    {
        _transport.ConnectThrows = new IOException("no such device");
        using var service = CreateService();

        await service.BeginHoldAsync();

        Assert.False(service.IsHolding);
        Assert.Equal(0, _transport.ReadCount);
        Assert.Contains(_logger.Warnings, w => w.Contains("Could not open the HID bootloader"));
    }

    /// <summary>
    /// Beginning a hold that is already established and live is a no-op — it must not open a second
    /// handle or start a second keep-alive loop over the same transport.
    /// </summary>
    [Fact]
    public async Task Beginning_a_live_hold_again_does_not_reconnect()
    {
        using var service = CreateService();
        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(1);

        await service.BeginHoldAsync();

        Assert.Equal([DevicePath], _transport.ConnectByPathCalls);
        Assert.Equal(0, _transport.DisconnectCount);
        Assert.True(service.IsHolding);
    }

    /// <summary>
    /// The stale-hold case, and the reason the no-op above is conditional. If the keep-alive loop exited
    /// on a device error, <c>IsHolding</c> is still true but nothing is keeping the link awake. Beginning
    /// again must tear that down and re-establish — not no-op and leave the device exposed to suspend.
    /// </summary>
    [Fact]
    public async Task Beginning_a_hold_whose_keep_alive_died_tears_it_down_and_re_establishes()
    {
        _transport.FailReadNumber = 1;
        using var service = CreateService();
        await service.BeginHoldAsync();
        await WaitUntilAsync(() => _logger.Warnings.Any(w => w.Contains("keep-alive read failed")));
        _transport.FailReadNumber = null;

        // Re-request until it takes. While the dead loop's task has not yet finished unwinding,
        // BeginHoldAsync is a pure no-op that touches the transport not at all, so repeating the call
        // cannot inflate any count below — it only removes a race on when that task observably completes.
        await WaitUntilAsync(async () =>
        {
            await service.BeginHoldAsync();
            return _transport.ConnectByPathCalls.Count == 2;
        });

        Assert.Contains(_logger.Informations, i => i.Contains("re-establishing the hold"));
        Assert.Equal(1, _transport.DisconnectCount);
        Assert.Equal([DevicePath, DevicePath], _transport.ConnectByPathCalls);
        Assert.True(service.IsHolding);
        await _transport.WaitForReadsAsync(2);
    }

    #endregion

    #region PauseForFlashAsync — an API with no production caller (see #417)

    /// <summary>
    /// Pausing stops the keep-alive read and leaves the handle OPEN — the one thing that distinguishes it
    /// from <c>ReleaseAsync</c>. Pinned as an API fact, not as a flash-path claim: nothing in the app
    /// calls this method, so "the flasher reuses the warm handle" is only what the production comment
    /// says, and #417 tracks reconciling that.
    /// </summary>
    [Fact]
    public async Task Pausing_for_a_flash_stops_the_read_but_leaves_the_handle_open()
    {
        using var service = CreateService();
        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(1);

        await service.PauseForFlashAsync();

        Assert.Equal(0, _transport.DisconnectCount);
        Assert.False(service.IsHolding);
        var afterPause = _transport.ReadCount;
        await Task.Delay(ReadTimeout + ReadTimeout);
        Assert.Equal(afterPause, _transport.ReadCount);
    }

    /// <summary>
    /// Pausing drains the in-flight read rather than abandoning it: the call does not return until the
    /// keep-alive loop has stopped, so no read is still outstanding against the transport afterwards.
    /// </summary>
    [Fact]
    public async Task Pausing_waits_for_the_in_flight_read_to_drain()
    {
        using var service = CreateService();
        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(1);

        await service.PauseForFlashAsync();

        Assert.False(_transport.ReadInFlight);
    }

    /// <summary>
    /// And it drains the read by letting it finish, not by cancelling it — <c>StopKeepAliveAsync(hard:
    /// false)</c> against release's <c>hard: true</c>. The distinction is invisible in the hold's public
    /// state, and a first mutation pass found it unpinned, so it is asserted directly here.
    /// </summary>
    [Fact]
    public async Task Pausing_lets_the_in_flight_read_end_on_its_own_rather_than_cancelling_it()
    {
        using var service = CreateService();
        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(1);

        await service.PauseForFlashAsync();

        Assert.Equal(0, _transport.CancelledReadCount);
    }

    /// <summary>
    /// A pause is a stop we asked for, so it is not a dropped device — firing <c>HoldDropped</c> here
    /// would tell the watcher a bootloader vanished when nothing had gone wrong.
    /// </summary>
    [Fact]
    public async Task Pausing_for_a_flash_does_not_report_a_dropped_hold()
    {
        using var service = CreateService();
        var dropped = 0;
        service.HoldDropped += (_, _) => Interlocked.Increment(ref dropped);
        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(1);

        await service.PauseForFlashAsync();
        await Task.Delay(ReadTimeout + ReadTimeout);

        Assert.Equal(0, Volatile.Read(ref dropped));
    }

    #endregion

    #region Releasing the hold — the path the watcher actually takes

    /// <summary>
    /// Releasing closes the handle, and this is the live transition: <c>BootloaderWatcher.PrepareFlashAsync</c>
    /// calls <c>ReleaseAsync</c> on the target hold so the flasher's own transport can open that device
    /// path, and the dialog's teardown calls it too. Left open, the exclusive handle locks every other
    /// user-mode opener — the flasher included — out of the device.
    /// </summary>
    [Fact]
    public async Task Releasing_disconnects_the_handle()
    {
        using var service = CreateService();
        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(1);

        await service.ReleaseAsync();

        Assert.Equal(1, _transport.DisconnectCount);
        Assert.False(service.IsHolding);
        Assert.False(_transport.ReadInFlight);
    }

    /// <summary>
    /// A disconnect that throws on the way out (the device was already yanked) is logged and swallowed.
    /// Release runs from dialog teardown, where an exception has nowhere useful to go.
    /// </summary>
    [Fact]
    public async Task A_disconnect_that_throws_during_release_is_logged_not_propagated()
    {
        _transport.DisconnectThrows = new IOException("device gone");
        using var service = CreateService();
        await service.BeginHoldAsync();

        await service.ReleaseAsync();

        Assert.False(service.IsHolding);
        Assert.Contains(_logger.Warnings, w => w.Contains("Error while releasing the HID bootloader handle"));
    }

    /// <summary>
    /// Releasing without ever holding still closes the transport. The service cannot assume its own
    /// <c>BeginHoldAsync</c> succeeded — the connect may have been refused after partially opening.
    /// </summary>
    [Fact]
    public async Task Releasing_a_hold_that_was_never_established_still_disconnects()
    {
        using var service = CreateService();

        await service.ReleaseAsync();

        Assert.Equal(1, _transport.DisconnectCount);
        Assert.False(service.IsHolding);
    }

    #endregion

    #region Losing the device

    /// <summary>
    /// The drop signal. A read that fails for any reason other than a timeout means the handle went away
    /// — device detached, flashed, surprise-removed — and the watcher has to hear about it.
    /// </summary>
    [Fact]
    public async Task A_failed_keep_alive_read_reports_the_hold_as_dropped()
    {
        _transport.FailReadNumber = 2;
        using var service = CreateService();
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.HoldDropped += (_, _) => dropped.TrySetResult();

        await service.BeginHoldAsync();

        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(_logger.Warnings, w => w.Contains("keep-alive read failed"));
    }

    /// <summary>
    /// A timeout is the healthy case, not a drop: a sitting bootloader sends nothing, so every read is
    /// expected to time out and be re-issued. Reporting a drop here would kill a perfectly good hold.
    /// </summary>
    [Fact]
    public async Task A_timed_out_keep_alive_read_is_not_a_drop()
    {
        using var service = CreateService();
        var dropped = 0;
        service.HoldDropped += (_, _) => Interlocked.Increment(ref dropped);

        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(3);

        Assert.Equal(0, Volatile.Read(ref dropped));
        Assert.True(service.IsHolding);
    }

    /// <summary>
    /// The drop is raised off the keep-alive task's own thread. <see cref="BootloaderHoldService.Dispose"/>
    /// waits on that task, and the watcher's handler disposes the hold — raising inline would deadlock the
    /// hold against itself.
    /// </summary>
    [Fact]
    public async Task A_dropped_hold_can_be_disposed_from_its_own_handler()
    {
        _transport.FailReadNumber = 1;
        var service = CreateService();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.HoldDropped += (_, _) =>
        {
            service.Dispose();
            disposed.TrySetResult();
        };

        await service.BeginHoldAsync();

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, _transport.DisposeCount);
    }

    #endregion

    #region Identity and teardown

    /// <summary>
    /// The path and name are what the watcher lists a held bootloader by, so they are surfaced verbatim.
    /// </summary>
    [Fact]
    public void The_hold_surfaces_the_device_it_targets()
    {
        using var service = CreateService();

        Assert.Equal(DevicePath, service.DevicePath);
        Assert.Equal(DeviceName, service.DeviceName);
        Assert.False(service.IsHolding);
    }

    /// <summary>
    /// Each hold owns its transport (the watcher news one up per device), so disposing the hold is what
    /// deterministically closes the exclusive HID handle. Without it a dropped hold keeps the handle until
    /// finalization and locks out a bootloader that re-appears at the same path.
    /// </summary>
    [Fact]
    public async Task Disposing_the_hold_disposes_the_transport_it_owns()
    {
        var service = CreateService();
        await service.BeginHoldAsync();
        await _transport.WaitForReadsAsync(1);

        service.Dispose();

        Assert.Equal(1, _transport.DisposeCount);
        Assert.False(_transport.ReadInFlight);
    }

    /// <summary>
    /// After disposal every entry point is inert. Teardown races are ordinary here — the dialog can close
    /// while the watcher is still reacting to a drop — and a second connect would reopen a handle nobody
    /// will ever close.
    /// </summary>
    [Fact]
    public async Task A_disposed_hold_ignores_begin_pause_and_release()
    {
        var service = CreateService();
        service.Dispose();

        await service.BeginHoldAsync();
        await service.PauseForFlashAsync();
        await service.ReleaseAsync();

        Assert.Empty(_transport.ConnectByPathCalls);
        Assert.Empty(_transport.ConnectCalls);
        Assert.Equal(0, _transport.DisconnectCount);
        Assert.False(service.IsHolding);
    }

    /// <summary>
    /// Disposing twice is harmless and disposes the transport once.
    /// </summary>
    [Fact]
    public void Disposing_twice_disposes_the_transport_once()
    {
        var service = CreateService();

        service.Dispose();
        service.Dispose();

        Assert.Equal(1, _transport.DisposeCount);
    }

    #endregion

    private static Task WaitUntilAsync(Func<bool> condition) =>
        WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("Timed out waiting for the expected condition.");
    }

    /// <summary>
    /// A fake HID transport that records what the hold does to it and lets a test choose which keep-alive
    /// read fails. The default read honours cancellation and then throws <see cref="TimeoutException"/> —
    /// the real transport's behaviour against a bootloader that is sitting there saying nothing.
    /// </summary>
    private sealed class FakeHidTransport : IHidTransport
    {
        private readonly Lock _gate = new();
        private readonly List<string> _connectByPathCalls = [];
        private readonly List<ConnectCall> _connectCalls = [];
        private readonly List<(int Target, TaskCompletionSource Signal)> _readWaiters = [];
        private int _readCount;
        private int _readsInFlight;
        private int _cancelledReadCount;
        private int _disconnectCount;
        private int _disposeCount;

        /// <summary>The 1-based keep-alive read that should fail with a non-timeout error, if any.</summary>
        internal int? FailReadNumber { get; set; }

        internal Exception? ConnectThrows { get; set; }

        internal Exception? DisconnectThrows { get; set; }

        internal IReadOnlyList<string> ConnectByPathCalls
        {
            get { lock (_gate) { return [.. _connectByPathCalls]; } }
        }

        internal IReadOnlyList<ConnectCall> ConnectCalls
        {
            get { lock (_gate) { return [.. _connectCalls]; } }
        }

        internal int ReadCount => Volatile.Read(ref _readCount);

        internal bool ReadInFlight => Volatile.Read(ref _readsInFlight) > 0;

        /// <summary>Reads that ended because their cancellation token fired, rather than by timing out.</summary>
        internal int CancelledReadCount => Volatile.Read(ref _cancelledReadCount);

        internal int DisconnectCount => Volatile.Read(ref _disconnectCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal Task WaitForReadsAsync(int count)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_readCount >= count)
                {
                    return Task.CompletedTask;
                }

                _readWaiters.Add((count, signal));
            }

            return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public bool IsConnected { get; private set; }

        public int? VendorId { get; private set; }

        public int? ProductId { get; private set; }

        public string? SerialNumber => null;

        public string? DevicePath { get; private set; }

        public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(1);

        public Task ConnectAsync(
            int vendorId, int productId, string? serialNumber = null, CancellationToken cancellationToken = default)
        {
            if (ConnectThrows is { } ex)
            {
                return Task.FromException(ex);
            }

            lock (_gate) { _connectCalls.Add(new ConnectCall(vendorId, productId, serialNumber)); }
            VendorId = vendorId;
            ProductId = productId;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task ConnectByPathAsync(string devicePath, CancellationToken cancellationToken = default)
        {
            if (ConnectThrows is { } ex)
            {
                return Task.FromException(ex);
            }

            lock (_gate) { _connectByPathCalls.Add(devicePath); }
            DevicePath = devicePath;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public async Task<byte[]> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var thisRead = Interlocked.Increment(ref _readCount);
            ReleaseReadWaiters(thisRead);
            Interlocked.Increment(ref _readsInFlight);
            try
            {
                if (FailReadNumber == thisRead)
                {
                    throw new IOException("HID read failed");
                }

                await Task.Delay(timeout ?? ReadTimeout, cancellationToken).ConfigureAwait(false);
                throw new TimeoutException("No HID report within the read timeout.");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelledReadCount);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _readsInFlight);
            }
        }

        public Task DisconnectAsync()
        {
            Interlocked.Increment(ref _disconnectCount);
            IsConnected = false;
            return DisconnectThrows is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public void Connect(int vendorId, int productId, string? serialNumber = null) =>
            throw new NotSupportedException("The hold only uses the async surface.");

        public void ConnectByPath(string devicePath) =>
            throw new NotSupportedException("The hold only uses the async surface.");

        public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The hold never writes to a sitting bootloader.");

        public void Write(byte[] data) =>
            throw new NotSupportedException("The hold never writes to a sitting bootloader.");

        public byte[] Read(TimeSpan? timeout = null) =>
            throw new NotSupportedException("The hold only uses the async surface.");

        public void Disconnect() =>
            throw new NotSupportedException("The hold only uses the async surface.");

        private void ReleaseReadWaiters(int readNumber)
        {
            List<TaskCompletionSource>? due = null;
            lock (_gate)
            {
                for (var i = _readWaiters.Count - 1; i >= 0; i--)
                {
                    if (_readWaiters[i].Target > readNumber)
                    {
                        continue;
                    }

                    (due ??= []).Add(_readWaiters[i].Signal);
                    _readWaiters.RemoveAt(i);
                }
            }

            if (due == null)
            {
                return;
            }

            foreach (var signal in due)
            {
                signal.TrySetResult();
            }
        }

        internal readonly record struct ConnectCall(int VendorId, int ProductId, string? SerialNumber);
    }
}
