using System.Runtime.CompilerServices;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Assembly-wide policy: keep the test run out of the developer's (and CI's) real DAQiFi
/// data directory.
///
/// <para>
/// Several library types resolve <c>Daqifi.Desktop.Common.AppDataPaths.DataDirectory</c> from a
/// static field initializer — <c>LoggingManager</c> is one — and resolving it eagerly creates the
/// directory and write-probes it. Merely referencing such a type from a test is therefore enough
/// to touch <c>~/Library/Application Support/DAQiFi</c> (or <c>%ProgramData%\DAQiFi</c>), next to
/// the real <c>DAQiFiDatabase.db</c>. <c>AppDataPaths</c> already supports the
/// <c>DAQIFI_DATA_DIR</c> override for exactly this reason, so point it at a throwaway path
/// before any test code runs.
/// </para>
///
/// <para>
/// A module initializer, not a fixture: <c>AppDataPaths</c> resolves once per process, into static
/// readonly state, so the override has to be in place before the FIRST touch — which a fixture
/// attached to one collection cannot guarantee. Module initializers run before any other code in
/// this assembly, which does.
/// </para>
/// </summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void RedirectAppDataDirectory()
    {
        // An explicitly supplied value wins: a harness that wants to aim the run somewhere
        // specific should not be overridden by this default.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DAQIFI_DATA_DIR")))
        {
            return;
        }

        // A stable path rather than a per-run GUID, so repeated runs reuse one directory instead
        // of littering the temp root. Nothing in the suite reads or writes inside it today; it
        // exists only to absorb AppDataPaths' eager create-and-probe.
        var directory = Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests");
        Environment.SetEnvironmentVariable("DAQIFI_DATA_DIR", directory);
    }
}
