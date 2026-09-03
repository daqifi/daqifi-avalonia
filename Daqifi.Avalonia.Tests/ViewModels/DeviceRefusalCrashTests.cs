using Avalonia.Controls;
using Daqifi.Core.Device.SdCard;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Serialises the one test class that stands an app host up.
/// </summary>
/// <remarks>
/// <see cref="DeviceRefusalCrashTests"/> calls <c>App.InitializeMobile()</c> — the only way to give
/// <c>LoggingManager.Instance</c> the <c>IDbContextFactory</c> it resolves off
/// <c>App.ServiceProvider</c>, which the <see cref="DaqifiViewModel.IsLogging"/> setter writes
/// through on its first line. That makes this class a SQLite user, and the suite's other SQLite
/// users call <c>SqliteConnection.ClearAllPools()</c>, which is process-wide and disposes
/// connections other classes are mid-query on. Running this class on its own keeps it out of that
/// race rather than adding to it.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppHostCollection
{
    public const string Name = "App host";
}

/// <summary>
/// Issue #214: a device that refuses a logging command used to end the process.
///
/// <para>Every logging command in this app is issued from a UI property setter —
/// <see cref="DaqifiViewModel.IsLogging"/> and <see cref="DaqifiViewModel.SelectedLoggingMode"/> —
/// and every one of them can throw. Core raises <c>DeviceNotConnectedException</c> the moment a
/// transport is gone, and <c>AbstractStreamingDevice.SwitchMode</c> refuses SD logging on anything
/// that is not USB. A throw out of a property setter reaches
/// <c>Dispatcher.UIThread.UnhandledException</c>, and <c>App.OnDispatcherUnhandledException</c>
/// only logs it — it never sets <c>Handled</c> — so the process dies mid-session and everything not
/// yet written for that session is lost.</para>
///
/// <para>Each test named <c>...DoesNotEndTheApp</c> throws out of the setter on unmodified
/// <c>main</c> and therefore fails there. What the assertions then pin is not merely "it did not
/// throw" but the decision that replaced the throw: the rest of the fleet is still commanded, the
/// user is told which device refused and why, and the session is only unwound when nothing
/// anywhere is recording.</para>
///
/// <para>The devices that REFUSE are real <see cref="DroppableTestDevice"/>s — concrete
/// <c>AbstractStreamingDevice</c>s with no Core device behind them, which is exactly the state a
/// wrapper is left in the moment a link drops — so the exception comes from the production frames
/// the issue names (<c>GetConnectedCoreDevice</c> → <c>StopStreaming</c> → <c>set_IsLogging</c>).
/// Only the devices that are supposed to SUCCEED are stubs, because succeeding is the part a test
/// has to fake.</para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class DeviceRefusalCrashTests : IDisposable
{
    private readonly DaqifiViewModel _viewModel;

    public DeviceRefusalCrashTests()
    {
        // Gives LoggingManager.Instance its context factory, against the throwaway data directory
        // the assembly's module initializer already points DAQIFI_DATA_DIR at. Idempotent.
        Daqifi.Desktop.App.InitializeMobile();

        _viewModel = new DaqifiViewModel(new NullDialogService());
    }

    public void Dispose()
    {
        // LoggingManager is a process-wide singleton, so a test that leaves a session open would
        // hand it to the next one.
        try { LoggingManager.Instance.Active = false; } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    #region Reproduction 1 — the disk-space hard stop

    /// <summary>
    /// The crash the issue leads with. The disk-space monitor hits its 50 MB hard stop,
    /// <c>DiskSpaceMonitorCoordinator.OnCriticalSpaceReached</c> calls the host's
    /// <c>StopLogging()</c>, and that writes <c>IsLogging = false</c> — the setter under test here.
    /// A device whose transport has gone throws <c>DeviceNotConnectedException</c> out of the stop
    /// loop, and on <c>main</c> that ends the process: the user's only warning about the full disk
    /// was a dialog that never got shown.
    /// </summary>
    [Fact]
    public void StoppingLogging_WhenTheDeviceRefuses_DoesNotEndTheApp()
    {
        var ghost = AGhostDevice("Nq1-GHOST");
        // Already streaming, so starting is a no-op for it and the session gets under way exactly
        // as it would have before the link dropped.
        ghost.IsStreaming = true;
        _viewModel.ConnectedDevices.Add(ghost);

        _viewModel.IsLogging = true;
        Assert.True(_viewModel.IsLogging);

        // On main this throw travels out of the setter, into the dispatcher, and out of the process.
        _viewModel.IsLogging = false;

        // The session ended locally even though the device would not confirm it. A device that will
        // not stop is usually one that is already gone; refusing to end the session over it would
        // leave the user with a toggle they cannot turn off.
        Assert.False(_viewModel.IsLogging);

        // And it is not silent — which is the other half of the report.
        Assert.Contains(_viewModel.NotificationList, n => n.Message.Contains("Nq1-GHOST"));
    }

    /// <summary>
    /// The fleet consequence, and the reason a blanket <c>try/catch</c> around the loop would not
    /// have been the fix: the refusing device is FIRST, so on <c>main</c> the healthy device behind
    /// it was never told to stop at all — it kept streaming into a session the app had already
    /// closed.
    /// </summary>
    [Fact]
    public void StoppingLogging_WhenOneDeviceRefuses_StillStopsTheRestOfTheFleet()
    {
        var ghost = AGhostDevice("Nq1-GHOST");
        ghost.IsStreaming = true;
        var healthy = new RecordingStreamingDevice("Nq1-OK");

        _viewModel.ConnectedDevices.Add(ghost);
        _viewModel.ConnectedDevices.Add(healthy);

        _viewModel.IsLogging = true;
        _viewModel.IsLogging = false;

        Assert.Contains("StopStreaming", healthy.Commands);
        Assert.False(_viewModel.IsLogging);
    }

    #endregion

    #region Reproduction 1, the start half — a mixed fleet where one device is unreachable

    /// <summary>
    /// The same fan-out, one branch up. Starting a session with an unreachable device in the fleet
    /// threw out of the setter before any healthy device had been asked to stream.
    /// <para>
    /// The decision the assertions pin: the session survives. The devices that did start are
    /// recording, and taking the run away from them because a sibling declined would lose data the
    /// user asked for — so logging stays ON and the refusal is reported instead.
    /// </para>
    /// </summary>
    [Fact]
    public void StartingLogging_WhenOneDeviceRefuses_KeepsTheSessionForTheRest()
    {
        var ghost = AGhostDevice("Nq1-GHOST");
        var healthy = new RecordingStreamingDevice("Nq1-OK");

        _viewModel.ConnectedDevices.Add(ghost);
        _viewModel.ConnectedDevices.Add(healthy);

        _viewModel.IsLogging = true;

        Assert.True(_viewModel.IsLogging);
        Assert.Contains("InitializeStreaming", healthy.Commands);

        var notification = Assert.Single(_viewModel.NotificationList);
        Assert.Contains("Nq1-GHOST", notification.Message);
        // Named the device AND said why, rather than "an error occurred".
        Assert.Contains("not connected", notification.Message, StringComparison.OrdinalIgnoreCase);
        // ...and did not claim the healthy device was left out of the run.
        Assert.DoesNotContain("Nq1-OK", notification.Message);
    }

    /// <summary>
    /// The other side of that decision. When EVERY device refuses, nothing anywhere is recording,
    /// so leaving the toggle ON would put the app back in the state the issue's third reproduction
    /// describes: the header reads LOGGING ON over a plot that says no channels are streaming. The
    /// session is unwound the same way a failed session row unwinds it (#183).
    /// </summary>
    [Fact]
    public void StartingLogging_WhenEveryDeviceRefuses_PutsTheToggleBackAndSaysSo()
    {
        _viewModel.ConnectedDevices.Add(AGhostDevice("Nq1-GHOST"));

        _viewModel.IsLogging = true;

        Assert.False(_viewModel.IsLogging);
        Assert.False(LoggingManager.Instance.Active);
        Assert.Contains(_viewModel.NotificationList, n => n.Message.Contains("Nq1-GHOST"));
    }

    /// <summary>
    /// The distinction the above depends on: an EMPTY fleet is not a fleet that refused. Arming the
    /// toggle before connecting anything is ordinary use, and it must not be mistaken for the
    /// nothing-is-recording case and snapped back off.
    /// </summary>
    [Fact]
    public void StartingLogging_WithNoDeviceConnected_LeavesTheToggleOn()
    {
        _viewModel.IsLogging = true;

        Assert.True(_viewModel.IsLogging);
        Assert.Empty(_viewModel.NotificationList);
    }

    /// <summary>
    /// The fifth failure the issue records, and the one that wears a message about nothing: both
    /// loops walked the live <c>ConnectedDevices</c> collection, and a device command that surfaces
    /// as a lost connection tears its own device out of that collection through
    /// <c>ConnectionManager.Disconnect</c> — ending the enumeration with
    /// <c>InvalidOperationException: Collection was modified</c>, which is the same process death.
    /// </summary>
    [Fact]
    public void StoppingLogging_WhenADeviceDisconnectsItselfMidCommand_DoesNotEndTheApp()
    {
        var selfRemoving = new RecordingStreamingDevice("Nq1-DROPS");
        var behindIt = new RecordingStreamingDevice("Nq1-OK");

        _viewModel.ConnectedDevices.Add(selfRemoving);
        _viewModel.ConnectedDevices.Add(behindIt);

        _viewModel.IsLogging = true;

        // What a lost connection really does, at the moment the command is issued.
        selfRemoving.OnCommand = _ => _viewModel.ConnectedDevices.Remove(selfRemoving);

        _viewModel.IsLogging = false;

        // The device behind the one that vanished was still reached.
        Assert.Contains("StopStreaming", behindIt.Commands);
        Assert.False(_viewModel.IsLogging);
    }

    #endregion

    #region Reproduction 2 — the mode switch with a mixed USB/WiFi fleet

    /// <summary>
    /// The second crash, and the one a user reaches by clicking a button that is enabled. The
    /// LOG TO DEVICE radio is enabled from the SELECTED device's <c>IsUsb</c>, but the setter
    /// applies the mode to EVERY connected device — and <c>AbstractStreamingDevice.SwitchMode</c>
    /// refuses SD logging on a WiFi one. The setter rolled the fleet back and then rethrew, out of
    /// the command behind the radio button and into the dispatcher. The rollback had already put
    /// every device back by that point, so the throw's only remaining effect was to close the app.
    /// </summary>
    [Fact]
    public void SwitchingToLogToDevice_WithAWifiDeviceInTheFleet_DoesNotEndTheApp()
    {
        var usb = new RecordingStreamingDevice("Nq1-USB", ConnectionType.Usb);
        var wifi = AGhostDevice("Nq1-WIFI", ConnectionType.Wifi);

        // USB first, so the fleet is genuinely half-moved when the refusal lands and the rollback
        // has real work to do.
        _viewModel.ConnectedDevices.Add(usb);
        _viewModel.ConnectedDevices.Add(wifi);

        _viewModel.SelectedLoggingMode = "Log to Device";

        // Every device is back where it started...
        Assert.Equal(DeviceMode.StreamToApp, usb.Mode);
        Assert.Equal(DeviceMode.StreamToApp, wifi.Mode);
        Assert.Equal(["SwitchMode(LogToDevice)", "SwitchMode(StreamToApp)"], usb.Commands);

        // ...and so is the app: a mode switch is all-or-nothing, because SelectedLoggingMode is a
        // single value and the UI cannot show half a fleet in each mode.
        Assert.Equal("Stream to App", _viewModel.SelectedLoggingMode);
        Assert.False(_viewModel.IsLogToDeviceMode);

        var notification = Assert.Single(_viewModel.NotificationList);
        Assert.Contains("Nq1-WIFI", notification.Message);
        Assert.Contains("USB", notification.Message);
    }

    /// <summary>
    /// The guard on the above: the ordinary all-USB switch must still work, or the fix would have
    /// bought crash-freedom by making the feature unreachable.
    /// </summary>
    [Fact]
    public void SwitchingToLogToDevice_WithAnAllUsbFleet_MovesTheWholeFleet()
    {
        var first = new RecordingStreamingDevice("Nq1-A", ConnectionType.Usb);
        var second = new RecordingStreamingDevice("Nq1-B", ConnectionType.Usb);

        _viewModel.ConnectedDevices.Add(first);
        _viewModel.ConnectedDevices.Add(second);

        _viewModel.SelectedLoggingMode = "Log to Device";

        Assert.Equal(DeviceMode.LogToDevice, first.Mode);
        Assert.Equal(DeviceMode.LogToDevice, second.Mode);
        Assert.Equal("Log to Device", _viewModel.SelectedLoggingMode);
        Assert.True(_viewModel.IsLogToDeviceMode);
        Assert.Empty(_viewModel.NotificationList);
    }

    /// <summary>
    /// The refusal is not restricted to the transport check. A device that accepts the mode but
    /// cannot start SD logging — no card in the slot, the commonest of these by far — must be the
    /// same survivable event, reported with the card's own reason rather than a generic one.
    /// </summary>
    [Fact]
    public void StartingSdCardLogging_WhenTheCardIsMissing_KeepsTheAppAndNamesTheReason()
    {
        var noCard = new RecordingStreamingDevice("Nq1-NOCARD", ConnectionType.Usb);

        // Core's own typed exception, constructed the way Core constructs it (from the raw device
        // response). Its wording belongs to the pinned package, so the assertion below reads the
        // message off the exception rather than restating a string this repo does not own.
        var missingCard = new SdCardNotPresentException(rawDeviceResponse: []);
        noCard.Refusals["StartSdCardLogging"] = missingCard;

        _viewModel.ConnectedDevices.Add(noCard);
        _viewModel.SelectedLoggingMode = "Log to Device";

        _viewModel.IsLogging = true;

        Assert.False(_viewModel.IsLogging);
        var notification = Assert.Single(_viewModel.NotificationList);
        Assert.Contains("Nq1-NOCARD", notification.Message);
        Assert.Contains(missingCard.Message, notification.Message);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A device whose transport has gone: a real <c>AbstractStreamingDevice</c> with no Core device
    /// behind it, which is the state the wrapper is left in the moment a link drops. Every logging
    /// command on it throws <c>DeviceNotConnectedException</c> from
    /// <c>AbstractStreamingDevice.GetConnectedCoreDevice</c>.
    /// </summary>
    private static DroppableTestDevice AGhostDevice(
        string serialNumber,
        ConnectionType connectionType = ConnectionType.Usb)
        => new(serialNumber, connectionType);

    private sealed class NullDialogService : IDialogService
    {
        public void Register(Control view) { }

        public void Unregister(Control view) { }

        public Task<bool?> ShowDialogAsync<T>(object ownerViewModel, object viewModel) where T : Window
            => Task.FromResult<bool?>(true);
    }

    #endregion
}
