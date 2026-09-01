using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Device.Firmware;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins the watcher's "a HID bootloader write is in flight" signal — <see
/// cref="IBootloaderWatcher.IsFlashInProgress"/> and <see cref="IBootloaderWatcher.FlashInProgressChanged"/>.
///
/// The connection dialog keys its serial + WiFi discovery on this fact, because a manual bootloader
/// flash never sets <c>ConnectionManager.DeviceBeingUpdated</c> and so is invisible to
/// <c>ConnectionManager.IsFirmwareUpdateInProgress</c>. Two properties of the signal are load-bearing
/// and neither is obvious from reading the code:
///
/// <list type="bullet">
/// <item>It must stay TRUE across the post-flash re-grab. The flag was previously cleared at the top of
/// the resume path, <em>before</em> awaiting <c>BeginHoldAsync</c> — so it advertised "flash over" while
/// the HID handle was still being reopened, exactly the window a resumed COM-port probe would land
/// in.</item>
/// <item>The falling edge must reach every subscriber even if an earlier one throws. It is the
/// connection dialog's only resume trigger when the write outlives the dialog that started it, so a
/// swallowed edge leaves discovery stopped for the rest of the dialog's life.</item>
/// </list>
///
/// None of this needs hardware: <see cref="IBootloaderDiscovery"/> and
/// <see cref="IBootloaderHoldService"/> are the seams the watcher was built with.
/// </summary>
public class BootloaderWatcherFlashStateTests
{
    private const string PathA = "hid://bootloader-a";

    private readonly FakeDiscovery _discovery = new();
    private readonly SilentLogger _logger = new();
    private readonly Dictionary<string, FakeHold> _holds = new(StringComparer.Ordinal);

    private BootloaderWatcher CreateWatcher() =>
        new(_discovery, (path, name) =>
        {
            var hold = new FakeHold(path, name);
            _holds[path] = hold;
            return hold;
        }, _logger);

    /// <summary>
    /// Starts the watcher and lets it grab one bootloader, returning once the hold is established.
    ///
    /// The wait is on the fake hold's own call count rather than on <c>watcher.Bootloaders</c>: the
    /// bound collection is mutated through <c>Dispatcher.UIThread</c>, which outside a running Avalonia
    /// app binds to whichever thread touched it first and is never pumped, so the row may legitimately
    /// never appear in a test host. Nothing here depends on the UI projection.
    /// </summary>
    private async Task<BootloaderWatcher> CreateStartedWatcherHoldingAsync(string devicePath)
    {
        var watcher = CreateWatcher();
        watcher.Start();
        _discovery.Raise(devicePath, "DAQiFi Bootloader");
        await WaitUntilAsync(() => _holds.TryGetValue(devicePath, out var hold) && hold.IsHolding);
        return watcher;
    }

    [Fact]
    public async Task No_flash_is_in_progress_before_one_is_prepared()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);

        Assert.False(watcher.IsFlashInProgress);
    }

    [Fact]
    public async Task Preparing_a_flash_marks_it_in_progress_and_raises_the_rising_edge()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var edges = new List<bool>();
        watcher.FlashInProgressChanged += (_, _) => edges.Add(watcher.IsFlashInProgress);

        await watcher.PrepareFlashAsync(PathA);

        Assert.True(watcher.IsFlashInProgress);
        Assert.Equal(new[] { true }, edges);
        Assert.False(_discovery.IsRunning);
        Assert.Equal(1, _holds[PathA].ReleaseCount);
    }

    [Fact]
    public async Task Disposing_the_flash_lease_clears_the_flag_and_raises_the_falling_edge()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var lease = await watcher.PrepareFlashAsync(PathA);
        var edges = new List<bool>();
        watcher.FlashInProgressChanged += (_, _) => edges.Add(watcher.IsFlashInProgress);

        await lease.DisposeAsync();

        Assert.False(watcher.IsFlashInProgress);
        Assert.Equal(new[] { false }, edges);
        Assert.True(_discovery.IsRunning);
        // Two: the original grab, plus the re-grab the lease performs.
        Assert.Equal(2, _holds[PathA].BeginHoldCount);
    }

    /// <summary>
    /// The ordering fix. The flag must not clear until the target has actually been re-grabbed —
    /// otherwise a subscriber polling it (the connection dialog's <c>Start*Discovery</c> guards, which
    /// hold no lock) sees "flash over" and resumes bus probing while the HID handle is mid-reopen.
    /// </summary>
    [Fact]
    public async Task The_flash_still_reads_as_in_progress_while_the_target_is_being_re_grabbed()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var lease = await watcher.PrepareFlashAsync(PathA);
        var hold = _holds[PathA];

        // Park the re-grab so the resume path is suspended in the middle of BeginHoldAsync.
        var reGrabReached = new TaskCompletionSource();
        var releaseReGrab = new TaskCompletionSource();
        hold.BeginHoldGate = () =>
        {
            reGrabReached.TrySetResult();
            return releaseReGrab.Task;
        };

        var disposal = lease.DisposeAsync().AsTask();
        await reGrabReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(
            watcher.IsFlashInProgress,
            "The flash must still read as in progress while the bootloader's handle is being reopened.");

        releaseReGrab.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(watcher.IsFlashInProgress);
    }

    /// <summary>
    /// The mirror-image risk of the ordering fix: clearing the flag later must not mean never clearing
    /// it. A re-grab that throws still has to end the flash, or the dialog's discovery stays paused for
    /// the rest of its life.
    /// </summary>
    [Fact]
    public async Task A_re_grab_that_throws_still_ends_the_flash_and_resumes_discovery()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var lease = await watcher.PrepareFlashAsync(PathA);
        var edges = new List<bool>();
        watcher.FlashInProgressChanged += (_, _) => edges.Add(watcher.IsFlashInProgress);
        _holds[PathA].BeginHoldGate = () => throw new InvalidOperationException("device vanished mid re-grab");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());

        Assert.False(watcher.IsFlashInProgress);
        Assert.Equal(new[] { false }, edges);
        Assert.True(_discovery.IsRunning);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_swallow_the_edge_for_the_subscribers_after_it()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        watcher.FlashInProgressChanged += (_, _) => throw new InvalidOperationException("subscriber blew up");
        var edges = new List<bool>();
        watcher.FlashInProgressChanged += (_, _) => edges.Add(watcher.IsFlashInProgress);

        var lease = await watcher.PrepareFlashAsync(PathA);
        await lease.DisposeAsync();

        Assert.Equal(new[] { true, false }, edges);
        Assert.False(watcher.IsFlashInProgress);
        Assert.True(_discovery.IsRunning);
        Assert.Equal(2, _logger.Errors);
    }

    /// <summary>
    /// A second <c>PrepareFlashAsync</c> while one is already in flight must not double-raise the rising
    /// edge — subscribers treat the edge as a state transition, not a ping.
    /// </summary>
    [Fact]
    public async Task Preparing_a_second_flash_while_one_is_running_does_not_re_raise_the_rising_edge()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var edges = new List<bool>();
        watcher.FlashInProgressChanged += (_, _) => edges.Add(watcher.IsFlashInProgress);

        var first = await watcher.PrepareFlashAsync(PathA);
        var second = await watcher.PrepareFlashAsync(PathA);

        Assert.Equal(new[] { true }, edges);
        Assert.True(watcher.IsFlashInProgress);

        // Leave nothing outstanding: an undisposed lease would hold discovery paused for the rest of
        // the watcher's life, which is exactly what the next test is about.
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    /// <summary>
    /// Every <c>PrepareFlashAsync</c> hands out its own lease, and the published flash state has to
    /// describe <em>all</em> of them: if the first disposal cleared it, the connection dialog's guard
    /// would open and discovery would probe the bus into a write that is still running.
    ///
    /// <para>
    /// Two overlapping flashes are not reachable through today's UI — the only caller,
    /// <c>FirmwareDialogViewModel</c>, is behind a modal opened from another modal — so this pins the
    /// contract rather than reproducing a live failure. It is the same reason the re-grab is deferred:
    /// reopening the handle while a second write to the same device is in flight would fight it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Disposing_one_of_two_overlapping_leases_leaves_the_flash_in_progress()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var first = await watcher.PrepareFlashAsync(PathA);
        var second = await watcher.PrepareFlashAsync(PathA);
        var edges = new List<bool>();
        watcher.FlashInProgressChanged += (_, _) => edges.Add(watcher.IsFlashInProgress);
        var beginHoldsBefore = _holds[PathA].BeginHoldCount;

        await first.DisposeAsync();

        Assert.True(
            watcher.IsFlashInProgress,
            "A flash must stay in progress while another lease is still outstanding.");
        Assert.False(_discovery.IsRunning, "Discovery must not resume while a lease is outstanding.");
        Assert.Empty(edges);
        Assert.Equal(
            beginHoldsBefore,
            _holds[PathA].BeginHoldCount);

        await second.DisposeAsync();

        Assert.False(watcher.IsFlashInProgress);
        Assert.Equal(new[] { false }, edges);
        Assert.True(_discovery.IsRunning);
        Assert.Equal(beginHoldsBefore + 1, _holds[PathA].BeginHoldCount);
    }

    /// <summary>
    /// The marker is raised before the target's hold is released, so that a coordinator auto-update
    /// ending during that release cannot see a gap. The cost is that a failed release would strand it:
    /// no lease is returned, so nothing would ever clear it, and every transport — HID here, and serial
    /// and WiFi through the connection dialog's guard — would stay dark for the life of the process
    /// over a flash that never began. Failing loudly is fine; failing quietly and permanently is not.
    /// </summary>
    [Fact]
    public async Task A_preparation_that_fails_to_release_the_hold_does_not_strand_the_pause()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var edges = new List<bool>();
        watcher.FlashInProgressChanged += (_, _) => edges.Add(watcher.IsFlashInProgress);
        _holds[PathA].ReleaseGate = () => throw new IOException("the handle went away");

        await Assert.ThrowsAsync<IOException>(() => watcher.PrepareFlashAsync(PathA));

        Assert.False(
            watcher.IsFlashInProgress,
            "A preparation that threw must not leave a flash reported as in progress.");
        Assert.True(_discovery.IsRunning, "Discovery must come back when the preparation failed.");
        Assert.Empty(edges);
    }

    /// <summary>
    /// A flash and a coordinator auto-update can overlap on a multi-device bench. Whichever lease is
    /// disposed first must not resume HID discovery while the other is still holding the bus quiet.
    /// </summary>
    [Fact]
    public async Task Discovery_resumes_only_once_both_the_flash_and_the_auto_update_suspend_have_ended()
    {
        using var watcher = await CreateStartedWatcherHoldingAsync(PathA);
        var suspendLease = await watcher.SuspendDiscoveryAsync();
        var flashLease = await watcher.PrepareFlashAsync(PathA);
        Assert.False(_discovery.IsRunning);

        await suspendLease.DisposeAsync();
        Assert.False(_discovery.IsRunning);
        Assert.True(watcher.IsFlashInProgress);

        await flashLease.DisposeAsync();
        Assert.True(_discovery.IsRunning);
        Assert.False(watcher.IsFlashInProgress);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Condition was not met within {timeoutMs} ms.");
    }

    #region Fakes
    private sealed class FakeDiscovery : IBootloaderDiscovery
    {
        public event EventHandler<BootloaderDiscoveredEventArgs>? BootloaderDiscovered;

        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Raise(string devicePath, string? deviceName) =>
            BootloaderDiscovered?.Invoke(this, new BootloaderDiscoveredEventArgs(devicePath, deviceName));
    }

    /// <summary>
    /// Stand-in for one bootloader's exclusive USB hold. <see cref="BeginHoldGate"/> lets a test suspend
    /// or fail the post-flash re-grab, which is the only way to observe what the watcher advertises
    /// while that re-grab is still running.
    /// </summary>
    private sealed class FakeHold(string devicePath, string? deviceName) : IBootloaderHoldService
    {
        public bool IsHolding { get; private set; }

        public string? DevicePath { get; } = devicePath;

        public string? DeviceName { get; } = deviceName;

        public int BeginHoldCount { get; private set; }

        public int ReleaseCount { get; private set; }

        /// <summary>Runs inside <see cref="BeginHoldAsync"/>; null means "succeed immediately".</summary>
        public Func<Task>? BeginHoldGate { get; set; }

        /// <summary>Runs inside <see cref="ReleaseAsync"/>; null means "succeed immediately".</summary>
        public Func<Task>? ReleaseGate { get; set; }

#pragma warning disable CS0067 // Never raised here: no test in this class drops a hold mid-flight.
        public event EventHandler? HoldDropped;
#pragma warning restore CS0067

        public async Task BeginHoldAsync(CancellationToken cancellationToken = default)
        {
            BeginHoldCount++;
            if (BeginHoldGate != null)
            {
                await BeginHoldGate();
            }

            IsHolding = true;
        }

        public Task PauseForFlashAsync() => Task.CompletedTask;

        public async Task ReleaseAsync()
        {
            ReleaseCount++;
            if (ReleaseGate != null)
            {
                await ReleaseGate();
            }

            IsHolding = false;
        }

        public void Dispose() => IsHolding = false;
    }

    /// <summary>Counts errors so a test can assert a faulting subscriber was recorded, not swallowed.</summary>
    private sealed class SilentLogger : IAppLogger
    {
        public int Errors { get; private set; }

        public void Information(string message) { }

        public void Warning(string message) { }

        public void Warning(Exception ex, string message) { }

        public void Error(string message) => Errors++;

        public void Error(Exception ex, string message) => Errors++;

        public void AddBreadcrumb(
            string category,
            string message,
            Daqifi.Desktop.Common.Loggers.BreadcrumbLevel level = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel.Info)
        { }

        public void SetDeviceContext(
            string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

        public void ClearDeviceContext() { }

        public void Shutdown() { }
    }
    #endregion
}
