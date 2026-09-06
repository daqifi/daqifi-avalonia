using System.Net.Sockets;
using System.Reflection;
using Daqifi.Core.Device.Discovery;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins what the connection dialog does when device discovery stops — the two ways it can, and what
/// the user is told about it (issue #290).
///
/// <para>
/// Both discovery loops used to put their general <c>catch (Exception)</c> outside the <c>while</c>,
/// so a single faulted sweep ended discovery for the life of the dialog rather than ending that one
/// sweep; and the serial watchdog's deliberate give-up after repeatedly wedged sweeps reported the
/// remedy only to <c>DAQifiAppLog.log</c>. In all three cases the bound state was indistinguishable
/// from "still looking", so the tab kept animating "Scanning for USB devices…" for ever.
/// </para>
///
/// <para>
/// The give-up itself is correct and is pinned here as such: a permanently broken finder still stops.
/// What these tests add is that a transient fault does not, and that whatever stops for good says so
/// where the overlay is.
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
    /// Upper bound on how long a test waits for a discovery loop to reach the state under test.
    /// Generous on purpose: it is a deadlock guard, not a timing assertion. The loops themselves
    /// pace at 2 s (serial) and 3 s (WiFi) between sweeps, which is what these tests actually wait on.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    #region Serial

    /// <summary>
    /// The scoping half of #290. Core's <c>SerialDeviceFinder</c> enumerates the machine's ports on
    /// every pass with no per-pass guard, so one hiccup there is one bad sweep — not a reason to stop
    /// looking for the rest of the dialog's life.
    /// </summary>
    [Fact]
    public async Task A_faulting_serial_sweep_ends_the_sweep_not_the_discovery_loop()
    {
        var secondSweepStarted = new TaskCompletionSource();
        var script = new SweepScript(sweep =>
        {
            if (sweep == 1)
            {
                return Task.FromException<IEnumerable<IDeviceInfo>>(
                    new IOException("enumerating serial ports failed"));
            }

            secondSweepStarted.TrySetResult();
            return NoDevices();
        });

        using var viewModel = StartSerialDiscovery(script, out var loop);

        await Task.WhenAny(secondSweepStarted.Task, loop).WaitAsync(Patience);

        Assert.True(
            secondSweepStarted.Task.IsCompletedSuccessfully,
            "One faulted sweep must end that sweep, not USB discovery — the loop exited instead of sweeping again.");
        Assert.False(loop.IsCompleted, "Discovery is still running, so its loop task must still be running.");
    }

    /// <summary>
    /// The other side of the same decision: tolerating a transient fault must not mean tolerating a
    /// permanently broken finder. After the third consecutive fault the loop stops — and says so.
    /// </summary>
    [Fact]
    public async Task Repeated_serial_sweep_faults_stop_discovery_and_say_why()
    {
        var script = new SweepScript(_ => Task.FromException<IEnumerable<IDeviceInfo>>(
            new IOException("enumerating serial ports failed")));

        using var viewModel = StartSerialDiscovery(script, out var loop);

        await loop.WaitAsync(Patience);

        Assert.Equal(MaxConsecutiveDiscoveryFaults, script.Sweeps);
        Assert.NotNull(viewModel.Value.SerialDiscoveryError);
        Assert.False(
            viewModel.Value.IsSerialDiscoveryScanning,
            "The animated 'Scanning for USB devices…' overlay binds to this, so it must stop making that claim.");
    }

    /// <summary>
    /// The headline of #290. The watchdog's give-up after repeatedly wedged sweeps is deliberate — a
    /// wedged CDC port cannot be un-wedged from this side — but it used to reach only the log file,
    /// remedy and all, while the tab kept spinning. The remedy has to reach the dialog.
    /// </summary>
    [Fact]
    public async Task The_watchdog_giving_up_tells_the_user_instead_of_only_the_log()
    {
        // A sweep that never returns: exactly what a wedged USB-CDC port does to Core's finder
        // (core#294, observed live 2026-07-13).
        var wedged = new TaskCompletionSource<IEnumerable<IDeviceInfo>>();
        var script = new SweepScript(_ => wedged.Task);

        using var viewModel = StartSerialDiscovery(script, out var loop, watchdogMs: 50);

        await loop.WaitAsync(Patience);

        var message = viewModel.Value.SerialDiscoveryError;
        Assert.Equal(MaxConsecutiveWatchdogTrips, script.Sweeps);
        Assert.NotNull(message);
        Assert.Contains("Power-cycle", message);
        Assert.False(
            viewModel.Value.IsSerialDiscoveryScanning,
            "The animated 'Scanning for USB devices…' overlay binds to this, so it must stop making that claim.");
        Assert.True(
            viewModel.Value.HasNoSerialDevices,
            "Nothing was ever discovered, so the list state itself is unchanged — the message is the only new signal.");

        wedged.TrySetCanceled();
    }

    /// <summary>
    /// The message describes the run that gave up, so starting discovery again has to retire it —
    /// on both entry points, including the one that keeps the discovered devices.
    /// </summary>
    [Fact]
    public void Starting_serial_discovery_again_retires_the_message_it_gave_up_with()
    {
        using var restarted = CreateViewModel(SilentScript());
        restarted.Value.SerialDiscoveryError = "USB discovery stopped.";

        InvokePrivate(restarted.Value, "StartSerialDiscovery");

        Assert.Null(restarted.Value.SerialDiscoveryError);
        Assert.True(restarted.Value.IsSerialDiscoveryScanning);

        using var resumed = CreateViewModel(SilentScript());
        resumed.Value.SerialDiscoveryError = "USB discovery stopped.";

        InvokePrivate(resumed.Value, "ResumeSerialDiscoveryKeepingDiscoveredDevices");

        Assert.Null(resumed.Value.SerialDiscoveryError);
    }

    #endregion

    #region WiFi

    /// <summary>
    /// The WiFi loop had the same catch in the same wrong place, and its overlay tells the same lie.
    /// </summary>
    [Fact]
    public async Task A_faulting_wifi_sweep_ends_the_sweep_not_the_discovery_loop()
    {
        var secondSweepStarted = new TaskCompletionSource();
        var script = new SweepScript(sweep =>
        {
            if (sweep == 1)
            {
                return Task.FromException<IEnumerable<IDeviceInfo>>(
                    new SocketException((int)SocketError.NetworkDown));
            }

            secondSweepStarted.TrySetResult();
            return NoDevices();
        });

        using var viewModel = StartWiFiDiscovery(script, out var loop);

        await Task.WhenAny(secondSweepStarted.Task, loop).WaitAsync(Patience);

        Assert.True(
            secondSweepStarted.Task.IsCompletedSuccessfully,
            "One faulted sweep must end that sweep, not WiFi discovery — the loop exited instead of sweeping again.");
        Assert.False(loop.IsCompleted, "Discovery is still running, so its loop task must still be running.");
    }

    /// <summary>
    /// And the same give-up, with the same obligation to say so.
    /// </summary>
    [Fact]
    public async Task Repeated_wifi_sweep_faults_stop_discovery_and_say_why()
    {
        var script = new SweepScript(_ => Task.FromException<IEnumerable<IDeviceInfo>>(
            new SocketException((int)SocketError.NetworkDown)));

        using var viewModel = StartWiFiDiscovery(script, out var loop);

        await loop.WaitAsync(Patience);

        Assert.Equal(MaxConsecutiveDiscoveryFaults, script.Sweeps);
        Assert.NotNull(viewModel.Value.WiFiDiscoveryError);
        Assert.False(
            viewModel.Value.IsWiFiDiscoveryScanning,
            "The animated 'Scanning for WiFi devices…' overlay binds to this, so it must stop making that claim.");
    }

    #endregion

    #region Harness

    /// <summary>The two loop bounds, read off the view model so a change to either is not silently absorbed.</summary>
    private static readonly int MaxConsecutiveDiscoveryFaults = PrivateConstant("MaxConsecutiveDiscoveryFaults");

    private static readonly int MaxConsecutiveWatchdogTrips = PrivateConstant("MaxConsecutiveWatchdogTrips");

    private static int PrivateConstant(string name)
    {
        var field = typeof(ConnectionDialogViewModel).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (int)field.GetRawConstantValue()!;
    }

    private static Task<IEnumerable<IDeviceInfo>> NoDevices() =>
        Task.FromResult(Enumerable.Empty<IDeviceInfo>());

    private static SweepScript SilentScript() => new(_ => NoDevices());

    /// <summary>
    /// Builds the view model, installs <paramref name="script"/> behind its serial-finder seam and
    /// starts serial discovery, handing back the loop task the dialog is now running.
    /// </summary>
    private static ClosingViewModel StartSerialDiscovery(
        SweepScript script,
        out Task loop,
        int? watchdogMs = null)
    {
        var viewModel = CreateViewModel(script, watchdogMs);
        InvokePrivate(viewModel.Value, "StartSerialDiscovery");
        loop = Assert.IsAssignableFrom<Task>(GetPrivateField(viewModel.Value, "_serialDiscoveryTask"));
        return viewModel;
    }

    /// <summary>
    /// The WiFi counterpart. The WiFi finder has no factory seam — the view model news one up in
    /// <c>StartWiFiDiscovery</c>, and a real one binds a UDP socket — so the scripted finder is
    /// installed into <c>_wifiFinder</c> directly and the loop is entered by hand.
    /// </summary>
    private static ClosingViewModel StartWiFiDiscovery(SweepScript script, out Task loop)
    {
        var viewModel = CreateViewModel(SilentScript());
        SetPrivateField(viewModel.Value, "_wifiFinder", new ScriptedWiFiFinder(script));

        // Owned by the returned ClosingViewModel, which cancels it before the finder is disposed.
        var cts = new CancellationTokenSource();
        SetPrivateField(viewModel.Value, "_wifiDiscoveryCts", cts);

        var method = typeof(ConnectionDialogViewModel).GetMethod(
            "RunContinuousWiFiDiscoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        loop = Assert.IsAssignableFrom<Task>(method.Invoke(viewModel.Value, [cts.Token]));
        return viewModel;
    }

    /// <summary>
    /// A view model with no bootloader watcher, its UI marshal replaced by a direct call (outside a
    /// running Avalonia app <c>Dispatcher.UIThread</c> is never pumped) and its serial finder
    /// replaced by a scripted one that opens no port.
    /// </summary>
    private static ClosingViewModel CreateViewModel(SweepScript script, int? watchdogMs = null)
    {
        var viewModel = new ConnectionDialogViewModel(null!, null);
        SetPrivateField(viewModel, "_marshalToUiThread", (Action<Action>)(action => action()));
        SetPrivateField(
            viewModel,
            "_createSerialFinder",
            (Func<SerialDeviceFinder>)(() => new ScriptedSerialFinder(script)));
        if (watchdogMs is { } ms)
        {
            // The shipped bound is 10 s and the give-up needs three trips, so pinning that path at
            // the real value would cost half a minute of wall clock for one assertion.
            SetPrivateField(viewModel, "_serialSweepWatchdogMs", ms);
        }

        return new ClosingViewModel(viewModel);
    }

    /// <summary>
    /// Decides every sweep from its 1-based sweep number, and counts them. Shared across the finder
    /// instances the watchdog rebuilds, so the count spans the whole discovery run rather than one
    /// finder's lifetime.
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
    private sealed class ScriptedSerialFinder(SweepScript script) : SerialDeviceFinder
    {
        public override Task<IEnumerable<IDeviceInfo>> DiscoverAsync(
            CancellationToken cancellationToken = default) => script.NextSweep();
    }

    /// <summary>The WiFi counterpart, which likewise binds no socket.</summary>
    private sealed class ScriptedWiFiFinder(SweepScript script) : WiFiDeviceFinder
    {
        public override Task<IEnumerable<IDeviceInfo>> DiscoverAsync(
            CancellationToken cancellationToken = default) => script.NextSweep();
    }

    /// <summary>
    /// Closes the view model at the end of a test, which cancels the discovery loops these tests
    /// start. Without it a loop would keep waking every couple of seconds for the rest of the run.
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

    private static void SetPrivateField(ConnectionDialogViewModel viewModel, string fieldName, object? value)
    {
        var field = typeof(ConnectionDialogViewModel).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(viewModel, value);
    }

    #endregion
}
