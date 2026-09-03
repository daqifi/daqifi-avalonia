using System.ComponentModel;
using System.Reflection;
using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the USB tab's failed-connect feedback.
///
/// <para>
/// Picking a discovered device out of the USB list and pressing Connect can fail for reasons the app
/// cannot pre-empt — the port is held by another application, or the device was left streaming over
/// WiFi and answers the switch-to-USB with a SCPI error. The dialog used to call
/// <c>ConnectionManager.Connect</c> and then close unconditionally, so every one of those failures
/// looked identical to success: the dialog dismissed itself and the device simply never appeared in
/// the device list, with nothing on screen saying why.
/// </para>
///
/// <para>
/// The status has to be read after <em>each</em> device, not once after the loop.
/// <c>ConnectionStatus</c> is a single shared field on the <c>ConnectionManager</c> singleton and the
/// list is multi-select, so a later device's success would otherwise overwrite an earlier device's
/// failure and the dialog would close reporting nothing.
/// </para>
///
/// <para>
/// These tests never open a COM port. They set <c>DeviceBeingUpdated</c>, which makes
/// <c>ConnectionManager.Connect</c> refuse USB connects outright and set <c>Error</c> before doing any
/// I/O — the same gate also holds the dialog's discovery restart down, so the failure path's
/// <c>StartSerialDiscovery</c> cannot spin up a real finder either. That is what makes a connect
/// failure reproducible here at all; nothing else about a firmware update is under test.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class ConnectionDialogSerialConnectErrorTests : IDisposable
{
    /// <summary>
    /// <c>ConnectionManager</c> is a process-wide singleton, so both the firmware gate and the
    /// connection status are shared between tests. Reset here rather than in each body so a failed
    /// assertion cannot strand the app-global "a firmware update is running" gate on.
    /// </summary>
    public void Dispose()
    {
        ConnectionManager.Instance.DeviceBeingUpdated = null;
        ConnectionManager.Instance.ConnectionStatus = DAQiFiConnectionStatus.Disconnected;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_failed_connect_reports_the_device_that_failed_instead_of_closing_the_dialog()
    {
        using var viewModel = CreateViewModel();
        var closeRequested = false;
        viewModel.Value.CloseRequested += (_, _) => closeRequested = true;

        BlockUsbConnects();

        await ConnectSerialAsync(viewModel.Value, new SerialStreamingDevice("COM-TEST-A"));

        Assert.False(
            closeRequested,
            "A failed connect must leave the dialog open — closing it is what made the failure invisible.");
        Assert.NotNull(viewModel.Value.SerialConnectError);
        Assert.Contains("COM-TEST-A", viewModel.Value.SerialConnectError);
    }

    /// <summary>
    /// The multi-select case the per-device check exists for: the first device fails, so the second
    /// must never be attempted and its result must not be able to mask the first one's failure.
    /// </summary>
    [Fact]
    public async Task A_failure_on_the_first_of_several_devices_stops_the_run_and_is_the_error_reported()
    {
        using var viewModel = CreateViewModel();
        var closeRequested = false;
        viewModel.Value.CloseRequested += (_, _) => closeRequested = true;

        BlockUsbConnects();

        await ConnectSerialAsync(
            viewModel.Value,
            new SerialStreamingDevice("COM-TEST-FIRST"),
            new SerialStreamingDevice("COM-TEST-SECOND"));

        Assert.False(closeRequested);
        Assert.NotNull(viewModel.Value.SerialConnectError);
        Assert.Contains("COM-TEST-FIRST", viewModel.Value.SerialConnectError);
        Assert.DoesNotContain("COM-TEST-SECOND", viewModel.Value.SerialConnectError);
    }

    /// <summary>
    /// The message is re-derived per attempt, so a stale failure from a previous press cannot sit under
    /// a later one and misattribute the blame to a device the user is no longer connecting to.
    /// </summary>
    [Fact]
    public async Task A_later_attempt_replaces_the_previous_attempts_message()
    {
        using var viewModel = CreateViewModel();
        BlockUsbConnects();

        await ConnectSerialAsync(viewModel.Value, new SerialStreamingDevice("COM-TEST-OLD"));
        Assert.Contains("COM-TEST-OLD", viewModel.Value.SerialConnectError);

        await ConnectSerialAsync(viewModel.Value, new SerialStreamingDevice("COM-TEST-NEW"));

        Assert.Contains("COM-TEST-NEW", viewModel.Value.SerialConnectError);
        Assert.DoesNotContain("COM-TEST-OLD", viewModel.Value.SerialConnectError);
    }

    /// <summary>
    /// Pressing Connect with nothing selected is not a failure and must not paint the pane red.
    /// </summary>
    [Fact]
    public async Task An_empty_selection_reports_nothing()
    {
        using var viewModel = CreateViewModel();
        BlockUsbConnects();

        await ConnectSerialAsync(viewModel.Value);

        Assert.Null(viewModel.Value.SerialConnectError);
    }

    /// <summary>
    /// Issue #212, at the surface the user sees it.
    ///
    /// <para>
    /// The dialog leaves every tab's Connect button live while an attempt is in flight, so a second
    /// connect can finish between this one failing and this one being reported. When the dialog read
    /// the shared <c>ConnectionStatus</c> field to find out what happened, the value waiting for it
    /// was the other connect's: it closed the dialog on a device that had failed, showing nothing,
    /// and the device simply never appeared in the device list.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_connect_finishing_elsewhere_first_is_not_credited_to_this_device()
    {
        using var viewModel = CreateViewModel();
        var closeRequested = false;
        viewModel.Value.CloseRequested += (_, _) => closeRequested = true;

        BlockUsbConnects();
        using var elsewhere = new ConnectFinishingSuccessfullyElsewhere();

        await ConnectSerialAsync(viewModel.Value, new SerialStreamingDevice("COM-TEST-A"));

        Assert.False(
            closeRequested,
            "COM-TEST-A did not connect, so the dialog must stay open — another device's success is " +
            "not this device's.");
        Assert.NotNull(viewModel.Value.SerialConnectError);
        Assert.Contains("COM-TEST-A", viewModel.Value.SerialConnectError);
    }

    /// <summary>
    /// The failure path restarts discovery so the list keeps refreshing after a failed attempt. It must
    /// still honour the firmware pause guard while doing so — otherwise recovering from a failed
    /// connect would sweep every COM port in the middle of a flash.
    /// </summary>
    [Fact]
    public async Task Recovering_from_a_failed_connect_does_not_probe_the_bus_while_a_flash_is_running()
    {
        using var viewModel = CreateViewModel();
        BlockUsbConnects();

        await ConnectSerialAsync(viewModel.Value, new SerialStreamingDevice("COM-TEST-A"));

        Assert.Null(GetPrivateField(viewModel.Value, "_serialFinder"));
    }

    #region Harness
    /// <summary>
    /// Makes <c>ConnectionManager.Connect</c> refuse USB connects and report <c>Error</c> without
    /// opening anything, by claiming a firmware update is in flight.
    /// </summary>
    private static void BlockUsbConnects()
    {
        ConnectionManager.Instance.ConnectionStatus = DAQiFiConnectionStatus.Disconnected;
        ConnectionManager.Instance.DeviceBeingUpdated = new TestDevice();
    }

    /// <summary>
    /// A second connect that succeeds while the one under test is in flight — the WiFi tab's
    /// Connect button, or a connect started from outside the dialog. Both are reachable: the dialog
    /// disables nothing while an attempt runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is represented by the one effect it has on the code under test — a write of
    /// <c>Connected</c> into the shared <c>ConnectionStatus</c> field, landing after this attempt's
    /// own terminal write and before the dialog reads it. That ordering is what the defect turns
    /// on; forcing the real thread interleaving that produces it would need a controlled scheduler
    /// and would buy no extra coverage of the dialog. Subscribing to the manager's own
    /// <c>PropertyChanged</c> is what puts the write in exactly that gap, because the field's setter
    /// raises it synchronously.
    /// </para>
    /// <para>
    /// Genuinely concurrent attempts are covered a level down, in
    /// <c>ConnectionManagerConnectResultTests</c>, which runs two overlapping connects for real.
    /// </para>
    /// </remarks>
    private sealed class ConnectFinishingSuccessfullyElsewhere : IDisposable
    {
        private bool _written;

        public ConnectFinishingSuccessfullyElsewhere() =>
            ConnectionManager.Instance.PropertyChanged += OnManagerPropertyChanged;

        private void OnManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_written || e.PropertyName != nameof(ConnectionManager.ConnectionStatus)) { return; }
            if (ConnectionManager.Instance.ConnectionStatus != DAQiFiConnectionStatus.Error) { return; }

            _written = true;
            ConnectionManager.Instance.ConnectionStatus = DAQiFiConnectionStatus.Connected;
        }

        public void Dispose() =>
            ConnectionManager.Instance.PropertyChanged -= OnManagerPropertyChanged;
    }

    /// <summary>
    /// Invokes the dialog's private serial-connect handler the way the Connect button's command does,
    /// passing the selected items as the list box would.
    /// </summary>
    private static async Task ConnectSerialAsync(
        ConnectionDialogViewModel viewModel,
        params IStreamingDevice[] selectedDevices)
    {
        var method = typeof(ConnectionDialogViewModel).GetMethod(
            "ConnectSerialAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task?)method.Invoke(viewModel, [selectedDevices]);
        Assert.NotNull(task);
        await task;
    }

    /// <summary>
    /// Builds a view model with no bootloader watcher and its UI marshal replaced by a direct call —
    /// outside a running Avalonia app <c>Dispatcher.UIThread</c> is never pumped, so the production
    /// marshal's blocking <c>Invoke</c> would never return.
    /// </summary>
    private static ClosingViewModel CreateViewModel()
    {
        var viewModel = new ConnectionDialogViewModel(null!, null);
        SetPrivateField(viewModel, "_marshalToUiThread", (Action<Action>)(action => action()));
        return new ClosingViewModel(viewModel);
    }

    /// <summary>
    /// Closes the view model at the end of a test, so it stops reacting to the shared
    /// <c>ConnectionManager</c> singleton once the test is over.
    /// </summary>
    private sealed class ClosingViewModel(ConnectionDialogViewModel value) : IDisposable
    {
        public ConnectionDialogViewModel Value { get; } = value;

        public void Dispose() => Value.Close();
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

    /// <summary>
    /// The cheapest real <see cref="IStreamingDevice"/>: <c>DeviceBeingUpdated</c> only needs an
    /// instance, and nothing here connects or sends.
    /// </summary>
    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }
}
