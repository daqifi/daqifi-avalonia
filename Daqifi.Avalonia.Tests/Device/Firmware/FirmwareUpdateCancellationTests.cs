using System.Collections.ObjectModel;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins the one guarantee the firmware Cancel button makes: when the UI says it is cancelling, the
/// token the flash is actually running on has been signalled.
///
/// <para>
/// Issue #234. The WiFi-only entry point used to create its own <c>CancellationTokenSource</c> in the
/// view model and hand the coordinator only the token, while <see cref="FirmwareUpdateCoordinator.CancelUpload"/>
/// cancelled a <em>different</em> field that only the combined update ever assigned. Cancel therefore
/// wrote "Canceling firmware update..." into the status line and signalled nothing — a flash the user
/// believed they had stopped kept running, which is the case where they unplug the board.
/// </para>
///
/// <para>
/// None of this needs hardware. The cancellation is observed at the coordinator's own injected seams:
/// the download service (WiFi-only path) and the host's WiFi-probe quiesce (combined path) both park
/// on the token they are given, so a test can assert on exactly the token the flash received.
/// </para>
/// </summary>
public class FirmwareUpdateCancellationTests : IDisposable
{
    /// <summary>Long enough that a cancelled flash always unwinds, short enough that a broken one fails fast.</summary>
    private static readonly TimeSpan UnwindTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeHost _host = new();
    private readonly ParkingDownloadService _downloads = new();
    private readonly StubUpdateService _updates = new();
    private readonly SilentLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-cancel-tests", Guid.NewGuid().ToString("N"));

    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero);

    public void Dispose()
    {
        _downloads.ReleaseAll();
        _host.ReleaseQuiesce();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The regression for #234: a Cancel during a WiFi-only flash must reach the flash.
    /// </summary>
    [Fact]
    public async Task Canceling_a_wifi_only_flash_cancels_the_flash()
    {
        var coordinator = CreateCoordinator();
        var device = WincDevice();
        var core = new BootloaderSessionStreamingDeviceAdapter(device.Name);

        // Reproduces DaqifiViewModel.UpdateWifiFirmwareOnly's wiring on main: the caller marks the host
        // as uploading (which is what enables the Cancel button) and owns the CancellationTokenSource.
        _host.IsFirmwareUploading = true;
        using var callerCts = new CancellationTokenSource();
        var flash = coordinator.UpdateWifiModuleAsync(core, device, callerCts.Token, force: true);

        await _downloads.WaitUntilRunningAsync();
        coordinator.CancelUpload();

        await AssertCanceledAsync(flash, callerCts);
    }

    /// <summary>
    /// The status line must never claim a cancellation that did not happen. This is the user-visible
    /// half of #234 and it holds no matter which entry point set the uploading flag.
    /// </summary>
    [Fact]
    public void Cancel_does_not_claim_to_be_canceling_when_nothing_is_cancelable()
    {
        var coordinator = CreateCoordinator();

        // The flag alone — the state the WiFi-only path put the host in before the coordinator owned
        // the token — must not be enough to make Cancel announce a cancellation.
        _host.IsFirmwareUploading = true;
        coordinator.CancelUpload();

        Assert.Equal(string.Empty, _host.FirmwareUpdateStatusText);
    }

    /// <summary>
    /// The combined PIC32 + WiFi path already routed Cancel correctly; this pins it so the shared
    /// ownership introduced for the WiFi-only path cannot silently regress it.
    /// </summary>
    [Fact]
    public async Task Canceling_a_combined_update_cancels_the_flash()
    {
        var coordinator = CreateCoordinator();
        _host.SelectedDevice = WincDevice();

        var upload = coordinator.UploadFirmwareAsync();

        await _host.WaitUntilQuiescingAsync();
        coordinator.CancelUpload();

        await upload.WaitAsync(UnwindTimeout);

        // UploadFirmwareAsync swallows the cancellation and reports it on the status line.
        Assert.Equal("Firmware update canceled.", _host.FirmwareUpdateStatusText);
        Assert.False(_host.IsFirmwareUploading);
    }

    /// <summary>
    /// A Cancel arriving after the run has finished is a no-op, not a status-line lie.
    /// </summary>
    [Fact]
    public async Task Cancel_after_a_wifi_only_flash_has_finished_is_a_no_op()
    {
        var coordinator = CreateCoordinator();
        var device = WincDevice();
        var core = new BootloaderSessionStreamingDeviceAdapter(device.Name);
        _downloads.CompleteImmediatelyWithNoPackage();

        _host.IsFirmwareUploading = true;
        using var callerCts = new CancellationTokenSource();

        // No package found is the cheapest clean exit from the WiFi flash that never touches serial.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleAsync(core, device, callerCts.Token, force: true));

        _host.FirmwareUpdateStatusText = string.Empty;
        _host.IsFirmwareUploading = false;
        coordinator.CancelUpload();

        Assert.Equal(string.Empty, _host.FirmwareUpdateStatusText);
    }

    #region Helpers
    /// <summary>
    /// A USB device that reports a separately-flashable WINC module, so the WiFi flash is attempted.
    /// The <see cref="System.IO.Ports.SerialPort"/> it constructs is never opened.
    /// </summary>
    private static SerialStreamingDevice WincDevice()
    {
        var device = new SerialStreamingDevice("COM-TEST-234");
        device.Metadata.Capabilities.HasWincWifiModule = true;
        return device;
    }

    private static async Task AssertCanceledAsync(Task flash, CancellationTokenSource callerCts)
    {
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flash.WaitAsync(UnwindTimeout));
        }
        finally
        {
            // Whatever the outcome, don't leave the parked flash running for the rest of the run.
            callerCts.Cancel();
        }
    }
    #endregion

    #region Fakes
    /// <summary>
    /// Download service that parks on the token it is handed, so the test can observe exactly which
    /// token the in-flight flash is running on.
    /// </summary>
    private sealed class ParkingDownloadService : IFirmwareDownloadService
    {
        private readonly TaskCompletionSource _running =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly CancellationTokenSource _release = new();
        private bool _returnNoPackage;

        /// <summary>Completes once the WiFi download has actually started.</summary>
        public Task WaitUntilRunningAsync() => _running.Task.WaitAsync(UnwindTimeout);

        /// <summary>Makes the next WiFi download return "no package" instead of parking.</summary>
        public void CompleteImmediatelyWithNoPackage() => _returnNoPackage = true;

        /// <summary>Frees any parked call so a failing test cannot leak it past teardown.</summary>
        public void ReleaseAll() => _release.Cancel();

        public async Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_returnNoPackage)
            {
                return null;
            }

            _running.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _release.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void InvalidateCache() { }
    }

    /// <summary>PIC32 update service that is never reached by these tests.</summary>
    private sealed class StubUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => Task.CompletedTask;

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Host seam. <c>FirmwareUpdateStatusText</c> is set-only on the interface, so the fake adds the
    /// getter the assertions read. <see cref="QuiesceWifiFirmwareProbeAsync"/> parks on its token,
    /// which is the combined path's first cancellable await.
    /// </summary>
    private sealed class FakeHost : IFirmwareUpdateHost
    {
        private readonly TaskCompletionSource _quiescing =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly CancellationTokenSource _release = new();

        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices { get; } = [];

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading { get; set; }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText { get; set; } = string.Empty;

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() { }

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded() { }

        /// <summary>Completes once the coordinator has reached the probe quiesce.</summary>
        public Task WaitUntilQuiescingAsync() => _quiescing.Task.WaitAsync(UnwindTimeout);

        /// <summary>Frees a parked quiesce so a failing test cannot leak it past teardown.</summary>
        public void ReleaseQuiesce() => _release.Cancel();

        public async Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
        {
            _quiescing.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _release.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

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
            Daqifi.Desktop.Common.Loggers.BreadcrumbLevel level = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel.Info)
        { }

        public void SetDeviceContext(
            string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

        public void ClearDeviceContext() { }

        public void Shutdown() { }
    }
    #endregion
}
