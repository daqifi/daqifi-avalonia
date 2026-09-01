using Daqifi.Core.Device;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins the signal a dropped device emits.
///
/// Core is the only party that observes a spontaneous transport drop — a router reboot, an AP
/// roam, a pulled cable, a silently dead TCP socket. It reports one as
/// <see cref="ConnectionStatus.Lost"/>. Before this event existed, the app's response to that
/// report was to clear <c>IsStreaming</c> and write a warning: the device stayed in
/// <c>ConnectedDevices</c>, its channels stayed subscribed to the logger, its Core device and
/// socket were never disposed, and the user got no notification. Nothing could subscribe to the
/// drop because there was nothing to subscribe to.
///
/// These cases fix which Core statuses count as a drop and which must not, because the cost of
/// getting that wrong runs in both directions: too narrow and the device is never torn down (the
/// bug), too wide and an app-initiated disconnect re-enters teardown on a device already being
/// torn down.
/// </summary>
public class AbstractStreamingDeviceConnectionLostTests
{
    [Fact]
    public void A_lost_transport_raises_ConnectionLost_naming_the_reason()
    {
        var device = new DroppableTestDevice();
        var reasons = new List<string>();
        device.ConnectionLost += (_, e) => reasons.Add(e.Reason);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(["connection lost"], reasons);
    }

    [Fact]
    public void The_device_raising_it_is_the_sender_so_a_subscriber_can_tear_the_right_one_down()
    {
        var device = new DroppableTestDevice();
        object? sender = null;
        device.ConnectionLost += (s, _) => sender = s;

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Same(device, sender);
    }

    /// <summary>
    /// Unreachable while this app leaves <c>ReconnectOptions</c> at its disabled default — a drop
    /// stops at <see cref="ConnectionStatus.Lost"/> and Core never reports
    /// <see cref="ConnectionStatus.Failed"/>. Handled anyway so that enabling automatic reconnect
    /// later cannot silently reintroduce the untorn-down device this event exists to prevent:
    /// <c>Failed</c> is where an exhausted reconnect settles, and it is terminal.
    /// </summary>
    [Fact]
    public void An_exhausted_reconnect_raises_it_too()
    {
        var device = new DroppableTestDevice();
        var reasons = new List<string>();
        device.ConnectionLost += (_, e) => reasons.Add(e.Reason);

        device.ReportCoreStatus(ConnectionStatus.Failed);

        Assert.Equal(["connection failed"], reasons);
    }

    /// <summary>
    /// <see cref="ConnectionStatus.Disconnected"/> is the case that must NOT raise, and is where
    /// this port diverges from upstream's status set. At the pinned Core (1.7.0) it is produced
    /// only by a caller-issued <c>Disconnect()</c>, and every app-initiated teardown path detaches
    /// this handler before touching the Core device — so treating it as a drop could only ever
    /// re-enter teardown on a device already being torn down. <see cref="ConnectionStatus.Retrying"/>
    /// is a reconnect in flight, which is the opposite of terminal.
    /// </summary>
    [Theory]
    [InlineData(ConnectionStatus.Connected)]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Retrying)]
    public void No_other_status_raises_it(ConnectionStatus status)
    {
        var device = new DroppableTestDevice();
        var raised = 0;
        device.ConnectionLost += (_, _) => raised++;

        device.ReportCoreStatus(status);

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// The user asking to disconnect is not a drop, and must not produce the "device disconnected"
    /// notification. In the app this is guaranteed structurally — <c>Disconnect</c> detaches Core's
    /// <c>StatusChanged</c> before the Core device is touched — so this pins the outcome that
    /// depends on it.
    /// </summary>
    [Fact]
    public void An_app_initiated_disconnect_does_not_raise_it()
    {
        var device = new DroppableTestDevice();
        var raised = 0;
        device.ConnectionLost += (_, _) => raised++;

        device.Disconnect();

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Two drops on one wrapper must produce two independent reports rather than one latched
    /// state, because the teardown they drive is idempotent and the second is what covers a
    /// reconnect that drops again.
    /// </summary>
    [Fact]
    public void Each_drop_is_reported_separately()
    {
        var device = new DroppableTestDevice();
        var raised = 0;
        device.ConnectionLost += (_, _) => raised++;

        device.ReportCoreStatus(ConnectionStatus.Lost);
        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(2, raised);
    }
}
