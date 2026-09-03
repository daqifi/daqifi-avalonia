#!/usr/bin/env python3
"""Self-test for check_test_sqlite_pooling.py.

The guard's whole value is that it FAILS when a fixture re-pools the suite. A guard that
only ever passes is worse than none, because it reads as coverage — so the violations
are what this exercises, along with the two false positives that would get it switched
off: prose that discusses `ClearAllPools`, and a test that PARSES a connection string to
assert on it.

Run directly (`python3 test_check_test_sqlite_pooling.py`); no test framework needed.
Exits 0 if every case behaves, 1 otherwise.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(HERE, "check_test_sqlite_pooling.py")

SEAM_REL = os.path.join("Daqifi.Avalonia.Tests", "TestDatabase.cs")

# A faithful miniature of the real seam.
SEAM_OK = '''
namespace Daqifi.Avalonia.Tests;

/// The suite's connections are unpooled; see #210. Prose may say `Data source=` and
/// SqliteConnection.ClearAllPools() freely, because comments are stripped first.
internal static class TestDatabase
{
    internal static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
}
'''

# The regression rule 3 exists for: the one file allowed to decide, deciding wrong.
SEAM_POOLED = SEAM_OK.replace(", Pooling = false", "")

CLEAN_FIXTURE = '''
namespace Daqifi.Avalonia.Tests.Loggers;

public class SomeTests
{
    private void Open() => new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
}
'''


def write(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def run(cwd: str, *args: str) -> int:
    return subprocess.run(
        [sys.executable, GUARD, *args],
        capture_output=True, text=True, cwd=cwd).returncode


def main() -> int:
    failures = []

    def check(label: str, expected: int, actual: int) -> None:
        status = "ok" if actual == expected else "FAILED"
        print(f"  [{status}] {label}: expected {expected}, got {actual}")
        if actual != expected:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        seam = os.path.join(tmp, SEAM_REL)
        fixture = os.path.join(tmp, "Daqifi.Avalonia.Tests", "Loggers", "SomeTests.cs")

        write(seam, SEAM_OK)
        write(fixture, CLEAN_FIXTURE)
        check("clean suite passes", 0, run(tmp, seam, fixture))
        check("clean suite passes via --glob", 0, run(tmp, "--glob"))

        # Rule 1 — the call, in a fixture and in the seam itself.
        write(fixture, CLEAN_FIXTURE.replace(
            "private void Open()", "public void Dispose() { SqliteConnection.ClearAllPools(); }\n    private void Open()"))
        check("ClearAllPools in a fixture fails", 1, run(tmp, seam, fixture))
        write(fixture, CLEAN_FIXTURE)

        # Rule 2 — each of the three shapes a data source gets named in.
        write(fixture, CLEAN_FIXTURE.replace(
            "TestDatabase.ConnectionString(DatabasePath)", '$"Data source={DatabasePath}"'))
        check("`Data source=` literal in a fixture fails", 1, run(tmp, seam, fixture))

        write(fixture, CLEAN_FIXTURE.replace(
            "TestDatabase.ConnectionString(DatabasePath)",
            "new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()"))
        check("`DataSource =` initialiser in a fixture fails", 1, run(tmp, seam, fixture))

        write(fixture, CLEAN_FIXTURE.replace(
            "new SqliteConnection(TestDatabase.ConnectionString(DatabasePath))",
            "new DbContextOptionsBuilder<LoggingContext>().UseSqlite(Whatever).Options"))
        check("`UseSqlite(...)` in a fixture fails", 1, run(tmp, seam, fixture))

        # The false positive that would get the guard switched off: prose. The real
        # DeviceRefusalCrashTests.cs discusses both, and must stay legal.
        write(fixture, '''
namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// The suite's other SQLite users used to call SqliteConnection.ClearAllPools(), which is
/// process-wide. They now route through TestDatabase instead of a raw `Data source=`.
/// </summary>
/* A block comment naming DataSource = whatever and UseSqlite( too. */
public class ProseOnlyTests { }
''')
        check("prose about ClearAllPools and Data source= passes", 0, run(tmp, seam, fixture))

        # The other false positive: PARSING a connection string to assert on it, which is
        # exactly what TestDatabasePoolingTests does. A read of `.DataSource` is not a
        # declaration of one.
        write(fixture, '''
namespace Daqifi.Avalonia.Tests;

public sealed class TestDatabasePoolingTests
{
    public void Pooling_is_off()
    {
        var builder = new SqliteConnectionStringBuilder(TestDatabase.ConnectionString(AnyPath));
        Assert.False(builder.Pooling);
        Assert.Equal(AnyPath, builder.DataSource);
    }
}
''')
        check("parsing a connection string to assert on it passes", 0, run(tmp, seam, fixture))
        write(fixture, CLEAN_FIXTURE)

        # A `//` inside a string must not be mistaken for a comment and blank out the
        # rest of the line — that would hide a violation sitting after it.
        write(fixture, '''
namespace Daqifi.Avalonia.Tests.Loggers;

public class PathTests
{
    private const string Url = "https://example.invalid/x";
    private void Open() => new SqliteConnection($"Data source={DatabasePath}");
}
''')
        check("a // inside a string does not hide a later violation", 1, run(tmp, seam, fixture))

        # Same for a verbatim string, where "" is the escape.
        write(fixture, '''
namespace Daqifi.Avalonia.Tests.Loggers;

public class VerbatimTests
{
    private const string Weird = @"C:\\x//y "" still a string";
    private void Open() => new SqliteConnection($"Data source={DatabasePath}");
}
''')
        check("a // inside a verbatim string does not hide a later violation", 1,
              run(tmp, seam, fixture))
        write(fixture, CLEAN_FIXTURE)

        # Rule 3 — the seam itself going back to pooled.
        write(seam, SEAM_POOLED)
        check("seam without `Pooling = false` fails", 1, run(tmp, seam, fixture))
        write(seam, SEAM_OK)

        # Input problems must be distinguishable from real violations (exit 2).
        check("seam absent from the inputs is an input error", 2, run(tmp, fixture))
        check("missing file is an input error", 2,
              run(tmp, os.path.join(tmp, "nope.cs")))
        check("no arguments is an input error", 2, run(tmp))

        # --glob with nothing to find must refuse rather than pass silently.
        with tempfile.TemporaryDirectory() as empty:
            check("--glob matching nothing is an input error", 2, run(empty, "--glob"))

    if failures:
        print(f"\n{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print("\nall cases passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
