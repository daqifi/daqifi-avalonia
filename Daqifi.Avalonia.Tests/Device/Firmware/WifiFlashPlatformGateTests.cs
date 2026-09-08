using System.Collections.ObjectModel;
using Daqifi.Core.Firmware;
using Daqifi.Core.Firmware.Winc;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Daqifi.Avalonia.Tests.ViewModels;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins issue #330: the WiFi half of a firmware update must not be attempted on a machine that
/// cannot run it, and must not be offered there either.
///
/// <para>
/// Microchip ships the WINC1500 programmer only as the Windows batch file
/// <c>winc_flash_tool.cmd</c>, which the coordinator launches through an external process runner.
/// Off Windows that launch fails — and on the auto path it fails <em>after</em> the PIC32 image has
/// been written and CRC-verified, so the exception reached <c>UploadFirmwareAsync</c>'s generic
/// handler and the user was told "Firmware update failed. Please try again." for an update that had
/// in fact succeeded, with a retry that could never work on that machine.
/// </para>
///
/// <para>
/// The gate is an operating-system test rather than Core's <see cref="WincFlashToolLocator"/>, which
/// was the obvious candidate and is what the issue proposed.
/// <see cref="The_flash_tool_file_is_present_off_windows_too_so_locating_it_gates_nothing"/> is why:
/// the locator asks whether the tool <em>file</em> is present under a firmware path, and the WiFi
/// package this app downloads carries <c>winc_flash_tool.cmd</c> inside it, so the probe answers
/// "yes" on exactly the platforms the gate exists to stop.
/// </para>
///
/// <para>
/// No hardware and no flash: the platform decision is taken before anything is downloaded or any
/// serial port is touched, so the seam these tests observe is the injected download service — which
/// on the unavailable branch must never be reached at all.
/// </para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class WifiFlashPlatformGateTests : IDisposable
{
    private readonly RecordingHost _host = new();
    private readonly RecordingDownloadService _downloads = new();
    private readonly StubUpdateService _updates = new();
    private readonly SilentLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-wifi-platform-gate-tests", Guid.NewGuid().ToString("N"));

    private FirmwareUpdateCoordinator CreateCoordinator(bool canFlashWifiModule) =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: canFlashWifiModule);

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region What the platform decision is, and why it is not the tool-file probe
    /// <summary>
    /// The capability the whole fix hangs off, stated once: Windows can flash the WINC module and
    /// nothing else can.
    /// </summary>
    [Fact]
    public void The_wifi_flash_capability_is_the_operating_system()
    {
        Assert.Equal(OperatingSystem.IsWindows(), FirmwareUpdateCoordinator.CanFlashWifiModule);
    }

    /// <summary>
    /// The reason the gate is not <see cref="WincFlashToolLocator.IsAvailable"/>, which the issue
    /// proposed and Core documents as the "can this machine flash the WiFi module?" probe.
    ///
    /// <para>
    /// The locator searches a firmware path for the tool file. The WiFi package this app hands it is
    /// the extracted source zipball of <c>daqifi/winc1500-Manual-UART-Firmware-Update</c>, whose tree
    /// is <c>winc/winc_flash_tool.cmd</c> alongside the firmware artifacts — the layout rebuilt
    /// below. That file is extracted on every platform, so the probe returns true on macOS and Linux
    /// as well, where the batch file still cannot execute. Off Windows this test therefore shows the
    /// two answers disagreeing, which is the whole reason the shipped gate reads the platform.
    /// </para>
    /// </summary>
    [Fact]
    public void The_flash_tool_file_is_present_off_windows_too_so_locating_it_gates_nothing()
    {
        var extracted = Path.Combine(_dataDirectory, "wifi-firmware-19.7.7");
        Directory.CreateDirectory(Path.Combine(extracted, "winc"));
        File.WriteAllText(Path.Combine(extracted, "winc", "winc_flash_tool.cmd"), "@echo off\n");

        var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

        // The probe says yes wherever this test runs, including the platforms that cannot run it.
        Assert.True(locator.IsAvailable(extracted));

        // And the app's own answer is the platform, so the two part company off Windows.
        Assert.Equal(OperatingSystem.IsWindows(), FirmwareUpdateCoordinator.CanFlashWifiModule);
    }
    #endregion

    #region The firmware run
    /// <summary>
    /// The unavailable branch: nothing is downloaded, no serial port is touched, and the run ends
    /// cleanly. Before the gate this call ran on into the WiFi download and the external tool launch,
    /// whose failure is what a caller reports as an update failure.
    /// </summary>
    [Fact]
    public async Task No_wifi_flash_is_attempted_where_the_tool_cannot_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice());

        Assert.False(_downloads.WifiDownloadWasRequested);
        Assert.False(_host.IsFirmwareUploading);
    }

    /// <summary>
    /// The user is told why, in the app's own words, rather than being left with a step that quietly
    /// did not happen.
    /// </summary>
    [Fact]
    public async Task The_run_says_why_the_wifi_module_was_skipped()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice());

        Assert.Contains(
            FirmwareUpdateCoordinator.WifiFlashUnavailableMessage,
            _host.Writes.Select(write => write.Text));

        // Written while the run is open, or the status line that renders it is hidden (issue #241).
        Assert.All(
            _host.Writes,
            write => Assert.True(
                write.WasUploading, $"'{write.Text}' was written while the status line was hidden."));
    }

    /// <summary>
    /// The negative control for the two above, and the reason this is a gate and not a deletion:
    /// where the tool can run, the WiFi step still runs. Reaching the download service is how far a
    /// flash gets before it needs hardware.
    /// </summary>
    [Fact]
    public async Task The_wifi_flash_still_runs_where_the_tool_can_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: true);

        // The download reports no package, which is the cheapest clean exit from a flash that has
        // genuinely started; the assertion is that it was asked at all.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleOnlyAsync(WincDevice()));

        Assert.True(_downloads.WifiDownloadWasRequested);
    }

    /// <summary>
    /// The gate does not swallow the more specific answer. A device with no separately-flashable WINC
    /// module is skipped for that reason, not blamed on the platform — the ESP32 parts integrate WiFi
    /// into the SoC and would be described wrongly by a "requires Windows" message.
    /// </summary>
    [Fact]
    public async Task A_device_without_a_winc_module_is_not_told_it_needs_windows()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(new SerialStreamingDevice("COM-TEST-330-NOWINC"));

        Assert.DoesNotContain(
            FirmwareUpdateCoordinator.WifiFlashUnavailableMessage,
            _host.Writes.Select(write => write.Text));
    }
    #endregion

    #region The standalone FLASH WIFI affordance
    /// <summary>
    /// The button must be enabled exactly where the flash can happen. It matters beyond the greying:
    /// <c>UpdateWifiFirmwareOnly</c> reports success when the coordinator returns, so a command that
    /// started a run the gate skips would announce "WiFi firmware update completed successfully" for
    /// a flash that never occurred — the one outcome worse than a false failure.
    /// </summary>
    [Fact]
    public void The_flash_wifi_button_is_enabled_only_where_the_module_can_be_flashed()
    {
        var shell = NewShell();
        shell.SelectedDevice = WincDevice();

        Assert.Equal(
            FirmwareUpdateCoordinator.CanFlashWifiModule,
            shell.UpdateWifiFirmwareOnlyCommand.CanExecute(null));
    }

    /// <summary>
    /// The negative control for the assertion above: the platform term was added to a guard that
    /// already had device terms, and must not have widened it into "enabled whenever Windows". A
    /// device with no WINC module stays out regardless.
    /// </summary>
    [Fact]
    public void A_device_without_a_winc_module_still_cannot_be_flashed()
    {
        var shell = NewShell();
        shell.SelectedDevice = new SerialStreamingDevice("COM-TEST-330-NOWINC");

        Assert.False(shell.UpdateWifiFirmwareOnlyCommand.CanExecute(null));
    }

    /// <summary>
    /// Both device panes explain the disabled button. These bindings are resolved by reflection, so
    /// nothing in either head's build can see them break — see <see cref="BindingFacts"/>.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/DevicesPanePrototype.axaml")]
    [InlineData("Daqifi.Avalonia/Views/Mobile/DevicesMobileView.axaml")]
    public void The_device_panes_explain_why_the_button_is_disabled(string viewPath)
    {
        BindingFacts.AssertBinds(viewPath, "{Binding Shell.WifiFlashUnavailableMessage}");
        BindingFacts.AssertBinds(viewPath, "IsVisible=\"{Binding !Shell.CanFlashWifiModule}\"");

        BindingFacts.AssertExposes(typeof(DaqifiViewModel), nameof(DaqifiViewModel.CanFlashWifiModule));
        BindingFacts.AssertExposes(
            typeof(DaqifiViewModel), nameof(DaqifiViewModel.WifiFlashUnavailableMessage));
    }
    #endregion

    #region Helpers
    /// <summary>
    /// The shell view model, on the throwaway data directory the assembly's module initializer points
    /// <c>DAQIFI_DATA_DIR</c> at. <c>InitializeMobile</c> is idempotent.
    /// </summary>
    private static DaqifiViewModel NewShell()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    /// <summary>A USB device that reports a separately-flashable WINC module.</summary>
    private static SerialStreamingDevice WincDevice()
    {
        var device = new SerialStreamingDevice("COM-TEST-330");
        device.Metadata.Capabilities.HasWincWifiModule = true;
        return device;
    }
    #endregion

    #region Fakes
    /// <summary>
    /// Download service that records whether the WiFi package was ever asked for — the observable
    /// difference between a flash that started and one the gate stopped — and reports no package, the
    /// cheapest clean exit from one that did.
    /// </summary>
    private sealed class RecordingDownloadService : IFirmwareDownloadService
    {
        public bool WifiDownloadWasRequested { get; private set; }

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            WifiDownloadWasRequested = true;
            return Task.FromResult<(string, string)?>(null);
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

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }
    }

    /// <summary>PIC32 update service that these tests never reach.</summary>
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
    /// Host seam recording every status write together with whether the run was open at the time,
    /// which is the condition the views gate the status line on.
    /// </summary>
    private sealed class RecordingHost : IFirmwareUpdateHost
    {
        public List<(string Text, bool WasUploading)> Writes { get; } = [];

        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices { get; } = [];

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading { get; set; }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText
        {
            set => Writes.Add((value, IsFirmwareUploading));
        }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() { }

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded() { }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
            Daqifi.Desktop.Common.Loggers.BreadcrumbLevel level
                = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel.Info)
        { }

        public void SetDeviceContext(
            string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

        public void ClearDeviceContext() { }

        public void Shutdown() { }
    }
    #endregion
}
