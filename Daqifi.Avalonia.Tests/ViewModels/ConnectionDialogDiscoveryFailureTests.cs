using System.ComponentModel;
using System.Reflection;
using Daqifi.Core.Device.Discovery;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins what the connection dialog does when device discovery keeps failing, and what the user is
/// told about it (issue #290).
///
/// <para>
/// Both discovery loops used to put their general <c>catch (Exception)</c> outside the <c>while</c>,
/// so a single faulted sweep ended discovery for the life of the dialog rather than ending that one
/// sweep; and the give-up reported its remedy only to <c>DAQifiAppLog.log</c>. In both cases the
/// bound state was indistinguishable from "still looking", so the tab kept animating "Scanning for
/// USB devices…" for ever.
/// </para>
///
/// <para>
/// The loop itself now belongs to Core's <c>ContinuousDeviceFinder</c>, which scopes a faulted pass
/// correctly on its own — it reports the fault through <c>ScanError</c> and keeps scanning. What
/// stays in the dialog, and is what these tests cover, is the give-up: a library is right to retry
/// for ever, and a UI that cannot say "this is not working" is left animating over a permanently
/// broken finder. These tests drive a real <c>ContinuousDeviceFinder</c> — only the wrapped finder is
/// a stand-in — so they exercise the same wiring production uses rather than a hand-invoked loop.
/// </para>
///
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because <c>StartSerialDiscovery</c> reads
/// <c>ConnectionManager.Instance.IsFirmwareUpdateInProgress</c> and refuses to start while it is set.
/// Nothing here opens a port or a socket: every finder is a scripted stand-in installed through the
/// view model's own seams.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class ConnectionDialogDiscoveryFailureTests
{
    /// <summary>
    /// Upper bound on how long a test waits for the dialog to reach the state under test. Generous on
    /// purpose: it is a deadlock guard, not a timing assertion. The scans are driven at
    /// <see cref="TestInterval"/> so the wait itself is milliseconds.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Scan cadence these tests run at. The shipped cadence is seconds per pass and the give-up needs
    /// three of them, so pinning that path at the real value would cost most of a minute in wall clock
    /// for assertions that are about ordering rather than timing.
    /// </summary>
    private static readonly TimeSpan TestInterval = TimeSpan.FromMilliseconds(5);

    #region Serial

    /// <summary>
    /// The scoping half of #290. The wrapped finder enumerates the machine's ports on every pass with
    /// no per-pass guard, so one hiccup there is one bad pass — not a reason to stop looking for the
    /// rest of the dialog's life.
    /// </summary>
    [Fact]
    public async Task A_faulting_serial_pass_ends_the_pass_not_the_scan()
    {
        var script = new SweepScript(sweep => sweep == 1
            ? Fault()
            : OneDeviceOn("COM-AFTER-FAULT"));

        using var viewModel = CreateViewModel(script);
        StartSerialDiscovery(viewModel.Value);

        // The device only ever arrives on a pass after the faulted one, so seeing it is proof the scan
        // kept going.
        await WaitUntil(
            viewModel.Value,
            vm => vm.AvailableSerialDevices.Count == 1,
            "One faulted pass must end that pass, not USB discovery.");

        Assert.Null(viewModel.Value.SerialDiscoveryError);
        Assert.NotNull(GetPrivateField(viewModel.Value, "_serialFinder"));
    }

    /// <summary>
    /// The other side of the same decision: tolerating a transient fault must not mean tolerating a
    /// permanently broken finder. After the third consecutive fault the scan stops — and says so.
    /// </summary>
    [Fact]
    public async Task Repeated_serial_pass_faults_stop_discovery_and_say_why()
    {
        var script = new SweepScript(_ => Fault());

        using var viewModel = CreateViewModel(script);
        StartSerialDiscovery(viewModel.Value);

        await WaitUntil(
            viewModel.Value,
            vm => vm.SerialDiscoveryError != null,
            "Three consecutive faulted passes must stop USB discovery and say so.");

        Assert.False(
            viewModel.Value.IsSerialDiscoveryScanning,
            "The animated 'Scanning for USB devices…' overlay binds to this, so it must stop making that claim.");
        Assert.Equal(
            MaxConsecutiveDiscoveryFaults,
            GetPrivateFieldValue<int>(viewModel.Value, "_serialConsecutiveFaults"));
    }

    /// <summary>
    /// The give-up must also retire the scan, not merely label it. A finder left running would keep
    /// probing every COM port on the machine for the life of the dialog while the tab says discovery
    /// has stopped — and would hold the very port the user is being told to power-cycle.
    /// </summary>
    [Fact]
    public async Task Giving_up_stops_the_scan_rather_than_just_labelling_it()
    {
        var script = new SweepScript(_ => Fault());

        using var viewModel = CreateViewModel(script);
        StartSerialDiscovery(viewModel.Value);

        await WaitUntil(
            viewModel.Value,
            vm => vm.SerialDiscoveryError != null,
            "Discovery must give up before this can be checked.");

        // The stop is started from the scan-loop thread and cannot be awaited from there, so it
        // completes just after the message lands.
        await WaitFor(
            () => GetPrivateField(viewModel.Value, "_serialFinder") == null,
            "The scan the dialog gave up on must be stopped, not left probing ports.");

        var sweepsAtGiveUp = script.Sweeps;
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(sweepsAtGiveUp, script.Sweeps);
    }

    /// <summary>
    /// The sharper version of the same case, and the one the overlay cannot carry: a pass found a
    /// board, so the list is no longer empty and the "Scanning…" overlay is already gone, and only
    /// then does discovery give up. The user is left with a stale tile that will never be joined by
    /// anything else, so this is exactly when they most need to be told — which means the message
    /// cannot live inside the empty-list overlay.
    /// </summary>
    [Fact]
    public async Task A_discovery_that_found_a_device_first_still_says_it_gave_up()
    {
        var script = new SweepScript(sweep => sweep == 1
            ? OneDeviceOn("COM7")
            : Fault());

        using var viewModel = CreateViewModel(script);
        StartSerialDiscovery(viewModel.Value);

        await WaitUntil(
            viewModel.Value,
            vm => vm.SerialDiscoveryError != null,
            "Discovery must still report giving up when it had already found a board.");

        Assert.False(
            viewModel.Value.HasNoSerialDevices,
            "The device found by the first pass is still listed, which is the whole point of this case.");
        Assert.False(viewModel.Value.IsSerialDiscoveryScanning);
    }

    /// <summary>
    /// "Consecutive" has to mean consecutive. The fault counter is reset by the wrapped finder's
    /// <c>DiscoveryCompleted</c>, which it raises only on a pass that did not fault — so a board that
    /// makes one pass in three go wrong is a nuisance, not a reason to stop looking for it.
    /// </summary>
    /// <remarks>
    /// The loop this replaced reset an in-scope local on every clean sweep. Nothing about the move to
    /// Core preserves that for free: <c>ContinuousDeviceFinder</c> reports faults but has no per-pass
    /// completion signal of its own, so the reset had to be re-derived from the wrapped finder's own
    /// event. Pinned here because getting it wrong is silent — discovery would simply give up on a
    /// device that was working.
    /// </remarks>
    [Fact]
    public async Task A_clean_pass_between_faults_stops_the_faults_adding_up()
    {
        // Never three in a row, but far more than three in total.
        var script = new SweepScript(sweep => sweep % 3 == 0
            ? OneDeviceOn("COM-FLAKY")
            : Fault());

        using var viewModel = CreateViewModel(script);
        StartSerialDiscovery(viewModel.Value);

        await WaitFor(
            () => script.Sweeps > 4 * MaxConsecutiveDiscoveryFaults,
            "The scan must keep running long enough for the total fault count to exceed the give-up bound.");

        Assert.Null(viewModel.Value.SerialDiscoveryError);
        Assert.NotNull(GetPrivateField(viewModel.Value, "_serialFinder"));

        // Not IsSerialDiscoveryScanning: the clean passes list COM-FLAKY, and the "Scanning for USB
        // devices…" overlay is bound to the list being empty, so it is correctly gone by now. What
        // must hold is that discovery never gave up.
        Assert.Equal("COM-FLAKY", Assert.Single(viewModel.Value.AvailableSerialDevices).PortName);
    }

    /// <summary>
    /// A retired scan's last pass can still complete after its replacement has started, and its
    /// "that pass was clean" signal must not be applied to the replacement's fault count — otherwise
    /// a scan that is failing every pass never reaches the give-up, because a ghost keeps zeroing it.
    /// </summary>
    /// <remarks>
    /// The reset is the one callback here that comes from the <em>wrapped</em> finder rather than
    /// from <c>ContinuousDeviceFinder</c>, so it does not get the currency guard for free the way the
    /// discovery, loss and error callbacks do — it has to carry its own. Raised directly at the
    /// retired finder because the race it stands for (a bounded stop that gave up, leaving a pass in
    /// flight) is not something a test can schedule reliably.
    /// </remarks>
    [Fact]
    public async Task A_retired_scans_late_clean_pass_does_not_reset_the_current_scans_faults()
    {
        var created = new List<ScriptedSerialFinder>();
        using var viewModel = CreateViewModel(SilentScript(), onSerialFinderCreated: created.Add);

        StartSerialDiscovery(viewModel.Value);
        var retired = Assert.Single(created);

        await StopSerialDiscoveryAsync(viewModel.Value);

        // The replacement faults on every pass, and the retired finder — still in flight, which is
        // the whole premise — completes a pass of its own alongside each of them. Driving the retired
        // finder from the replacement's script is what makes the interleaving deterministic; the race
        // it stands for cannot be scheduled from outside.
        SetPrivateField(
            viewModel.Value,
            "_createSerialFinder",
            (Func<SerialDeviceFinder>)(() => new ScriptedSerialFinder(new SweepScript(_ =>
            {
                retired.RaiseCompleted();
                return Fault();
            }))));
        StartSerialDiscovery(viewModel.Value);

        await WaitUntil(
            viewModel.Value,
            vm => vm.SerialDiscoveryError != null,
            "A retired scan's late clean pass must not keep the current scan from ever giving up.");
    }

    /// <summary>
    /// Starting discovery again is the remedy the message tells the user to reach for, so it has to
    /// clear the message it gave up with.
    /// </summary>
    [Fact]
    public void Starting_serial_discovery_again_retires_the_message_it_gave_up_with()
    {
        using var viewModel = CreateViewModel(SilentScript());
        viewModel.Value.SerialDiscoveryError = "USB discovery stopped after repeated errors.";

        StartSerialDiscovery(viewModel.Value);

        Assert.Null(viewModel.Value.SerialDiscoveryError);
        Assert.True(viewModel.Value.IsSerialDiscoveryScanning);
    }

    #endregion

    #region WiFi

    /// <summary>The WiFi counterpart of <see cref="A_faulting_serial_pass_ends_the_pass_not_the_scan"/>.</summary>
    [Fact]
    public async Task A_faulting_wifi_pass_ends_the_pass_not_the_scan()
    {
        var script = new SweepScript(sweep => sweep == 1 ? Fault() : NoDevices());

        using var viewModel = CreateViewModel(SilentScript(), wifiScript: script);
        StartWiFiDiscovery(viewModel.Value);

        await WaitFor(
            () => script.Sweeps >= 3,
            "One faulted pass must end that pass, not WiFi discovery — the scan stopped instead of sweeping again.");

        Assert.Null(viewModel.Value.WiFiDiscoveryError);
    }

    /// <summary>The WiFi counterpart of <see cref="Repeated_serial_pass_faults_stop_discovery_and_say_why"/>.</summary>
    [Fact]
    public async Task Repeated_wifi_pass_faults_stop_discovery_and_say_why()
    {
        var script = new SweepScript(_ => Fault());

        using var viewModel = CreateViewModel(SilentScript(), wifiScript: script);
        StartWiFiDiscovery(viewModel.Value);

        await WaitUntil(
            viewModel.Value,
            vm => vm.WiFiDiscoveryError != null,
            "Three consecutive faulted passes must stop WiFi discovery and say so.");

        Assert.False(viewModel.Value.IsWiFiDiscoveryScanning);
    }

    #endregion

    #region Harness

    private static int MaxConsecutiveDiscoveryFaults =>
        GetPrivateConstant(nameof(MaxConsecutiveDiscoveryFaults));

    private static int GetPrivateConstant(string name)
    {
        var field = typeof(ConnectionDialogViewModel).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (int)field.GetRawConstantValue()!;
    }

    private static Task<IEnumerable<IDeviceInfo>> NoDevices() =>
        Task.FromResult(Enumerable.Empty<IDeviceInfo>());

    private static Task<IEnumerable<IDeviceInfo>> Fault() =>
        Task.FromException<IEnumerable<IDeviceInfo>>(new IOException("enumerating ports failed"));

    private static Task<IEnumerable<IDeviceInfo>> OneDeviceOn(string portName) =>
        Task.FromResult<IEnumerable<IDeviceInfo>>(
        [
            new DeviceInfo
            {
                Name = portName,
                SerialNumber = "SN-" + portName,
                FirmwareVersion = "1.0.1.24",
                PortName = portName,
                ConnectionType = ConnectionType.Serial,
            },
        ]);

    private static SweepScript SilentScript() => new(_ => NoDevices());

    private static void StartSerialDiscovery(ConnectionDialogViewModel viewModel) =>
        InvokePrivate(viewModel, "StartSerialDiscovery");

    private static void StartWiFiDiscovery(ConnectionDialogViewModel viewModel) =>
        InvokePrivate(viewModel, "StartWiFiDiscovery");

    /// <summary>Awaits the dialog's own serial stop, so the scan is really retired.</summary>
    private static async Task StopSerialDiscoveryAsync(ConnectionDialogViewModel viewModel)
    {
        var method = typeof(ConnectionDialogViewModel).GetMethod(
            "StopSerialDiscoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method.Invoke(viewModel, null));
    }

    /// <summary>
    /// Waits for a bound property to reach the state under test, driven by the view model's own
    /// change notifications rather than by polling.
    /// </summary>
    private static async Task WaitUntil(
        ConnectionDialogViewModel viewModel,
        Func<ConnectionDialogViewModel, bool> reached,
        string because)
    {
        var satisfied = new TaskCompletionSource();

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (reached(viewModel)) { satisfied.TrySetResult(); }
        }

        viewModel.PropertyChanged += OnChanged;
        try
        {
            // Cover the case where it is already true, or became true between the check and the
            // subscription.
            if (reached(viewModel)) { return; }

            await satisfied.Task.WaitAsync(Patience);
        }
        catch (TimeoutException)
        {
            Assert.Fail(because);
        }
        finally
        {
            viewModel.PropertyChanged -= OnChanged;
        }
    }

    /// <summary>
    /// The counterpart for state that raises no change notification (a scan's pass count, a private
    /// field). Polls, because there is nothing to subscribe to.
    /// </summary>
    private static async Task WaitFor(Func<bool> reached, string because)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (reached()) { return; }
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.Fail(because);
    }

    /// <summary>
    /// A view model with no bootloader watcher, its UI marshal replaced by a direct call (outside a
    /// running Avalonia app <c>Dispatcher.UIThread</c> is never pumped), both finder seams replaced by
    /// scripted stand-ins that open nothing, and the scan cadence shortened.
    /// </summary>
    private static ClosingViewModel CreateViewModel(
        SweepScript script,
        SweepScript? wifiScript = null,
        Action<ScriptedSerialFinder>? onSerialFinderCreated = null)
    {
        var viewModel = new ConnectionDialogViewModel(null!, null);
        SetPrivateField(viewModel, "_marshalToUiThread", (Action<Action>)(action => action()));
        SetPrivateField(
            viewModel,
            "_createSerialFinder",
            (Func<SerialDeviceFinder>)(() =>
            {
                var finder = new ScriptedSerialFinder(script);
                onSerialFinderCreated?.Invoke(finder);
                return finder;
            }));
        SetPrivateField(
            viewModel,
            "_createWifiFinder",
            (Func<WiFiDeviceFinder>)(() => new ScriptedWiFiFinder(wifiScript ?? SilentScript())));
        SetPrivateField(viewModel, "_serialScanInterval", TestInterval);
        SetPrivateField(viewModel, "_wifiScanInterval", TestInterval);

        return new ClosingViewModel(viewModel);
    }

    /// <summary>
    /// Decides every pass from its 1-based pass number, and counts them.
    /// </summary>
    private sealed class SweepScript(Func<int, Task<IEnumerable<IDeviceInfo>>> decide)
    {
        private int _sweeps;

        public int Sweeps => Volatile.Read(ref _sweeps);

        public Task<IEnumerable<IDeviceInfo>> NextSweep() => decide(Interlocked.Increment(ref _sweeps));
    }

    /// <summary>
    /// A serial finder that reports whatever the script says and opens nothing. The real one
    /// <c>SerialPort.Open</c>s every DAQiFi VID/PID port on the machine as soon as discovery starts,
    /// which a unit test must never do.
    /// </summary>
    /// <remarks>
    /// Raises <c>DiscoveryCompleted</c> on the passes that do not fault, because that is what the real
    /// finder does — it raises it on its success path only — and the dialog's fault counter is reset
    /// by it.
    /// </remarks>
    private sealed class ScriptedSerialFinder(SweepScript script) : SerialDeviceFinder
    {
        public override async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            var found = await script.NextSweep().ConfigureAwait(false);
            OnDiscoveryCompleted();
            return found;
        }

        /// <summary>
        /// Raises the completion the way a pass ending late would, for the retired-scan race that
        /// cannot be scheduled reliably from outside.
        /// </summary>
        public void RaiseCompleted() => OnDiscoveryCompleted();
    }

    /// <summary>The WiFi counterpart, which likewise binds no socket.</summary>
    private sealed class ScriptedWiFiFinder(SweepScript script) : WiFiDeviceFinder
    {
        public override async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            var found = await script.NextSweep().ConfigureAwait(false);
            OnDiscoveryCompleted();
            return found;
        }
    }

    /// <summary>
    /// Closes the view model at the end of a test, which stops the scans these tests start. Without it
    /// a scan would keep sweeping for the rest of the run.
    /// </summary>
    private sealed class ClosingViewModel(ConnectionDialogViewModel value) : IDisposable
    {
        public ConnectionDialogViewModel Value { get; } = value;

        public void Dispose() => Value.Close();
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
}
