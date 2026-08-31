#!/usr/bin/env python3
"""Assert a Dependabot PR actually made every bump its description claims.

A Dependabot PR body is a list of claims — "Updated X from A to B." — and nothing
verifies those claims against the diff. They can be wrong. PR #130 announced
`Daqifi.Core` 1.3.0 -> 1.7.0 in its body and never touched the version in the
csproj; ten of its fourteen announced updates were missing from the diff. The PR
was titled "Bump the minor-and-patch group with 14 updates", so the only way to
notice was to read the diff package by package against the body.

That is the failure this guards: a bump that is announced, believed, and never
made. It is worse than no PR at all, because the PR is the thing that would
otherwise have told you the dependency was stale — a reviewer who skims the body
comes away believing Core moved. Core sat at 1.3.0 for five weeks partly on the
strength of exactly that. See .github/dependency-updates/README.md and #132.

The check is deliberately one-directional. It asserts every CLAIM was applied; it
does not assert every applied change was claimed, because Dependabot legitimately
touches things a body never mentions.

A claim is checked against EVERY .csproj that pins the package, not just one:
`Avalonia.Fonts.Inter` is pinned by three projects here, and a grouped
multi-directory PR that moved one and left another behind would otherwise pass on
the strength of the one it did move.

"Every .csproj" means every one Dependabot actually manages — the `directories`
list in .github/dependabot.yml. Scoping to that matters in both directions. The
iOS head is not in the list, so its pins drift on purpose and must not fail a PR
that could never have touched them; and if the list later grows, this check grows
with it rather than silently keeping the old scope.

Lock files are evidence ONLY for a package no in-scope .csproj pins — a
transitive-only update, the shape a security bump takes, which moves a lock file
and nothing else. They are never used to fail a claim, because a legitimate
single-directory PR leaves the OTHER directories' lock files recording the old
transitive version (#93 changed only Daqifi.Avalonia/), and failing on that would
reject good PRs. Lock files could not hide the #130 case anyway: there the lock
file recorded `Daqifi.Core` at 1.3.0 right alongside the csproj.

Usage:
    check_dependabot_claims.py --body-file <file> <manifest> [...]
    check_dependabot_claims.py --body-file <file> --glob   # discover under cwd

A manifest is a .csproj or a packages.lock.json. Explicit paths are taken as
already in scope; --glob discovers them and applies the dependabot.yml scope.

Exit codes are distinct on purpose, so a caller can tell a real violation apart
from a broken invocation:

    0  every claim in the body is reflected in a manifest or lock file
    1  a genuine violation: a claimed bump was not applied
    2  the check could not run: bad arguments, an unreadable body or manifest, no
       manifests, no readable dependabot.yml scope, or a body with no parseable
       claims (which means either this is not a Dependabot PR or the parser below
       has gone stale — both are reasons to stop, not to report success)
"""

from __future__ import annotations

import glob
import json
import os
import re
import sys

# The three shapes Dependabot uses to state a single-package bump. All appear at
# the start of a line, outside any <details> block:
#
#   Updated [Daqifi.Core](https://github.com/daqifi/daqifi-core) from 1.3.0 to 1.7.0.
#   Bumps [Sentry](https://github.com/getsentry/sentry-dotnet) from 6.7.0 to 6.8.0.
#   Updates `Daqifi.Core` from 1.3.0 to 1.7.0
#
# The trailing period is optional and is not part of the version — hence the
# non-greedy version capture with the period pulled out of it. Lines like
# "Bumps the minor-and-patch group with 2 updates in the /X directory: ..." open a
# multi-directory body and carry no from/to, so they simply do not match.
CLAIM = re.compile(
    r"^(?:Updated|Updates|Bumps)\s+"
    r"(?:\[(?P<linked>[^\]]+)\]\([^)]*\)|`(?P<quoted>[^`]+)`)\s+"
    r"from\s+(?P<old>\S+)\s+to\s+(?P<new>\S+?)\.?\s*$"
)

# <PackageReference Include="X" Version="Y" /> in either attribute order, and
# whether or not the element has children (Microsoft.EntityFrameworkCore.Tools
# carries PrivateAssets/IncludeAssets, so its tag does not self-close).
PACKAGE_REF = re.compile(r"<PackageReference\b[^>]*>")
ATTR = re.compile(r"""(\w+)\s*=\s*["']([^"']*)["']""")

DEFAULT_CONFIG = os.path.join(".github", "dependabot.yml")
DIRECTORIES_KEY = re.compile(r"^directories\s*:")
DIRECTORY_KEY = re.compile(r"""^directory\s*:\s*["']?([^"'#]+)""")
LIST_ITEM = re.compile(r"""^-\s*["']?([^"'#]+)""")


def managed_directories(config_path: str) -> list[str]:
    """Repo-relative directories Dependabot is configured to update.

    A deliberately small parser rather than a YAML dependency: this reads one
    well-known key out of one file we control, and PyYAML is not guaranteed on
    every runner. '/' (the whole repo) normalises to '', which matches everything.
    """
    directories: list[str] = []
    in_list = False
    with open(config_path, encoding="utf-8") as handle:
        for raw in handle:
            stripped = raw.strip()
            if not stripped or stripped.startswith("#"):
                continue
            if DIRECTORIES_KEY.match(stripped):
                in_list = True
                continue
            singular = DIRECTORY_KEY.match(stripped)
            if singular:
                directories.append(singular.group(1).strip())
                in_list = False
                continue
            if in_list:
                item = LIST_ITEM.match(stripped)
                if item:
                    directories.append(item.group(1).strip())
                    continue
                in_list = False
    return [d.strip().strip("/") for d in directories]


def in_scope(path: str, directories: list[str]) -> bool:
    """Is this manifest inside a directory Dependabot manages?"""
    normalised = os.path.normpath(path).replace(os.sep, "/")
    for directory in directories:
        if not directory:  # '/' — the whole repository
            return True
        if normalised == directory or normalised.startswith(directory + "/"):
            return True
    return False


def claims(body: str) -> list[tuple[str, str, str]]:
    """Extract (package, old, new) from a PR body, ignoring <details> blocks.

    Release notes and changelogs are embedded in <details>, and they routinely
    contain their own "Updates `foo` from ..." lines. Counting those would invent
    claims the PR never made, so only depth-0 lines are read.
    """
    found: list[tuple[str, str, str]] = []
    depth = 0
    for line in body.splitlines():
        lowered = line.lower()
        opens = lowered.count("<details")
        closes = lowered.count("</details")
        if depth == 0 and opens == 0:
            match = CLAIM.match(line.strip())
            if match:
                name = match.group("linked") or match.group("quoted")
                found.append((name.strip(), match.group("old"),
                              match.group("new")))
        depth = max(0, depth + opens - closes)
    return found


def csproj_versions(text: str) -> dict[str, set[str]]:
    """Map package name -> pinned versions declared in one project file."""
    pins: dict[str, set[str]] = {}
    for tag in PACKAGE_REF.findall(text):
        attrs = dict(ATTR.findall(tag))
        name = attrs.get("Include")
        version = attrs.get("Version")
        if not name or not version:
            continue
        # Property-valued pins (third_party/oxyplot-avalonia uses
        # Version="$(AvaloniaVersion)") carry no literal to compare against, and
        # those projects are out of Dependabot's scope anyway.
        if "$(" in version:
            continue
        pins.setdefault(name, set()).add(version)
    return pins


def lockfile_versions(text: str) -> dict[str, set[str]]:
    """Map package name -> resolved versions recorded in a packages.lock.json.

    One package can appear under several target frameworks at different resolved
    versions, so every one is kept — a claim is satisfied by any of them.
    """
    payload = json.loads(text)
    pins: dict[str, set[str]] = {}
    for entry in (payload.get("dependencies") or {}).values():
        if not isinstance(entry, dict):
            continue
        for name, detail in entry.items():
            if not isinstance(detail, dict):
                continue
            resolved = detail.get("resolved")
            if resolved:
                pins.setdefault(name, set()).add(resolved)
    return pins


def manifest_versions(manifest_path: str) -> dict[str, set[str]]:
    """Map package name -> versions recorded by one .csproj or lock file."""
    with open(manifest_path, encoding="utf-8") as handle:
        text = handle.read()
    if os.path.basename(manifest_path) == "packages.lock.json":
        return lockfile_versions(text)
    return csproj_versions(text)


def main(argv: list[str]) -> int:
    if "--body-file" not in argv:
        print(__doc__)
        return 2

    args = argv[1:]
    config_path = DEFAULT_CONFIG
    if "--dependabot-config" in args:
        i = args.index("--dependabot-config")
        if i + 1 >= len(args):
            print("FAIL: --dependabot-config needs a path.")
            return 2
        config_path = args[i + 1]
        del args[i:i + 2]

    body_index = args.index("--body-file")
    if body_index + 1 >= len(args):
        print("FAIL: --body-file needs a path.")
        return 2
    body_path = args[body_index + 1]
    rest = args[:body_index] + args[body_index + 2:]

    try:
        with open(body_path, encoding="utf-8") as handle:
            body = handle.read()
    except OSError as exc:
        print(f"FAIL: cannot read body file {body_path}: {exc}")
        return 2

    if "--glob" in rest:
        if len(rest) > 1:
            print("FAIL: --glob discovers the manifests itself; do not also "
                  "pass paths.")
            return 2
        try:
            directories = managed_directories(config_path)
        except OSError as exc:
            print(f"FAIL: cannot read {config_path}: {exc}")
            return 2
        if not directories:
            # Without a scope this would check the whole repo, including heads
            # Dependabot cannot touch — turning every Avalonia bump into a false
            # failure on the unmanaged iOS head.
            print(f"FAIL: {config_path} lists no `directories` to scope the "
                  "check to.")
            return 2
        paths = sorted(
            p for pattern in ("**/*.csproj", "**/packages.lock.json")
            for p in glob.glob(pattern, recursive=True)
            if "obj" + os.sep not in p and "bin" + os.sep not in p
            and in_scope(p, directories))
        if not paths:
            print(f"FAIL: --glob matched no .csproj or packages.lock.json in "
                  f"the directories {config_path} manages "
                  f"({', '.join(directories)}) — nothing was checked.")
            return 2
        print(f"Scope from {config_path}: {', '.join(directories)}")
    elif rest:
        paths = rest
    else:
        print("FAIL: no manifests given. Pass .csproj / packages.lock.json "
              "paths, or --glob.")
        return 2

    # package -> version -> [manifests]. Project pins and lock-file resolutions
    # are kept apart because they answer different questions: a project pin is
    # what the PR was supposed to change, a lock resolution is only evidence that
    # SOMETHING moved.
    pins: dict[str, dict[str, list[str]]] = {}
    resolved: dict[str, set[str]] = {}
    for path in paths:
        try:
            found = manifest_versions(path)
        except (OSError, json.JSONDecodeError) as exc:
            # 2, not 1: an unreadable manifest is a tooling problem, and a caller
            # must be able to tell that apart from "the PR really did lie".
            print(f"FAIL: cannot read {path}: {exc}")
            return 2
        is_lock = os.path.basename(path) == "packages.lock.json"
        for name, versions in found.items():
            for version in versions:
                if is_lock:
                    resolved.setdefault(name, set()).add(version)
                else:
                    pins.setdefault(name, {}).setdefault(version, []).append(path)

    if not pins and not resolved:
        print("FAIL: no package version found in any manifest or lock file.")
        return 2

    stated = claims(body)
    if not stated:
        # A Dependabot body always states at least one bump. Zero means the body
        # is not one, or CLAIM above no longer matches Dependabot's wording — and
        # a parser that silently matches nothing reads as a passing check while
        # verifying nothing at all.
        print("FAIL: no 'Updated X from A to B' claims found in the body. "
              "Either this is not a Dependabot PR description, or the claim "
              "patterns in this script have gone stale against Dependabot's "
              "current wording.")
        return 2

    failures: list[str] = []
    print(f"Checked {len(paths)} manifest(s) against {len(stated)} claim(s):")
    for name, old_version, new_version in stated:
        declared = pins.get(name)

        if declared is None:
            # No in-scope project pins it. Either it is transitive — where the
            # lock file is the only place the bump can show — or the claim is
            # about something this repo does not have.
            if new_version in resolved.get(name, set()):
                print(f"  [ok] {name} {old_version} -> {new_version} "
                      "(transitive; recorded in a lock file)")
                continue
            failures.append(
                f"{name}: the body claims {old_version} -> {new_version}, but no "
                f"in-scope project pins {name} and no lock file resolves it to "
                f"{new_version}. Either the package was removed or the claim is "
                "about a project outside this check's scope.")
            continue

        # Every project that pins it must be at the claimed version. Checking
        # only that SOME project reached it lets a grouped multi-directory PR
        # move one head and leave another stale — `Avalonia.Fonts.Inter` is
        # pinned by three projects here, so that is a real input shape.
        stale = {version: files for version, files in declared.items()
                 if version != new_version}
        if not stale:
            print(f"  [ok] {name} {old_version} -> {new_version}")
            continue
        where = "; ".join(
            f"{version} in {', '.join(sorted(files))}"
            for version, files in sorted(stale.items()))
        failures.append(
            f"{name}: the body claims {old_version} -> {new_version}, but "
            f"{where}. The PR announces a bump it did not make everywhere it "
            "said — do not read its description as evidence the dependency "
            "moved.")

    if failures:
        print()
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    print("\nOK: every bump the description claims is applied.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
