using System.Collections.ObjectModel;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
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
    private readonly RecordingAppLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-cancel-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// <c>canFlashWifiModule: true</c> because these tests are about a WiFi flash that <em>runs</em>:
    /// the platform gate added for issue #330 skips the WiFi step entirely where Microchip's
    /// Windows-only WINC tool cannot launch, which off Windows would take every assertion below out
    /// of reach. The gate itself is covered in <c>WifiFlashPlatformGateTests</c>.
    /// </summary>
    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: true);

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

        var flash = coordinator.UpdateWifiModuleOnlyAsync(WincDevice());
        await _downloads.WaitUntilRunningAsync();

        coordinator.CancelUpload();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flash.WaitAsync(UnwindTimeout));
        Assert.Equal("Canceling firmware update...", _host.FirmwareUpdateStatusText);
    }

    /// <summary>
    /// The Cancel button's CanExecute is the host's uploading flag, so the WiFi-only run must raise it
    /// — and must have a cancellable source by the time it does, which is what the flag now implies.
    /// </summary>
    [Fact]
    public async Task A_wifi_only_flash_marks_the_host_uploading_while_it_runs()
    {
        var coordinator = CreateCoordinator();

        var flash = coordinator.UpdateWifiModuleOnlyAsync(WincDevice());
        await _downloads.WaitUntilRunningAsync();

        Assert.True(_host.IsFirmwareUploading);

        coordinator.CancelUpload();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flash.WaitAsync(UnwindTimeout));
        Assert.False(_host.IsFirmwareUploading);
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

        // "No package found" is the cheapest clean exit from the WiFi flash that never touches serial.
        _downloads.CompleteImmediatelyWithNoPackage();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleOnlyAsync(WincDevice()));

        _host.FirmwareUpdateStatusText = string.Empty;
        coordinator.CancelUpload();

        Assert.Equal(string.Empty, _host.FirmwareUpdateStatusText);
        Assert.False(_host.IsFirmwareUploading);
    }

    /// <summary>
    /// Issue #409: the pre-flash WiFi version probe runs on the flash's own token, so a Cancel that
    /// lands between the PIC32 flash and the WiFi step puts no <c>GETChipInfo?</c> on the wire.
    /// <see cref="WifiChipInfoProbeTests.A_probe_cancelled_before_it_starts_never_queries_the_device"/>
    /// pins Core's half (a cancelled token sends nothing); this pins the app's half — that the token
    /// the coordinator hands the probe is the one Cancel signals, rather than
    /// <see cref="CancellationToken.None"/>.
    /// </summary>
    /// <remarks>
    /// The device is the app's real <see cref="SerialStreamingDevice"/> over a real Core device on a
    /// <see cref="Daqifi.Avalonia.Tests.Device.CapturingTransport"/>, so what is asserted is what
    /// reached the wire. The power-on is the proof the run got as far as the probe: the coordinator
    /// sends it on the line immediately before the query.
    /// </remarks>
    [Fact]
    public async Task Canceling_after_the_pic32_flash_keeps_the_wifi_probe_off_the_wire()
    {
        var coordinator = CreateCoordinator();
        using var harness = ConnectedWincDevice();
        _host.SelectedDevice = harness.Device;
        _host.ParkOnQuiesce = false;
        _downloads.LatestFirmwarePath = FirmwareFile();

        // The user presses Cancel as the PIC32 flash finishes: the stub returns normally, so the run
        // carries on into the WiFi step holding a cancelled token.
        _updates.OnPic32Flash = coordinator.CancelUpload;
        harness.Transport.ClearSent();

        await coordinator.UploadFirmwareAsync().WaitAsync(UnwindTimeout);

        Assert.True(
            harness.Transport.WaitForSentText(ScpiMessageProducer.TurnDeviceOn.Data, UnwindTimeout),
            $"The run never reached the WiFi probe; saw: {harness.Transport.SentText}");
        Assert.DoesNotContain(
            ScpiMessageProducer.GetLanChipInfo.Data, harness.Transport.SentText, StringComparison.Ordinal);
        Assert.Equal("Firmware update canceled.", _host.FirmwareUpdateStatusText);
    }

    #region Helpers
    /// <summary>A stand-in PIC32 image: the coordinator checks only that the file exists.</summary>
    private string FirmwareFile()
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "firmware.hex");
        File.WriteAllText(path, ":00000001FF\n");
        return path;
    }

    /// <summary>
    /// A WINC-bearing USB device wrapping a connected Core device whose transport records every write.
    /// </summary>
    private static WireHarness ConnectedWincDevice()
    {
        var transport = new Daqifi.Avalonia.Tests.Device.CapturingTransport();
        var core = new DaqifiStreamingDevice("core", transport, NullLogger.Instance);
        core.Connect();
        var device = new SerialStreamingDevice("COM-TEST-409", core);
        device.Metadata.Capabilities.HasWincWifiModule = true;
        return new WireHarness(transport, core, device);
    }

    private sealed class WireHarness(
        Daqifi.Avalonia.Tests.Device.CapturingTransport transport,
        DaqifiStreamingDevice core,
        SerialStreamingDevice device) : IDisposable
    {
        public Daqifi.Avalonia.Tests.Device.CapturingTransport Transport { get; } = transport;

        public SerialStreamingDevice Device { get; } = device;

        public void Dispose()
        {
            // Release the parked reader first, as CurrentRateCapEnforcementTests does, so Core's
            // teardown does not spend its consumer-join bound on every run.
            Transport.CloseStream();
            core.Dispose();
            Transport.Dispose();
        }
    }

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

        /// <summary>What the PIC32 package download returns; null means "no package".</summary>
        public string? LatestFirmwarePath { get; set; }

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(LatestFirmwarePath);

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

    /// <summary>
    /// PIC32 update service that flashes nothing. <see cref="OnPic32Flash"/> runs in place of the
    /// flash, which is how a test lands a Cancel at the moment the PIC32 half completes.
    /// </summary>
    private sealed class StubUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

        public Action? OnPic32Flash { get; set; }

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
            CancellationToken cancellationToken = default)
        {
            OnPic32Flash?.Invoke();
            return Task.CompletedTask;
        }

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

        /// <summary>False lets the quiesce complete at once, for a run meant to get past it.</summary>
        public bool ParkOnQuiesce { get; set; } = true;

        public async Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
        {
            if (!ParkOnQuiesce)
            {
                return;
            }

            _quiescing.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _release.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
    #endregion
}
