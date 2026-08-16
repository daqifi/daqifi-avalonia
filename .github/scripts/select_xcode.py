#!/usr/bin/env python3
"""Pick the installed Xcode that .NET for iOS will accept, and print its Developer dir.

.NET for iOS validates the host Xcode for EXACT major.minor equality, not a floor.
From `Xamarin.Shared.Sdk.targets`, target `_ValidateXcodeVersion`:

    <_IsMatchingXcode Condition="'$(_RecommendedXcodeVersionMajorMinorOnly)' ==
                                 '$(_XcodeVersionMajorMinorOnly)'">true</_IsMatchingXcode>

So a NEWER Xcode fails exactly like an older one, and upgrading Xcode does not fix a
mismatch — it causes one (daqifi-avalonia #103). The two ways out are to disable the
check with `-p:ValidateXcodeVersion=false`, which switches off a real compatibility gate,
or to select an Xcode that matches. On a GitHub macOS runner the second is nearly free:
the image ships every minor of its supported Xcode major, so the wanted one is usually
already sitting in /Applications.

The required version is NOT hardcoded anywhere. The caller reads it out of the SDK
(`dotnet msbuild <ios csproj> -getProperty:_RecommendedXcodeVersion`) and passes it here,
because it moves with the pinned SDK: the 10.0.2xx band wanted 26.4 and the currently
pinned 10.0.302 wants a different one — visible in the committed lock file, whose target
framework reads `net10.0-ios26.5`.

Usage:
    select_xcode.py <required-version> [--applications-dir DIR]

Prints the chosen `.../Contents/Developer` path on stdout and nothing else, so a caller
can do:

    sudo xcode-select -s "$(select_xcode.py "$required")"

Diagnostics go to stderr. Exit codes are distinct on purpose, so a caller can tell a real
toolchain mismatch apart from a broken invocation:

    0  a matching Xcode was found; its Developer dir is on stdout
    1  Xcodes are installed but NONE matches the required major.minor — a genuine
       toolchain mismatch, which is the case this exists to catch
    2  the check could not run: bad or missing arguments, an unparseable required
       version, a missing applications directory, no Xcode bundles at all, or no
       bundle whose version could be read
"""

from __future__ import annotations

import argparse
import glob
import os
import plistlib
import sys

DEFAULT_APPLICATIONS_DIR = "/Applications"

# Matches both the runner image's versioned bundles (Xcode_26.5.app) and a plain
# Xcode.app, which is what a developer machine and the image's default alias look like.
BUNDLE_GLOB = "Xcode*.app"


def log(message: str) -> None:
    print(message, file=sys.stderr)


def parse_version(text: str) -> tuple[int, ...] | None:
    """'26.4.1' -> (26, 4, 1). None if it is not a dotted numeric version."""
    parts = text.strip().split(".")
    if not parts or not all(part.isdigit() for part in parts):
        return None
    return tuple(int(part) for part in parts)


def bundle_version(app_path: str) -> tuple[int, ...] | None:
    """Read CFBundleShortVersionString from an Xcode bundle's Contents/version.plist.

    version.plist rather than Info.plist: it is the file `xcodebuild -version` and the
    .NET iOS SDK's own Xcode probing both read, and it is present in every Xcode bundle
    including the ones shipped as aliases on the runner image.
    """
    plist_path = os.path.join(app_path, "Contents", "version.plist")
    try:
        with open(plist_path, "rb") as handle:
            plist = plistlib.load(handle)
    except (OSError, plistlib.InvalidFileException) as exc:
        log(f"  skipping {app_path}: cannot read Contents/version.plist ({exc})")
        return None

    raw = plist.get("CFBundleShortVersionString")
    if not isinstance(raw, str):
        log(f"  skipping {app_path}: version.plist has no CFBundleShortVersionString")
        return None

    version = parse_version(raw)
    if version is None:
        log(f"  skipping {app_path}: unparseable version {raw!r}")
        return None
    return version


def format_version(version: tuple[int, ...]) -> str:
    return ".".join(str(part) for part in version)


def discover(applications_dir: str) -> dict[str, tuple[int, ...]]:
    """Map developer-dir realpath -> version, one entry per distinct Xcode.

    Keyed on the REALPATH of Contents/Developer because the runner image publishes
    aliases alongside the real bundles — /Applications/Xcode_26.4.app is a link to
    Xcode_26.4.1.app, and /Applications/Xcode.app to whichever is default — so a
    path-keyed sweep would report the same Xcode two or three times.
    """
    found: dict[str, tuple[int, ...]] = {}
    for app_path in sorted(glob.glob(os.path.join(applications_dir, BUNDLE_GLOB))):
        developer = os.path.join(app_path, "Contents", "Developer")
        if not os.path.isdir(developer):
            log(f"  skipping {app_path}: no Contents/Developer")
            continue
        version = bundle_version(app_path)
        if version is None:
            continue
        found.setdefault(os.path.realpath(developer), version)
    return found


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="select_xcode.py",
        description="Print the Developer dir of the installed Xcode matching a version.",
        add_help=True)
    parser.add_argument(
        "required",
        help="the version .NET for iOS wants, e.g. 26.5; only major.minor is compared, "
             "because that is all _ValidateXcodeVersion compares")
    parser.add_argument(
        "--applications-dir", default=DEFAULT_APPLICATIONS_DIR,
        help=f"where to look for Xcode bundles (default: {DEFAULT_APPLICATIONS_DIR})")
    # argparse exits 2 on a usage error, which is already the code this contract wants.
    args = parser.parse_args(argv[1:])

    required = parse_version(args.required)
    if required is None or len(required) < 2:
        log(f"FAIL: {args.required!r} is not a major.minor version.")
        return 2
    wanted = required[:2]

    if not os.path.isdir(args.applications_dir):
        log(f"FAIL: {args.applications_dir} does not exist — this does not look like a "
            "macOS host with Xcode installed.")
        return 2

    log(f"Looking for Xcode {format_version(wanted)}.x under {args.applications_dir}")
    installed = discover(args.applications_dir)
    if not installed:
        log(f"FAIL: no readable Xcode bundle under {args.applications_dir}. Nothing to "
            "select, so this is an environment problem rather than a version mismatch.")
        return 2

    for developer, version in sorted(installed.items(), key=lambda kv: kv[1]):
        log(f"  found Xcode {format_version(version)} at {developer}")

    matches = {developer: version
               for developer, version in installed.items()
               if version[:2] == wanted}
    if not matches:
        available = ", ".join(
            format_version(version) for version in sorted(installed.values()))
        log(f"FAIL: no installed Xcode is {format_version(wanted)}.x — available: "
            f"{available}. .NET for iOS compares major.minor for EQUALITY, so a newer "
            "Xcode does NOT satisfy this.")
        log("")
        log("The usual cause on CI is the runner image moving to a new macOS major, "
            "which carries only its own Xcode major. Ways out, best first:")
        log("  1. pin `runs-on:` to the previous image (e.g. macos-26) until the SDK "
            "catches up — keeps the compatibility check armed;")
        log("  2. move global.json to an SDK whose iOS workload wants an Xcode this "
            "image has, and refresh the lock files;")
        log("  3. last resort, build with -p:ValidateXcodeVersion=false, accepting that "
            "the next real toolchain mismatch will be silent.")
        log("See daqifi-avalonia #103 for why 3 is not the default.")
        return 1

    # Highest patch within the matching minor, then path, so the choice is deterministic
    # when the image ships e.g. both 26.4.0 and 26.4.1.
    developer, version = max(matches.items(), key=lambda kv: (kv[1], kv[0]))
    log(f"Selected Xcode {format_version(version)}")
    print(developer)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
