using Avalonia.Controls;
using Daqifi.Core.Device;
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
/// through on its first line.
///
/// <para><c>App.ServiceProvider</c> is STATIC, and that is the whole reason this class runs alone:
/// two classes standing an app host up concurrently would overwrite each other's container. An app
/// host cannot be made per-test, so serialising the one class that needs one is the narrowest
/// available fix — and it is scoped to exactly that class, not to the suite.</para>
///
/// <para>It used to say something else. Before issue #210 this remark blamed the suite's other
/// SQLite users, which each called the process-global <c>SqliteConnection.ClearAllPools()</c> and
/// could dispose a connection this class was mid-query on. None of them call it any more — they
/// go through <see cref="TestDatabase"/>, whose connections are unpooled and therefore invisible
/// to a pool clear. This class is the one that stays pooled, because the connections it uses are
/// production's own (<c>App.cs</c>), so keeping it off the parallel schedule still helps; it is no
/// longer what justifies the collection.</para>
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

    #region What a refusal must not leave behind (Qodo round 1)

    /// <summary>
    /// The trap this change exists to avoid, reached by the fix itself. When
    /// <c>StopSdCardLogging</c> is refused, <c>AbstractStreamingDevice</c> leaves
    /// <c>IsLoggingToSdCard</c> reading <c>true</c> — it clears the flag only after the device
    /// answers — and the <see cref="DaqifiViewModel.IsLogging"/> getter ORs <c>_isLogging</c> with
    /// exactly that flag. So the stop reported success, the getter said <c>true</c>, and the toggle
    /// sprang straight back ON: the user turns logging off, is told a device did not confirm, and
    /// watches the switch refuse to move.
    /// <para>
    /// The command never got through, so the flag is stale rather than evidence, and the app stops
    /// asserting it until it learns something new about that device.
    /// </para>
    /// </summary>
    [Fact]
    public void StoppingLogging_WhenAnSdCardStopIsRefused_StillLeavesTheToggleOff()
    {
        var sdDevice = new RecordingStreamingDevice("Nq1-SD", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(sdDevice);
        _viewModel.SelectedLoggingMode = "Log to Device";

        _viewModel.IsLogging = true;
        Assert.True(sdDevice.IsLoggingToSdCard);
        Assert.True(_viewModel.IsLogging);

        // The link goes while the card is logging: the stop cannot be delivered, so the device's
        // flag is never cleared.
        sdDevice.Refusals["StopSdCardLogging"] = new DeviceNotConnectedException();

        _viewModel.IsLogging = false;

        Assert.True(sdDevice.IsLoggingToSdCard);   // the device object still says so...
        Assert.False(_viewModel.IsLogging);        // ...and the toggle is off anyway.
        Assert.False(_viewModel.IsSdCardLoggingActive);
        Assert.Contains(_viewModel.NotificationList, n => n.Message.Contains("Nq1-SD"));
    }

    /// <summary>
    /// The guard on the above: the device-state fallback is the whole reason the getter reads
    /// device flags at all — it is what makes the toggle tell the truth after reconnecting to a
    /// device that kept SD-logging across a desktop restart. Dropping a stale flag after a REFUSED
    /// stop must not turn into ignoring device state generally.
    /// </summary>
    [Fact]
    public void ADeviceThatIsSdLoggingOnItsOwn_StillReportsTheSessionAsActive()
    {
        var stillLogging = new RecordingStreamingDevice("Nq1-SD", ConnectionType.Usb);
        stillLogging.SwitchMode(DeviceMode.LogToDevice);
        stillLogging.StartSdCardLogging();

        _viewModel.ConnectedDevices.Add(stillLogging);

        // Nothing was ever toggled in this app; the device is simply already logging.
        Assert.True(_viewModel.IsLogging);
        Assert.True(_viewModel.IsSdCardLoggingActive);
    }

    /// <summary>
    /// The rollback's own failure. A device that accepted the new mode and then refuses to go back
    /// is left in the mode the app is about to stop claiming it is in — and
    /// <c>LoggingFleet.Start</c> picks each device's command from its ACTUAL <c>Mode</c>, so on the
    /// next start it would log to its SD card while the app showed "Stream to App", producing a run
    /// that never reaches the desktop and that the user has no reason to look for on the card.
    /// Saying "the mode was left as X" over that is false, so the stranded device is named too.
    /// </summary>
    [Fact]
    public void SwitchingMode_WhenTheRollbackAlsoFails_NamesTheDeviceItCouldNotPutBack()
    {
        var stubborn = new RecordingStreamingDevice("Nq1-STUCK", ConnectionType.Usb);
        var wifi = AGhostDevice("Nq1-WIFI", ConnectionType.Wifi);

        _viewModel.ConnectedDevices.Add(stubborn);
        _viewModel.ConnectedDevices.Add(wifi);

        // It takes the new mode, then the WiFi device refuses, and it will not come back.
        stubborn.Refusals["SwitchMode(StreamToApp)"] = new DeviceNotConnectedException();

        _viewModel.SelectedLoggingMode = "Log to Device";

        // Really stranded, not merely reported as such.
        Assert.Equal(DeviceMode.LogToDevice, stubborn.Mode);
        Assert.Equal("Stream to App", _viewModel.SelectedLoggingMode);

        var notification = Assert.Single(_viewModel.NotificationList);
        Assert.Contains("Nq1-WIFI", notification.Message);    // what refused
        Assert.Contains("Nq1-STUCK", notification.Message);   // what could not be put back
        Assert.Contains("could NOT be put back", notification.Message);
    }

    /// <summary>
    /// And the guard on that one: an ordinary refusal whose rollback works must NOT tell the user
    /// a device is stranded, or the warning stops meaning anything.
    /// </summary>
    [Fact]
    public void SwitchingMode_WhenTheRollbackSucceeds_SaysNothingAboutStrandedDevices()
    {
        _viewModel.ConnectedDevices.Add(new RecordingStreamingDevice("Nq1-USB", ConnectionType.Usb));
        _viewModel.ConnectedDevices.Add(AGhostDevice("Nq1-WIFI", ConnectionType.Wifi));

        _viewModel.SelectedLoggingMode = "Log to Device";

        var notification = Assert.Single(_viewModel.NotificationList);
        Assert.DoesNotContain("could NOT be put back", notification.Message);
    }

    #endregion

    #region What the suppression itself must not do (Qodo round 2)

    /// <summary>
    /// The retry, which is the obvious next thing a user does. After a refused SD stop leaves the
    /// toggle OFF, they click it ON again — and if the device refuses that too, nothing is
    /// recording and the toggle must stay OFF.
    /// <para>
    /// It did not. The doubt from the failed stop was cleared at the TOP of the start, before
    /// anyone had asked the device anything, so the moment the retry was refused the stale
    /// <c>IsLoggingToSdCard</c> was visible again and the getter reported logging active — even as
    /// the all-refused unwind set <c>_isLogging</c> false. The bookkeeping now happens after the
    /// fan-out, driven by what each device actually answered.
    /// </para>
    /// </summary>
    [Fact]
    public void RetryingAfterARefusedSdStop_WhenTheRetryIsRefusedToo_LeavesTheToggleOff()
    {
        var sdDevice = new RecordingStreamingDevice("Nq1-SD", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(sdDevice);
        _viewModel.SelectedLoggingMode = "Log to Device";

        _viewModel.IsLogging = true;
        Assert.True(sdDevice.IsLoggingToSdCard);

        // The link goes: neither the stop nor anything after it can be delivered.
        sdDevice.Refusals["StopSdCardLogging"] = new DeviceNotConnectedException();
        sdDevice.Refusals["StartSdCardLogging"] = new DeviceNotConnectedException();

        _viewModel.IsLogging = false;
        Assert.False(_viewModel.IsLogging);

        // The retry.
        _viewModel.IsLogging = true;

        Assert.True(sdDevice.IsLoggingToSdCard);   // still stale...
        Assert.False(_viewModel.IsLogging);        // ...and the toggle still does not lie.
    }

    /// <summary>
    /// The retry that WORKS must clear the doubt, or a device would stay invisible for the rest of
    /// the session after one bad stop.
    /// </summary>
    [Fact]
    public void RetryingAfterARefusedSdStop_WhenTheRetrySucceeds_ReportsTheSessionActiveAgain()
    {
        var sdDevice = new RecordingStreamingDevice("Nq1-SD", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(sdDevice);
        _viewModel.SelectedLoggingMode = "Log to Device";

        _viewModel.IsLogging = true;
        sdDevice.Refusals["StopSdCardLogging"] = new DeviceNotConnectedException();
        _viewModel.IsLogging = false;
        Assert.False(_viewModel.IsLogging);

        // The link comes back, and this time the device answers.
        sdDevice.Refusals.Remove("StopSdCardLogging");
        _viewModel.IsLogging = true;

        Assert.True(_viewModel.IsLogging);
        Assert.True(_viewModel.IsSdCardLoggingActive);
    }

    /// <summary>
    /// The stop that fails BY disconnecting — the ordering trap. <c>ConnectionManager.Disconnect</c>
    /// tears the wrapper out of <c>ConnectedDevices</c> while the command is in flight, so the
    /// collection-changed cleanup runs BEFORE the refusal is processed. An entry recorded after
    /// that point would never be cleaned up by anything: it does nothing while the device is gone,
    /// and it would suppress the device's freshly reported state if that same wrapper reconnected.
    /// </summary>
    [Fact]
    public void AStopThatFailsByDisconnecting_DoesNotStaySuppressedIfTheDeviceComesBack()
    {
        var dropper = new RecordingStreamingDevice("Nq1-DROPS", ConnectionType.Usb);
        _viewModel.ConnectedDevices.Add(dropper);
        _viewModel.SelectedLoggingMode = "Log to Device";

        _viewModel.IsLogging = true;
        Assert.True(dropper.IsLoggingToSdCard);

        // The command's own failure is the disconnect: removed from the fleet, then it throws.
        dropper.OnCommand = command =>
        {
            if (command == "StopSdCardLogging")
            {
                _viewModel.ConnectedDevices.Remove(dropper);
            }
        };
        dropper.Refusals["StopSdCardLogging"] = new DeviceNotConnectedException();

        _viewModel.IsLogging = false;
        Assert.False(_viewModel.IsLogging);

        // The same wrapper reconnects, still reporting that it is logging to its card. That is a
        // fresh report, and it must be believed.
        dropper.OnCommand = null;
        _viewModel.ConnectedDevices.Add(dropper);

        Assert.True(_viewModel.IsSdCardLoggingActive);
        Assert.True(_viewModel.IsLogging);
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

    // NullDialogService moved to its own file when the notifications suite (#250) needed the same
    // stub; it is in this namespace, so the use above is unchanged.

    #endregion
}
