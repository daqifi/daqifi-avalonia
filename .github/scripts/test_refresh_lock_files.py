#!/usr/bin/env python3
"""Self-test for refresh_lock_files.sh — its REFUSALS, which are the load-bearing part.

The script's happy path regenerates six lock files and needs the pinned SDK plus the
android and ios workloads, so it is not testable on a Linux CI runner. Its refusals are,
and they are what matters: a refresh that runs with the wrong toolchain, or that skips a
head whose workload is missing, produces a lock file that looks refreshed and still
fails NU1004. Silence there is the whole failure mode this repo keeps re-learning.

So every case here drives the script with a STUB `dotnet` via $DOTNET and asserts it
exits 2 without touching a single lock file. No network, no restore, no real SDK.

Exit codes match the other guards in this directory:

    0  every case passed
    1  the script did not refuse when it should have, or wrote something
    2  the test could not run
"""

from __future__ import annotations

import os
import pathlib
import stat
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = REPO / ".github" / "scripts" / "refresh_lock_files.sh"


def pinned_sdk() -> str:
    import json
    with open(REPO / "global.json", encoding="utf-8") as handle:
        return json.load(handle)["sdk"]["version"]


def make_stub(directory: str, version: str, workloads: list[str]) -> str:
    """A fake `dotnet` answering only --version and `workload list`.

    Anything else — notably `restore` — exits non-zero, so if the script ever gets past
    its preflight in a test it fails loudly instead of appearing to pass.
    """
    listing = "\n".join(
        f"{name}    1.0.0/10.0.100    SDK 10.0.300" for name in workloads)
    stub = pathlib.Path(directory) / "dotnet"
    stub.write_text(
        "#!/usr/bin/env bash\n"
        'if [ "$1" = "--version" ]; then\n'
        f'  echo "{version}"\n'
        "  exit 0\n"
        "fi\n"
        'if [ "$1" = "workload" ] && [ "$2" = "list" ]; then\n'
        '  echo "Installed Workload Id      Manifest Version      Installation Source"\n'
        '  echo "-------------------------------------------------------------------"\n'
        f'  cat <<EOF\n{listing}\nEOF\n'
        "  exit 0\n"
        "fi\n"
        'echo "stub dotnet: refusing to run \'$*\'" >&2\n'
        "exit 97\n",
        encoding="utf-8")
    stub.chmod(stub.stat().st_mode | stat.S_IEXEC)
    return str(stub)


def lock_file_state() -> dict[str, bytes]:
    return {
        str(path.relative_to(REPO)): path.read_bytes()
        for path in sorted(REPO.glob("**/packages.lock.json"))
        if "obj" not in path.parts
    }


def run(stub: str) -> subprocess.CompletedProcess:
    env = dict(os.environ, DOTNET=stub)
    return subprocess.run(
        ["bash", str(SCRIPT)], env=env, cwd=str(REPO),
        capture_output=True, text=True)


CASES = [
    # (name, stub version, stub workloads, expected fragment in stderr)
    ("a wrong SDK is refused, not worked around",
     "9.9.999", ["android", "ios"], "does not report the pinned SDK"),
    ("a missing android workload is refused",
     None, ["ios"], "missing workload(s): android"),
    ("a missing ios workload is refused",
     None, ["android"], "missing workload(s): ios"),
    ("both workloads missing are named together",
     None, [], "missing workload(s): android ios"),
]


def main() -> int:
    if not SCRIPT.is_file():
        print(f"FAIL: {SCRIPT} not found")
        return 2

    version = pinned_sdk()
    before = lock_file_state()
    if not before:
        print("FAIL: no packages.lock.json found — the write check would be vacuous.")
        return 2

    failures: list[str] = []
    with tempfile.TemporaryDirectory() as tmp:
        for index, (name, stub_version, workloads, fragment) in enumerate(CASES):
            case_dir = pathlib.Path(tmp) / str(index)
            case_dir.mkdir()
            stub = make_stub(str(case_dir), stub_version or version, workloads)
            result = run(stub)

            if result.returncode != 2:
                failures.append(
                    f"{name}: expected exit 2, got {result.returncode}. "
                    f"stdout={result.stdout.strip()!r} "
                    f"stderr={result.stderr.strip()!r}")
            elif fragment not in result.stderr:
                failures.append(
                    f"{name}: exit 2 but stderr never said {fragment!r} — "
                    f"got {result.stderr.strip()!r}")
            else:
                print(f"  [ok] {name}")

            # A refusal that still wrote something is the exact failure the refusal
            # exists to prevent, so this is checked every time and not just once.
            if lock_file_state() != before:
                failures.append(f"{name}: refused but MODIFIED a lock file")

    # The stub answers nothing but --version and `workload list`, so reaching a real
    # restore would exit 97. Asserting the happy path is not attempted here keeps this
    # test honest about what it does and does not cover.
    print(f"Checked {len(CASES)} refusal case(s) against {len(before)} lock file(s); "
          "the happy path needs the real SDK and both workloads and is not covered.")

    if failures:
        print()
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    print("OK: refresh_lock_files.sh refuses every unusable toolchain without writing.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
