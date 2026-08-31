namespace Daqifi.Desktop.Device;

/// <summary>
/// What the device was busy doing when an SD card file operation was refused. The two cases need
/// different advice — one says stop logging, the other says stop streaming — so the reason travels
/// with the exception rather than being re-derived from device state at the point it is reported.
/// </summary>
public enum SdOperationBlockedReason
{
    /// <summary>The device is logging to its own SD card.</summary>
    SdCardLogging,

    /// <summary>The device is streaming samples to the app.</summary>
    Streaming
}

/// <summary>
/// Thrown by <see cref="AbstractStreamingDevice"/>'s own quiescence guard when an SD card file
/// operation is attempted while the device is busy streaming or logging to its card.
/// </summary>
/// <remarks>
/// <para>
/// This is an ordinary user-facing condition, not a defect: opening the Device Logs pane on a
/// streaming device is a reasonable thing to do, and the app fires a refresh automatically when a
/// device is selected. It needs a TYPE so
/// <see cref="ViewModels.SdCardFailureClassifier"/> can report it as such. Until it had one it was
/// a plain <see cref="InvalidOperationException"/>, which reached the classifier's default arm —
/// an Error plus a Sentry issue, under the guidance "check the device connection and try again",
/// for a device whose connection was never in question (issue #146). Giving app-owned guards a
/// type the classifier matches is the same move made for the connectivity guards in #133/#134.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> deliberately. The three guarded methods —
/// refresh, download, delete — all document
/// <c>&lt;exception cref="InvalidOperationException"&gt;Thrown when streaming or SD logging is
/// active.&lt;/exception&gt;</c>, and that contract stays true. Nothing in the app catches that
/// type around SD, so narrowing it costs nothing and breaks nothing.
/// </para>
/// </remarks>
public sealed class SdOperationBlockedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SdOperationBlockedException"/> class.
    /// </summary>
    /// <param name="reason">What the device was busy doing.</param>
    public SdOperationBlockedException(SdOperationBlockedReason reason)
        : base(reason == SdOperationBlockedReason.SdCardLogging
            ? "Cannot perform SD card file operations while logging to the SD card. Stop logging first."
            : "Cannot perform SD card file operations while streaming. Stop streaming first.")
    {
        Reason = reason;
    }

    /// <summary>What the device was busy doing when the operation was refused.</summary>
    public SdOperationBlockedReason Reason { get; }
}
