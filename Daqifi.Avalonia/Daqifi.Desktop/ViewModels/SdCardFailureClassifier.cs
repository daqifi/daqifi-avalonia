using Daqifi.Core.Communication.Transport; // TransportNotConnectedException (typed link-loss)
using Daqifi.Core.Device; // FeatureNotSupportedException (firmware feature gating, Core ADR 0001)
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Device; // SdOperationBlockedException (the app's own quiescence guard)
using Daqifi.Desktop.Loggers;

namespace Daqifi.Desktop.ViewModels;

/// <summary>
/// How an SD card operation failed, expressed in terms the UI can act on.
/// </summary>
/// <param name="State">
/// The <see cref="SdCardState"/> the card should be shown in. Only applied when
/// <paramref name="IsExpectedDeviceCondition"/> is <c>true</c> — an unexpected failure
/// (an app bug) says nothing about the card, so the existing state is left alone.
/// </param>
/// <param name="StatusMessage">
/// Terse description bound to <see cref="DeviceLogsViewModel.SdCardErrorMessage"/> (status line
/// and error panel). Empty when the state itself already says everything, as for a missing card.
/// </param>
/// <param name="Guidance">
/// The actionable sentence shown to the user: what they should do about it.
/// </param>
/// <param name="IsExpectedDeviceCondition">
/// <c>true</c> for device/environmental conditions the desktop cannot prevent (no card, wedged SD
/// subsystem, filesystem error). These are logged at Warning so they do not raise Sentry issues;
/// anything else is a genuine defect and keeps the Error path.
/// </param>
/// <param name="IsCardUnavailable">
/// <c>true</c> when the SD subsystem — not just this one file — is unusable, so further file
/// operations against the same device would fail the same way. Batch imports stop early on these
/// rather than retrying every remaining file through the same multi-second failure.
/// </param>
// @port: Daqifi.Desktop.ViewModels.SdCardFailure
public sealed record SdCardFailure(
    SdCardState State,
    string StatusMessage,
    string Guidance,
    bool IsExpectedDeviceCondition,
    bool IsCardUnavailable);

/// <summary>
/// Maps exceptions raised by SD card operations onto the user-facing
/// <see cref="SdCardState"/> surface, and decides whether a failure is an expected device
/// condition (log at Warning, no Sentry issue) or a genuine defect (log at Error).
///
/// Daqifi.Core throws typed, already-actionable exceptions for the device conditions
/// (<see cref="SdCardNotPresentException"/>, <see cref="SdCardEmptyTransferException"/>, …).
/// Before issue #754 the desktop let those escape to the generic Error path, which filed a
/// Sentry issue for what is really "power-cycle the device" — and told the user nothing.
/// </summary>
// @port: Daqifi.Desktop.ViewModels.SdCardFailureClassifier
public static class SdCardFailureClassifier
{
    #region Constants
    /// <summary>Guidance shown when the card is readable but its contents are not.</summary>
    internal const string GENERIC_CARD_GUIDANCE =
        "The card may be corrupt or busy. Try a different card or reformat (FAT32).";

    /// <summary>
    /// Guidance for the wedged-SD-subsystem family. The device answers SCPI and lists files but
    /// serves no data; only a power cycle clears it (firmware issue daqifi-nyquist-firmware#567).
    /// </summary>
    internal const string POWER_CYCLE_GUIDANCE =
        "The device's SD card subsystem is not responding. Power-cycle the device and try again.";

    /// <summary>Guidance for a device with no card in the slot.</summary>
    internal const string NO_CARD_GUIDANCE =
        "No SD card is installed in the device. Insert a card and refresh.";

    /// <summary>Guidance for a failure the desktop could not attribute to the card.</summary>
    internal const string UNEXPECTED_FAILURE_GUIDANCE =
        "Please check the device connection and try again.";

    /// <summary>
    /// Guidance for a device whose firmware predates SD file transfer over WiFi. The firmware
    /// gained it in v3.7.0 (daqifi-nyquist-firmware #598/#599); older units still serve SD over a
    /// USB cable, which is the immediate workaround.
    /// </summary>
    internal const string FIRMWARE_TOO_OLD_FOR_WIFI_SD_GUIDANCE =
        "This device's firmware is too old for SD card access over WiFi. Update the firmware, " +
        "or connect the device by USB to read its SD card.";

    /// <summary>Guidance when the link died out from under an in-flight SD operation.</summary>
    internal const string TRANSPORT_GONE_GUIDANCE =
        "The connection to the device was lost. Reconnect and try again.";

    /// <summary>Guidance for an SD operation refused because the device is streaming.</summary>
    internal const string STOP_STREAMING_GUIDANCE =
        "The device is streaming. Stop streaming to work with files on its SD card.";

    /// <summary>Guidance for an SD operation refused because the device is logging to its card.</summary>
    internal const string STOP_SD_LOGGING_GUIDANCE =
        "The device is logging to its SD card. Stop logging to work with the files on it.";
    #endregion

    #region Public Methods
    /// <summary>
    /// Classifies an exception thrown by an SD card refresh, download, or import.
    /// </summary>
    /// <param name="ex">The exception to classify. Never null.</param>
    /// <returns>The UI-facing description of the failure.</returns>
    public static SdCardFailure Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            SdCardNotPresentException => new SdCardFailure(
                State: SdCardState.NotPresent,
                // The NotPresent panel is self-explanatory, so it carries no secondary message.
                StatusMessage: string.Empty,
                Guidance: NO_CARD_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: true),

            // The device opened the file and closed it again without sending a byte. This is
            // AMBIGUOUS — Core cannot distinguish a wedged SD subsystem from a genuinely empty (0-byte)
            // log file (an interrupted logging session leaves those on FAT) — so it throws this for
            // both. Keep the power-cycle guidance, but @port-divergence: do NOT mark the whole card
            // unavailable. Upstream sets this true, which makes a batch import abort on the FIRST empty
            // file and silently skip every later (healthy) file; treating it as per-file lets the batch
            // skip just this file and keep going. (Upstream shares the abort — reported upstream.)
            SdCardEmptyTransferException => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: "The device returned no data for this file.",
                Guidance: POWER_CYCLE_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: false),

            SdCardBusyException => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: "The device's SD card is busy.",
                Guidance: "The device is still using the SD card. Stop logging, wait a moment, and try again.",
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: true),

            SdCardFilesystemException filesystem => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: filesystem.DeviceMessage ?? filesystem.Message,
                Guidance: GENERIC_CARD_GUIDANCE,
                IsExpectedDeviceCondition: true,
                // @port-divergence (as above): a filesystem error can be specific to one corrupt file,
                // so per-file rather than card-wide — a batch skips it and keeps going.
                IsCardUnavailable: false),

            // The listing did not arrive in full: the device never answered the file-list query,
            // or stopped answering part-way through it. Core 1.7.0 can finally tell that apart
            // from a healthy empty card — it appends a SYSTem:ERRor? terminator to the same text
            // exchange and reads the firmware's own end-of-listing marker, so "the reply arrived
            // whole" is a fact rather than an inference (daqifi-core#396).
            //
            // Until that landed this app had no type to match, so RefreshSdCardFiles spent a
            // second round-trip (GetSdCardStorageAsync) on every empty listing to prove the device
            // was answering at all. That only narrowed the ambiguity — a later reply says nothing
            // about whether the listing itself was complete — and it is gone now that Core answers
            // the question directly. This arm is what replaces it.
            //
            // MUST precede the SdCardOperationException arm below: this type derives from it, and
            // the base arm would hand a connection problem the "card may be corrupt, try
            // reformatting" guidance.
            //
            // Card-wide, like the other transport-loss arms: an unfinished listing is a statement
            // about the link or the device, not about any one file, so a batch import stops instead
            // of re-failing every remaining file through the same dead link.
            SdCardListIncompleteException => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: "The device did not finish listing the SD card.",
                Guidance: TRANSPORT_GONE_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: true),

            SdCardOperationException operation => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: operation.LastScpiError ?? operation.Message,
                Guidance: GENERIC_CARD_GUIDANCE,
                IsExpectedDeviceCondition: true,
                // A rejected SCPI command can be specific to one file, so the rest of the card
                // is still worth trying.
                IsCardUnavailable: false),

            // Raised when a download stalls: either the desktop's own SUSTAINED-silence watchdog
            // (StallTimeout > 0 — e.g. 90s of no data, genuinely device-wide), or Core's own ~500ms
            // per-read serial timeout normalized to this type in SdCardSessionImporter with
            // StallTimeout == 0. Both are expected device conditions (power-cycle guidance, no Sentry).
            // @port-divergence on card-unavailability: ONLY the sustained watchdog stall is treated as
            // device-wide (abort the batch, paint the card panel). The transport per-read timeout also
            // fires on a single momentary inter-chunk gap on a healthy device (SD read-latency spike,
            // USB backpressure, a GC pause), so it's per-file — don't abort, or one hiccup would skip
            // every later healthy file.
            SdCardDownloadStalledException stalled => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: "The device stopped responding during the transfer.",
                Guidance: POWER_CYCLE_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: stalled.StallTimeout > TimeSpan.Zero),

            // Core gates SD file transfer over WiFi behind DeviceFeature.SdFileTransferOverWifi
            // (min firmware v3.7.0, per its ADR 0001) and throws this when the connected device's
            // firmware predates it. Core's general floor is 3.5.0, so a perfectly healthy 3.5.x/3.6.x
            // unit — which streams fine and connects over WiFi — lands here the moment the SD pane
            // opens. That is an ordinary device condition, not an app defect: log at Warning (no
            // Sentry issue) and tell the user the actionable thing (update firmware, or use USB).
            // Card-wide, so a batch import aborts instead of re-hitting the identical gate per file.
            FeatureNotSupportedException featureNotSupported => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: featureNotSupported.RequiredVersion is { } required
                    ? $"SD card access over WiFi requires device firmware {required} or newer."
                    : "SD card access over WiFi is not supported by this device's firmware.",
                Guidance: FIRMWARE_TOO_OLD_FOR_WIFI_SD_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: true),

            // The link died under an in-flight SD operation — the user unplugged the device, or a
            // WiFi/TCP session ended. Matched by TYPE, not by inspecting ambient device state at the
            // call site: whether an exception is a disconnect artifact is a property of the
            // exception, and deciding it from "was the device still connected when this was caught"
            // would also downgrade a genuine app defect that merely coincided with a disconnect,
            // losing its Error/Sentry report.
            //
            // Deliberately ONLY the typed transport error. ObjectDisposedException was tried here and
            // is far too broad: an import also disposes a DbContext and file streams, so a disposal
            // defect in parsing or database write would be silently downgraded to "reconnect and try
            // again" and hide a real bug. A suppressed Error costs more than a noisy one, so anything
            // less specific than this type keeps the Error path.
            // Device-wide, so a batch import stops instead of re-failing every remaining file.
            TransportNotConnectedException => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: "The connection to the device was lost.",
                Guidance: TRANSPORT_GONE_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: true),

            // Core's own up-front connectivity guards, as distinct from the transport error above:
            // the device was already known to be gone when the call was made, rather than the link
            // dying under an operation already in flight. Same user-facing outcome — an ordinary
            // disconnect mid-refresh must not raise an Error and a Sentry issue, which is the exact
            // false-positive this classifier exists to prevent.
            //
            // Matched by TYPE since Core 1.7.0 (daqifi-core#395). This arm used to match exception
            // MESSAGES — "Device is not connected" and "…disposing or disconnecting" — because Core
            // threw a plain InvalidOperationException from these guards and exposed no type to match.
            // It now throws DeviceNotConnectedException from ConnectionGuard.EnsureConnected and from
            // both TextExchangeEngine sites, so the string matching is gone.
            //
            // Note this type derives from InvalidOperationException, so it must stay ABOVE any
            // broader InvalidOperationException arm. Core still throws plain ones for genuinely
            // different conditions (SD logging over LAN, a download already in flight, double
            // enumeration of a parse stream); those keep the default Error path, as before.
            DeviceNotConnectedException => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: "The connection to the device was lost.",
                Guidance: TRANSPORT_GONE_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: true),

            // The app's OWN quiescence guard: an SD file operation was attempted while the device
            // was streaming or logging to its card. Ordinary and self-inflicted in the harmless
            // sense — the Device Logs pane fires a refresh the moment a device is selected, so
            // simply opening it mid-stream lands here without the user asking for anything.
            //
            // Untyped, this fell through to the default arm below and was reported as an app
            // defect: an Error, a Sentry issue, and "check the device connection and try again"
            // over a connection that was fine, directly contradicting the status line right above
            // it (issue #146). Typing the guard is the same fix applied to the connectivity guards
            // in #133/#134 — an app-owned condition the classifier can only recognise if the app
            // gives it a type.
            //
            // Card-wide: the device is busy, which has nothing to do with any one file, so a batch
            // import stops rather than re-failing every remaining file against the same guard.
            //
            // Note this derives from InvalidOperationException, as DeviceNotConnectedException
            // above does. Neither derives from the other, so their relative order is free; both
            // must stay above any broader InvalidOperationException arm, should one ever be added.
            SdOperationBlockedException blocked => new SdCardFailure(
                // Busy, not Error: the card is healthy and the condition clears the moment the user
                // stops what the device is doing. Painting the red "SD CARD ERROR" panel over that
                // is the same misreport as filing a Sentry issue for it (Qodo "Busy still shown as
                // error").
                State: SdCardState.Busy,
                StatusMessage: blocked.Message,
                Guidance: blocked.Reason == SdOperationBlockedReason.SdCardLogging
                    ? STOP_SD_LOGGING_GUIDANCE
                    : STOP_STREAMING_GUIDANCE,
                IsExpectedDeviceCondition: true,
                IsCardUnavailable: true),

            _ => new SdCardFailure(
                State: SdCardState.Error,
                StatusMessage: ex.Message,
                Guidance: UNEXPECTED_FAILURE_GUIDANCE,
                IsExpectedDeviceCondition: false,
                IsCardUnavailable: false)
        };
    }
    #endregion
}
