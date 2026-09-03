#!/usr/bin/env python3
"""Assert the unit suite opens no POOLED SQLite connection, and clears no pools.

Issue #210. `SqliteConnection.ClearAllPools()` is process-global — not scoped to a
connection string, a pool, or the calling class — and xUnit runs test classes in
parallel in one process. Inside Microsoft.Data.Sqlite, `SqliteConnectionPool.Clear()`
marks EVERY connection in the pool `DoNotPool()`, in-use ones included, and then
reclaims the ones reading as leaked, which disposes them. A connection is momentarily
`Leaked` while it is being handed to an opening caller — `Activate` sets `_active =
true` before it points its WeakReference at the outer connection. So a pool clear on
one test's thread can dispose the `sqlite3` handle another test is about to use, and
that test fails with `ObjectDisposedException: 'SQLitePCL.sqlite3'` for reasons having
nothing to do with what it asserts.

The fix was to make the suite's own connections UNPOOLED, so a pool clear has nothing
to reach. `Daqifi.Avalonia.Tests/TestDatabase.cs` is the single place that decides so.

Why this is a source guard and not a test. `TestDatabasePoolingTests` already asserts
that TestDatabase hands out unpooled connections. What it cannot see is a fixture that
never asks it — a new class writing its own `Data source=` re-pools the suite and every
test still passes. Nor would running the suite notice: the flake has been chased for
348 full runs across two machines without appearing once, so green tells you nothing
about whether the race is open. The invariant is only observable in the source.

Three rules over the files handed in (or discovered with --glob):

  1. NO POOL CLEARING — nothing under Daqifi.Avalonia.Tests/ may call ClearAllPools.
     There is nothing left for it to release, and calling it re-creates the hazard for
     the one class that IS still pooled (DeviceRefusalCrashTests, which uses
     production's own container).

  2. ONE PLACE NAMES A DATA SOURCE — only TestDatabase.cs may build a SQLite
     connection string. Everything else routes through it.

  3. THE SEAM IS ACTUALLY UNPOOLED — TestDatabase.cs must set `Pooling = false`.
     Without this, rules 1 and 2 would pass over a suite that had quietly gone back to
     pooling in the one file allowed to decide.

Comments are stripped before matching, so the prose in these files may discuss
`ClearAllPools` and `Data source=` freely — as TestDatabase.cs and
DeviceRefusalCrashTests.cs both do.

Usage:
    check_test_sqlite_pooling.py <File.cs> [...]
    check_test_sqlite_pooling.py --glob          # discover under the repo root

Exit codes are distinct so a caller can tell a real violation from a broken run:

    0  every rule holds
    1  a genuine violation
    2  the check could not run: no arguments, nothing matched, an unreadable file,
       or TestDatabase.cs missing from the inputs
"""

from __future__ import annotations

import glob
import os
import re
import sys

# The one file allowed to name a data source, relative to the repo root.
SEAM = "Daqifi.Avalonia.Tests/TestDatabase.cs"
TEST_ROOT = "Daqifi.Avalonia.Tests"

# Rule 1. The call, not the word — `nameof(ClearAllPools)` or a <see cref> in prose is
# not a call, and prose is stripped before this runs anyway.
CLEAR_ALL_POOLS = re.compile(r"\bClearAllPools\s*\(")

# Rule 2, in the three shapes a data source can be named:
#   - a connection-string literal:      "Data Source=...", "Data source=..."
#   - a builder initialiser:            DataSource = path
#   - EF's provider call:               UseSqlite(...)
# `(?<!\.)` keeps a READ of the property (`builder.DataSource`) out of it: parsing a
# connection string to assert on it is what TestDatabasePoolingTests legitimately does.
DATA_SOURCE = [
    (re.compile(r"Data\s*Source\s*=", re.IGNORECASE), "a `Data Source=` connection-string literal"),
    (re.compile(r"(?<!\.)\bDataSource\s*="), "a `DataSource =` connection-string-builder initialiser"),
    (re.compile(r"\bUseSqlite\s*\("), "a `UseSqlite(...)` call"),
]

# Rule 3.
POOLING_OFF = re.compile(r"\bPooling\s*=\s*false\b", re.IGNORECASE)


def strip_comments(source: str) -> str:
    """Blank out // and /* */ comments, preserving string literals and line count.

    Verbatim strings (@"...") and escaped quotes are both handled, because a path in a
    test fixture is exactly where a stray `//` shows up inside a string.
    """
    out = []
    i = 0
    n = len(source)
    while i < n:
        char = source[i]
        # Verbatim string: @"..." where "" is an escaped quote.
        if char == "@" and i + 1 < n and source[i + 1] == '"':
            out.append(source[i:i + 2])
            i += 2
            while i < n:
                if source[i] == '"':
                    if i + 1 < n and source[i + 1] == '"':
                        out.append('""')
                        i += 2
                        continue
                    out.append('"')
                    i += 1
                    break
                out.append(source[i])
                i += 1
            continue
        # Regular string or char literal, with backslash escapes.
        if char in ('"', "'"):
            quote = char
            out.append(char)
            i += 1
            while i < n:
                if source[i] == "\\" and i + 1 < n:
                    out.append(source[i:i + 2])
                    i += 2
                    continue
                out.append(source[i])
                if source[i] == quote:
                    i += 1
                    break
                i += 1
            continue
        # Line comment — covers /// doc comments.
        if char == "/" and i + 1 < n and source[i + 1] == "/":
            while i < n and source[i] != "\n":
                i += 1
            continue
        # Block comment. Newlines are kept so reported line numbers stay true.
        if char == "/" and i + 1 < n and source[i + 1] == "*":
            i += 2
            while i < n and not (source[i] == "*" and i + 1 < n and source[i + 1] == "/"):
                if source[i] == "\n":
                    out.append("\n")
                i += 1
            i += 2
            continue
        out.append(char)
        i += 1
    return "".join(out)


def normalise(path: str) -> str:
    """Repo-relative, forward-slashed, for stable comparison and messages."""
    return os.path.relpath(os.path.abspath(path), os.getcwd()).replace(os.sep, "/")


def main(argv: list[str]) -> int:
    if len(argv) == 1:
        print(__doc__)
        return 2

    if argv[1] == "--glob":
        paths = sorted(glob.glob(f"{TEST_ROOT}/**/*.cs", recursive=True))
        # A silent empty sweep would report success having checked nothing — the same
        # failure shape this guard exists to prevent.
        if not paths:
            print(f"FAIL: --glob matched no .cs files under {TEST_ROOT}/ — "
                  "nothing was checked.")
            return 2
    else:
        paths = argv[1:]

    failures: list[str] = []
    seam_source: str | None = None

    for path in paths:
        rel = normalise(path)
        try:
            with open(path, encoding="utf-8") as handle:
                code = strip_comments(handle.read())
        except OSError as exc:
            print(f"FAIL: cannot read {path}: {exc}")
            return 2

        lines = code.splitlines()

        # Rule 1 — everywhere, including the seam.
        for number, line in enumerate(lines, start=1):
            if CLEAR_ALL_POOLS.search(line):
                failures.append(
                    f"{rel}:{number}: calls ClearAllPools(). It is process-global and "
                    "disposes connections other test classes are mid-query on (#210). "
                    "TestDatabase's connections are unpooled, so there is nothing to "
                    "release — delete the call.")

        if rel.endswith(SEAM):
            seam_source = code
            continue

        # Rule 2 — everywhere except the seam.
        for number, line in enumerate(lines, start=1):
            for pattern, what in DATA_SOURCE:
                if pattern.search(line):
                    failures.append(
                        f"{rel}:{number}: names a SQLite data source directly "
                        f"({what}). That connection is POOLED by default, which "
                        f"re-opens #210 for the whole suite. Route it through "
                        f"{SEAM} instead.")

    # Rule 3 — the seam has to have been handed in, or rules 1 and 2 are vacuous.
    if seam_source is None:
        print(f"FAIL: {SEAM} was not among the {len(paths)} file(s) checked. "
              "Without it the other rules cannot be trusted.")
        return 2

    if not POOLING_OFF.search(seam_source):
        failures.append(
            f"{SEAM}: does not set `Pooling = false`. It is the one place that "
            "decides the suite's connections are unpooled; without it every fixture "
            "routing through it is pooled again (#210).")

    print(f"Checked {len(paths)} file(s) under {TEST_ROOT}/; "
          f"data sources named only in {SEAM}")

    if failures:
        print()
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    print("OK: the unit suite opens no pooled SQLite connection and clears no pools.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
