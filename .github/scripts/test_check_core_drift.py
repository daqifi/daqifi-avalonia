#!/usr/bin/env python3
"""Self-test for check_core_drift.py.

The watcher's whole value is that it FAILS while the pin is stale. A watcher that
only ever passes is worse than none, because a quiet weekly green reads as "Core
is current" — which is precisely the belief that let 1.3.0 sit for five weeks. So
the drift and cannot-run paths are what this exercises, not just the happy one.

Runs entirely offline: --index-url takes a local file, so no case here depends on
nuget.org being reachable or on what it happens to be serving today.

Run directly (`python3 test_check_core_drift.py`); no test framework needed.
Exits 0 if every case behaves, 1 otherwise.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(HERE, "check_core_drift.py")

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Daqifi.Core" Version="{core}" />
    <PackageReference Include="Sentry" Version="6.9.0" />
  </ItemGroup>
</Project>
"""

NO_CORE_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="$(AvaloniaVersion)" />
  </ItemGroup>
</Project>
"""

# The real shape of Daqifi.Core's history at the time of #132, plus a prerelease
# and a 1.10.0 that string comparison would sort below 1.9.0.
PUBLISHED = ["0.28.0", "1.0.0", "1.3.0", "1.4.0", "1.5.0", "1.6.0", "1.7.0",
             "1.8.0-rc.1", "1.9.0", "1.10.0"]


def main() -> int:
    failures = []

    def check(label: str, expected: int, actual: int) -> None:
        status = "ok" if actual == expected else "FAILED"
        print(f"  [{status}] {label}: expected {expected}, got {actual}")
        if actual != expected:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        proj = os.path.join(tmp, "app.csproj")
        index = os.path.join(tmp, "index.json")

        def write(path: str, text: str) -> None:
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(text)

        def write_index(versions: list[str]) -> None:
            write(index, json.dumps({"versions": versions}))

        def run(*args: str, capture: bool = False):
            result = subprocess.run(
                [sys.executable, GUARD, "--index-url", index, *args],
                capture_output=True, text=True)
            return result if capture else result.returncode

        write_index(PUBLISHED)

        write(proj, CSPROJ.format(core="1.10.0"))
        check("pin at the latest stable passes", 0, run(proj))

        # THE #132 CASE: the pin the repo actually sat on for five weeks.
        write(proj, CSPROJ.format(core="1.3.0"))
        check("a stale pin is reported as drift", 1, run(proj))

        result = run(proj, capture=True)
        missed_all = all(v in result.stdout
                         for v in ("1.4.0", "1.5.0", "1.6.0", "1.7.0"))
        check("drift names every missed release", True, missed_all)
        check("a prerelease is not a missed release",
              False, "1.8.0-rc.1" in result.stdout)

        # String ordering puts '1.9.0' above '1.10.0' and would call a 1.10.0 pin
        # stale forever — a false alarm every week, which trains the alert away.
        write(proj, CSPROJ.format(core="1.9.0"))
        result = run(proj, capture=True)
        check("versions order numerically, not as strings", 1, result.returncode)
        check("1.10.0 is the latest, not 1.9.0",
              True, "latest stable 1.10.0" in result.stdout)

        # A pin ahead of anything published (an unlisted or yanked release) is not
        # drift, but it is worth saying out loud rather than reporting plain green.
        write(proj, CSPROJ.format(core="2.0.0"))
        result = run(proj, capture=True)
        check("a pin ahead of the index passes", 0, result.returncode)
        check("being ahead is stated, not silently green",
              True, "AHEAD" in result.stdout)

        write(proj, CSPROJ.format(core="1.3.0"))

        # GITHUB_OUTPUT is what the workflow builds the issue body from; if it
        # silently stopped being written the issue would go out blank.
        outputs = os.path.join(tmp, "outputs.txt")
        env = {**os.environ, "GITHUB_OUTPUT": outputs}
        subprocess.run(
            [sys.executable, GUARD, "--index-url", index, proj],
            capture_output=True, text=True, env=env)
        with open(outputs, encoding="utf-8") as handle:
            written = handle.read()
        check("GITHUB_OUTPUT carries the pin", True, "pinned=1.3.0" in written)
        check("GITHUB_OUTPUT carries the latest",
              True, "latest=1.10.0" in written)
        check("GITHUB_OUTPUT carries the missed releases",
              True, "missed=1.4.0,1.5.0,1.6.0,1.7.0,1.9.0,1.10.0" in written)

        # Two projects pinning the same package at different versions: the repo is
        # only as current as its OLDEST pin, so reporting whichever manifest was
        # read first would understate the drift — potentially all the way to
        # "up to date" while a second project sits four releases back.
        second = os.path.join(tmp, "other.csproj")
        write(proj, CSPROJ.format(core="1.10.0"))
        write(second, CSPROJ.format(core="1.4.0"))
        result = run(proj, second, capture=True)
        check("disagreeing pins report drift from the oldest", 1,
              result.returncode)
        check("the oldest pin is the one reported",
              True, "pinned 1.4.0" in result.stdout)
        check("disagreeing pins are called out",
              True, "more than one version" in result.stdout)
        # Reversing the order must not change the verdict.
        check("manifest order does not change the verdict", 1,
              run(second, proj))

        write(proj, CSPROJ.format(core="1.3.0"))
        os.remove(second)

        # A package nothing pins must NEVER look like a package that is current —
        # a rename or a glob that stopped matching would otherwise read as healthy
        # forever, which is the exact silence this whole check exists to break.
        check("an unpinned package is an input error", 2,
              run("--package", "Not.Pinned", proj))

        no_core = os.path.join(tmp, "other.csproj")
        write(no_core, NO_CORE_CSPROJ)
        check("a property-valued pin does not count as pinned", 2, run(no_core))

        write_index([])
        check("an index with no stable versions is an input error", 2, run(proj))

        write_index(["1.8.0-rc.1"])
        check("a prerelease-only index is an input error", 2, run(proj))

        write(index, "not json")
        check("a malformed index is an input error", 2, run(proj))

        check("an unreadable index is an input error", 2,
              subprocess.run(
                  [sys.executable, GUARD, "--index-url",
                   os.path.join(tmp, "nope.json"), proj],
                  capture_output=True, text=True).returncode)

        write_index(PUBLISHED)
        check("a missing manifest is an input error", 2,
              run(os.path.join(tmp, "nope.csproj")))
        check("no arguments is an input error", 2, run())
        check("--glob with explicit paths is an input error", 2,
              run("--glob", proj))
        check("--package without a value is an input error", 2,
              run(proj, "--package"))

    if failures:
        print(f"\n{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print("\nall cases passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
