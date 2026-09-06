using System.ComponentModel;
using Daqifi.Core.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #282: the two rate readouts did not follow a rate the device itself lowered, so a session
/// recorded at one rate while the screen stated another.
///
/// <para>
/// The device lowers its own rate, and since PR #283 it routinely does:
/// <c>AbstractStreamingDevice.HoldRateToCurrentConfigurationCap</c> holds the rate under what the
/// enabled channel set can sustain at the start of every session — 7746 Hz for one analog input on
/// the bench Nq1 (fw 3.7.2), against the 22000 Hz that board advertises as its ceiling — and
/// <c>ClampRateToAdvertisedCeiling</c> lowers it again whenever a re-described device moves that
/// ceiling. Both assign <c>StreamingFrequency</c> on the wrapper, which raises
/// <c>PropertyChanged</c>. Nothing was listening.
/// </para>
///
/// <para>
/// The two readouts were stale for different reasons and the fixes differ:
/// <c>DaqifiViewModel.SelectedStreamingFrequency</c> was a cached copy written only by its own
/// setter and is now a read-through of the device; <c>DevicesPaneViewModel.FrequencyHz</c> already
/// read the device live but was announced only by <c>SelectedTile</c>, so a drawer left open on one
/// device never re-read it. The tests below are split the same way, because the evidence is
/// different: the chip states a wrong number, while the drawer holds the right one and never shows
/// it.
/// </para>
///
/// <para>
/// The rate figures used here are the bench board's real ones, so a reader can line the tests up
/// with what the device does: 22000 Hz advertised, 15492 Hz asked for, 7746 Hz settled on.
/// </para>
/// </summary>
public class DeviceLoweredRateReadoutTests
{
    /// <summary>Repo-relative path of the view carrying the header RATE chip.</summary>
    private const string LiveGraphView =
        "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LiveGraphPane.axaml";

    private const string DesktopDevicesView =
        "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/DevicesPanePrototype.axaml";

    private const string MobileDevicesView =
        "Daqifi.Avalonia/Views/Mobile/DevicesMobileView.axaml";

    #region The RATE chip follows the device
    /// <summary>
    /// The defect, stated as the user meets it: the rate is set, the device settles on a lower one
    /// when the session starts, and the chip goes on stating the rate that was asked for.
    /// </summary>
    [Fact]
    public void The_rate_chip_states_the_rate_the_device_settled_on_not_the_one_it_was_asked_for()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var device = DeviceAdvertising(22000);
        shell.ConnectedDevices.Add(device);
        shell.SelectedDevice = device;

        shell.SelectedStreamingFrequency = 15492;
        Assert.Equal(15492, shell.SelectedStreamingFrequency);

        // What HoldRateToCurrentConfigurationCap does at the start of a session on one analog input.
        device.StreamingFrequency = 7746;

        Assert.Equal(7746, shell.SelectedStreamingFrequency);
    }

    /// <summary>
    /// And says so. The chip is a reflection binding with no C# subscriber, so a getter that
    /// returns the right number without a notification is a chip that never re-reads it.
    /// </summary>
    [Fact]
    public void The_rate_chip_is_told_when_the_device_lowers_the_rate()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var device = DeviceAdvertising(22000);
        shell.ConnectedDevices.Add(device);
        shell.SelectedDevice = device;
        shell.SelectedStreamingFrequency = 15492;

        var announced = new List<string?>();
        shell.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        device.StreamingFrequency = 7746;

        Assert.Contains(nameof(DaqifiViewModel.SelectedStreamingFrequency), announced);
    }

    /// <summary>
    /// A ceiling that moves lowers the rate too, without any session starting — this is the path
    /// <c>HydrateDeviceMetadata</c> takes when a reconnecting device re-describes itself, and it
    /// predates PR #283. Same readout, same notification, so it is pinned on the same seam rather
    /// than left to be rediscovered.
    /// </summary>
    [Fact]
    public void The_rate_chip_follows_a_rate_lowered_by_a_ceiling_that_moved()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var device = DeviceAdvertising(22000);
        shell.ConnectedDevices.Add(device);
        shell.SelectedDevice = device;
        shell.SelectedStreamingFrequency = 15492;

        // The device re-describes itself with a lower ceiling, and the wrapper brings the stored
        // rate under it (AbstractStreamingDevice.ClampRateToAdvertisedCeiling).
        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        device.StreamingFrequency = device.MaxStreamingFrequency;

        Assert.Equal(1000, shell.SelectedStreamingFrequency);
    }
    #endregion

    #region Which device the one chip describes
    /// <summary>
    /// One chip, possibly several devices. It describes the device the Devices drawer selected —
    /// which is also the device its own setter writes to — so moving the drawer to another device
    /// moves the chip with it.
    /// </summary>
    [Fact]
    public void The_rate_chip_describes_the_device_the_drawer_selected()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var first = DeviceAdvertising(22000, "COM-282-A");
        var second = DeviceAdvertising(22000, "COM-282-B");
        shell.ConnectedDevices.Add(first);
        shell.ConnectedDevices.Add(second);

        shell.SelectedDevice = first;
        shell.SelectedStreamingFrequency = 1000;
        Assert.Equal(1000, shell.SelectedStreamingFrequency);

        second.StreamingFrequency = 500;

        // The user opens the drawer on the other device (DevicesPaneViewModel.OpenSettings).
        shell.SelectedDevice = second;

        Assert.Equal(500, shell.SelectedStreamingFrequency);
        Assert.Equal(1000, first.StreamingFrequency);
    }

    /// <summary>
    /// And a rate written for one device is never written to the other: the chip is a readout of
    /// one device, not an aggregate the fleet follows.
    /// </summary>
    [Fact]
    public void Writing_the_chip_moves_only_the_selected_device()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var first = DeviceAdvertising(22000, "COM-282-A");
        var second = DeviceAdvertising(22000, "COM-282-B");
        second.StreamingFrequency = 250;
        shell.ConnectedDevices.Add(first);
        shell.ConnectedDevices.Add(second);
        shell.SelectedDevice = first;

        shell.SelectedStreamingFrequency = 1000;

        Assert.Equal(250, second.StreamingFrequency);
    }

    /// <summary>
    /// Before any drawer has been opened there is no selection, and the chip still has to read
    /// something. It reads the first connected device — which for the single-device case that is
    /// nearly every session is simply "the device".
    /// </summary>
    [Fact]
    public void The_rate_chip_reads_the_first_connected_device_until_a_drawer_is_opened()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var device = DeviceAdvertising(22000);
        device.StreamingFrequency = 7746;

        shell.ConnectedDevices.Add(device);

        Assert.Null(shell.SelectedDevice);
        Assert.Equal(7746, shell.SelectedStreamingFrequency);
    }

    /// <summary>
    /// With nothing connected there is no device to read, and the setter has nowhere to write. It
    /// must not throw: the chip's setter is reached from a two-way binding, and a throw out of a
    /// property setter travels into the dispatcher and ends the process (the #183/#214 shape).
    /// </summary>
    [Fact]
    public void The_rate_chip_survives_having_no_device_to_describe()
    {
        var shell = new DaqifiViewModel(new NullDialogService());

        shell.SelectedStreamingFrequency = 1000;

        Assert.Equal(0, shell.SelectedStreamingFrequency);
    }
    #endregion

    #region The bindings that carry these readouts
    /// <summary>
    /// The chip is a reflection binding — no view in this repo declares an <c>x:DataType</c> — so a
    /// rename on either side fails silently at runtime while both heads build green. This change
    /// turned the member the chip names into a computed property, which is exactly the edit that
    /// can lose it.
    /// </summary>
    [Fact]
    public void The_live_graph_header_binds_the_shells_rate_readout()
    {
        BindingFacts.AssertBinds(LiveGraphView, "Text=\"{Binding SelectedStreamingFrequency}\"");

        BindingFacts.AssertExposes(typeof(DaqifiViewModel), "SelectedStreamingFrequency");
    }

    /// <summary>The same pair for the drawer's FREQUENCY slider, on both views over that one pane.</summary>
    [Theory]
    [InlineData(DesktopDevicesView)]
    [InlineData(MobileDevicesView)]
    public void The_devices_drawer_binds_the_panes_rate_readout(string viewPath)
    {
        BindingFacts.AssertBinds(viewPath, "Value=\"{Binding FrequencyHz, Delay=500}\"");

        BindingFacts.AssertExposes(typeof(DevicesPaneViewModel), "FrequencyHz");
    }
    #endregion

    #region Helpers
    /// <summary>
    /// A concrete wrapper advertising <paramref name="maxSamplingRate"/> Hz. Nothing here connects
    /// or sends: <c>StreamingFrequency</c> is stored on the wrapper and only reaches Core at a
    /// handoff, which no test drives.
    /// </summary>
    internal static SerialStreamingDevice DeviceAdvertising(
        int maxSamplingRate, string portName = "COM-TEST-282")
    {
        var device = new SerialStreamingDevice(portName);
        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = maxSamplingRate };
        return device;
    }
    #endregion
}

/// <summary>
/// The half of issue #282 that needs the <c>ConnectionManager</c> singleton: the drawer's readout,
/// whose view model reads the registry when it is built, and the connect path that used to seed the
/// RATE chip.
/// </summary>
/// <remarks>
/// In <see cref="ConnectionManagerSingletonCollection"/> because
/// <see cref="DevicesPaneViewModel"/>'s constructor subscribes to <c>ConnectionManager.Instance</c>
/// and enumerates its device list, and <see cref="DaqifiViewModel.UpdateConnectedDeviceUI"/> reads
/// that same list — a class registering a device on another thread would be enumerated mid-write.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class DeviceLoweredRateDrawerTests : IDisposable
{
    private readonly List<IStreamingDevice> _registered = [];

    public void Dispose()
    {
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }

        GC.SuppressFinalize(this);
    }

    #region The drawer's FREQUENCY readout
    /// <summary>
    /// The drawer reads the device rather than a copy, which is why this half of the issue is not
    /// about a wrong number. Stated so the next reader can see what the notification test below is
    /// and is not claiming.
    /// </summary>
    [Fact]
    public void The_open_drawer_reads_the_rate_the_device_settled_on()
    {
        using var pane = OpenDrawerOn(out var device);

        device.StreamingFrequency = 7746;

        Assert.Equal(7746, pane.FrequencyHz);
    }

    /// <summary>
    /// The defect: nothing announces it. The drawer stays open on one device for the whole session,
    /// so <c>SelectedTile</c> — its only announcement — never fires again, and the slider goes on
    /// showing the rate it was drawn with.
    /// </summary>
    [Fact]
    public void The_open_drawer_is_told_when_the_device_lowers_the_rate()
    {
        using var pane = OpenDrawerOn(out var device);

        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        device.StreamingFrequency = 7746;

        Assert.Contains(nameof(DevicesPaneViewModel.FrequencyHz), announced);
    }

    /// <summary>
    /// The subscription follows the drawer. A device the drawer has moved off must not go on
    /// announcing into a readout that no longer describes it — and a disposed pane must not be held
    /// alive by a device that outlives it.
    /// </summary>
    [Fact]
    public void A_device_the_drawer_left_stops_announcing_into_it()
    {
        using var pane = OpenDrawerOn(out var device);
        pane.CloseSettingsCommand.Execute(null);

        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        device.StreamingFrequency = 7746;

        Assert.DoesNotContain(nameof(DevicesPaneViewModel.FrequencyHz), announced);
    }

    /// <summary>Same guarantee for teardown, which closes no drawer.</summary>
    [Fact]
    public void A_disposed_pane_stops_listening_to_the_device_it_was_showing()
    {
        var pane = OpenDrawerOn(out var device);

        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);
        pane.Dispose();

        device.StreamingFrequency = 7746;

        Assert.DoesNotContain(nameof(DevicesPaneViewModel.FrequencyHz), announced);
    }
    #endregion

    #region The connect path that used to seed the chip
    /// <summary>
    /// The RATE chip's connect-time seed (issue #686) is deleted by this change, so the guarantee it
    /// gave is pinned on the path it sat in: after a device connects, the chip states that device's
    /// rate without anyone touching the slider.
    /// </summary>
    [Fact]
    public async Task A_connected_device_puts_its_rate_on_the_chip_with_no_seed()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var device = DeviceLoweredRateReadoutTests.DeviceAdvertising(22000);
        device.StreamingFrequency = 7746;
        Register(device);

        await shell.UpdateConnectedDeviceUI();

        Assert.Equal(7746, shell.SelectedStreamingFrequency);
    }
    #endregion

    #region Helpers
    /// <summary>
    /// A Devices pane with its settings drawer open on a fresh device, exactly as
    /// <c>OpenSettings</c> leaves it — including the shell's <c>SelectedDevice</c>, which the
    /// drawer's slider writes through.
    /// </summary>
    private DevicesPaneViewModel OpenDrawerOn(out SerialStreamingDevice device)
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = new DevicesPaneViewModel(shell);

        device = DeviceLoweredRateReadoutTests.DeviceAdvertising(22000);
        device.StreamingFrequency = 15492;
        pane.OpenSettingsCommand.Execute(new DeviceTileViewModel(device));

        Assert.Equal(15492, pane.FrequencyHz);
        return pane;
    }

    private void Register(IStreamingDevice device)
    {
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
    }
    #endregion
}
