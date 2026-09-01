using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// A concrete <see cref="AbstractStreamingDevice"/> whose only addition is a way to replay the
/// Core status transition that a real transport reports when a connection goes away.
/// </summary>
/// <remarks>
/// <para>
/// The base class needs three members implemented and none of them is exercised here: no test
/// using this double connects, writes, or sends anything. There is no Core device behind it,
/// which is exactly the state a wrapper is in for the properties these tests read
/// (<c>IsConnected</c> is false, <c>IsStreaming</c> is false), and it keeps the tests off any
/// transport.
/// </para>
/// <para>
/// Driving <c>OnCoreStatusChanged</c> directly is the seam these tests use because the real
/// producer is <c>DaqifiDevice.StatusChanged</c>, which cannot be raised without a live transport
/// — the one part of the path a unit test cannot own. Everything downstream of it, which is the
/// whole of the behaviour under test, is real code.
/// </para>
/// </remarks>
internal sealed class DroppableTestDevice : AbstractStreamingDevice
{
    public DroppableTestDevice(string serialNumber = "SN-TEST", ConnectionType connectionType = ConnectionType.Wifi)
    {
        DeviceSerialNo = serialNumber;
        Name = serialNumber;
        _connectionType = connectionType;
    }

    private readonly ConnectionType _connectionType;

    public override ConnectionType ConnectionType => _connectionType;

    public override bool Write(string command) => throw new NotSupportedException();

    protected override void SendMessage(IOutboundMessage<string> message) =>
        throw new NotSupportedException();

    /// <summary>
    /// Reports a successful connect without opening anything, so a test can drive
    /// <c>ConnectionManager.Connect</c> down its accept-the-device path. The real
    /// <see cref="AbstractStreamingDevice.Connect"/> needs a transport this double has no business
    /// owning; what the test is checking is what the manager does once a device has connected.
    /// </summary>
    public bool PretendConnectSucceeds { get; init; }

    /// <summary>
    /// Reports a successful connect whose transport is already gone by the time the call returns —
    /// the narrow window between <c>Connect()</c> succeeding and the manager wiring its loss
    /// handler, where a drop is unobservable and the only signal left is the device's own state.
    /// </summary>
    public bool PretendTransportDiesDuringConnect { get; init; }

    /// <summary>
    /// Backs <see cref="IsConnected"/> for the pretend-connect path. The real property reads
    /// <c>CoreDevice?.IsConnected</c>, and this double has no Core device — so without this the
    /// double would report itself permanently disconnected and could never be accepted.
    /// </summary>
    private bool _pretendConnected;

    public override bool IsConnected =>
        PretendConnectSucceeds || PretendTransportDiesDuringConnect ? _pretendConnected : base.IsConnected;

    public override bool Connect()
    {
        if (PretendTransportDiesDuringConnect)
        {
            _pretendConnected = false;
            return true;
        }

        if (!PretendConnectSucceeds)
        {
            return base.Connect();
        }

        _pretendConnected = true;
        return true;
    }

    public override bool Disconnect()
    {
        _pretendConnected = false;
        return base.Disconnect();
    }

    /// <summary>Replays what Core reports when the transport's status changes.</summary>
    /// <remarks>
    /// The state is settled BEFORE the raise, matching the real wrapper: Core has already moved the
    /// device off Connected by the time it reports the transition, so a subscriber reading
    /// <see cref="IsConnected"/> during teardown must see the settled value.
    /// </remarks>
    public void ReportCoreStatus(ConnectionStatus status)
    {
        if (status is ConnectionStatus.Lost or ConnectionStatus.Failed)
        {
            _pretendConnected = false;
        }

        OnCoreStatusChanged(this, new DeviceStatusEventArgs(status));
    }
}

/// <summary>
/// A minimal <see cref="AbstractChannel"/>. The teardown tests care only about channel identity
/// and about which instances get released, so everything else is the simplest implementation that
/// satisfies the base class.
/// </summary>
internal sealed class FakeChannel : AbstractChannel
{
    public FakeChannel(string name, string deviceSerialNo)
    {
        _name = name;
        DeviceSerialNo = deviceSerialNo;
    }

    private string _name;
    private ChannelDirection _direction = ChannelDirection.Input;
    private bool _isActive = true;

    public override string Name
    {
        get => _name;
        set => _name = value;
    }

    public override ChannelDirection Direction
    {
        get => _direction;
        set => _direction = value;
    }

    public override int Index => 0;

    public override ChannelType Type => ChannelType.Analog;

    public override bool IsActive
    {
        get => _isActive;
        set => _isActive = value;
    }

    public override bool IsDigital => false;

    public override bool IsAnalog => true;
}
