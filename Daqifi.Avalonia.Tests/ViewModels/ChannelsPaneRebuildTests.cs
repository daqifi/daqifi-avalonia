using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.ViewModels;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins what <c>ChannelsPaneViewModel.Rebuild</c> produces — the method that builds the entire
/// Channels pane, and which had no behavioural coverage at all.
///
/// <para>
/// Everything the pane shows is built here in one pass: the device chips
/// (<see cref="ChannelsPaneViewModel.ConnectedDeviceNames"/>), the three tile shelves, the
/// channel → owning-device map the settings drawer resolves through, and the header counts.
/// None of it is reachable from the compiler: the collections are bound as <c>ItemsSource</c> in
/// <c>ChannelsPanePrototype.axaml</c> and <c>ChannelsMobileView.axaml</c>, so a change that
/// reorders or mis-shelves a tile builds green and is only visible on screen. These tests assert
/// the resulting SEQUENCE, not just the membership, for exactly that reason.
/// </para>
///
/// <para>
/// <c>Rebuild</c> is private and runs on two triggers: the constructor calls it directly, and a
/// change to <c>ConnectionManager.ConnectedDevices</c> re-runs it. These tests all use the
/// constructor path — devices registered first, then the pane opened — which is synchronous, and
/// is what a user opening the pane on an already-connected rig runs. The second trigger reaches
/// the SAME method through <c>Dispatcher.UIThread.Post</c>, and this project stands up no
/// dispatcher thread to drain that queue on (draining it from an arbitrary xUnit worker throws
/// <c>VerifyAccess</c>), so what these pin is the method's output rather than the marshalling
/// in front of it.
/// </para>
///
/// <para>
/// Devices are registered through the production <c>ConnectionManager.Instance</c>, so the list
/// the pane walks is the real one. <c>App.InitializeMobile()</c> is what gives
/// <c>LoggingManager.Instance</c> — which the constructor subscribes to — its context factory,
/// against the throwaway data directory the assembly's module initializer already points
/// <c>DAQIFI_DATA_DIR</c> at. It is idempotent.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class ChannelsPaneRebuildTests : IDisposable
{
    private readonly List<IStreamingDevice> _registered = [];
    private readonly List<ChannelsPaneViewModel> _panes = [];

    public ChannelsPaneRebuildTests() => Daqifi.Desktop.App.InitializeMobile();

    /// <summary>
    /// A pane holds a subscription to the process-wide <c>ConnectionManager</c> until it is
    /// disposed, so one left alive would rebuild itself against the next test's devices.
    /// </summary>
    public void Dispose()
    {
        foreach (var pane in _panes)
        {
            try { pane.Dispose(); } catch { /* best-effort cleanup */ }
        }
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }
    }

    #region The device chips

    /// <summary>
    /// The chip row is an <c>ItemsSource</c>, so its order is what the user reads left to right.
    /// It follows the registry, which is connection order.
    /// </summary>
    [Fact]
    public void The_pane_chips_every_connected_device_in_connection_order()
    {
        Connect("AAAA0001", Analog("AI0"));
        Connect("BBBB0002", Analog("AI0"));
        Connect("CCCC0003", Analog("AI0"));

        var pane = OpenPane();

        Assert.Equal(new[] { "AAAA0001", "BBBB0002", "CCCC0003" }, pane.ConnectedDeviceNames);
    }

    /// <summary>With nothing connected the pane is empty rather than stale.</summary>
    [Fact]
    public void With_nothing_connected_the_pane_is_empty()
    {
        var pane = OpenPane();

        Assert.Empty(pane.ConnectedDeviceNames);
        Assert.Empty(pane.AnalogInputs);
        Assert.Empty(pane.DigitalInputs);
        Assert.Empty(pane.DigitalOutputs);
        Assert.False(pane.HasConnectedDevice);
        Assert.False(pane.HasMultipleDevices);
        Assert.Equal("", pane.DeviceName);
    }

    /// <summary>
    /// The header flags and the single-device name the pane shows. Asserted on one device and on
    /// two, because <c>HasMultipleDevices</c> is a <c>bool</c> whose default is the single-device
    /// answer — only the two-device row can fail if it stops being computed.
    /// </summary>
    [Fact]
    public void One_device_names_it_and_reports_a_single_device()
    {
        Connect("AAAA0001", Analog("AI0"));

        var pane = OpenPane();

        Assert.True(pane.HasConnectedDevice);
        Assert.False(pane.HasMultipleDevices);
        Assert.Equal("AAAA0001", pane.DeviceName);
    }

    [Fact]
    public void Two_devices_report_multiple_and_name_the_first()
    {
        Connect("AAAA0001", Analog("AI0"));
        Connect("BBBB0002", Analog("AI0"));

        var pane = OpenPane();

        Assert.True(pane.HasConnectedDevice);
        Assert.True(pane.HasMultipleDevices);
        Assert.Equal("AAAA0001", pane.DeviceName);
    }

    #endregion

    #region Which shelf each channel lands on

    /// <summary>
    /// The shelving rules, all five on one device: an analog input shelves with the analog
    /// inputs; a digital input with the digital inputs; a digital channel in output direction
    /// with the outputs; a PWM-enabled digital INPUT with the outputs too, because a PWM channel
    /// drives its pin regardless of the stored direction (issue #664); and an analog OUTPUT is
    /// shown nowhere — the pane drops it.
    /// </summary>
    [Fact]
    public void Each_channel_shelves_by_type_direction_and_pwm()
    {
        Connect(
            "AAAA0001",
            Analog("AI0"),
            Digital("DI0"),
            Digital("DO0", ChannelDirection.Output),
            Digital("DP0", pwmEnabled: true),
            Analog("AO0", ChannelDirection.Output));

        var pane = OpenPane();

        Assert.Equal(new[] { "AI0" }, Names(pane.AnalogInputs));
        Assert.Equal(new[] { "DI0" }, Names(pane.DigitalInputs));
        // DO0 before DP0: both are outputs and the pane sorts by name within a device.
        Assert.Equal(new[] { "DO0", "DP0" }, Names(pane.DigitalOutputs));
    }

    /// <summary>
    /// The header counts the shelves, and counts the ACTIVE tiles separately. Two of the four
    /// channels here are inactive so no count can pass by matching its shelf's size.
    /// </summary>
    [Fact]
    public void The_header_counts_the_tiles_and_the_active_ones_separately()
    {
        Connect(
            "AAAA0001",
            Analog("AI0"),
            Analog("AI1", isActive: false),
            Digital("DI0"),
            Digital("DO0", ChannelDirection.Output, isActive: false));

        var pane = OpenPane();

        Assert.Equal(2, pane.TotalAnalogCount);
        Assert.Equal(1, pane.ActiveAnalogCount);
        Assert.Equal(1, pane.TotalDigitalInCount);
        Assert.Equal(1, pane.ActiveDigitalInCount);
        Assert.Equal(1, pane.TotalDigitalOutCount);
        Assert.Equal(0, pane.ActiveDigitalOutCount);
        Assert.Equal(2, pane.TotalActive);
    }

    #endregion

    #region The order tiles end up in

    /// <summary>
    /// The tile order the user sees: devices in connection order, and within a device the
    /// channel names in NUMERIC order — <c>AI2</c> before <c>AI10</c>, not the byte-wise answer
    /// (see <c>ChannelOrderingTests</c> for the rule itself). Both devices are given their
    /// channels in the byte-wise order, so a pane that did not sort at all would produce exactly
    /// the wrong sequence rather than accidentally the right one.
    /// </summary>
    [Fact]
    public void Tiles_run_device_by_device_and_numerically_within_each()
    {
        Connect("AAAA0001", Analog("AI0"), Analog("AI10"), Analog("AI2"));
        Connect("BBBB0002", Analog("AI0"), Analog("AI10"), Analog("AI2"));

        var pane = OpenPane();

        Assert.Equal(
            new[]
            {
                "AAAA0001/AI0", "AAAA0001/AI2", "AAAA0001/AI10",
                "BBBB0002/AI0", "BBBB0002/AI2", "BBBB0002/AI10",
            },
            pane.AnalogInputs.Select(t => $"{t.DeviceName}/{t.Name}"));
    }

    /// <summary>
    /// Every tile carries the name of the device that owns it, and shows it only when more than
    /// one device is connected. <c>ShowDeviceLabel</c> is fixed at construction from the pane's
    /// multi-device answer, so a tile built before the pane knows how many devices there are
    /// would hide the label on a two-device rig — the case asserted here, which forces the flag
    /// away from its <c>false</c> default.
    /// </summary>
    [Fact]
    public void Every_tile_labels_its_device_when_more_than_one_is_connected()
    {
        Connect("AAAA0001", Analog("AI0"), Digital("DI0"), Digital("DO0", ChannelDirection.Output));
        Connect("BBBB0002", Analog("AI0"), Digital("DI0"), Digital("DO0", ChannelDirection.Output));

        var pane = OpenPane();

        var tiles = pane.AnalogInputs.Concat(pane.DigitalInputs).Concat(pane.DigitalOutputs).ToList();
        Assert.Equal(6, tiles.Count);
        Assert.All(tiles, t => Assert.True(t.ShowDeviceLabel));
        Assert.Equal(3, tiles.Count(t => t.DeviceName == "AAAA0001"));
        Assert.Equal(3, tiles.Count(t => t.DeviceName == "BBBB0002"));
    }

    /// <summary>The other half of the same rule: one device, no labels.</summary>
    [Fact]
    public void A_single_device_leaves_the_tile_labels_off()
    {
        Connect("AAAA0001", Analog("AI0"), Digital("DI0"));

        var pane = OpenPane();

        var tiles = pane.AnalogInputs.Concat(pane.DigitalInputs).ToList();
        Assert.Equal(2, tiles.Count);
        Assert.All(tiles, t => Assert.False(t.ShowDeviceLabel));
        Assert.All(tiles, t => Assert.Equal("AAAA0001", t.DeviceName));
    }

    #endregion

    #region The ownership map the settings drawer resolves through

    /// <summary>
    /// Opening a tile's settings drawer points <c>SelectedDevice</c> at the device that owns that
    /// channel, resolved through the map the rebuild captured. The tile chosen belongs to the
    /// SECOND device, so a map that was never populated (the drawer would get <c>null</c>) and a
    /// map that resolved everything to the first device both fail.
    /// </summary>
    [Fact]
    public void The_settings_drawer_opens_on_the_device_that_owns_the_channel()
    {
        var a = Connect("AAAA0001", Analog("AI0"));
        var b = Connect("BBBB0002", Analog("AI0"));

        var pane = OpenPane();
        var tileOnB = pane.AnalogInputs.Single(t => t.DeviceName == "BBBB0002");

        pane.OpenSettingsCommand.Execute(tileOnB);

        Assert.True(pane.IsSettingsOpen);
        Assert.Same(b, pane.SelectedDevice);
        Assert.NotSame(a, pane.SelectedDevice);
    }

    #endregion

    #region Helpers

    private static string[] Names(IEnumerable<ChannelTileViewModel> tiles) =>
        [.. tiles.Select(t => t.Name)];

    private ChannelsPaneViewModel OpenPane()
    {
        var pane = new ChannelsPaneViewModel();
        _panes.Add(pane);
        return pane;
    }

    private RecordingStreamingDevice Connect(string serial, params IChannel[] channels)
    {
        var device = new RecordingStreamingDevice(serial) { DataChannels = [.. channels] };
        foreach (var channel in channels)
        {
            channel.DeviceSerialNo = serial;
            channel.DeviceName = serial;
        }
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
        return device;
    }

    private static PaneTestChannel Analog(
        string name, ChannelDirection direction = ChannelDirection.Input, bool isActive = true) =>
        new(name, ChannelType.Analog, direction, isActive);

    private static PaneTestChannel Digital(
        string name,
        ChannelDirection direction = ChannelDirection.Input,
        bool isActive = true,
        bool pwmEnabled = false) =>
        new(name, ChannelType.Digital, direction, isActive) { PwmOn = pwmEnabled };

    #endregion
}

/// <summary>
/// A channel that can be any of the shapes the Channels pane shelves. <c>FakeChannel</c> in
/// <c>DroppedDeviceTestDoubles</c> is analog-only and cannot express a digital output or a
/// PWM-enabled channel, which are two of the five cases the pane distinguishes.
/// </summary>
internal sealed class PaneTestChannel : AbstractChannel
{
    private string _name;
    private ChannelDirection _direction;
    private bool _isActive;

    internal PaneTestChannel(string name, ChannelType type, ChannelDirection direction, bool isActive)
    {
        _name = name;
        _direction = direction;
        _isActive = isActive;
        Type = type;
        DeviceName = "";
        DeviceSerialNo = "";
    }

    /// <summary>Whether PWM is running on this channel. Only meaningful on a digital one.</summary>
    internal bool PwmOn { get; init; }

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

    public override bool IsActive
    {
        get => _isActive;
        set => _isActive = value;
    }

    public override int Index => 0;

    public override ChannelType Type { get; }

    public override bool IsDigital => Type == ChannelType.Digital;

    public override bool IsAnalog => Type == ChannelType.Analog;

    public override bool IsPwmCapable => IsDigital;

    public override bool IsPwmEnabled
    {
        get => PwmOn;
        set { }
    }
}
