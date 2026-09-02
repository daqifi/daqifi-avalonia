// DO NOT manually delete the `// @port:` markers — they link symbols back to
// the correspondence map.

using System.IO;

namespace Daqifi.Desktop.Exporter;

/// <summary>
/// Decides whether a failed write is the destination's fault, and turns it into a sentence that
/// tells the user what to do about it. The single place either question is answered, for every
/// path in the app that writes a user-chosen file.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="Daqifi.Desktop.ViewModels.ExportDialogViewModel"/>, which had the only
/// copy. The CSV export classified its destination failures properly while the two PNG graph-save
/// commands had no <c>try</c> at all and killed the process on an unwritable destination
/// (issue #182) — one behaviour, in one place, is what stops the two drifting apart again.
/// </para>
/// <para>
/// The split matters beyond wording: a destination failure is a user/environmental condition and is
/// logged at Warning (no Sentry issue), the same way the device layer classifies an unreachable
/// device. Everything else keeps the Error/Sentry path so real defects stay visible.
/// </para>
/// </remarks>
internal static class DestinationFailureClassifier
{
    /// <summary>
    /// True when a failure is the destination's fault — denied, gone, or held by another program.
    /// Deliberately narrow: a generic <see cref="IOException"/> (a full disk, a failing drive, an
    /// EF/SQLite read error) keeps the default Error/Sentry treatment so real problems stay visible.
    /// </summary>
    // @port: Daqifi.Desktop.ViewModels.ExportDialogViewModel.IsDestinationBlocked
    internal static bool IsBlocked(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        DirectoryNotFoundException => true,
        IOException io => IsSharingViolation(io),
        _ => false,
    };

    /// <summary>
    /// Turns a file-access failure into a message that tells the user what to do about it.
    /// </summary>
    /// <param name="ex">The failure to describe.</param>
    /// <param name="filepath">The destination that could not be written.</param>
    /// <param name="fallbackName">
    /// What to call the file when <paramref name="filepath"/> is blank, phrased to drop into
    /// "Could not write {name}". Each caller names its own artefact so the message never says
    /// "export file" about a graph image.
    /// </param>
    // @port: Daqifi.Desktop.ViewModels.ExportDialogViewModel.DescribeFileFailure
    internal static string Describe(Exception ex, string filepath, string fallbackName)
    {
        var name = string.IsNullOrWhiteSpace(filepath) ? fallbackName : $"'{Path.GetFileName(filepath)}'";

        return ex switch
        {
            UnauthorizedAccessException =>
                $"Could not write {name} — access was denied. Choose a different folder, or check that the file is not read-only.",
            DirectoryNotFoundException =>
                $"Could not write {name} — that folder no longer exists. Choose a different location and try again.",
            IOException io when IsSharingViolation(io) =>
                $"Could not write {name} — it is open in another program. Close it and try again.",
            _ =>
                $"Could not write {name}. {ex.Message}",
        };
    }

    /// <summary>
    /// True when Windows refused the handle because someone else already holds the file
    /// (ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION) — the "it's still open in Excel" case, as
    /// opposed to a full disk or an I/O error, which deserve a different message.
    /// </summary>
    /// <remarks>
    /// Gated to Windows on purpose. .NET on macOS also raises file exceptions carrying FACILITY_WIN32
    /// HResults built from the Unix errno (a missing directory measured as 0x80070003 there), so the
    /// low word is an errno, not a Win32 status code, and 32/33 would mean something else entirely.
    /// The genuine "held by another program" case on macOS arrives as an <see cref="IOException"/>
    /// with no facility at all, and is caught by the export path's pre-flight probe rather than by
    /// this check.
    /// </remarks>
    // @port: Daqifi.Desktop.ViewModels.ExportDialogViewModel.IsSharingViolation
    internal static bool IsSharingViolation(IOException ex)
    {
        const int FACILITY_WIN32 = unchecked((int)0x80070000);
        const int ERROR_SHARING_VIOLATION = 32;
        const int ERROR_LOCK_VIOLATION = 33;

        if (!OperatingSystem.IsWindows()) { return false; }

        if ((ex.HResult & unchecked((int)0xFFFF0000)) != FACILITY_WIN32) { return false; }

        var win32Error = ex.HResult & 0xFFFF;
        return win32Error is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION;
    }
}
