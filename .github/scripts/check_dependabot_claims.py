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
touches files a body never mentions (lock files, transitive pins).

Usage:
    check_dependabot_claims.py --body-file <file> <project.csproj> [...]
    check_dependabot_claims.py --body-file <file> --glob   # discover under cwd

Exit codes are distinct on purpose, so a caller can tell a real violation apart
from a broken invocation:

    0  every claim in the body is reflected in a manifest
    1  a genuine violation: a claimed bump was not applied
    2  the check could not run: bad arguments, an unreadable body or manifest, no
       manifests, or a body with no parseable claims (which means either this is
       not a Dependabot PR or the parser below has gone stale — both are reasons
       to stop, not to report success)
"""

from __future__ import annotations

import glob
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


def pinned_versions(manifest_path: str) -> dict[str, str]:
    """Map package name -> pinned version for one project file."""
    with open(manifest_path, encoding="utf-8") as handle:
        text = handle.read()

    pins: dict[str, str] = {}
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
        pins[name] = version
    return pins


def main(argv: list[str]) -> int:
    if "--body-file" not in argv:
        print(__doc__)
        return 2

    body_index = argv.index("--body-file")
    if body_index + 1 >= len(argv):
        print("FAIL: --body-file needs a path.")
        return 2
    body_path = argv[body_index + 1]
    rest = argv[1:body_index] + argv[body_index + 2:]

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
        paths = sorted(p for p in glob.glob("**/*.csproj", recursive=True)
                       if "obj" + os.sep not in p and "bin" + os.sep not in p)
        if not paths:
            print("FAIL: --glob matched no .csproj — nothing was checked.")
            return 2
    elif rest:
        paths = rest
    else:
        print("FAIL: no manifests given. Pass .csproj paths or --glob.")
        return 2

    # package -> version -> [manifests]
    pins: dict[str, dict[str, list[str]]] = {}
    for path in paths:
        try:
            found = pinned_versions(path)
        except OSError as exc:
            # 2, not 1: an unreadable manifest is a tooling problem, and a caller
            # must be able to tell that apart from "the PR really did lie".
            print(f"FAIL: cannot read {path}: {exc}")
            return 2
        for name, version in found.items():
            pins.setdefault(name, {}).setdefault(version, []).append(path)

    if not pins:
        print("FAIL: no PackageReference with a literal version in any manifest.")
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
    for name, old, new in stated:
        versions = pins.get(name)
        if versions is None:
            failures.append(
                f"{name}: the body claims {old} -> {new}, but no manifest pins "
                f"{name} at all. Either the package was removed or the claim is "
                "about a project outside this check's inputs.")
            continue
        if new in versions:
            print(f"  [ok] {name} {old} -> {new}")
            continue
        where = "; ".join(
            f"{ver} in {', '.join(sorted(paths_))}"
            for ver, paths_ in sorted(versions.items()))
        failures.append(
            f"{name}: the body claims {old} -> {new}, but the manifests still "
            f"pin {where}. The PR announces a bump it did not make — do not "
            "read its description as evidence the dependency moved.")

    if failures:
        print()
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    print("\nOK: every bump the description claims is applied in a manifest.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
