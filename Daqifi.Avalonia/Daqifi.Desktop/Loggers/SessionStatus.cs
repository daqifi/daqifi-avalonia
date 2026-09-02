// Downstream addition — no upstream counterpart, so no `// @port:` marker.

namespace Daqifi.Desktop.Logger;

/// <summary>
/// Whether a <see cref="LoggingSession"/> row is finished, still being written by an import, or
/// left behind by one that failed. This is the single place the app decides what "incomplete"
/// means; before it existed, nothing distinguished a session an import had finished from one it
/// had abandoned half way, so a failed SD import reloaded at the next launch looking complete.
/// </summary>
/// <remarks>
/// <para>The rule the rest of the code follows, in full:</para>
/// <list type="bullet">
/// <item><description><see cref="Complete"/> — the default, and what every pre-existing row
/// migrates to. Listed when it has samples; a <see cref="Complete"/> row with no samples is the
/// abandoned stream-logging row the startup purge in
/// <c>LoggingManager.LoadPersistedLoggingSessions</c> exists to remove.</description></item>
/// <item><description><see cref="Importing"/> — an import owns this row right now. Exempt from
/// the startup purge, so a refresh can never delete a live import's row out from under it.
/// A row still in this state when the app starts was left by a process that died mid-import, and
/// startup moves it to <see cref="ImportFailed"/>.</description></item>
/// <item><description><see cref="ImportFailed"/> — the import threw, or was cancelled, after it
/// had already written samples. Those samples are kept (the source log is often truncated, so a
/// re-import cannot produce them again) and the session is shown flagged rather than presented as
/// a finished one.</description></item>
/// </list>
/// <para>The invariant that makes this small: a persisted row always either has samples or is
/// being written. An import that ends with nothing — a failure before the first batch landed, or
/// an empty log — removes its own row instead of leaving a zero-sample one behind for the purge
/// to delete silently at the next launch.</para>
/// <para>Persisted as the underlying <see cref="int"/>, so the numbers are part of the database
/// schema: add new members, never renumber existing ones.</para>
/// </remarks>
public enum SessionStatus
{
    /// <summary>The session is finished and its data is all there. Zero is the column default, so
    /// every row written before this column existed reads as this.</summary>
    Complete = 0,

    /// <summary>An SD card import created this row and is still writing samples into it.</summary>
    Importing = 1,

    /// <summary>An SD card import created this row and did not finish: the data is partial.</summary>
    ImportFailed = 2
}
