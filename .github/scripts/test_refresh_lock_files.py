#!/usr/bin/env python3
"""Self-test for refresh_lock_files.sh — its REFUSALS and its ROLLBACK.

The script's happy path regenerates six lock files and needs the pinned SDK plus the
android and ios workloads, so it is not testable on a Linux CI runner. What is testable
is everything it promises to do when things go wrong, which is the part that matters: a
refresh that half-finishes produces a lock-file set that looks refreshed, reads as an
ordinary diff, and still fails NU1004 on the head it never reached.

Two groups, both driven by a stub `dotnet` handed to the script through $DOTNET, so
neither needs a real SDK, a workload, or the network:

1. PREFLIGHT — a wrong SDK or a missing workload exits 2 having written nothing.
2. ROLLBACK — a restore that fails partway exits 1 and leaves every lock file
   byte-identical to how it started. This group includes a CONTROL case whose stub
   never fails, because a rollback test whose stub never wrote anything would pass
   without proving a thing.

Exit codes match the other guards in this directory:

    0  every case passed
    1  the script misbehaved — did not refuse, did not roll back, or wrote something
    2  the test could not run
"""

from __future__ import annotations

import json
import os
import pathlib
import stat
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = REPO / ".github" / "scripts" / "refresh_lock_files.sh"

PREAMBLE = """#!/usr/bin/env bash
if [ "$1" = "--version" ]; then
  echo "{version}"
  exit 0
fi
if [ "$1" = "workload" ] && [ "$2" = "list" ]; then
  echo "Installed Workload Id      Manifest Version      Installation Source"
  echo "-------------------------------------------------------------------"
  cat <<'EOF'
{listing}
EOF
  exit 0
fi
"""


def pinned_sdk() -> str:
    with open(REPO / "global.json", encoding="utf-8") as handle:
        return json.load(handle)["sdk"]["version"]


def _write_stub(path: pathlib.Path, body: str) -> str:
    path.write_text(body, encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)
    return str(path)


def preflight_stub(directory: str, version: str, workloads: list[str]) -> str:
    """A fake `dotnet` answering only --version and `workload list`.

    Anything else — notably `restore` — exits 97, so a test that accidentally gets past
    the preflight fails loudly instead of appearing to pass.
    """
    listing = "\n".join(
        f"{name}    1.0.0/10.0.100    SDK 10.0.300" for name in workloads)
    body = PREAMBLE.format(version=version, listing=listing) + (
        'echo "stub dotnet: refusing to run \'$*\'" >&2\n'
        "exit 97\n")
    return _write_stub(pathlib.Path(directory) / "dotnet", body)


def restore_stub(directory: str, version: str, fail_at: int | None) -> str:
    """A fake `dotnet` that passes preflight and MUTATES each lock file it restores.

    The mutation is the point: without it the rollback assertion would hold trivially.
    `fail_at` is the 1-based restore that exits non-zero; None means every restore
    succeeds, which is the control.
    """
    counter = pathlib.Path(directory) / "counter"
    listing = "\n".join(
        f"{name}    1.0.0/10.0.100    SDK 10.0.300" for name in ("android", "ios"))
    fail_clause = ""
    if fail_at is not None:
        fail_clause = (
            f'  if [ "$n" -ge {fail_at} ]; then\n'
            '    echo "error NU1605: stub restore failure" >&2\n'
            "    exit 1\n"
            "  fi\n")
    body = PREAMBLE.format(version=version, listing=listing) + (
        'if [ "$1" = "restore" ]; then\n'
        '  lock="$(dirname "$2")/packages.lock.json"\n'
        f'  n=$(( $(cat "{counter}" 2>/dev/null || echo 0) + 1 ))\n'
        f'  echo "$n" > "{counter}"\n'
        '  printf "\\n// stub restore %s\\n" "$n" >> "$lock"\n'
        + fail_clause +
        "  exit 0\n"
        "fi\n"
        'echo "stub dotnet: refusing to run \'$*\'" >&2\n'
        "exit 97\n")
    return _write_stub(pathlib.Path(directory) / "dotnet", body)


def lock_files() -> list[pathlib.Path]:
    return sorted(
        path for path in REPO.glob("**/packages.lock.json")
        if "obj" not in path.parts)


def lock_state() -> dict[str, bytes]:
    return {str(p.relative_to(REPO)): p.read_bytes() for p in lock_files()}


def restore_state(state: dict[str, bytes]) -> None:
    for relative, blob in state.items():
        (REPO / relative).write_bytes(blob)


def run(stub: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["bash", str(SCRIPT)], env=dict(os.environ, DOTNET=stub),
        cwd=str(REPO), capture_output=True, text=True)


PREFLIGHT_CASES = [
    # (name, stub version override, workloads, expected stderr fragment)
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
    before = lock_state()
    if len(before) < 2:
        print("FAIL: fewer than two packages.lock.json found — the write and rollback "
              "checks would be vacuous.")
        return 2

    failures: list[str] = []
    try:
        with tempfile.TemporaryDirectory() as tmp:
            for index, (name, override, workloads, fragment) in enumerate(
                    PREFLIGHT_CASES):
                case = pathlib.Path(tmp) / f"pre{index}"
                case.mkdir()
                stub = preflight_stub(str(case), override or version, workloads)
                result = run(stub)

                if result.returncode != 2:
                    failures.append(
                        f"{name}: expected exit 2, got {result.returncode}. "
                        f"stderr={result.stderr.strip()!r}")
                elif fragment not in result.stderr:
                    failures.append(
                        f"{name}: exit 2 but stderr never said {fragment!r} — "
                        f"got {result.stderr.strip()!r}")
                else:
                    print(f"  [ok] {name}")

                if lock_state() != before:
                    failures.append(f"{name}: refused but MODIFIED a lock file")
                    restore_state(before)

            # CONTROL. Establishes that the stub's writes really do land, so the
            # rollback case below cannot pass by never having written anything.
            case = pathlib.Path(tmp) / "control"
            case.mkdir()
            result = run(restore_stub(str(case), version, fail_at=None))
            if result.returncode != 0:
                failures.append(
                    "control: a stub that never fails should exit 0, got "
                    f"{result.returncode}. stderr={result.stderr.strip()!r}")
            if lock_state() == before:
                failures.append(
                    "control: the stub restore wrote nothing, so the rollback case "
                    "below would prove nothing. The stub or the script's project list "
                    "has changed.")
            else:
                print("  [ok] control: a successful stub run does modify lock files")
            restore_state(before)

            # ROLLBACK. Third restore fails, so restores one and two have already
            # rewritten their lock files by the time it does.
            case = pathlib.Path(tmp) / "rollback"
            case.mkdir()
            result = run(restore_stub(str(case), version, fail_at=3))
            if result.returncode != 1:
                failures.append(
                    "rollback: a mid-sequence restore failure should exit 1, got "
                    f"{result.returncode}. stderr={result.stderr.strip()!r}")
            elif "rolled back" not in result.stderr:
                failures.append(
                    "rollback: exit 1 but stderr never said the files were rolled "
                    f"back — got {result.stderr.strip()!r}")
            else:
                print("  [ok] a restore failing partway exits 1 and says so")

            after = lock_state()
            if after != before:
                changed = sorted(k for k in after if after[k] != before.get(k))
                failures.append(
                    "rollback: lock files were left modified after a failed restore — "
                    f"{changed}")
            else:
                print("  [ok] every lock file is byte-identical after the failure")
    finally:
        # Whatever happened above, never leave the working tree holding stub output.
        restore_state(before)

    print(f"Checked {len(PREFLIGHT_CASES)} refusal case(s) and 2 rollback case(s) "
          f"against {len(before)} lock file(s); the happy path needs the real SDK and "
          "both workloads and is not covered.")

    if failures:
        print()
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    print("OK: refresh_lock_files.sh refuses an unusable toolchain and rolls back a "
          "failed restore, writing nothing either way.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
