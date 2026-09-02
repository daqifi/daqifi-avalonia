using System.Collections.ObjectModel;
using System.Reflection;
using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the connection dialog's three-way firmware pause guard.
///
/// While a board is being written, nothing may probe the bus: the serial finder opens every DAQiFi
/// COM port each cycle and the WiFi finder UDP-broadcasts, and either can starve a flash. The dialog
/// used to gate that on <c>ConnectionManager.IsFirmwareUpdateInProgress</c> alone — which only knows
/// about a coordinator <em>auto</em>-update. A <em>manual</em> HID bootloader flash never sets
/// <c>DeviceBeingUpdated</c>, so on a two-board bench one device's auto-update finishing would restart
/// serial and WiFi discovery in the middle of the other device's HID flash.
///
/// The guard now has three reasons, and the two risks it has to balance pull in opposite directions:
/// every reason must be able to <em>hold</em> discovery down on its own, and discovery must still come
/// back once they have all cleared, in whichever order that happens. Both directions are covered here.
///
/// <para>
/// These tests never let a finder be created: a real one would open COM ports and broadcast from the
/// test host. Where a restart is triggered deliberately, either a pause reason is still active (so the
/// guard refuses it) or the drain tasks are parked so the restart defers forever. A test that goes red
/// because the guard was removed <em>will</em> create one — that is the point of the assertion.
/// </para>
/// </summary>
public class ConnectionDialogFirmwarePauseTests : IDisposable
{
    /// <summary>
    /// <c>ConnectionManager</c> is a process-wide singleton, so <c>DeviceBeingUpdated</c> is shared
    /// state between tests. Every test resets it here rather than in its own body, so a failing
    /// assertion cannot leave the app-global "a firmware update is running" gate stuck on.
    /// </summary>
    public void Dispose()
    {
        ConnectionManager.Instance.DeviceBeingUpdated = null;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Nothing_is_paused_when_no_firmware_operation_is_running()
    {
        using var viewModel = CreateViewModel(new FakeBootloaderWatcher());

        Assert.False(IsDiscoveryPausedForFirmware(viewModel.Value));
    }

    [Fact]
    public void An_auto_update_pauses_discovery()
    {
        using var viewModel = CreateViewModel(new FakeBootloaderWatcher());

        ConnectionManager.Instance.DeviceBeingUpdated = new TestDevice();

        Assert.True(IsDiscoveryPausedForFirmware(viewModel.Value));
        StartDiscovery(viewModel.Value);
        AssertNoFinderWasCreated(viewModel.Value);
    }

    [Fact]
    public void An_open_hid_firmware_dialog_pauses_discovery_even_though_the_connection_manager_knows_nothing_about_it()
    {
        using var viewModel = CreateViewModel(new FakeBootloaderWatcher());
        SetPrivateField(viewModel.Value, "_hidFirmwareDialogOpen", true);

        Assert.True(IsDiscoveryPausedForFirmware(viewModel.Value));
        Assert.False(ConnectionManager.Instance.IsFirmwareUpdateInProgress);

        StartDiscovery(viewModel.Value);
        AssertNoFinderWasCreated(viewModel.Value);
    }

    [Fact]
    public void A_bootloader_write_still_in_flight_pauses_discovery()
    {
        var watcher = new FakeBootloaderWatcher();
        using var viewModel = CreateViewModel(watcher);
        watcher.SetFlashInProgress(true);

        Assert.True(IsDiscoveryPausedForFirmware(viewModel.Value));

        StartDiscovery(viewModel.Value);
        AssertNoFinderWasCreated(viewModel.Value);
    }

    /// <summary>
    /// The reported failure, end to end: device A is auto-updating while the user opens the HID
    /// firmware dialog for device B. A's update finishes mid-window, and its
    /// <c>FirmwareUpdateInProgressChanged</c> handler used to restart discovery unconditionally —
    /// re-opening every COM port and resuming WiFi broadcasts inside the window the dialog had
    /// deliberately drained, right as the user hits Upload.
    /// </summary>
    [Fact]
    public async Task An_auto_update_of_another_device_ending_does_not_restart_discovery_during_a_hid_flash()
    {
        using var viewModel = CreateViewModel(new FakeBootloaderWatcher());
        ConnectionManager.Instance.DeviceBeingUpdated = new TestDevice();

        var ranInsideWindow = false;
        await RunHidFlashWindowAsync(viewModel.Value, () =>
        {
            ranInsideWindow = true;
            Assert.True(
                IsDiscoveryPausedForFirmware(viewModel.Value),
                "Discovery must be paused for the whole HID firmware dialog window.");

            // Device A's auto-update finishes. This raises the real event and runs the real handler.
            ConnectionManager.Instance.DeviceBeingUpdated = null;

            AssertNoFinderWasCreated(viewModel.Value);
            Assert.True(
                IsDiscoveryPausedForFirmware(viewModel.Value),
                "The open HID firmware dialog must keep discovery paused after the auto-update clears.");

            // Keep one reason active across the window's own exit restart, so this test never spins up a
            // real COM-port/UDP finder. Resuming once every reason clears is covered separately.
            ConnectionManager.Instance.DeviceBeingUpdated = new TestDevice();
            return Task.CompletedTask;
        });

        Assert.True(ranInsideWindow, "The quiesced window must have run the dialog action.");
        AssertNoFinderWasCreated(viewModel.Value);
        Assert.False(
            GetPrivateFieldValue<bool>(viewModel.Value, "_hidFirmwareDialogOpen"),
            "The HID firmware dialog pause reason must clear when the dialog closes.");
    }

    /// <summary>
    /// The pause reason goes up <em>before</em> the transports are drained, not after them. Draining
    /// them is not instant — a wedged port can hold a discovery loop for seconds — and an auto-update
    /// finishing inside that window would restart the very discovery being torn down. Reading the gate
    /// after the drains would not tell these two orderings apart, so this reads it during one.
    /// </summary>
    [Fact]
    public async Task The_pause_is_in_place_before_the_transports_finish_draining()
    {
        using var viewModel = CreateViewModel(new FakeBootloaderWatcher());
        var viewModelUnderTest = viewModel.Value;

        // Stands in for a discovery loop that has not finished winding down: the quiesce window awaits
        // this before it can show the firmware dialog.
        var drainCanFinish = new TaskCompletionSource();
        SetPrivateField(viewModelUnderTest, "_wifiDiscoveryTask", drainCanFinish.Task);

        // The call returns only once the window has suspended on that drain — an async method runs
        // synchronously up to its first real suspension point — so control is back here at a moment
        // that is provably inside the teardown. No sleep, no polling.
        var window = RunHidFlashWindowAsync(viewModelUnderTest, () =>
        {
            // Hold one reason across the window's exit restart so no real finder is ever created.
            ConnectionManager.Instance.DeviceBeingUpdated = new TestDevice();
            return Task.CompletedTask;
        });

        var pausedWhileDraining = IsDiscoveryPausedForFirmware(viewModelUnderTest);

        drainCanFinish.SetResult();
        await window;

        Assert.True(
            pausedWhileDraining,
            "The pause must already be in place while the transports are still draining, or an "
            + "auto-update ending mid-teardown restarts the discovery being torn down.");
    }

    /// <summary>
    /// The mirror-image risk: a guard that refuses restarts must not leave discovery paused forever.
    /// Here the auto-update clears first and the bootloader write second, so the watcher's falling edge
    /// is the only thing left that can retry the restart — which is why the dialog subscribes to it.
    /// </summary>
    [Fact]
    public void The_pause_clears_when_the_bootloader_write_finishes_after_the_auto_update()
    {
        var watcher = new FakeBootloaderWatcher();
        using var viewModel = CreateViewModel(watcher);

        ConnectionManager.Instance.DeviceBeingUpdated = new TestDevice();
        ParkDiscoveryDrains(viewModel.Value);
        watcher.SetFlashInProgress(true);
        Assert.True(IsDiscoveryPausedForFirmware(viewModel.Value));

        ConnectionManager.Instance.DeviceBeingUpdated = null;
        Assert.True(
            IsDiscoveryPausedForFirmware(viewModel.Value),
            "Discovery must stay paused while the HID bootloader write is still in flight.");

        watcher.SetFlashInProgress(false);

        Assert.False(
            IsDiscoveryPausedForFirmware(viewModel.Value),
            "Discovery must be free to restart once both firmware pause reasons have cleared.");
        Assert.True(
            watcher.FlashInProgressSubscriberCount > 0,
            "The dialog must subscribe to the watcher's flash-state event, or a write finishing last " +
            "would leave discovery paused for the rest of the dialog's life.");
    }

    /// <summary>Same as above with the opposite completion order.</summary>
    [Fact]
    public void The_pause_clears_when_the_auto_update_finishes_after_the_bootloader_write()
    {
        var watcher = new FakeBootloaderWatcher();
        using var viewModel = CreateViewModel(watcher);

        ConnectionManager.Instance.DeviceBeingUpdated = new TestDevice();
        ParkDiscoveryDrains(viewModel.Value);
        watcher.SetFlashInProgress(true);

        watcher.SetFlashInProgress(false);
        Assert.True(
            IsDiscoveryPausedForFirmware(viewModel.Value),
            "Discovery must stay paused while the auto-update is still running.");

        ConnectionManager.Instance.DeviceBeingUpdated = null;

        Assert.False(IsDiscoveryPausedForFirmware(viewModel.Value));
    }

    /// <summary>
    /// The watcher is an app-global singleton, so a leaked handler would keep a dismissed dialog alive
    /// and let it restart discovery long after it was closed.
    /// </summary>
    [Fact]
    public void Closing_the_dialog_unsubscribes_it_from_the_watchers_flash_event()
    {
        var watcher = new FakeBootloaderWatcher();
        using var viewModel = CreateViewModel(watcher);
        Assert.True(watcher.FlashInProgressSubscriberCount > 0);

        viewModel.Value.Close();

        Assert.Equal(0, watcher.FlashInProgressSubscriberCount);
    }

    #region Harness
    /// <summary>
    /// Builds a view model wired to <paramref name="watcher"/>, with its UI marshal replaced by a
    /// direct call.
    ///
    /// <para>
    /// That replacement is required, not a convenience. Outside a running Avalonia app
    /// <c>Dispatcher.UIThread</c> binds to whichever thread touches it first and is never pumped, so the
    /// production marshal's blocking <c>Invoke</c> would never return when a handler is reached from any
    /// other thread — and xUnit gives no thread affinity. Everything these tests exercise is
    /// synchronous, single-threaded state, so running the callbacks inline is faithful.
    /// </para>
    ///
    /// <para>
    /// The dialog service is null: nothing here shows a dialog. The HID flash window is driven directly
    /// with a test delegate standing in for the modal firmware dialog.
    /// </para>
    /// </summary>
    private static ClosingViewModel CreateViewModel(IBootloaderWatcher watcher)
    {
        var viewModel = new ConnectionDialogViewModel(null!, watcher);
        SetPrivateField(viewModel, "_marshalToUiThread", (Action<Action>)(action => action()));
        return new ClosingViewModel(viewModel);
    }

    /// <summary>
    /// Closes the view model at the end of a test. Without it a finished test's dialog stays subscribed
    /// to the <c>ConnectionManager</c> singleton and reacts to the next test's firmware transitions.
    /// </summary>
    private sealed class ClosingViewModel(ConnectionDialogViewModel value) : IDisposable
    {
        public ConnectionDialogViewModel Value { get; } = value;

        public void Dispose() => Value.Close();
    }

    /// <summary>
    /// Parks both discovery drains on a task that never completes, so a restart triggered by these
    /// tests defers instead of creating a real finder. The assertions then read the pause gate itself,
    /// which is what every <c>Start*Discovery</c> consults before touching hardware.
    /// </summary>
    private static void ParkDiscoveryDrains(ConnectionDialogViewModel viewModel)
    {
        var neverDrains = new TaskCompletionSource().Task;
        SetPrivateField(viewModel, "_wifiDiscoveryTask", neverDrains);
        SetPrivateField(viewModel, "_serialDiscoveryTask", neverDrains);
    }

    /// <summary>
    /// Calls the two guarded discovery starts the way <c>StartConnectionFinders</c> does (without its
    /// breadcrumb, which is not under test).
    ///
    /// <para>
    /// The firmware guard inside those methods is the <em>only</em> thing standing between this call and
    /// a real UDP socket plus a sweep of every COM port, which is exactly why the assertion that follows
    /// is worth making: a guard that has been removed cannot be caught any other way. The cost is that a
    /// red run on a machine with a DAQiFi attached will briefly probe it — bounded by the view model
    /// being closed when the test ends, including on a failed assertion.
    /// </para>
    /// </summary>
    private static void StartDiscovery(ConnectionDialogViewModel viewModel)
    {
        InvokePrivate(viewModel, "StartWiFiDiscovery");
        InvokePrivate(viewModel, "StartSerialDiscovery");
    }

    private static void AssertNoFinderWasCreated(ConnectionDialogViewModel viewModel)
    {
        Assert.Null(GetPrivateField(viewModel, "_serialFinder"));
        Assert.Null(GetPrivateField(viewModel, "_wifiFinder"));
    }

    /// <summary>
    /// Runs the dialog's HID firmware-dialog quiesce window, with <paramref name="whileOpen"/> standing
    /// in for the modal firmware dialog.
    /// </summary>
    private static async Task RunHidFlashWindowAsync(ConnectionDialogViewModel viewModel, Func<Task> whileOpen)
    {
        var method = typeof(ConnectionDialogViewModel).GetMethod(
            "RunWithHidFlashQuiescedAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task?)method.Invoke(viewModel, [whileOpen]);
        Assert.NotNull(task);
        await task;
    }

    private static bool IsDiscoveryPausedForFirmware(ConnectionDialogViewModel viewModel)
    {
        var property = typeof(ConnectionDialogViewModel).GetProperty(
            "IsDiscoveryPausedForFirmware", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return (bool)property.GetValue(viewModel)!;
    }

    private static void InvokePrivate(ConnectionDialogViewModel viewModel, string methodName)
    {
        var method = typeof(ConnectionDialogViewModel).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(viewModel, null);
    }

    private static object? GetPrivateField(ConnectionDialogViewModel viewModel, string fieldName)
    {
        var field = typeof(ConnectionDialogViewModel).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(viewModel);
    }

    private static T GetPrivateFieldValue<T>(ConnectionDialogViewModel viewModel, string fieldName) =>
        (T)GetPrivateField(viewModel, fieldName)!;

    private static void SetPrivateField(ConnectionDialogViewModel viewModel, string fieldName, object? value)
    {
        var field = typeof(ConnectionDialogViewModel).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(viewModel, value);
    }
    #endregion

    #region Fakes
    /// <summary>
    /// The cheapest real <see cref="IStreamingDevice"/>: <c>DeviceBeingUpdated</c> only needs an
    /// instance, and nothing in these tests connects or sends.
    /// </summary>
    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Minimal <see cref="IBootloaderWatcher"/> stand-in that lets a test drive the flash-in-progress
    /// state the dialog gates on, and reports how many handlers are attached to the change event.
    /// </summary>
    private sealed class FakeBootloaderWatcher : IBootloaderWatcher
    {
        private readonly ObservableCollection<HeldBootloader> _bootloaders = [];
        private EventHandler? _flashInProgressChanged;

        public FakeBootloaderWatcher() =>
            Bootloaders = new ReadOnlyObservableCollection<HeldBootloader>(_bootloaders);

        public ReadOnlyObservableCollection<HeldBootloader> Bootloaders { get; }

#pragma warning disable CS0067 // Part of the interface; no test in this class drops a hold.
        public event EventHandler<BootloaderHoldDroppedEventArgs>? HoldDropped;
#pragma warning restore CS0067

        public event EventHandler? FlashInProgressChanged
        {
            add => _flashInProgressChanged += value;
            remove => _flashInProgressChanged -= value;
        }

        public bool IsFlashInProgress { get; private set; }

        public int FlashInProgressSubscriberCount => _flashInProgressChanged?.GetInvocationList().Length ?? 0;

        public void SetFlashInProgress(bool inProgress)
        {
            if (IsFlashInProgress == inProgress)
            {
                return;
            }

            IsFlashInProgress = inProgress;
            _flashInProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Start() { }

        public Task<IAsyncDisposable> PrepareFlashAsync(string devicePath)
        {
            SetFlashInProgress(true);
            return Task.FromResult<IAsyncDisposable>(new FakeLease(() => SetFlashInProgress(false)));
        }

        public Task<IAsyncDisposable> SuspendDiscoveryAsync() =>
            Task.FromResult<IAsyncDisposable>(new FakeLease(() => { }));

        private sealed class FakeLease(Action onDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }
    #endregion
}
