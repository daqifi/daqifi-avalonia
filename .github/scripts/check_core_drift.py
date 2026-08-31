#!/usr/bin/env python3
"""Report how far the pinned Daqifi.Core is behind the latest release on nuget.org.

Daqifi.Core is the package this app wraps — it decides how the app talks to
hardware — and its drift was invisible for five weeks. Dependabot did open bumps
(#93 -> 1.4.0, #95 -> 1.5.0), but its grouped PR titles never named Core, so
neither appeared in a PR list as a Core update; Dependabot then auto-closed #95
on 2026-08-17 with no replacement, and for the next two weeks nothing anywhere
said the pin was stale while 1.6.0 and 1.7.0 shipped.

This check exists because it does not depend on Dependabot at all. Dependabot can
open nothing, close its own PR, or open one that announces a bump it never made
(#130 did exactly that) — and this still reports the truth, because it compares
the committed pin against nuget.org directly. See
.github/dependency-updates/README.md and #132.

check_dependabot_claims.py is the other half: it catches a PR that lies about what
it changed. This one catches the absence of a PR entirely.

Usage:
    check_core_drift.py <project.csproj> [...]
    check_core_drift.py --glob                    # discover under cwd
    check_core_drift.py --package Some.Package --glob
    check_core_drift.py --index-url <url|path> --glob   # offline / self-test

When GITHUB_OUTPUT is set, `pinned`, `latest`, `behind_by` and `missed` are
written there for the calling workflow to build an issue body from.

Exit codes are distinct on purpose, so a caller can tell real drift apart from a
broken invocation:

    0  the pin is at (or ahead of) the latest stable release
    1  the pin is BEHIND — one or more stable releases have not been taken up
    2  the check could not run: bad arguments, no manifest pins the package, an
       unreadable manifest, or the version index could not be read or parsed
"""

from __future__ import annotations

import glob
import json
import os
import re
import sys
import urllib.error
import urllib.request

DEFAULT_PACKAGE = "Daqifi.Core"
# The flat-container index is the simplest published-versions list nuget.org has:
# a single JSON array, oldest first, no auth, no paging.
INDEX_URL = "https://api.nuget.org/v3-flatcontainer/{lower}/index.json"

PACKAGE_REF = re.compile(r"<PackageReference\b[^>]*>")
ATTR = re.compile(r"""(\w+)\s*=\s*["']([^"']*)["']""")


def pinned_versions(paths: list[str], package: str) -> list[tuple[str, str]]:
    """Every (version, manifest) pinning `package`, in the order encountered.

    EVERY reference, not one per project and not the first found. Two projects can
    pin the same package at different versions, and a single project can reference
    it more than once — conditional ItemGroups for different frameworks or RIDs are
    an ordinary MSBuild shape. Keeping only one per file would let a newer
    reference hide an older one in the same project, and the caller measures drift
    from the OLDEST pin, so a discarded version is a silently understated report.
    """
    found: list[tuple[str, str]] = []
    for path in paths:
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
        for tag in PACKAGE_REF.findall(text):
            attrs = dict(ATTR.findall(tag))
            version = attrs.get("Version")
            if attrs.get("Include") == package and version and "$(" not in version:
                found.append((version, path))
    return found


def sort_key(version: str) -> tuple[int, ...]:
    """Order release versions numerically, not as strings ('1.10.0' > '1.9.0')."""
    return tuple(int(part) for part in version.split("."))


def stable_versions(payload: dict) -> list[str]:
    """Released versions only, oldest first.

    Prereleases ('1.8.0-rc.1') and any version with a non-numeric component are
    dropped: taking one up is a deliberate act, never something to be nagged about.
    """
    keep = []
    for version in payload.get("versions") or []:
        if "-" in version or "+" in version:
            continue
        if not re.fullmatch(r"\d+(\.\d+)*", version):
            continue
        keep.append(version)
    return sorted(keep, key=sort_key)


def main(argv: list[str]) -> int:
    args = argv[1:]
    package = DEFAULT_PACKAGE
    index_url = None

    for flag, setter in (("--package", "package"), ("--index-url", "index")):
        if flag in args:
            i = args.index(flag)
            if i + 1 >= len(args):
                print(f"FAIL: {flag} needs a value.")
                return 2
            value = args[i + 1]
            del args[i:i + 2]
            if setter == "package":
                package = value
            else:
                index_url = value

    if not args:
        print(__doc__)
        return 2

    if "--glob" in args:
        if len(args) > 1:
            print("FAIL: --glob discovers the manifests itself; do not also "
                  "pass paths.")
            return 2
        paths = sorted(p for p in glob.glob("**/*.csproj", recursive=True)
                       if "obj" + os.sep not in p and "bin" + os.sep not in p)
        if not paths:
            print("FAIL: --glob matched no .csproj — nothing was checked.")
            return 2
    else:
        paths = args

    try:
        found = pinned_versions(paths, package)
    except OSError as exc:
        print(f"FAIL: cannot read a manifest: {exc}")
        return 2

    if not found:
        # 2, not 0: "no pin found" and "the pin is current" must never look alike.
        # A rename or a glob that stopped matching would otherwise read as healthy.
        print(f"FAIL: no manifest pins {package} at a literal version. "
              f"Searched: {', '.join(paths)}")
        return 2

    try:
        # Sorted, not min(): ties on version must resolve to the same manifest on
        # every run, or the reported file would depend on filesystem order.
        pinned, manifest = sorted(found, key=lambda pair: (sort_key(pair[0]),
                                                          pair[1]))[0]
    except ValueError:
        detail = "; ".join(f"{v} in {p}" for v, p in sorted(found))
        print(f"FAIL: {package} is pinned at a version that cannot be ordered "
              f"({detail}).")
        return 2

    if len({version for version, _ in found}) > 1:
        # References disagreeing about the version is its own problem, and drift
        # is measured from the oldest of them — catching up the newest would leave
        # the repo just as far behind.
        detail = "; ".join(f"{v} in {p}" for v, p in sorted(found))
        print(f"NOTE: {package} is pinned at more than one version — {detail}. "
              f"Reporting drift from the oldest ({pinned}).")

    if index_url is None:
        index_url = INDEX_URL.format(lower=package.lower())
    if "://" not in index_url:  # a bare path, for offline use
        index_url = "file://" + os.path.abspath(index_url)

    try:
        with urllib.request.urlopen(index_url, timeout=30) as response:
            payload = json.load(response)
    except (urllib.error.URLError, OSError, json.JSONDecodeError,
            ValueError) as exc:
        print(f"FAIL: cannot read the version index at {index_url}: {exc}")
        return 2

    released = stable_versions(payload)
    if not released:
        print(f"FAIL: the version index at {index_url} lists no stable "
              f"versions of {package}.")
        return 2

    latest = released[-1]
    try:
        behind = [v for v in released if sort_key(v) > sort_key(pinned)]
    except ValueError:
        print(f"FAIL: pinned version {pinned!r} is not comparable to the "
              "released versions.")
        return 2

    outputs = {
        "package": package,
        "pinned": pinned,
        "latest": latest,
        "manifest": manifest or "",
        "behind_by": str(len(behind)),
        "missed": ",".join(behind),
    }
    github_output = os.environ.get("GITHUB_OUTPUT")
    if github_output:
        with open(github_output, "a", encoding="utf-8") as handle:
            for key, value in outputs.items():
                handle.write(f"{key}={value}\n")

    print(f"{package}: pinned {pinned} ({manifest}), latest stable {latest}")

    if not behind:
        if sort_key(pinned) > sort_key(latest):
            # Pinned ahead of anything published — an unlisted or yanked release,
            # or a pin landed before the push. Not drift, but say so rather than
            # printing a bare "up to date" that hides it.
            print(f"OK: pinned {pinned} is AHEAD of the latest published "
                  f"stable {latest} — nothing to take up.")
        else:
            print("OK: the pin is at the latest stable release.")
        return 0

    print()
    print(f"BEHIND by {len(behind)} release(s): {', '.join(behind)}")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
