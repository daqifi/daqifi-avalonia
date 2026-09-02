using System.IO;

namespace Daqifi.Desktop.Exporter;

/// <summary>
/// Turns the sessions in one export into the file names it writes, and is the only place that
/// decides what those names are. Both export paths use it: the desktop Export dialog
/// (<see cref="Daqifi.Desktop.ViewModels.ExportDialogViewModel"/>) and the mobile Storage tab
/// (<c>LoggedSessionsMobileViewModel</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two sessions must never resolve to the same path. <c>OptimizedLoggingSessionExporter.RunExport</c>
/// opens its <see cref="StreamWriter"/> with <c>append: false</c>, so a repeat path does not merge or
/// fail — the later session silently truncates the earlier one, and both writes report success. That
/// is data loss the user is told did not happen, so the disambiguation belongs somewhere neither
/// caller can forget it (issue #186; the desktop dialog had shipped without it).
/// </para>
/// <para>
/// A session's name is arbitrary user text — it is renameable, and the SD-card importer generates
/// names of its own — so it is sanitized first and disambiguated afterwards, in that order.
/// Sanitizing second would let two names that are distinct beforehand (<c>a/b</c> and <c>a_b</c>)
/// converge on one file afterwards, which is exactly one of the reported collisions.
/// </para>
/// </remarks>
internal sealed class ExportFileNamer
{
    /// <summary>
    /// Base names already handed out by this instance — i.e. by this one export.
    /// </summary>
    /// <remarks>
    /// Case-INSENSITIVE, so <c>Run</c> and <c>run</c> are disambiguated even though a
    /// case-sensitive file system (Linux, and an APFS volume formatted that way) would have
    /// tolerated both. Over-disambiguating costs a suffix; under-disambiguating costs a session.
    /// </remarks>
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file this session should be written to, inside <paramref name="directory"/>, given
    /// everything already named by this export.
    /// </summary>
    /// <param name="directory">Directory the export is writing into.</param>
    /// <param name="sessionName">The session's display name, or null when it could not be resolved.</param>
    /// <param name="sessionId">The session's id, used for the fallback name and to keep it unique.</param>
    internal string NextCsvPath(string directory, string? sessionName, int sessionId) =>
        Path.Combine(directory, $"{NextName(sessionName, sessionId)}.csv");

    /// <summary>
    /// As <see cref="NextCsvPath"/>, but returns the bare base name (no directory, no extension).
    /// </summary>
    internal string NextName(string? sessionName, int sessionId)
    {
        var baseName = SafeName(sessionName, sessionId);
        var name = baseName;
        var n = 2;
        // The disambiguated name is itself recorded, so a session genuinely called "Run (2)"
        // alongside two called "Run" still gets a file of its own.
        while (!_used.Add(name))
        {
            name = $"{baseName} ({n++})";
        }

        return name;
    }

    /// <summary>
    /// A session's display name reduced to a legal file name, with no disambiguation — for an
    /// export of ONE session, where there is nothing to collide with.
    /// </summary>
    /// <remarks>
    /// The blank fallback is defensive rather than routine: <see cref="Logger.LoggingSession.Name"/>
    /// already substitutes <c>"Session {ID}"</c> for a blank stored name, so this fires only when
    /// the caller could not resolve a name at all — the desktop dialog looks the session up by id in
    /// a list it may no longer be in. The underscore spelling is that path's long-standing one.
    /// </remarks>
    // @port: Daqifi.Desktop.ViewModels.ExportDialogViewModel.MakeSafeFileName
    internal static string SafeName(string? sessionName, int sessionId)
    {
        var name = string.IsNullOrWhiteSpace(sessionName) ? $"Session_{sessionId}" : sessionName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
