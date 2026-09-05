using System.Collections.ObjectModel;
using System.Reflection;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins the two halves of issue #241: that a running firmware upload can be cancelled from the UI,
/// and that the status line describing it is actually rendered.
///
/// <para>
/// Both commands (<c>DaqifiViewModel.CancelFirmwareUploadCommand</c> and
/// <c>FirmwareDialogViewModel.CancelUploadFirmwareCommand</c>) were implemented, correct and bound by
/// no view; <c>FirmwareUpdateStatusText</c> was assigned 18 times and read zero times. Nothing in C#
/// could detect either, because both are absences in XAML — and Avalonia bindings here are resolved
/// by reflection, so a missing or misspelled one fails silently at runtime while the build stays
/// green. The XAML facts below are therefore deliberately textual: they assert the binding exists in
/// the view AND that the member it names exists on the view model, which is the pair a reflection
/// binding needs and the pair nothing else in the build checks.
/// </para>
///
/// <para>
/// The status line is rendered only while <c>IsFirmwareUploading</c> is true, which makes it
/// unambiguously "what the running flash is doing" and means it can never be left displaying a
/// finished run's last message. The coordinator facts pin the invariant that design depends on:
/// every status write happens inside a run. Four writes did not satisfy it — one was fixed by opening
/// the run earlier, one moved out of <c>DaqifiViewModel</c> into the coordinator, and two could never
/// satisfy it from where they stood and were deleted (see <c>DaqifiViewModel</c>).
/// </para>
///
/// <para>
/// The fakes here are intentionally separate from the ones in
/// <see cref="FirmwareUpdateCancellationTests"/>: those park on the token they are handed so a test
/// can observe which token a flash is running on, while these record the status writes and the flag
/// state at each one. Merging them would mean a double that both parks and records, configured per
/// test — more indirection than either file saves.
/// </para>
/// </summary>
public class FirmwareUploadAffordanceTests : IDisposable
{
    private readonly RecordingHost _host = new();
    private readonly NoPackageDownloadService _downloads = new();
    private readonly StubUpdateService _updates = new();
    private readonly SilentLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-affordance-tests", Guid.NewGuid().ToString("N"));

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
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region The status line is only ever written while it is on screen
    /// <summary>
    /// The combined update's first message — the one that tells the user the click registered — must
    /// land after the run has opened, or it is written to a hidden control and lost.
    /// </summary>
    [Fact]
    public async Task The_combined_update_announces_itself_while_the_status_line_is_visible()
    {
        var coordinator = CreateCoordinator();
        _host.SelectedDevice = UsbDevice();

        // Ends at the "device is not connected" guard, which is the cheapest exit from the combined
        // path that still runs the whole preamble. Nothing here touches a serial port.
        await coordinator.UploadFirmwareAsync();

        Assert.Equal("Preparing firmware update...", _host.Writes[0].Text);
        Assert.All(_host.Writes, write => Assert.True(write.WasUploading, $"'{write.Text}' was written while the status line was hidden."));
    }

    /// <summary>
    /// The same guarantee for the WiFi-only path, whose "Preparing..." message used to be written by
    /// the view model — before the coordinator had opened the run, so never on screen.
    /// </summary>
    [Fact]
    public async Task The_wifi_only_update_announces_itself_while_the_status_line_is_visible()
    {
        var coordinator = CreateCoordinator();

        // "No package found" is the cheapest clean exit from the WiFi flash that never touches serial.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleOnlyAsync(WincDevice()));

        Assert.Equal("Preparing WiFi firmware update...", _host.Writes[0].Text);
        Assert.All(_host.Writes, write => Assert.True(write.WasUploading, $"'{write.Text}' was written while the status line was hidden."));
    }

    /// <summary>
    /// Opening the run before the first status write puts the uploading flag up before anything that
    /// can throw. Everything after <c>BeginUpload</c> must therefore sit inside the try: a throw
    /// between the two would leave the flag raised with no finally to lower it, and the pane stuck
    /// "uploading" behind a Cancel button with nothing left to cancel.
    /// </summary>
    [Fact]
    public async Task A_throw_from_the_first_status_write_still_ends_the_run()
    {
        var coordinator = CreateCoordinator();
        _host.SelectedDevice = UsbDevice();
        _host.ThrowOnStatusWrite = true;

        await coordinator.UploadFirmwareAsync();

        Assert.False(_host.IsFirmwareUploading);
    }

    /// <summary>
    /// The status line now has exactly one writer — the coordinator, through the host seam. The view
    /// model's own three writes all ran outside a run and could never have been displayed.
    /// </summary>
    [Fact]
    public void The_shell_view_model_no_longer_writes_the_status_line_itself()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "Daqifi.Avalonia", "Daqifi.Desktop", "ViewModels", "DaqifiViewModel.cs"));

        Assert.DoesNotContain("FirmwareUpdateStatusText =", source, StringComparison.Ordinal);
    }
    #endregion

    #region The cancel control and the status line exist in the views
    /// <summary>
    /// The bootloader dialog raises a modal scrim over every control it has, including its own Cancel
    /// button, so before this binding existed a stalled flash could only be escaped by killing the
    /// app. This is the only binding of the command.
    /// </summary>
    [Fact]
    public void The_bootloader_dialog_binds_its_cancel_command()
    {
        AssertViewBinds(
            Path.Combine("Daqifi.Avalonia", "Daqifi.Desktop", "View", "FirmwareDialog.axaml"),
            "{Binding CancelUploadFirmwareCommand}");

        AssertViewModelExposes(typeof(FirmwareDialogViewModel), "CancelUploadFirmwareCommand");
    }

    /// <summary>
    /// The device pane drives both the combined update and the WiFi-only flash, and both write the
    /// same status line and raise the same uploading flag — so one cancel and one status line serve
    /// both. Desktop and mobile are separate views over the same view model and must not drift.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/DevicesPanePrototype.axaml")]
    [InlineData("Daqifi.Avalonia/Views/Mobile/DevicesMobileView.axaml")]
    public void The_device_pane_binds_cancel_and_the_status_line(string viewPath)
    {
        AssertViewBinds(viewPath, "{Binding Shell.CancelFirmwareUploadCommand}");
        AssertViewBinds(viewPath, "{Binding Shell.FirmwareUpdateStatusText}");

        // Both are gated on the uploading flag, which is what keeps a finished run's last message off
        // the screen and the cancel button out of an idle pane.
        AssertViewBinds(viewPath, "IsVisible=\"{Binding Shell.IsFirmwareUploading}\"");

        AssertViewModelExposes(typeof(DaqifiViewModel), "CancelFirmwareUploadCommand");
        AssertViewModelExposes(typeof(DaqifiViewModel), "FirmwareUpdateStatusText");
        AssertViewModelExposes(typeof(DaqifiViewModel), "IsFirmwareUploading");
    }
    #endregion

    #region Helpers
    /// <summary>A USB device whose Core side is not connected, so the combined path stops at its guard.</summary>
    private static SerialStreamingDevice UsbDevice() => new("COM-TEST-241");

    /// <summary>A USB device that reports a separately-flashable WINC module, so the WiFi flash is attempted.</summary>
    private static SerialStreamingDevice WincDevice()
    {
        var device = new SerialStreamingDevice("COM-TEST-241-WINC");
        device.Metadata.Capabilities.HasWincWifiModule = true;
        return device;
    }

    private static void AssertViewBinds(string repoRelativeViewPath, string expectedBinding)
    {
        var fullPath = Path.Combine(RepoRoot(), repoRelativeViewPath.Replace('/', Path.DirectorySeparatorChar));
        var markup = File.ReadAllText(fullPath);

        Assert.Contains(expectedBinding, markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reflection binding resolves against the runtime type, so the member must be public and
    /// readable there — the half of the binding the compiler never checks.
    /// </summary>
    private static void AssertViewModelExposes(Type viewModel, string memberName)
    {
        var property = viewModel.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.True(property.CanRead, $"{viewModel.Name}.{memberName} cannot be read by a binding.");
    }

    /// <summary>Walks up from the test binary to the checkout, identified by the solution file.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Daqifi.Avalonia.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
    #endregion

    #region Fakes
    /// <summary>
    /// Host seam that records every status write together with whether the run was open at the time —
    /// which is exactly the condition the views gate the status line on.
    /// <c>FirmwareUpdateStatusText</c> is set-only on the interface, so this adds the recording.
    /// </summary>
    private sealed class RecordingHost : IFirmwareUpdateHost
    {
        public List<(string Text, bool WasUploading)> Writes { get; } = [];

        /// <summary>
        /// Makes the bound setter throw, standing in for a binding handler that faults. Nothing here
        /// simulates Avalonia; it only needs to be a throw from the first statement of a run.
        /// </summary>
        public bool ThrowOnStatusWrite { get; set; }

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
            set
            {
                Writes.Add((value, IsFirmwareUploading));
                if (ThrowOnStatusWrite)
                {
                    throw new InvalidOperationException("bound status setter faulted");
                }
            }
        }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() { }

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded() { }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>Download service that reports no publishable package, the cheapest clean exit from a flash.</summary>
    private sealed class NoPackageDownloadService : IFirmwareDownloadService
    {
        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<(string, string)?>(null);

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
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
