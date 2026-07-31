#!/usr/bin/env python3
"""Self-test for check_avalonia_versions.py.

The guard's whole value is that it FAILS on a split dependency graph. A guard that
silently only ever passes is worse than none, because it reads as coverage — so the
failure paths are what this exercises, not just the happy one.

Run directly (`python3 test_check_avalonia_versions.py`); no test framework needed.
Exits 0 if every case behaves, 1 otherwise.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(HERE, "check_avalonia_versions.py")

CORE = "12.0.5"
# A realistic clean graph: core packages in lockstep, satellites off on their own
# versions exactly as the real restore produces them.
CLEAN = {
    "Avalonia": CORE,
    "Avalonia.Desktop": CORE,
    "Avalonia.Themes.Fluent": CORE,
    "Avalonia.BuildServices": "11.3.2",
    "Avalonia.Controls.DataGrid": "12.0.1",
    "Avalonia.Angle.Windows.Natives": "2.1.27548.20260419",
}


def write_assets(path: str, packages: dict[str, str]) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(
            {"libraries": {f"{n}/{v}": {"type": "package"}
                           for n, v in packages.items()}},
            handle)


def run(*args: str) -> int:
    return subprocess.run(
        [sys.executable, GUARD, *args],
        capture_output=True, text=True).returncode


def main() -> int:
    failures = []

    def check(label: str, expected: int, actual: int) -> None:
        status = "ok" if actual == expected else "FAILED"
        print(f"  [{status}] {label}: expected {expected}, got {actual}")
        if actual != expected:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        a = os.path.join(tmp, "a", "obj", "project.assets.json")
        b = os.path.join(tmp, "b", "obj", "project.assets.json")

        write_assets(a, CLEAN)
        write_assets(b, CLEAN)
        check("clean graph passes", 0, run(a, b))

        # A core package off the root Avalonia version.
        write_assets(a, {**CLEAN, "Avalonia.Desktop": "12.1.0"})
        check("lockstep break fails", 1, run(a, b))

        # The subtle one: a satellite is exempt from the CORE version, but that must
        # not license it to differ BETWEEN heads — that is still a split graph.
        write_assets(a, CLEAN)
        write_assets(b, {**CLEAN, "Avalonia.Controls.DataGrid": "12.1.0"})
        check("satellite split across projects fails", 1, run(a, b))

        # Two heads on different core versions entirely.
        write_assets(b, {n: ("12.1.0" if v == CORE else v)
                         for n, v in CLEAN.items()})
        check("core split across projects fails", 1, run(a, b))

        # Input problems must be distinguishable from real violations (exit 2).
        check("missing file is an input error", 2,
              run(os.path.join(tmp, "nope.json")))

        empty = os.path.join(tmp, "empty.json")
        with open(empty, "w", encoding="utf-8") as handle:
            json.dump({"libraries": {}}, handle)
        check("no Avalonia packages is an input error", 2, run(empty))

        malformed = os.path.join(tmp, "bad.json")
        with open(malformed, "w", encoding="utf-8") as handle:
            handle.write("not json")
        check("malformed json is an input error", 2, run(malformed))

        check("no arguments is an input error", 2, run())

        # Project references carry type "project" and a meaningless version; counting
        # them as packages would invent phantom splits.
        proj = os.path.join(tmp, "proj", "obj", "project.assets.json")
        os.makedirs(os.path.dirname(proj), exist_ok=True)
        with open(proj, "w", encoding="utf-8") as handle:
            json.dump({"libraries": {
                **{f"{n}/{v}": {"type": "package"} for n, v in CLEAN.items()},
                "Avalonia.Something/1.0.0": {"type": "project"},
            }}, handle)
        check("project references are ignored", 0, run(proj))

    if failures:
        print(f"\n{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print("\nall cases passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
