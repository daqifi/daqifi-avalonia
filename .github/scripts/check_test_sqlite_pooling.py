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
348 full runs without appearing once, so green tells you nothing about whether the race
is open. The invariant is only observable in the source.

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

PRECISION. Each rule is matched against the part of the file where its violation can
actually live, which is what keeps it from crying wolf and from missing the real thing:

  - comments are discarded outright, so the prose in TestDatabase.cs and
    DeviceRefusalCrashTests.cs may discuss `ClearAllPools` and `Data source=` freely;
  - a `Data Source=` connection string is looked for ONLY INSIDE STRING LITERALS.
    Searched over code as well, it would flag an ordinary local named `dataSource`,
    because `Data\\s*Source\\s*=` matches `dataSource =` case-insensitively;
  - `DataSource =` and `UseSqlite(` are looked for ONLY IN CODE, with literals removed.
    Both the object-initialiser form (`{ DataSource = path }`) and the qualified form
    (`builder.DataSource = path`) are rejected: both produce a pooled connection
    string. Only an ASSIGNMENT matches, so reading the property
    (`Assert.Equal(path, builder.DataSource)`) or comparing it (`== path`) stays legal
    — that is how TestDatabasePoolingTests parses a connection string to assert on it.

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

# Build output. Restore generates .cs under obj/ (AssemblyInfo, GlobalUsings, source
# generators), which is not source anybody edits — scanning it would report violations
# nobody can fix, and the count would depend on whether the tree had been built.
GENERATED_DIRS = ("/obj/", "/bin/")

# Rule 1. The call, not the word: prose is stripped before this runs.
CLEAR_ALL_POOLS = re.compile(r"\bClearAllPools\s*\(")

# Rule 2, over CODE with string literals removed. `(?!=)` keeps `==` out, so a
# comparison reads as what it is. No dot exclusion: `builder.DataSource = path` is
# every bit as pooled as `{ DataSource = path }`.
DATA_SOURCE_CODE = [
    (re.compile(r"\bDataSource\s*=(?!=)"),
     "a `DataSource =` assignment on a connection-string builder"),
    (re.compile(r"\bUseSqlite\s*\("), "a `UseSqlite(...)` call"),
]

# Rule 2, over STRING LITERAL CONTENT only — see PRECISION above.
DATA_SOURCE_LITERAL = re.compile(r"Data\s*Source\s*=", re.IGNORECASE)

# Rule 3.
POOLING_OFF = re.compile(r"\bPooling\s*=\s*false\b", re.IGNORECASE)


def split_code_and_literals(source: str) -> tuple[list[str], list[str]]:
    """Separate C# source into per-line CODE and per-line STRING CONTENT.

    Comments are dropped from both. Line numbering is preserved in each view, so a
    match in either can be reported against the real line. Verbatim strings (@"...",
    where "" escapes a quote) and backslash escapes are both handled, because a
    Windows path in a fixture is exactly where a stray quote or `//` turns up.
    """
    code: list[str] = []
    literal: list[str] = []
    line_code: list[str] = []
    line_literal: list[str] = []

    def end_line() -> None:
        code.append("".join(line_code))
        literal.append("".join(line_literal))
        line_code.clear()
        line_literal.clear()

    i = 0
    n = len(source)
    while i < n:
        char = source[i]

        if char == "\n":
            end_line()
            i += 1
            continue

        # Verbatim string: @"..." with "" as the escaped quote.
        if char == "@" and i + 1 < n and source[i + 1] == '"':
            i += 2
            while i < n:
                if source[i] == '"':
                    if i + 1 < n and source[i + 1] == '"':
                        line_literal.append('"')
                        i += 2
                        continue
                    i += 1
                    break
                if source[i] == "\n":
                    end_line()
                else:
                    line_literal.append(source[i])
                i += 1
            continue

        # Regular string or char literal, with backslash escapes.
        if char in ('"', "'"):
            quote = char
            i += 1
            while i < n:
                if source[i] == "\\" and i + 1 < n:
                    line_literal.append(source[i + 1])
                    i += 2
                    continue
                if source[i] == quote:
                    i += 1
                    break
                if source[i] == "\n":
                    end_line()
                else:
                    line_literal.append(source[i])
                i += 1
            continue

        # Line comment — covers /// doc comments.
        if char == "/" and i + 1 < n and source[i + 1] == "/":
            while i < n and source[i] != "\n":
                i += 1
            continue

        # Block comment.
        if char == "/" and i + 1 < n and source[i + 1] == "*":
            i += 2
            while i < n and not (source[i] == "*" and i + 1 < n and source[i + 1] == "/"):
                if source[i] == "\n":
                    end_line()
                i += 1
            i += 2
            continue

        line_code.append(char)
        i += 1

    end_line()
    return code, literal


def normalise(path: str) -> str:
    """Repo-relative, forward-slashed, for stable comparison and messages."""
    return os.path.relpath(os.path.abspath(path), os.getcwd()).replace(os.sep, "/")


def main(argv: list[str]) -> int:
    if len(argv) == 1:
        print(__doc__)
        return 2

    if argv[1] == "--glob":
        paths = [
            path for path in sorted(glob.glob(f"{TEST_ROOT}/**/*.cs", recursive=True))
            if not any(part in path.replace(os.sep, "/") for part in GENERATED_DIRS)
        ]
        # A silent empty sweep would report success having checked nothing — the same
        # failure shape this guard exists to prevent.
        if not paths:
            print(f"FAIL: --glob matched no .cs files under {TEST_ROOT}/ — "
                  "nothing was checked.")
            return 2
    else:
        paths = argv[1:]

    failures: list[str] = []
    seam_code: str | None = None

    for path in paths:
        rel = normalise(path)
        try:
            with open(path, encoding="utf-8") as handle:
                code_lines, literal_lines = split_code_and_literals(handle.read())
        except OSError as exc:
            print(f"FAIL: cannot read {path}: {exc}")
            return 2

        # Rule 1 — everywhere, including the seam.
        for number, line in enumerate(code_lines, start=1):
            if CLEAR_ALL_POOLS.search(line):
                failures.append(
                    f"{rel}:{number}: calls ClearAllPools(). It is process-global and "
                    "disposes connections other test classes are mid-query on (#210). "
                    "TestDatabase's connections are unpooled, so there is nothing to "
                    "release — delete the call.")

        if rel.endswith(SEAM):
            seam_code = "\n".join(code_lines)
            continue

        # Rule 2 — everywhere except the seam, each pattern over its own view.
        for number, line in enumerate(code_lines, start=1):
            for pattern, what in DATA_SOURCE_CODE:
                if pattern.search(line):
                    failures.append(
                        f"{rel}:{number}: names a SQLite data source directly "
                        f"({what}). That connection is POOLED by default, which "
                        f"re-opens #210 for the whole suite. Route it through "
                        f"{SEAM} instead.")
        for number, line in enumerate(literal_lines, start=1):
            if DATA_SOURCE_LITERAL.search(line):
                failures.append(
                    f"{rel}:{number}: names a SQLite data source directly (a "
                    f"`Data Source=` connection-string literal). That connection is "
                    f"POOLED by default, which re-opens #210 for the whole suite. "
                    f"Route it through {SEAM} instead.")

    # Rule 3 — the seam has to have been handed in, or rules 1 and 2 are vacuous.
    if seam_code is None:
        print(f"FAIL: {SEAM} was not among the {len(paths)} file(s) checked. "
              "Without it the other rules cannot be trusted.")
        return 2

    if not POOLING_OFF.search(seam_code):
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
