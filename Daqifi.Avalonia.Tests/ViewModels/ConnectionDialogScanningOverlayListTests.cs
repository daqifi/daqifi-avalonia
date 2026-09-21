using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Reflection;
using Daqifi.Core.Device.Discovery;
using Daqifi.Desktop;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins each connection-dialog tab's "Scanning…" overlay to the device list underneath it: the
/// overlay shows exactly when the list is empty, and the view is TOLD when that changes.
///
/// <para>
/// Both halves matter because they fail differently. A wrong value draws the overlay over a list
/// that has devices in it (or hides it over an empty one). A right value with no
/// <c>PropertyChanged</c> is invisible to every assertion on the value and still leaves the screen
/// stale, because the binding never re-reads it — so each list-changing path below asserts the
/// notification as well as the state.
/// </para>
///
/// <para>
/// The serial tab's own list paths are covered in <see cref="ConnectionDialogSerialListTests"/>;
/// what is here for serial is only the notification, which that class does not watch. WiFi and the
/// HID Firmware tab had no list coverage at all.
/// </para>
///
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because the view model's constructor
/// subscribes to that singleton and the discovery starts read its firmware gate. Nothing here opens a
/// port or binds a socket: both finder seams are replaced by finders that report nothing.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class ConnectionDialogScanningOverlayListTests
{
    #region WiFi
    [Fact]
    public void A_wifi_device_found_by_the_current_scan_hides_the_overlay_and_says_so()
    {
        using var viewModel = CreateViewModel();
        InvokePrivate(viewModel.Value, "StartWiFiDiscovery");
        var scan = GetPrivateField(viewModel.Value, "_wifiFinder");
        Assert.True(viewModel.Value.IsWiFiDiscoveryScanning);

        var raised = RecordPropertyChanges(viewModel.Value);
        RaiseWifiDiscovery(viewModel.Value, scan, "AA:BB:CC:00:00:01");

        Assert.Single(viewModel.Value.AvailableWiFiDevices);
        Assert.False(viewModel.Value.IsWiFiDiscoveryScanning);
        Assert.Contains(nameof(ConnectionDialogViewModel.IsWiFiDiscoveryScanning), raised);
    }

    [Fact]
    public void A_retired_wifi_scans_late_discovery_leaves_the_overlay_up()
    {
        using var viewModel = CreateViewModel();

        // Never started, so no scan is current and every sender is a retired one.
        RaiseWifiDiscovery(viewModel.Value, new object(), "AA:BB:CC:00:00:02");

        Assert.Empty(viewModel.Value.AvailableWiFiDevices);
        Assert.True(viewModel.Value.IsWiFiDiscoveryScanning);
    }

    [Fact]
    public void Losing_one_of_two_wifi_devices_keeps_the_overlay_away()
    {
        using var viewModel = CreateViewModel();
        InvokePrivate(viewModel.Value, "StartWiFiDiscovery");
        var scan = GetPrivateField(viewModel.Value, "_wifiFinder");
        RaiseWifiDiscovery(viewModel.Value, scan, "AA:BB:CC:00:00:03");
        RaiseWifiDiscovery(viewModel.Value, scan, "AA:BB:CC:00:00:04");

        RaiseWifiLoss(viewModel.Value, scan, "AA:BB:CC:00:00:03");

        Assert.Single(viewModel.Value.AvailableWiFiDevices);
        Assert.False(viewModel.Value.IsWiFiDiscoveryScanning);
    }

    [Fact]
    public void Losing_the_last_wifi_device_restores_the_overlay_and_says_so()
    {
        using var viewModel = CreateViewModel();
        InvokePrivate(viewModel.Value, "StartWiFiDiscovery");
        var scan = GetPrivateField(viewModel.Value, "_wifiFinder");
        RaiseWifiDiscovery(viewModel.Value, scan, "AA:BB:CC:00:00:05");
        Assert.False(viewModel.Value.IsWiFiDiscoveryScanning);

        var raised = RecordPropertyChanges(viewModel.Value);
        RaiseWifiLoss(viewModel.Value, scan, "AA:BB:CC:00:00:05");

        Assert.Empty(viewModel.Value.AvailableWiFiDevices);
        Assert.True(viewModel.Value.IsWiFiDiscoveryScanning);
        Assert.Contains(nameof(ConnectionDialogViewModel.IsWiFiDiscoveryScanning), raised);
    }

    /// <summary>
    /// The resume-after-firmware path: a fresh scan drops what the previous one found (issue #621),
    /// so the overlay has to come back with it.
    /// </summary>
    [Fact]
    public async Task Restarting_wifi_discovery_drops_the_old_list_and_restores_the_overlay()
    {
        using var viewModel = CreateViewModel();
        InvokePrivate(viewModel.Value, "StartWiFiDiscovery");
        RaiseWifiDiscovery(
            viewModel.Value, GetPrivateField(viewModel.Value, "_wifiFinder"), "AA:BB:CC:00:00:06");
        Assert.False(viewModel.Value.IsWiFiDiscoveryScanning);

        await InvokePrivateAsync(viewModel.Value, "StopWiFiDiscoveryAsync");
        var raised = RecordPropertyChanges(viewModel.Value);
        InvokePrivate(viewModel.Value, "StartWiFiDiscovery");

        Assert.Empty(viewModel.Value.AvailableWiFiDevices);
        Assert.True(viewModel.Value.IsWiFiDiscoveryScanning);
        Assert.Contains(nameof(ConnectionDialogViewModel.IsWiFiDiscoveryScanning), raised);
    }
    #endregion

    #region Serial (notification only)
    [Fact]
    public void A_serial_device_found_by_the_current_scan_says_the_overlay_changed()
    {
        using var viewModel = CreateViewModel();
        InvokePrivate(viewModel.Value, "StartSerialDiscovery");
        var scan = GetPrivateField(viewModel.Value, "_serialFinder");

        var raised = RecordPropertyChanges(viewModel.Value);
        RaiseSerialDiscovery(viewModel.Value, scan, "COM-OVERLAY");

        Assert.False(viewModel.Value.IsSerialDiscoveryScanning);
        Assert.Contains(nameof(ConnectionDialogViewModel.IsSerialDiscoveryScanning), raised);
    }

    [Fact]
    public void Losing_the_last_serial_device_says_the_overlay_changed()
    {
        using var viewModel = CreateViewModel();
        InvokePrivate(viewModel.Value, "StartSerialDiscovery");
        var scan = GetPrivateField(viewModel.Value, "_serialFinder");
        RaiseSerialDiscovery(viewModel.Value, scan, "COM-OVERLAY");

        var raised = RecordPropertyChanges(viewModel.Value);
        RaiseSerialLoss(viewModel.Value, scan, "COM-OVERLAY");

        Assert.True(viewModel.Value.IsSerialDiscoveryScanning);
        Assert.Contains(nameof(ConnectionDialogViewModel.IsSerialDiscoveryScanning), raised);
    }
    #endregion

    #region HID
    [Fact]
    public void Without_a_watcher_the_firmware_tab_shows_its_overlay()
    {
        using var viewModel = CreateViewModel();

        Assert.True(viewModel.Value.HasNoHidDevices);
    }

    /// <summary>
    /// The watcher is app-global, so it can already be holding a bootloader when the dialog opens.
    /// </summary>
    [Fact]
    public void A_bootloader_held_before_the_dialog_opens_hides_the_overlay_from_the_start()
    {
        var watcher = new ListOnlyBootloaderWatcher();
        watcher.Hold("held-before-open");

        using var viewModel = CreateViewModel(watcher);

        Assert.False(viewModel.Value.HasNoHidDevices);
    }

    [Fact]
    public void A_bootloader_arriving_while_open_hides_the_overlay_and_says_so()
    {
        var watcher = new ListOnlyBootloaderWatcher();
        using var viewModel = CreateViewModel(watcher);
        Assert.True(viewModel.Value.HasNoHidDevices);

        var raised = RecordPropertyChanges(viewModel.Value);
        watcher.Hold("arrives-while-open");

        Assert.False(viewModel.Value.HasNoHidDevices);
        Assert.Contains(nameof(ConnectionDialogViewModel.HasNoHidDevices), raised);
    }

    [Fact]
    public void The_last_bootloader_leaving_restores_the_overlay_and_says_so()
    {
        var watcher = new ListOnlyBootloaderWatcher();
        watcher.Hold("stays");
        watcher.Hold("leaves");
        using var viewModel = CreateViewModel(watcher);

        watcher.Drop("leaves");
        Assert.False(viewModel.Value.HasNoHidDevices);

        var raised = RecordPropertyChanges(viewModel.Value);
        watcher.Drop("stays");

        Assert.True(viewModel.Value.HasNoHidDevices);
        Assert.Contains(nameof(ConnectionDialogViewModel.HasNoHidDevices), raised);
    }
    #endregion

    #region Harness
    private static List<string?> RecordPropertyChanges(INotifyPropertyChanged source)
    {
        var raised = new List<string?>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    private static void RaiseWifiDiscovery(ConnectionDialogViewModel viewModel, object? scan, string mac) =>
        viewModel.HandleCoreWifiDeviceDiscovered(scan, new DeviceDiscoveredEventArgs(WifiInfo(mac)));

    private static void RaiseWifiLoss(ConnectionDialogViewModel viewModel, object? scan, string mac) =>
        InvokePrivate(viewModel, "HandleCoreWifiDeviceLost", scan, new DeviceLostEventArgs(WifiInfo(mac)));

    private static DeviceInfo WifiInfo(string mac) => new()
    {
        Name = "Nq1 " + mac,
        SerialNumber = "SN-" + mac,
        MacAddress = mac,
        IPAddress = IPAddress.Parse("192.0.2.10"),
        Port = 9760,
    };

    private static void RaiseSerialDiscovery(ConnectionDialogViewModel viewModel, object? scan, string port) =>
        InvokePrivate(viewModel, "HandleCoreSerialDeviceDiscovered", scan, new DeviceDiscoveredEventArgs(SerialInfo(port)));

    private static void RaiseSerialLoss(ConnectionDialogViewModel viewModel, object? scan, string port) =>
        InvokePrivate(viewModel, "HandleCoreSerialDeviceLost", scan, new DeviceLostEventArgs(SerialInfo(port)));

    private static DeviceInfo SerialInfo(string port) => new()
    {
        Name = port,
        SerialNumber = "SN-" + port,
        FirmwareVersion = "1.0.1.24",
        PortName = port,
    };

    /// <summary>
    /// A view model with its UI marshal run inline (the test host never pumps
    /// <c>Dispatcher.UIThread</c>) and both finders replaced by ones that report nothing and touch no
    /// hardware.
    /// </summary>
    private static ClosingViewModel CreateViewModel(IBootloaderWatcher? watcher = null)
    {
        var viewModel = new ConnectionDialogViewModel(null!, watcher);
        SetPrivateField(viewModel, "_marshalToUiThread", (Action<Action>)(action => action()));
        SetPrivateField(viewModel, "_createSerialFinder", (Func<SerialDeviceFinder>)(() => new SilentSerialFinder()));
        SetPrivateField(viewModel, "_createWifiFinder", (Func<WiFiDeviceFinder>)(() => new SilentWiFiFinder()));
        return new ClosingViewModel(viewModel);
    }

    private sealed class ClosingViewModel(ConnectionDialogViewModel value) : IDisposable
    {
        public ConnectionDialogViewModel Value { get; } = value;

        public void Dispose() => Value.Close();
    }

    private sealed class SilentSerialFinder : SerialDeviceFinder
    {
        public override Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Empty<IDeviceInfo>());
    }

    private sealed class SilentWiFiFinder : WiFiDeviceFinder
    {
        public override Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Empty<IDeviceInfo>());
    }

    /// <summary>
    /// Just the list half of <see cref="IBootloaderWatcher"/>: bootloaders arrive and leave, and nothing
    /// is ever flashed.
    /// </summary>
    private sealed class ListOnlyBootloaderWatcher : IBootloaderWatcher
    {
        private readonly ObservableCollection<HeldBootloader> _held = [];

        public ListOnlyBootloaderWatcher() => Bootloaders = new ReadOnlyObservableCollection<HeldBootloader>(_held);

        public ReadOnlyObservableCollection<HeldBootloader> Bootloaders { get; }

        public void Hold(string path) => _held.Add(new HeldBootloader(path, "Bootloader " + path));

        public void Drop(string path) => _held.Remove(_held.Single(b => b.DevicePath == path));

#pragma warning disable CS0067 // Part of the interface; nothing here flashes or drops a hold.
        public event EventHandler<BootloaderHoldDroppedEventArgs>? HoldDropped;
        public event EventHandler? FlashInProgressChanged;
#pragma warning restore CS0067

        public bool IsFlashInProgress => false;

        public void Start() { }

        public Task<IAsyncDisposable> PrepareFlashAsync(string devicePath) => throw new NotSupportedException();

        public Task<IAsyncDisposable> SuspendDiscoveryAsync() => throw new NotSupportedException();
    }

    private static void InvokePrivate(ConnectionDialogViewModel viewModel, string methodName, params object?[] args)
    {
        var method = typeof(ConnectionDialogViewModel).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(viewModel, args.Length == 0 ? null : args);
    }

    private static async Task InvokePrivateAsync(ConnectionDialogViewModel viewModel, string methodName)
    {
        var method = typeof(ConnectionDialogViewModel).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method.Invoke(viewModel, null));
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
