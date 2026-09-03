using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Services.DeviceWatcher;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins that <see cref="ConnectionManager.Connect"/> reports the outcome of the device it was
/// given, and not whatever the last connect to finish anywhere in the process happened to leave
/// behind (issue #212).
///
/// <para>
/// <c>ConnectionStatus</c> is one field for the whole process. Every caller used to await
/// <c>Connect(device)</c> and then read it, which is only correct while exactly one connect can be
/// running — and the connection dialog leaves all of its Connect buttons live while an attempt is
/// in flight, so two can be. The value a caller sees after its await is then whichever attempt
/// wrote last, which need not be its own.
/// </para>
///
/// <para>
/// These construct their own <see cref="ConnectionManager"/> — no singleton, no shared state with
/// any other test class, and (per <c>ConnectionManagerSingletonCollection</c>) deliberately outside
/// that collection. Nothing here opens a port or a socket: the devices are
/// <see cref="DroppableTestDevice"/>s that report a connect result without a transport.
/// </para>
/// </summary>
public class ConnectionManagerConnectResultTests
{
    /// <summary>
    /// A manager with its own device list, an inert watcher (so no background serial poller starts),
    /// no logger subscriptions and its UI marshal run inline. Everything about the connect path
    /// itself is production code.
    /// </summary>
    private static ConnectionManager NewManager() =>
        new(new NoOpDeviceWatcher(),
            () => new List<IChannel>(),
            _ => { },
            action => action());

    /// <summary>
    /// The defect, at the level it lives at. Two connects, one after the other; the first fails and
    /// the second succeeds. The first attempt's own result still says it failed, while the shared
    /// field — the thing every caller used to read after its await — now holds the second device's
    /// outcome.
    /// </summary>
    [Fact]
    public async Task A_failed_connect_stays_failed_after_a_later_connect_succeeds()
    {
        var manager = NewManager();
        var failing = new DroppableTestDevice("SN-FAILS");
        var succeeding = new DroppableTestDevice("SN-WORKS") { PretendConnectSucceeds = true };

        var failedAttempt = await manager.Connect(failing);
        var succeededAttempt = await manager.Connect(succeeding);

        Assert.Same(failing, failedAttempt.Device);
        Assert.False(failedAttempt.IsConnected);
        Assert.Same(succeeding, succeededAttempt.Device);
        Assert.True(succeededAttempt.IsConnected);

        // The device really did not connect — the result is not merely a label.
        Assert.DoesNotContain(failing, manager.ConnectedDevices);
        Assert.Contains(succeeding, manager.ConnectedDevices);

        // And the field a caller used to read now reports Connected, which is true of the second
        // device and false of the first. Reading it after awaiting the first connect is exactly
        // how the dialog came to close on a device that had failed.
        Assert.Equal(DAQiFiConnectionStatus.Connected, manager.ConnectionStatus);
    }

    /// <summary>
    /// The same thing with the two attempts genuinely overlapping: both are inside
    /// <c>device.Connect()</c> at the same moment, and the successful one is released last so its
    /// status is the one left in the shared field. Each caller still gets its own device back.
    /// </summary>
    [Fact]
    public async Task Overlapping_connects_each_come_back_with_their_own_devices_result()
    {
        var manager = NewManager();
        var releaseFailing = new TaskCompletionSource();
        var releaseSucceeding = new TaskCompletionSource();
        var failing = new DroppableTestDevice("SN-FAILS") { ConnectGate = releaseFailing };
        var succeeding = new DroppableTestDevice("SN-WORKS")
        {
            PretendConnectSucceeds = true,
            ConnectGate = releaseSucceeding
        };

        var failingAttempt = manager.Connect(failing);
        var succeedingAttempt = manager.Connect(succeeding);

        // Both are in flight before either is allowed to finish.
        await failing.ConnectEntered.Task;
        await succeeding.ConnectEntered.Task;

        releaseFailing.SetResult();
        var failedResult = await failingAttempt;

        releaseSucceeding.SetResult();
        var succeededResult = await succeedingAttempt;

        Assert.Same(failing, failedResult.Device);
        Assert.False(failedResult.IsConnected);
        Assert.Same(succeeding, succeededResult.Device);
        Assert.True(succeededResult.IsConnected);
    }

    /// <summary>
    /// A device the user already has connected counts as success and always has: they asked for a
    /// connected device and they have one, so the dialog closes rather than painting an error over
    /// a device sitting in the device list.
    /// </summary>
    [Fact]
    public async Task Choosing_to_keep_the_device_already_connected_is_reported_as_connected()
    {
        var manager = NewManager();
        manager.ConnectedDevices.Add(new DroppableTestDevice("SN-DUP") { PretendConnectSucceeds = true });
        manager.DuplicateDeviceHandler = _ => Task.FromResult(DuplicateDeviceAction.KeepExisting);

        var second = new DroppableTestDevice("SN-DUP", ConnectionType.Usb);
        var result = await manager.Connect(second);

        Assert.Equal(DAQiFiConnectionStatus.AlreadyConnected, result.Status);
        Assert.True(result.IsConnected);
        Assert.False(result.WasCancelledByUser);
        Assert.Same(second, result.Device);
    }

    /// <summary>
    /// Dismissing the duplicate-device prompt is the user calling the connect off, and has to be
    /// distinguishable from a failure: telling them "could not connect" blames the hardware for
    /// their own decision, and closing the dialog acts on a connect they declined.
    /// </summary>
    [Fact]
    public async Task Declining_the_duplicate_prompt_is_reported_as_cancelled_not_failed()
    {
        var manager = NewManager();
        manager.ConnectedDevices.Add(new DroppableTestDevice("SN-DUP") { PretendConnectSucceeds = true });
        manager.DuplicateDeviceHandler = _ => Task.FromResult(DuplicateDeviceAction.Cancel);

        var result = await manager.Connect(new DroppableTestDevice("SN-DUP", ConnectionType.Usb));

        Assert.True(result.WasCancelledByUser);
        Assert.False(result.IsConnected);
        Assert.Equal(DAQiFiConnectionStatus.Disconnected, result.Status);
    }

    /// <summary>
    /// The ordinary case, so the two properties are pinned in both directions: a plain failure is a
    /// failure and not a cancellation.
    /// </summary>
    [Fact]
    public async Task A_device_that_will_not_connect_is_reported_as_failed_not_cancelled()
    {
        var manager = NewManager();

        var result = await manager.Connect(new DroppableTestDevice("SN-NOPE"));

        Assert.False(result.IsConnected);
        Assert.False(result.WasCancelledByUser);
        Assert.Equal(DAQiFiConnectionStatus.Error, result.Status);
    }
}
