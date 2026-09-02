using System;
using System.IO;
using Daqifi.Desktop.Common.Loggers;

namespace Daqifi.Desktop.Helpers;

/// <summary>
/// The two file-system rules every file the app owns inside its own data directory is written and
/// recovered by: a write is atomic, and a file that turns out to be damaged is renamed aside
/// rather than destroyed.
/// </summary>
/// <remarks>
/// <para>
/// These rules were arrived at independently by the settings store (#193) and the profiles store
/// (#184) and written out twice, character for character, comments included. Two copies of a rule
/// are two chances to get it wrong, and that is not hypothetical here: the profiles copy shipped
/// with <c>overwrite: true</c> and silently destroyed an earlier quarantine (#197), and both
/// copies at one point keyed the retry on <c>File.Exists</c>, which is <c>false</c> for a
/// directory and abandoned the quarantine (#198). One implementation, one test suite.
/// </para>
/// <para>
/// <b>App-data files only, deliberately.</b> A destination the USER chose needs a superset of
/// this and has one — <c>GraphImageSaver</c> resolves symbolic links, carries the existing file's
/// permissions onto the replacement with <see cref="File.Replace(string, string, string?)"/>, and
/// falls back to a non-atomic in-place write inside a folder that accepts no new entries (#194).
/// None of that applies to a file in a directory the app created and owns, and adopting it here
/// would be behaviour nobody asked for. The name of this type is the boundary: if the path came
/// from a file dialog, it does not belong here.
/// </para>
/// </remarks>
internal static class AppDataFile
{
    /// <summary>How many names <see cref="MoveAside"/> will try before giving up.</summary>
    private const int QuarantineNameAttempts = 100;

    /// <summary>
    /// Moves <paramref name="sourcePath"/> to <paramref name="preferredPath"/>, or to the first
    /// free <c>-1</c>, <c>-2</c>, … variant of it if that name is taken.
    /// </summary>
    /// <remarks>
    /// RENAMED, never deleted: it is the user's file, an unparseable one is very often still
    /// readable by hand, and it is the only record of what they had chosen.
    /// <para>
    /// <c>overwrite: false</c>, matching <c>DatabaseMigrator.QuarantineDatabase</c> — a quarantine
    /// that clobbers an earlier quarantine destroys the very thing the rename exists to preserve,
    /// and reports success while doing it. The millisecond timestamp callers put in the name is not
    /// enough on its own: two app instances share this directory and both can reach recovery on the
    /// same damaged file inside the same millisecond.
    /// </para>
    /// <para>
    /// Unlike the database, though, abandoning the move leaves the damaged file at its real name
    /// and restores the exact permanent failure #184 and #193 exist to end — so a taken name is
    /// retried under the next one rather than given up on. Retrying the move itself, rather than
    /// testing for a free name and then using it, is also what closes the gap between the two in
    /// which another instance can take the name.
    /// </para>
    /// </remarks>
    /// <returns>The path the file was moved to, or <c>null</c> if it could not be moved.</returns>
    internal static string? MoveAside(string sourcePath, string preferredPath)
    {
        Exception? lastFailure = null;

        for (var attempt = 0; attempt < QuarantineNameAttempts; attempt++)
        {
            var candidate = attempt == 0 ? preferredPath : $"{preferredPath}-{attempt}";

            try
            {
                File.Move(sourcePath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException ex) when (Path.Exists(candidate))
            {
                // The name is taken — by another instance's quarantine, by one of this instance's
                // own within the same millisecond, or by a directory that happens to sit there.
                // Take the next name. Path.Exists rather than File.Exists precisely so a DIRECTORY
                // counts as taken: File.Exists is false for one, which would send this to the catch
                // below, abandon the quarantine, and leave the damaged file in place. Any other
                // IOException (no space, unwritable directory) does fall through to that catch.
                lastFailure = ex;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                break;
            }
        }

        var message = $"The damaged file at {sourcePath} could not be moved aside.";
        if (lastFailure is null) { AppLogger.Instance.Error(message); }
        else { AppLogger.Instance.Error(lastFailure, message); }

        return null;
    }

    /// <summary>
    /// Writes <paramref name="destinationPath"/> through a temporary file so the file on disk is
    /// only ever the whole of the old content or the whole of the new.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="System.Xml.Linq.XDocument.Save(string)"/> and its kind truncate the target
    /// first: a power cut, a full disk or a crash part-way through that write is exactly how the
    /// half-written, unparseable files in #184 and #193 come about. A rename either happens or
    /// does not.
    /// </para>
    /// <para>
    /// The temporary name is unique per write, not a fixed <c>.tmp</c>: two app instances share
    /// one data directory, and a single well-known temp path lets each truncate, move or delete
    /// the other's half-written file — reintroducing exactly the corruption this exists to
    /// prevent. This does not make a concurrent read-modify-write safe (last writer still wins, as
    /// it always has); it makes each individual write atomic and independent.
    /// </para>
    /// <para>
    /// The failure is rethrown untouched — it is what the caller needs — after a best-effort
    /// removal of the temporary file, so a run of failed writes does not litter the data directory.
    /// </para>
    /// </remarks>
    /// <param name="destinationPath">The file to end up with the new content.</param>
    /// <param name="write">
    /// Writes the new content to the path it is handed. It must create that file; it is never
    /// given an existing one.
    /// </param>
    internal static void WriteAtomically(string destinationPath, Action<string> write)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            write(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { /* nothing useful to do */ }
            throw;
        }
    }
}
