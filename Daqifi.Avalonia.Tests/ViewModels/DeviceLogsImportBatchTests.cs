using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.Services;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// What <c>DeviceLogsViewModel</c> decides while an SD card import is running: which files it asks
/// the importer for, when it stops asking, and when a failure is allowed to paint the card-wide
/// error panel.
///
/// <para>
/// <see cref="ImportAllSummaryTests"/> covers the other half — how a finished
/// <c>ImportAllOutcome</c> is worded — but it builds the outcome by hand, so nothing until now
/// exercised the loop that FILLS it. That loop holds the decision
/// <see cref="Daqifi.Desktop.ViewModels.SdCardFailure.IsCardUnavailable"/> exists for: a failure
/// that might be about one file is skipped and the batch carries on, while a failure that is about
/// the card stops it. Getting that backwards is silent and expensive — the file list comes back in
/// the same order every time, so aborting on a per-file fault makes every file listed after it
/// permanently unreachable through IMPORT ALL, and carrying on past a card-wide one grinds the user
/// through one multi-second failure per remaining file.
/// </para>
///
/// <para>
/// These drive the view model through <c>ISdCardSessionImporter</c>, the injection seam on its
/// internal constructor. The success path is deliberately absent: a persisted import calls
/// <c>AddImportedSession</c>, which reaches <c>LoggingManager.Instance</c> — a singleton whose
/// constructor resolves off <c>App.ServiceProvider</c>, null outside the app host — through
/// <c>Dispatcher.UIThread.Invoke</c>, which never returns in a process with no pumped dispatcher.
/// Both are global, neither is injectable here, and this project builds no application lifetime
/// (see its csproj). So everything below keeps the importer on a path that does not persist a
/// session: a skip, or a throw.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class DeviceLogsImportBatchTests : IDisposable
{
    private const string FileA = "LOG_0001.bin";
    private const string FileB = "LOG_0002.bin";
    private const string FileC = "LOG_0003.bin";

    private readonly List<IStreamingDevice> _registered = [];
    private readonly RecordingMessageBox _dialogs = new();
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }
    }

    #region Which files the batch asks for
    [Fact]
    public async Task Import_all_asks_the_importer_for_every_listed_file_in_order()
    {
        var (vm, _, importer) = await PaneWithThreeFiles();

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        Assert.Equal([FileA, FileB, FileC], importer.Requested);
    }

    /// <summary>
    /// The reason <c>IsCardUnavailable</c> is documented as "deliberately narrow": a fault the
    /// device attributes to one file must not cost the user the files listed after it.
    /// </summary>
    [Fact]
    public async Task A_failure_that_may_be_this_one_file_skips_it_and_the_batch_carries_on()
    {
        var (vm, _, importer) = await PaneWithThreeFiles();
        importer.Failures[FileB] = new SdCardTransferErrorException(FileB, bytesReceived: 4096);

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        Assert.Equal([FileA, FileB, FileC], importer.Requested);

        // And the failed file is named in the summary so the user can retry it on its own: B went
        // through the catch's skip, A and C through the no-session skip, so all three are counted.
        var summary = ImportAllSummary();
        Assert.Contains("Skipped 3 file(s).", summary);
        Assert.Contains($"• {FileB}", summary);
        Assert.DoesNotContain("Import stopped", summary);
    }

    /// <summary>
    /// And the other side of it: with no card in the slot every remaining file would fail the same
    /// way, so the batch stops instead of re-proving it once per file.
    /// </summary>
    [Fact]
    public async Task A_card_wide_failure_stops_the_batch_instead_of_re_failing_every_file()
    {
        var (vm, _, importer) = await PaneWithThreeFiles();
        importer.Failures[FileB] = new SdCardNotPresentException(rawDeviceResponse: []);

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        Assert.Equal([FileA, FileB], importer.Requested);
        Assert.DoesNotContain(FileC, importer.Requested);

        // Reported as where the run stopped, not as one more skipped file.
        var summary = ImportAllSummary();
        Assert.Contains($"Import stopped at {FileB}:", summary);
        Assert.Contains("Skipped 1 file(s).", summary);
        Assert.DoesNotContain($"• {FileB}", summary);
    }

    /// <summary>
    /// A stall is the case where the same exception type has to fall on both sides of that line.
    /// The per-read stall fires on a momentary gap on a healthy device, so it costs one file; the
    /// whole-transfer deadline and a closed transport are statements about the device, so they end
    /// the run. The classifier decides which (<see cref="SdCardFailureClassifierTests"/>); this
    /// pins that the loop acts on the answer.
    /// </summary>
    [Theory]
    [InlineData(SdCardTransferStallReason.NoDataReceived, true)]
    [InlineData(SdCardTransferStallReason.TransferTimeout, false)]
    [InlineData(SdCardTransferStallReason.TransportClosed, false)]
    public async Task A_stall_ends_the_batch_only_when_it_is_evidence_about_the_device(
        SdCardTransferStallReason reason, bool laterFilesStillTried)
    {
        var (vm, _, importer) = await PaneWithThreeFiles();
        importer.Failures[FileB] =
            new SdCardTransferStalledException(FileB, bytesReceived: 0, reason, TimeSpan.FromSeconds(90));

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        Assert.Equal(laterFilesStillTried, importer.Requested.Contains(FileC));
    }

    /// <summary>
    /// Nothing threw — the importer simply produced no finished session, which is what an empty log
    /// left by an interrupted logging session does. That is a skip, not a stop.
    /// </summary>
    [Fact]
    public async Task A_file_that_produced_no_session_is_skipped_and_the_batch_carries_on()
    {
        var (vm, _, importer) = await PaneWithThreeFiles();

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        // Every response this fake gives is a non-persisted one, so all three took the skip path.
        Assert.Equal([FileA, FileB, FileC], importer.Requested);

        // Carrying on is only half of it: each empty log must be COUNTED as skipped, not dropped
        // from the summary and not counted as imported, and the importer's guidance read out once.
        var summary = ImportAllSummary();
        Assert.StartsWith("Imported 0 of 3 files.", summary);
        Assert.Contains("Skipped 3 file(s).", summary);
        Assert.Contains($"• {FileA}", summary);
        Assert.Contains($"• {FileB}", summary);
        Assert.Contains($"• {FileC}", summary);
        Assert.Equal(2, summary.Split(RecordingSessionImporter.NoSamplesGuidance).Length); // exactly once
    }

    /// <summary>
    /// The connectivity re-check at the top of each iteration. Without it the loop would keep
    /// issuing downloads against a dead transport — <c>CoreDevice</c> stays non-null, so the
    /// downstream null-guard still passes.
    /// </summary>
    [Fact]
    public async Task A_device_that_goes_away_mid_batch_stops_the_run()
    {
        var (vm, device, importer) = await PaneWithThreeFiles();
        importer.OnImport = file =>
        {
            if (file == FileA)
            {
                device.IsConnected = false;
            }
        };

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        Assert.Equal([FileA], importer.Requested);
        Assert.Contains("Import stopped early: the device disconnected.", ImportAllSummary());
    }
    #endregion

    #region When a failure is allowed to paint the card-wide panel
    /// <summary>
    /// <c>HasFiles</c> requires <c>SdCardState == Ok</c>, so painting the card-wide Error panel for
    /// a single bad file would hide the whole list while the batch was still importing the rest of
    /// it — a sticky panel contradicting the summary the run ends with.
    /// </summary>
    [Fact]
    public async Task A_per_file_failure_leaves_the_card_panel_alone()
    {
        var (vm, _, importer) = await PaneWithThreeFiles();
        importer.Failures[FileB] = new SdCardTransferErrorException(FileB, bytesReceived: 4096);

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        Assert.Equal(SdCardState.Ok, vm.SdCardState);
    }

    [Fact]
    public async Task A_card_wide_failure_paints_the_card_panel()
    {
        var (vm, _, importer) = await PaneWithThreeFiles();
        importer.Failures[FileB] = new SdCardNotPresentException(rawDeviceResponse: []);

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        Assert.Equal(SdCardState.NotPresent, vm.SdCardState);
    }

    /// <summary>
    /// A single import snapshots the device it started on, so a selection change while it runs can
    /// neither retarget the download nor misattribute its failure. Reading <c>SelectedDevice</c>
    /// again in the failure path instead would satisfy the "still the selected device" guard and
    /// paint the card-wide panel over a second device whose card is fine.
    /// </summary>
    [Fact]
    public async Task A_failure_is_not_painted_onto_a_device_the_user_moved_to_mid_import()
    {
        var a = Device("AAAA0001", FileA);
        var b = Device("BBBB0002", FileB);
        Register(a);
        Register(b);

        var importer = new RecordingSessionImporter();
        var vm = OpenPane(importer);
        await Settle(vm);
        Assert.Same(a, vm.SelectedDevice);
        Assert.Equal(SdCardState.Ok, vm.SdCardState);

        importer.Failures[FileA] = new SdCardNotPresentException(rawDeviceResponse: []);
        importer.OnImport = _ =>
        {
            // The user moves the combo to B while A's download is in flight, and B's card lists
            // fine. Waited out here so the assertion below cannot race that refresh.
            vm.SelectedDevice = b;
            vm.InitialRefreshTask?.GetAwaiter().GetResult();
        };

        await vm.ImportFileCommand.ExecuteAsync(new SdCardFile { FileName = FileA });

        Assert.Same(a, Assert.Single(importer.Devices));
        Assert.Same(b, vm.SelectedDevice);
        Assert.Equal(SdCardState.Ok, vm.SdCardState);
    }
    #endregion

    #region Helpers
    /// <summary>One connected device listing three log files, with the pane settled on it.</summary>
    private async Task<(DeviceLogsViewModel Vm, RecordingStreamingDevice Device, RecordingSessionImporter Importer)>
        PaneWithThreeFiles()
    {
        var device = Device("AAAA0001", FileA, FileB, FileC);
        Register(device);

        var importer = new RecordingSessionImporter();
        var vm = OpenPane(importer);
        await Settle(vm);

        Assert.Same(device, vm.SelectedDevice);
        Assert.Equal([FileA, FileB, FileC], vm.DeviceFiles.Select(f => f.FileName));
        Assert.Equal(SdCardState.Ok, vm.SdCardState);

        return (vm, device, importer);
    }

    private static RecordingStreamingDevice Device(string serial, params string[] files) =>
        new(serial) { SdCardFiles = [.. files.Select(f => new SdCardFile { FileName = f })] };

    private DeviceLogsViewModel OpenPane(RecordingSessionImporter importer) =>
        // Marshal inline, and not at all once the test has torn down — the view model outlives it
        // on the process-wide ConnectionManager. Same arrangement as DeviceLogsSelectionTests.
        new(new RecordingAppLogger(), importer, update =>
        {
            if (!_disposed)
            {
                update();
            }
        }, _dialogs);

    /// <summary>The body of the one "Import Complete" dialog the batch ended with.</summary>
    private string ImportAllSummary() =>
        Assert.Single(_dialogs.Shown, d => d.Caption == "Import Complete").Text;

    /// <summary>Waits out the SD listing the current selection started.</summary>
    private static async Task Settle(DeviceLogsViewModel vm)
    {
        if (vm.InitialRefreshTask != null)
        {
            await vm.InitialRefreshTask;
        }
    }

    private void Register(IStreamingDevice device)
    {
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
    }

    /// <summary>
    /// An importer that records what it was asked to import and can be told to fail a named file.
    /// Every request it does not fail answers with a result carrying no persisted session, which is
    /// the batch's skip path — see this class's remarks for why the persisted path is out of reach.
    /// </summary>
    private sealed class RecordingSessionImporter : ISdCardSessionImporter
    {
        internal const string NoSamplesGuidance = "The log held no samples.";

        /// <summary>File names this importer was asked for, in order.</summary>
        internal List<string> Requested { get; } = [];

        /// <summary>The device each request named, in the same order.</summary>
        internal List<IStreamingDevice> Devices { get; } = [];

        /// <summary>When set, the named file throws this instead of returning a result.</summary>
        internal Dictionary<string, Exception> Failures { get; } = [];

        /// <summary>Runs when a request arrives, after it is recorded and before it fails.</summary>
        internal Action<string>? OnImport { get; set; }

        public Task<SdCardImportResult> ImportFromDeviceAsync(
            IStreamingDevice device,
            string fileName,
            ImportOptions? options = null,
            IProgress<ImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            Requested.Add(fileName);
            Devices.Add(device);
            OnImport?.Invoke(fileName);

            if (Failures.TryGetValue(fileName, out var failure))
            {
                return Task.FromException<SdCardImportResult>(failure);
            }

            return Task.FromResult(new SdCardImportResult
            {
                Session = new LoggingSession(),
                SamplesImported = 0,
                SessionPersisted = false,
                OutcomeGuidance = NoSamplesGuidance,
                TimestampQuality = new ImportTimestampQuality(),
            });
        }
    }

    /// <summary>Records every dialog instead of showing it.</summary>
    private sealed class RecordingMessageBox : IMessageBoxService
    {
        internal List<(string Caption, string Text)> Shown { get; } = [];

        public Task<MessageBoxResult> ShowAsync(
            string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            Shown.Add((caption, messageBoxText));
            return Task.FromResult(MessageBoxResult.OK);
        }
    }
    #endregion
}
