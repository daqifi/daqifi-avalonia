using System.Collections.Specialized;
using System.ComponentModel;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #410: in Logged Data → DEVICE LOGS, a device connecting or dropping snapped the DEVICE
/// combo back to the first device, re-listed that device's SD card, and left IMPORT ALL armed
/// against it — even though the device the user had picked was still connected.
///
/// <para>
/// The mechanism needs the view: <c>UpdateConnectedDevices</c> rebuilt its collection with
/// <c>Clear()</c> + re-add, the combo's two-way <c>SelectedItem</c> wrote <c>null</c> back when its
/// items emptied, and the "nothing selected" fallback then chose device #1. This project constructs
/// no Avalonia controls (see its csproj), so <see cref="AttachDeviceCombo"/> stands in for the
/// combo by doing exactly what the issue's headless repro observed it do: when the selected item
/// is no longer among the items, write <c>null</c> into <c>SelectedDevice</c>. Both views over this
/// view-model bind it that way (<c>DeviceLogsView.axaml</c> and <c>DeviceLogsMobileView.axaml</c>),
/// so the fix — and these tests — cover desktop and mobile alike.
/// </para>
///
/// <para>
/// Devices are registered through the production <c>ConnectionManager.Instance</c>, the same route
/// the issue's repro used, so the notification that drives the update is the real one.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class DeviceLogsSelectionTests : IDisposable
{
    private readonly List<IStreamingDevice> _registered = [];

    /// <summary>Every SD refresh a view-model in this test started, so a test can wait them out.</summary>
    private readonly List<Task> _refreshes = [];

    /// <summary>
    /// Set on teardown. The view-model subscribes to the process-wide <c>ConnectionManager</c> and
    /// never lets go, so a view-model from an earlier test would otherwise react to this test's
    /// devices — select one and send it an SD listing this test would count.
    /// </summary>
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }
    }

    #region An unrelated device changing leaves the selection alone
    /// <summary>The issue's own repro: pick B, then a third device connects.</summary>
    [Fact]
    public async Task Another_device_connecting_keeps_the_device_the_user_picked()
    {
        var (vm, _, b) = await PaneWithUserOnSecondDevice();

        Register(new RecordingStreamingDevice("CCCC0003"));
        await Settle();

        Assert.Same(b, vm.SelectedDevice);
    }

    [Fact]
    public async Task Another_device_dropping_keeps_the_device_the_user_picked()
    {
        var (vm, _, b) = await PaneWithUserOnSecondDevice();
        var c = new RecordingStreamingDevice("CCCC0003");
        Register(c);
        await Settle();

        ConnectionManager.Instance.UnregisterConnectedDevice(c);
        await Settle();

        Assert.Same(b, vm.SelectedDevice);
    }

    /// <summary>
    /// The consequence that reaches the device: every connect of any device sent the first device a
    /// fresh SD listing. Neither the device the user is looking at nor any other should be sent one
    /// for a change that did not touch them.
    /// </summary>
    [Fact]
    public async Task Another_device_connecting_sends_no_SD_listing_to_any_device()
    {
        var (_, a, b) = await PaneWithUserOnSecondDevice();
        var aBefore = a.SdCardListings;
        var bBefore = b.SdCardListings;

        var c = new RecordingStreamingDevice("CCCC0003");
        Register(c);
        await Settle();

        Assert.Equal(aBefore, a.SdCardListings);
        Assert.Equal(bBefore, b.SdCardListings);
        Assert.Equal(0, c.SdCardListings);
    }

    /// <summary>
    /// The issue's single-selection case: with only device #1 ever selected, a second device
    /// connecting still re-listed device #1's card (the selection went null, then #1 again).
    /// </summary>
    [Fact]
    public async Task A_second_device_connecting_does_not_re_list_the_first_devices_card()
    {
        var a = new RecordingStreamingDevice("AAAA0001");
        Register(a);
        var vm = OpenPane();
        await Settle();
        Assert.Same(a, vm.SelectedDevice);
        var before = a.SdCardListings;

        Register(new RecordingStreamingDevice("BBBB0002"));
        await Settle();

        Assert.Same(a, vm.SelectedDevice);
        Assert.Equal(before, a.SdCardListings);
    }

    /// <summary>
    /// And the selection is never written at all — not even to null and back. Every write runs
    /// <c>OnSelectedDeviceChanged</c>, which resets the SD panel and starts a listing.
    /// </summary>
    [Fact]
    public async Task Another_device_connecting_writes_nothing_to_the_selection()
    {
        var (vm, _, _) = await PaneWithUserOnSecondDevice();
        var writes = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceLogsViewModel.SelectedDevice))
            {
                writes.Add(vm.SelectedDevice?.DeviceSerialNo);
            }
        };

        Register(new RecordingStreamingDevice("CCCC0003"));
        await Settle();

        Assert.Empty(writes);
    }
    #endregion

    #region The selected device itself leaving still falls back
    /// <summary>
    /// What the issue says must keep working: when the device the user picked is the one that
    /// leaves, the pane moves to the first device still connected and lists its card.
    /// </summary>
    [Fact]
    public async Task The_selected_device_leaving_falls_back_to_the_first_remaining_device()
    {
        var (vm, a, b) = await PaneWithUserOnSecondDevice();
        var aBefore = a.SdCardListings;

        ConnectionManager.Instance.UnregisterConnectedDevice(b);
        await Settle();

        Assert.Same(a, vm.SelectedDevice);
        Assert.Equal(aBefore + 1, a.SdCardListings);
        Assert.DoesNotContain(b, vm.ConnectedDevices);
    }

    /// <summary>
    /// The same fallback without a combo to null the selection first: the view-model decides for
    /// itself that the device left, rather than relying on the view to clear it. Before #410 the
    /// view-model only ever checked for null, so with no view attached it went on holding the
    /// departed device.
    /// </summary>
    [Fact]
    public async Task The_selected_device_leaving_falls_back_even_with_no_view_attached()
    {
        var a = new RecordingStreamingDevice("AAAA0001");
        var b = new RecordingStreamingDevice("BBBB0002");
        Register(a);
        Register(b);
        var vm = OpenPane(withCombo: false);
        vm.SelectedDevice = b;
        await Settle();

        ConnectionManager.Instance.UnregisterConnectedDevice(b);
        await Settle();

        Assert.Same(a, vm.SelectedDevice);
    }

    [Fact]
    public async Task The_last_device_leaving_clears_the_selection()
    {
        var a = new RecordingStreamingDevice("AAAA0001");
        Register(a);
        var vm = OpenPane(withCombo: false);
        await Settle();
        Assert.Same(a, vm.SelectedDevice);

        ConnectionManager.Instance.UnregisterConnectedDevice(a);
        await Settle();

        Assert.Null(vm.SelectedDevice);
        Assert.Empty(vm.ConnectedDevices);
    }

    /// <summary>
    /// The list itself still follows the registry: arrivals appended in the order they connected,
    /// departures removed.
    /// </summary>
    [Fact]
    public async Task The_device_list_follows_connects_and_drops()
    {
        var (vm, a, b) = await PaneWithUserOnSecondDevice();
        var c = new RecordingStreamingDevice("CCCC0003");

        Register(c);
        ConnectionManager.Instance.UnregisterConnectedDevice(a);
        await Settle();

        Assert.Equal(new IStreamingDevice[] { b, c }, vm.ConnectedDevices);
    }
    #endregion

    #region Helpers
    /// <summary>
    /// A and B connected, the pane open, and the user moved the combo from A (the default) to B —
    /// the "before" of the issue's screenshots.
    /// </summary>
    private async Task<(DeviceLogsViewModel Vm, RecordingStreamingDevice A, RecordingStreamingDevice B)>
        PaneWithUserOnSecondDevice()
    {
        var a = new RecordingStreamingDevice("AAAA0001");
        var b = new RecordingStreamingDevice("BBBB0002");
        Register(a);
        Register(b);
        var vm = OpenPane();
        Assert.Same(a, vm.SelectedDevice);

        vm.SelectedDevice = b;
        await Settle();

        Assert.Same(b, vm.SelectedDevice);
        return (vm, a, b);
    }

    private DeviceLogsViewModel OpenPane(bool withCombo = true)
    {
        // Run the update inline on the calling thread, unless this test has finished — see _disposed.
        var vm = new DeviceLogsViewModel(new RecordingAppLogger(), null, update =>
        {
            if (!_disposed)
            {
                update();
            }
        });

        CollectRefreshes(vm);
        if (withCombo)
        {
            AttachDeviceCombo(vm);
        }

        return vm;
    }

    /// <summary>
    /// Stands in for <c>ComboBox ItemsSource="{Binding ConnectedDevices}"
    /// SelectedItem="{Binding SelectedDevice}"</c>: when the selected item leaves the items — a
    /// <c>Clear()</c> or the removal of that item — the combo's selection goes to nothing, and the
    /// two-way binding writes <c>null</c> into the view-model. The issue recorded exactly that write
    /// (<c>[null, AAAA0001]</c>) against the real control.
    /// </summary>
    private static void AttachDeviceCombo(DeviceLogsViewModel vm)
    {
        vm.ConnectedDevices.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove
                && vm.SelectedDevice != null
                && !vm.ConnectedDevices.Contains(vm.SelectedDevice))
            {
                vm.SelectedDevice = null!;
            }
        };
    }

    /// <summary>
    /// Records every SD refresh the view-model starts. <c>OnSelectedDeviceChanged</c> starts it and
    /// sets <c>InitialRefreshTask</c> before the property notification is raised, so the handler
    /// sees the task belonging to that selection.
    /// </summary>
    private void CollectRefreshes(DeviceLogsViewModel vm)
    {
        Track(vm.InitialRefreshTask);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceLogsViewModel.SelectedDevice))
            {
                Track(vm.InitialRefreshTask);
            }
        };
    }

    private void Track(Task? refresh)
    {
        if (refresh != null && !_refreshes.Contains(refresh))
        {
            _refreshes.Add(refresh);
        }
    }

    /// <summary>Waits for every SD refresh started so far, so the listing counts are final.</summary>
    private Task Settle() => Task.WhenAll(_refreshes.ToArray());

    private void Register(IStreamingDevice device)
    {
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
    }
    #endregion
}
