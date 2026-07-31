#!/usr/bin/env python3
"""Assert the Avalonia dependency graph has not split across projects.

A SPLIT graph is the failure mode this guards: two heads resolving different
versions of the same Avalonia assembly. That does not fail the build — it fails
at RUNTIME, typically as a MissingMethodException the moment the mismatched type
is first touched. The port has already been bitten by exactly this shape once
(Projektanker.Icons.Avalonia under Avalonia 12:
`MissingMethodException: TemplateBinding.ProvideValue`), which is why it is a
gate rather than a convention.

Two checks run over every project.assets.json handed in:

  1. LOCKSTEP — every Avalonia-published package resolves to the same version as
     the root `Avalonia` package, except the satellites listed in SATELLITES
     below, each of which ships on its own cadence and is exempt for a stated
     reason.

  2. CONSISTENCY — every Avalonia-prefixed package, satellites INCLUDED, resolves
     to exactly one version across all projects. A satellite being exempt from
     the core version does not license it to differ between the Desktop and
     Android heads; that is the same split, just one package down.

Usage:
    check_avalonia_versions.py <project.assets.json> [...]
    check_avalonia_versions.py --glob            # discover under the repo root

Exits 0 when both checks pass, 1 on any violation, 2 on bad input.
"""

from __future__ import annotations

import glob
import json
import sys
from collections import defaultdict

# Packages published by the Avalonia project that legitimately do NOT track the
# core runtime version. Each entry must say why, and be re-justified rather than
# extended casually — every addition here is a hole in check 1.
SATELLITES: dict[str, str] = {
    # Build-time MSBuild helper: ships build/ and tools/ only, no lib/, so it
    # contributes no runtime assembly that could mismatch. Avalonia 12.0.5 itself
    # depends on 11.3.2, so requiring lockstep here can never pass.
    "Avalonia.BuildServices":
        "build-time only (no lib/); Avalonia 12.0.5 itself depends on 11.3.2",
    # Released on its own cadence — the 12.0 line stops at 12.0.1, so there is no
    # 12.0.5 to move to. Re-check on any Avalonia minor bump: 12.1.0 exists.
    "Avalonia.Controls.DataGrid":
        "independent cadence; 12.0.x line ends at 12.0.1 (no 12.0.5 published)",
    # Native ANGLE binaries, versioned off the upstream ANGLE build
    # (e.g. 2.1.27548.20260419) — not comparable to an Avalonia version at all.
    "Avalonia.Angle.Windows.Natives":
        "native ANGLE build, versioned upstream (not an Avalonia version)",
}

ROOT_PACKAGE = "Avalonia"


def avalonia_packages(assets_path: str) -> dict[str, str]:
    """Map package name -> resolved version for Avalonia-prefixed libraries."""
    with open(assets_path, encoding="utf-8") as handle:
        assets = json.load(handle)

    found: dict[str, str] = {}
    for key, entry in (assets.get("libraries") or {}).items():
        # Keys are "Name/Version"; project references appear here too, with
        # type "project" and no meaningful version.
        if entry.get("type") != "package":
            continue
        name, _, version = key.rpartition("/")
        if name == ROOT_PACKAGE or name.startswith(ROOT_PACKAGE + "."):
            found[name] = version
    return found


def main(argv: list[str]) -> int:
    if len(argv) == 1:
        print(__doc__)
        return 2

    if argv[1] == "--glob":
        paths = sorted(
            glob.glob("**/obj/project.assets.json", recursive=True))
        # A silent empty sweep would report success without checking anything.
        if not paths:
            print("FAIL: --glob matched no project.assets.json — nothing was "
                  "restored, so nothing was checked.")
            return 1
    else:
        paths = argv[1:]

    # package -> version -> [projects]
    seen: dict[str, dict[str, list[str]]] = defaultdict(lambda: defaultdict(list))
    core_versions: dict[str, str] = {}
    failures: list[str] = []

    for path in paths:
        try:
            packages = avalonia_packages(path)
        except (OSError, json.JSONDecodeError) as exc:
            print(f"FAIL: cannot read {path}: {exc}")
            return 1

        if not packages:
            continue

        root = packages.get(ROOT_PACKAGE)
        if root is None:
            failures.append(
                f"{path}: resolves Avalonia packages but not `{ROOT_PACKAGE}` "
                "itself — cannot establish the core version")
            continue
        core_versions[path] = root

        for name, version in sorted(packages.items()):
            seen[name][version].append(path)
            if name in SATELLITES or name == ROOT_PACKAGE:
                continue
            if version != root:
                failures.append(
                    f"{path}: {name} {version} != core {ROOT_PACKAGE} {root}. "
                    "Bump it in lockstep, or add it to SATELLITES with a reason "
                    "if it genuinely ships on its own cadence.")

    if not core_versions:
        print("FAIL: no project.assets.json referenced any Avalonia package.")
        return 1

    # Check 2 — one version per package across every project.
    for name in sorted(seen):
        versions = seen[name]
        if len(versions) > 1:
            detail = "; ".join(
                f"{ver} in {', '.join(sorted(projects))}"
                for ver, projects in sorted(versions.items()))
            failures.append(
                f"SPLIT GRAPH: {name} resolves to multiple versions — {detail}. "
                "Every head must load the same assembly version.")

    distinct_cores = sorted(set(core_versions.values()))
    print(f"Checked {len(core_versions)} project(s); "
          f"core {ROOT_PACKAGE} = {', '.join(distinct_cores)}")
    for name, reason in sorted(SATELLITES.items()):
        if name in seen:
            versions = ", ".join(sorted(seen[name]))
            print(f"  satellite (exempt): {name} {versions} — {reason}")

    if failures:
        print()
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    print("OK: Avalonia graph is in lockstep and consistent across projects.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
