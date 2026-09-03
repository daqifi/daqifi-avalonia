using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Helpers;

namespace Daqifi.Desktop.Device;

/// <summary>
/// One device's refusal of a logging command, in the terms the UI reports it.
/// </summary>
/// <param name="DeviceName">
/// The device the command was issued against, named the way the rest of the UI names it.
/// </param>
/// <param name="Exception">The failure the device (or the wrapper) raised.</param>
// Downstream-only: no upstream counterpart. Upstream WPF issues these commands from the property
// setters directly and has the same crash.
public sealed record DeviceCommandRefusal(string DeviceName, Exception Exception)
{
    /// <summary>
    /// The one-line sentence shown to the user. Core's own exception messages are already written
    /// for a person ("Device is not connected.", "No SD card is installed in the device.",
    /// "SD Card logging is only available when connected via USB"), so this names the device and
    /// hands the message through rather than maintaining a second copy of Core's taxonomy.
    /// </summary>
    public string Description => $"{DeviceName}: {Exception.Message}";
}

/// <summary>
/// What happened when one command was fanned out over the connected fleet.
/// </summary>
/// <param name="Succeeded">The devices that accepted the command.</param>
/// <param name="Refusals">The devices that declined it, and why.</param>
public sealed record FleetCommandResult(
    IReadOnlyList<IStreamingDevice> Succeeded,
    IReadOnlyList<DeviceCommandRefusal> Refusals)
{
    /// <summary>True when at least one device declined the command.</summary>
    public bool AnyRefused => Refusals.Count > 0;

    /// <summary>
    /// True when the command was issued to at least one device and every one of them declined —
    /// the case where carrying on would leave the app claiming to do something that is not
    /// happening anywhere. An empty fleet is NOT this: nothing refused, there was simply nobody
    /// to ask.
    /// </summary>
    public bool EveryDeviceRefused => Refusals.Count > 0 && Succeeded.Count == 0;

    /// <summary>The refusal sentences, newline-joined, for a notification or dialog body.</summary>
    public string RefusalSummary => string.Join(Environment.NewLine, Refusals.Select(r => r.Description));
}

/// <summary>
/// Issues a logging command to every connected device and reports what each of them did, instead
/// of letting the first refusal decide the outcome for the whole fleet.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of issue #214. <c>DaqifiViewModel.IsLogging</c> and
/// <c>DaqifiViewModel.SelectedLoggingMode</c> drove the fleet from bare loops inside UI property
/// setters, and every device call in those loops can throw: Core raises
/// <see cref="Daqifi.Core.Device.DeviceNotConnectedException"/> the moment a transport is gone
/// (daqifi-core#395), <see cref="Daqifi.Core.Device.SdCard.SdCardNotPresentException"/> and
/// <see cref="Daqifi.Core.Device.SdCard.SdCardBusyException"/> for a missing or wedged card, and
/// the app's own <see cref="AbstractStreamingDevice.SwitchMode"/> refuses SD logging on anything
/// that is not USB. A throw out of a property setter reaches
/// <c>Dispatcher.UIThread.UnhandledException</c>, which <c>App.OnDispatcherUnhandledException</c>
/// only logs — it never sets <c>Handled</c> — so the process ends mid-session. Same ground as
/// #183, one loop further down; <c>LoggingManager</c> carries the note.
/// </para>
/// <para>
/// The decision this class encodes is that <b>a fleet is not an all-or-nothing unit for
/// start/stop</b>. One device refusing to start or stop says nothing about the others, so the
/// others are commanded anyway and the refusals are collected and reported together. The caller
/// then decides what the result means for the session — see
/// <see cref="FleetCommandResult.EveryDeviceRefused"/>, which is the one outcome that has to
/// unwind the session, because nothing is recording.
/// </para>
/// <para>
/// A mode switch is different and stays all-or-nothing: <c>SelectedLoggingMode</c> is a single
/// value for the whole app and the UI has no way to show half a fleet in each mode, so a fleet
/// that cannot all move is put back where it was. That rollback already existed; what did not was
/// surviving it.
/// </para>
/// <para>
/// Every method takes a snapshot of the device collection before iterating. The live collection is
/// <c>DaqifiViewModel.ConnectedDevices</c>, and a command that surfaces as a lost connection tears
/// its own device out of it through <c>ConnectionManager.Disconnect</c> — which ended the old
/// loops with <c>InvalidOperationException: Collection was modified</c>, the same process death
/// wearing a message about nothing.
/// </para>
/// </remarks>
// Downstream-only: no upstream counterpart.
public static class LoggingFleet
{
    /// <summary>
    /// Starts logging on every device, each in whichever mode it is currently in.
    /// </summary>
    /// <param name="devices">The connected fleet. Enumerated once, up front.</param>
    /// <param name="appLogger">Logger for the per-device diagnostics.</param>
    public static FleetCommandResult Start(IEnumerable<IStreamingDevice> devices, IAppLogger appLogger)
        => Issue(devices, appLogger, "start logging on", device =>
        {
            if (device.Mode == DeviceMode.StreamToApp)
            {
                device.InitializeStreaming();
            }
            else if (device.Mode == DeviceMode.LogToDevice)
            {
                device.StartSdCardLogging();
            }
        });

    /// <summary>
    /// Stops logging on every device, each in whichever mode it is currently in.
    /// </summary>
    /// <remarks>
    /// A refusal here is reported but changes nothing: the app's own session ends either way. A
    /// device that will not stop is usually one that is already gone, and refusing to end the
    /// local session over it would strand the user with a toggle they cannot turn off.
    /// </remarks>
    /// <param name="devices">The connected fleet. Enumerated once, up front.</param>
    /// <param name="appLogger">Logger for the per-device diagnostics.</param>
    public static FleetCommandResult Stop(IEnumerable<IStreamingDevice> devices, IAppLogger appLogger)
        => Issue(devices, appLogger, "stop logging on", device =>
        {
            if (device.Mode == DeviceMode.StreamToApp)
            {
                device.StopStreaming();
            }
            else if (device.Mode == DeviceMode.LogToDevice)
            {
                device.StopSdCardLogging();
            }
        });

    /// <summary>
    /// Moves the whole fleet to <paramref name="mode"/>, or puts it back where it was.
    /// </summary>
    /// <remarks>
    /// All-or-nothing, for the reason given on the class: there is one <c>SelectedLoggingMode</c>
    /// for the app. On any refusal every device that did move is moved back, and a rollback that
    /// itself fails is logged and does not stop the rest of the rollback — the alternative is
    /// abandoning the remaining devices in a mode the UI is about to stop claiming they are in.
    /// </remarks>
    /// <param name="devices">The connected fleet. Enumerated once, up front.</param>
    /// <param name="mode">The mode to move every device to.</param>
    /// <param name="appLogger">Logger for the per-device diagnostics.</param>
    /// <returns>
    /// A result whose <see cref="FleetCommandResult.Succeeded"/> is empty when the switch was
    /// rolled back, so a caller can treat "no refusals" as "the fleet moved".
    /// </returns>
    public static FleetCommandResult SwitchMode(
        IEnumerable<IStreamingDevice> devices,
        DeviceMode mode,
        IAppLogger appLogger)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(appLogger);

        var fleet = devices.ToList();
        var originalModes = fleet.ToDictionary(
            device => device,
            device => device.Mode,
            ReferenceComparer<IStreamingDevice>.Instance);

        var moved = new List<IStreamingDevice>();
        var refusals = new List<DeviceCommandRefusal>();

        foreach (var device in fleet)
        {
            try
            {
                device.SwitchMode(mode);
                moved.Add(device);
            }
            catch (Exception ex)
            {
                appLogger.Warning(ex, $"{Describe(device)} refused the switch to {mode} logging mode.");
                refusals.Add(new DeviceCommandRefusal(Describe(device), ex));

                // One refusal settles it for everyone, so there is nothing to gain by asking the
                // rest and a partially-moved fleet to unwind if we did.
                break;
            }
        }

        if (refusals.Count == 0)
        {
            return new FleetCommandResult(moved, refusals);
        }

        foreach (var (device, originalMode) in originalModes)
        {
            if (device.Mode == originalMode)
            {
                continue;
            }

            try
            {
                device.SwitchMode(originalMode);
            }
            catch (Exception rollbackException)
            {
                appLogger.Warning(
                    $"Failed to roll back logging mode for {Describe(device)}: {rollbackException.Message}");
            }
        }

        return new FleetCommandResult([], refusals);
    }

    /// <summary>
    /// The shared fan-out: snapshot, command each device, keep going past a refusal, collect.
    /// </summary>
    /// <param name="devices">The connected fleet.</param>
    /// <param name="appLogger">Logger for the per-device diagnostics.</param>
    /// <param name="what">
    /// What was being attempted, for the log line — "start logging on" / "stop logging on".
    /// </param>
    /// <param name="command">The command to issue to one device.</param>
    private static FleetCommandResult Issue(
        IEnumerable<IStreamingDevice> devices,
        IAppLogger appLogger,
        string what,
        Action<IStreamingDevice> command)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(appLogger);

        var succeeded = new List<IStreamingDevice>();
        var refusals = new List<DeviceCommandRefusal>();

        foreach (var device in devices.ToList())
        {
            try
            {
                command(device);
                succeeded.Add(device);
            }
            catch (Exception ex)
            {
                // Warning, not Error: a device declining a command is a device/environmental
                // condition the app cannot prevent (no card, no link, wrong transport), and the
                // repo routes those away from Sentry — same rule SdCardFailureClassifier applies.
                appLogger.Warning(ex, $"Could not {what} {Describe(device)}.");
                refusals.Add(new DeviceCommandRefusal(Describe(device), ex));
            }
        }

        return new FleetCommandResult(succeeded, refusals);
    }

    /// <summary>
    /// How a device is named to the user. <c>DeviceDisplayName</c> is the same friendly-name →
    /// serial → COM/IP fallback the device tiles show, so a refusal names the device the way the
    /// user is already looking at it.
    /// </summary>
    private static string Describe(IStreamingDevice device)
    {
        var name = device.DeviceDisplayName;
        return string.IsNullOrWhiteSpace(name) ? "Device" : name;
    }
}
