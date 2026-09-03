using System.Reflection;
using Daqifi.Core.Device.Discovery;
using Daqifi.Desktop;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins what happens to the USB tab's device list when the serial finder is torn down and
/// recreated (issue #212, part 2).
///
/// <para>
/// The WiFi side has cleared its list on finder recreate since issue #621, with a comment
/// explaining that the bound list otherwise outlives the finder that populated it and keeps
/// devices that are no longer answering. The serial side never did, so the USB tab accumulated.
/// The case that bites is the firmware-flash resume: a flashed board re-enumerates its USB-CDC
/// port, can come back under a different port name, and the pre-flash row stays in the list
/// advertising a port that no longer exists and a firmware version it no longer runs.
/// </para>
///
/// <para>
/// The split these pin is deliberate. Clearing is right where discovery was torn down and is being
/// started afresh; it is wrong immediately after a failed connect, where the device that just
/// failed is the one the user is about to press Connect on again and a sweep only runs every two
/// seconds.
/// </para>
///
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because <c>StartSerialDiscovery</c> reads
/// <c>ConnectionManager.Instance.IsFirmwareUpdateInProgress</c> and refuses to start while it is
/// set — a sibling class flipping that flag in parallel would silently skip the code under test.
/// No SQLite, no app host, and nothing here opens a serial port: the finder is replaced through
/// the view model's own factory seam by one that never probes.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class ConnectionDialogSerialListTests
{
    /// <summary>
    /// The firmware-resume path: discovery was stopped for the flash and is started again
    /// afterwards. What the old finder found must not survive into the new session.
    /// </summary>
    [Fact]
    public void Starting_serial_discovery_afresh_drops_what_the_previous_finder_found()
    {
        using var viewModel = CreateViewModel();
        SeedDiscoveredDevice(viewModel.Value, "COM-STALE");

        InvokePrivate(viewModel.Value, "StartSerialDiscovery");

        Assert.Empty(viewModel.Value.AvailableSerialDevices);
        Assert.True(
            viewModel.Value.HasNoSerialDevices,
            "The 'Scanning for USB devices…' overlay is bound to this, so it has to follow the list.");
    }

    /// <summary>
    /// The other half of the decision. Recovering from a failed connect restarts discovery too, but
    /// blanking the list back to "Scanning…" would take away the exact device the user is most
    /// likely to retry and make them wait for it to be rediscovered. Upstream does not clear here
    /// either.
    /// </summary>
    [Fact]
    public void Resuming_after_a_failed_connect_keeps_the_device_the_user_is_about_to_retry()
    {
        using var viewModel = CreateViewModel();
        var stillListed = SeedDiscoveredDevice(viewModel.Value, "COM-RETRY");

        InvokePrivate(viewModel.Value, "ResumeSerialDiscoveryKeepingDiscoveredDevices");

        Assert.Same(stillListed, Assert.Single(viewModel.Value.AvailableSerialDevices));
        Assert.False(viewModel.Value.HasNoSerialDevices);
    }

    /// <summary>
    /// Both entry points really do recreate the finder — otherwise the first test above would pass
    /// by taking an early return rather than by clearing, and the second would be asserting that
    /// nothing happened.
    /// </summary>
    [Fact]
    public void Both_entry_points_recreate_the_finder()
    {
        using var fresh = CreateViewModel();
        InvokePrivate(fresh.Value, "StartSerialDiscovery");
        Assert.NotNull(GetPrivateField(fresh.Value, "_serialFinder"));

        using var resumed = CreateViewModel();
        InvokePrivate(resumed.Value, "ResumeSerialDiscoveryKeepingDiscoveredDevices");
        Assert.NotNull(GetPrivateField(resumed.Value, "_serialFinder"));
    }

    /// <summary>
    /// The clear is only worth anything if the finder it retired cannot undo it. Unsubscribing
    /// <c>DeviceDiscovered</c> does not revoke a raise already in flight, and the mutation it
    /// queues onto the UI thread can land after the clear — so a retired finder could otherwise
    /// put the pre-flash port straight back. The watchdog makes this concrete by deliberately
    /// abandoning timed-out sweeps.
    /// </summary>
    [Fact]
    public void A_retired_finders_late_discovery_cannot_put_a_ghost_back()
    {
        using var viewModel = CreateViewModel();

        InvokePrivate(viewModel.Value, "StartSerialDiscovery");
        var retiredFinder = GetPrivateField(viewModel.Value, "_serialFinder");
        Assert.NotNull(retiredFinder);

        // A second start retires the first finder and installs a new one, exactly as the
        // firmware-flash resume does.
        SetPrivateField(viewModel.Value, "_serialDiscoveryTask", null);
        InvokePrivate(viewModel.Value, "StartSerialDiscovery");
        Assert.NotSame(retiredFinder, GetPrivateField(viewModel.Value, "_serialFinder"));

        RaiseDiscovery(viewModel.Value, retiredFinder, "COM-GHOST");

        Assert.Empty(viewModel.Value.AvailableSerialDevices);
        Assert.True(viewModel.Value.HasNoSerialDevices);
    }

    /// <summary>
    /// The other direction, so the guard is pinned as a filter rather than as a blanket refusal:
    /// the finder that is actually current still populates the list.
    /// </summary>
    [Fact]
    public void The_current_finders_discovery_still_reaches_the_list()
    {
        using var viewModel = CreateViewModel();

        InvokePrivate(viewModel.Value, "StartSerialDiscovery");
        var currentFinder = GetPrivateField(viewModel.Value, "_serialFinder");

        RaiseDiscovery(viewModel.Value, currentFinder, "COM-REAL");

        var listed = Assert.Single(viewModel.Value.AvailableSerialDevices);
        Assert.Equal("COM-REAL", listed.Port?.PortName);
        Assert.False(viewModel.Value.HasNoSerialDevices);
    }

    #region Harness
    /// <summary>
    /// Raises <c>DeviceDiscovered</c> at the view model the way a finder does, with
    /// <paramref name="finder"/> as the sender.
    /// </summary>
    private static void RaiseDiscovery(
        ConnectionDialogViewModel viewModel,
        object? finder,
        string portName)
    {
        var handler = typeof(ConnectionDialogViewModel).GetMethod(
            "HandleCoreSerialDeviceDiscovered", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);

        var deviceInfo = new DeviceInfo
        {
            Name = portName,
            SerialNumber = "SN-" + portName,
            FirmwareVersion = "1.0.1.24",
            PortName = portName,
            ConnectionType = Daqifi.Core.Device.Discovery.ConnectionType.Serial
        };

        handler.Invoke(viewModel, [finder, new DeviceDiscoveredEventArgs(deviceInfo)]);
    }

    private static SerialStreamingDevice SeedDiscoveredDevice(
        ConnectionDialogViewModel viewModel,
        string portName)
    {
        var device = new SerialStreamingDevice(portName);
        viewModel.AvailableSerialDevices.Add(device);
        viewModel.HasNoSerialDevices = false;
        return device;
    }

    /// <summary>
    /// A view model with no bootloader watcher, its UI marshal replaced by a direct call (outside a
    /// running Avalonia app <c>Dispatcher.UIThread</c> is never pumped) and its serial finder
    /// replaced by one that never touches a port.
    /// </summary>
    private static ClosingViewModel CreateViewModel()
    {
        var viewModel = new ConnectionDialogViewModel(null!, null);
        SetPrivateField(viewModel, "_marshalToUiThread", (Action<Action>)(action => action()));
        SetPrivateField(
            viewModel,
            "_createSerialFinder",
            (Func<SerialDeviceFinder>)(() => new SilentSerialDeviceFinder()));
        return new ClosingViewModel(viewModel);
    }

    /// <summary>
    /// Closes the view model at the end of a test, which cancels the discovery loop these tests
    /// start. Without it the loop would keep waking every two seconds for the rest of the run.
    /// </summary>
    private sealed class ClosingViewModel(ConnectionDialogViewModel value) : IDisposable
    {
        public ConnectionDialogViewModel Value { get; } = value;

        public void Dispose() => Value.Close();
    }

    /// <summary>
    /// A finder that reports nothing and opens nothing. The real one <c>SerialPort.Open</c>s every
    /// DAQiFi VID/PID port on the machine as soon as discovery starts, which a unit test must never
    /// do — a developer's attached board, or one another process on the CI/loop machine is
    /// mid-connect on, is not this test's to probe.
    /// </summary>
    private sealed class SilentSerialDeviceFinder : SerialDeviceFinder
    {
        public override Task<IEnumerable<IDeviceInfo>> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Empty<IDeviceInfo>());
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
