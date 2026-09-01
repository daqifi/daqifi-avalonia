using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Services.DeviceWatcher;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins what happens to a device the app is holding when its connection goes away underneath it.
///
/// The accumulation these cover is per-drop and permanent for the process: a device left in
/// <see cref="ConnectionManager.ConnectedDevices"/> keeps its wrapper, its Core device and that
/// device's transport — for WiFi, an open socket — reachable from a static singleton, because the
/// only code that disposes them is <c>Disconnect</c>, which nothing was calling. Its channels stay
/// in <c>LoggingManager</c> marked active, so they keep counting toward "can start logging" and
/// keep being listed as live inputs on a device that is gone.
///
/// The <c>WasNotTornDown</c> assertions are the regression half: each is the state the app was
/// actually left in before this handler existed, and each fails if the teardown is removed or
/// short-circuited.
/// </summary>
public class ConnectionManagerTeardownTests
{
    /// <summary>
    /// A manager with its own device list, an inert watcher (so no background serial poller
    /// starts), channel release routed into <paramref name="released"/> instead of
    /// <c>LoggingManager</c> (which cannot be constructed without a running Avalonia application),
    /// and its UI marshal run inline (outside a running application, Avalonia's dispatcher queues
    /// onto a thread nothing pumps). Everything else — the teardown, the firmware-update carve-out,
    /// the subscription bookkeeping, the notification — is the production code path.
    /// </summary>
    private static ConnectionManager NewManager(List<IChannel> released) =>
        new(new NoOpDeviceWatcher(), released.Add, action => action());

    private static DroppableTestDevice ConnectedWifiDevice(
        ConnectionManager manager, string serial = "SN-WIFI-1", int channelCount = 2)
    {
        var device = new DroppableTestDevice(serial);
        for (var i = 0; i < channelCount; i++)
        {
            device.DataChannels.Add(new FakeChannel($"AI{i}", serial));
        }

        manager.ConnectedDevices.Add(device);
        manager.SubscribeDeviceEvents(device);
        return device;
    }

    /// <summary>
    /// The one case that goes through <see cref="ConnectionManager.Connect"/> itself, so the
    /// wiring the other cases set up by hand is pinned to what the production connect path
    /// actually does. Everything else uses the hand-wired helper to avoid paying Connect's
    /// one-second post-connect settle nine more times.
    /// </summary>
    [Fact]
    public async Task Connecting_a_device_wires_it_for_teardown()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = new DroppableTestDevice("SN-CONNECTED") { PretendConnectSucceeds = true };
        device.DataChannels.Add(new FakeChannel("AI0", "SN-CONNECTED"));

        await manager.Connect(device);
        Assert.Contains(device, manager.ConnectedDevices);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.DoesNotContain(device, manager.ConnectedDevices);
        Assert.Single(released);
    }

    [Fact]
    public void A_dropped_wifi_device_is_removed_from_the_connected_list()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.DoesNotContain(device, manager.ConnectedDevices);
    }

    [Fact]
    public void A_dropped_wifi_device_releases_every_one_of_its_channels()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager, channelCount: 3);
        var expected = device.DataChannels.ToList();

        device.ReportCoreStatus(ConnectionStatus.Lost);

        // By name and by instance: the channels released must be this device's own objects, not
        // equal-by-name ones — AbstractChannel.Equals compares Name alone, and two boards both
        // expose AI0.
        Assert.Equal(3, released.Count);
        Assert.All(expected, channel => Assert.Contains(released, r => ReferenceEquals(r, channel)));
    }

    /// <summary>
    /// Channels have to be released BEFORE the device's own teardown, because
    /// <c>AbstractStreamingDevice.Disconnect</c> clears <c>DataChannels</c>. Release them after and
    /// there is nothing left to enumerate, so they stay subscribed for the process lifetime — the
    /// exact leak, reintroduced by an ordering change that looks harmless.
    /// </summary>
    [Fact]
    public void Channels_are_released_before_the_device_clears_them()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager, channelCount: 2);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(2, released.Count);
        Assert.Empty(device.DataChannels);
    }

    [Fact]
    public void The_user_is_told_which_device_went_away_and_why()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager, serial: "Nq1-0042");

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.True(manager.NotifyConnection);
        Assert.Contains("Nq1-0042", manager.LastDisconnectReason);
        Assert.Contains("connection lost", manager.LastDisconnectReason);
    }

    /// <summary>
    /// A device being flashed drops its transport when it reboots into the bootloader and back —
    /// an expected part of the update that Core reconnects itself. Tearing it down here disposes
    /// the Core device out from under Core's reconnect and times the update out even though the
    /// flash succeeded (issue #738), so this carve-out is load-bearing, not defensive.
    /// </summary>
    [Fact]
    public void A_device_being_firmware_updated_is_left_alone()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager);
        manager.DeviceBeingUpdated = device;

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Contains(device, manager.ConnectedDevices);
        Assert.Empty(released);
        Assert.False(manager.NotifyConnection);
        Assert.Equal(2, device.DataChannels.Count);
    }

    /// <summary>
    /// Core's own port-presence poll and this port's <c>IDeviceWatcher</c> both report a USB
    /// unplug, so two teardowns can arrive for one drop. The second must find nothing to do rather
    /// than disconnect a device already gone and raise a second notification.
    /// </summary>
    [Fact]
    public void A_second_report_for_the_same_drop_does_nothing()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager, channelCount: 2);

        device.ReportCoreStatus(ConnectionStatus.Lost);
        manager.NotifyConnection = false;
        manager.LastDisconnectReason = string.Empty;

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(2, released.Count);
        Assert.False(manager.NotifyConnection);
        Assert.Equal(string.Empty, manager.LastDisconnectReason);
    }

    /// <summary>
    /// The symmetry that keeps the fix from becoming its own leak: an explicitly disconnected
    /// device must not still be wired to this manager. A wrapper that outlives its session — the
    /// connection dialog keeps discovered devices around — would otherwise hold the singleton
    /// manager reachable through the handler, and a late drop report would tear down a device the
    /// user already disconnected.
    /// </summary>
    [Fact]
    public void An_explicitly_disconnected_device_is_unwired_from_the_manager()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager);
        var other = ConnectedWifiDevice(manager, serial: "SN-WIFI-2");

        manager.Disconnect(device);
        released.Clear();

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.False(manager.NotifyConnection);
        Assert.Empty(released);
        // The manager is otherwise intact: the drop reached nobody, it did not empty the list.
        Assert.Contains(other, manager.ConnectedDevices);
    }

    /// <summary>
    /// Connecting the same wrapper twice without an explicit disconnect in between must not double
    /// the wiring: one drop, one teardown, one notification.
    /// </summary>
    [Fact]
    public void Subscribing_twice_still_tears_down_once()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var device = ConnectedWifiDevice(manager, channelCount: 2);
        manager.SubscribeDeviceEvents(device);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(2, released.Count);
        Assert.DoesNotContain(device, manager.ConnectedDevices);
    }

    /// <summary>
    /// One device dropping must not disturb the others the user still has connected.
    /// </summary>
    [Fact]
    public void Only_the_device_that_dropped_is_torn_down()
    {
        var released = new List<IChannel>();
        var manager = NewManager(released);
        var dropped = ConnectedWifiDevice(manager, serial: "SN-A", channelCount: 2);
        var kept = ConnectedWifiDevice(manager, serial: "SN-B", channelCount: 2);

        dropped.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.DoesNotContain(dropped, manager.ConnectedDevices);
        Assert.Contains(kept, manager.ConnectedDevices);
        Assert.Equal(2, kept.DataChannels.Count);
        Assert.All(released, channel => Assert.Equal("SN-A", channel.DeviceSerialNo));
    }
}
