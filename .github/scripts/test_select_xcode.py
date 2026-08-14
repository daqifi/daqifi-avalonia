#!/usr/bin/env python3
"""Self-test for select_xcode.py.

The selector's value is entirely in the cases where it REFUSES: a runner image that no
longer carries the Xcode the pinned SDK's workload wants must fail the job with that
sentence, not fall through to the newest Xcode and let the build die 200 lines later in
`_ValidateXcodeVersion`. So the refusals are what this exercises, alongside the two
selection rules that are easy to get wrong — alias de-duplication and highest-patch —
neither of which a passing CI run would ever reveal as broken.

Run directly (`python3 test_select_xcode.py`); no test framework needed.
Exits 0 if every case behaves, 1 otherwise.
"""

from __future__ import annotations

import os
import plistlib
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SELECTOR = os.path.join(HERE, "select_xcode.py")


def make_xcode(apps_dir: str, name: str, version: str | None) -> str:
    """Create a fake Xcode bundle. version=None writes a version.plist with no version."""
    app = os.path.join(apps_dir, name)
    os.makedirs(os.path.join(app, "Contents", "Developer"), exist_ok=True)
    plist: dict[str, str] = {"ProductBuildVersion": "17F113"}
    if version is not None:
        plist["CFBundleShortVersionString"] = version
    with open(os.path.join(app, "Contents", "version.plist"), "wb") as handle:
        plistlib.dump(plist, handle)
    return app


def alias(apps_dir: str, name: str, target: str) -> None:
    """Mirror the runner image, which publishes Xcode_26.4.app as a link to 26.4.1."""
    os.symlink(target, os.path.join(apps_dir, name))


def run(*args: str) -> tuple[int, str]:
    result = subprocess.run(
        [sys.executable, SELECTOR, *args], capture_output=True, text=True)
    return result.returncode, result.stdout.strip()


def main() -> int:
    failures: list[str] = []

    def check(label: str, expected: object, actual: object) -> None:
        status = "ok" if actual == expected else "FAILED"
        print(f"  [{status}] {label}: expected {expected!r}, got {actual!r}")
        if actual != expected:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        # An image like macos-26: several minors of one major, plus aliases.
        apps = os.path.join(tmp, "Applications")
        os.makedirs(apps)
        make_xcode(apps, "Xcode_26.6.app", "26.6")
        make_xcode(apps, "Xcode_26.5.app", "26.5")
        real_2641 = make_xcode(apps, "Xcode_26.4.1.app", "26.4.1")
        make_xcode(apps, "Xcode_26.3.app", "26.3")
        alias(apps, "Xcode_26.4.app", real_2641)
        alias(apps, "Xcode.app", os.path.join(apps, "Xcode_26.6.app"))

        def select(required: str, directory: str = apps) -> tuple[int, str]:
            return run(required, "--applications-dir", directory)

        code, out = select("26.5")
        check("exact minor is selected", 0, code)
        check("exact minor prints its Developer dir",
              os.path.realpath(os.path.join(apps, "Xcode_26.5.app",
                                            "Contents", "Developer")),
              out)

        # Only major.minor is compared, so 26.4 must resolve to the 26.4.1 bundle — and
        # via the real path, not the alias that happens to sort first.
        code, out = select("26.4")
        check("patch-level bundle satisfies a major.minor request", 0, code)
        check("alias resolves to the real bundle",
              os.path.realpath(os.path.join(real_2641, "Contents", "Developer")), out)

        # The property from #103 that makes this script necessary at all: a newer Xcode
        # is NOT a substitute. 26.7 is absent, 26.6 is present, and this must still fail.
        code, out = select("26.7")
        check("a newer Xcode does not satisfy an absent version", 1, code)
        check("a refusal prints nothing on stdout", "", out)

        code, _ = select("25.0")
        check("an older major is a mismatch, not a fallback", 1, code)

        # Two patches of the same minor: the highest wins, deterministically.
        patches = os.path.join(tmp, "Patches")
        os.makedirs(patches)
        make_xcode(patches, "Xcode_26.4.0.app", "26.4.0")
        newest = make_xcode(patches, "Xcode_26.4.1.app", "26.4.1")
        code, out = select("26.4", patches)
        check("highest patch within the minor wins", 0, code)
        check("highest patch prints the 26.4.1 Developer dir",
              os.path.realpath(os.path.join(newest, "Contents", "Developer")), out)

        # A bundle the sweep cannot make sense of must not sink the whole selection:
        # one with no version key, and one with no Contents/Developer at all (which is
        # what a partially-installed or stub bundle looks like).
        mixed = os.path.join(tmp, "Mixed")
        os.makedirs(mixed)
        make_xcode(mixed, "Xcode_broken.app", None)
        os.makedirs(os.path.join(mixed, "Xcode_stub.app", "Contents"))
        good = make_xcode(mixed, "Xcode_26.5.app", "26.5")
        code, out = select("26.5", mixed)
        check("an unreadable bundle is skipped, not fatal", 0, code)
        check("the readable bundle is still selected",
              os.path.realpath(os.path.join(good, "Contents", "Developer")), out)

        # Input problems must be distinguishable from a real mismatch (exit 2).
        empty = os.path.join(tmp, "Empty")
        os.makedirs(empty)
        check("no Xcode at all is an environment error", 2, select("26.5", empty)[0])

        unreadable_only = os.path.join(tmp, "Unreadable")
        os.makedirs(unreadable_only)
        make_xcode(unreadable_only, "Xcode_26.5.app", "not-a-version")
        check("no readable version anywhere is an environment error", 2,
              select("26.5", unreadable_only)[0])

        check("a missing applications dir is an input error", 2,
              select("26.5", os.path.join(tmp, "nope"))[0])
        check("a non-numeric required version is an input error", 2,
              select("twenty-six")[0])
        check("a major-only required version is an input error", 2, select("26")[0])
        check("no arguments is an input error", 2, run()[0])

    if failures:
        print(f"\n{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print("\nall cases passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
