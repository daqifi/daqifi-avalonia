#!/usr/bin/env python3
"""Self-test for check_dependabot_claims.py.

The guard's whole value is that it FAILS on a PR that announces a bump it never
made. A guard that only ever passes is worse than none, because it reads as
coverage — so the failure paths are what this exercises, not just the happy one.

The headline case is REAL: PR #130's body announced `Daqifi.Core` 1.3.0 -> 1.7.0
while its csproj still said 1.3.0. That shape is reproduced verbatim below.

Run directly (`python3 test_check_dependabot_claims.py`); no test framework needed.
Exits 0 if every case behaves, 1 otherwise.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(HERE, "check_dependabot_claims.py")

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Daqifi.Core" Version="{core}" />
    <PackageReference Include="Sentry" Version="{sentry}" />
    <!-- Not self-closing, exactly as the real EFCore.Tools reference is -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="{efcore}">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <!-- Attributes in the other order -->
    <PackageReference Version="{nlog}" Include="NLog" />
  </ItemGroup>
</Project>
"""

# Property-valued pins, as third_party/oxyplot-avalonia uses. There is no literal
# to compare against and the project is out of Dependabot's scope.
VENDORED_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="$(AvaloniaVersion)" />
  </ItemGroup>
</Project>
"""


def body(*claims: str, notes: str = "") -> str:
    """Assemble a Dependabot-shaped body: claim lines separated by <details>."""
    parts = []
    for claim in claims:
        parts.append(claim)
        parts.append("")
        parts.append("<details>")
        parts.append("<summary>Release notes</summary>")
        parts.append(notes)
        parts.append("</details>")
        parts.append("")
    return "\n".join(parts)


def main() -> int:
    failures = []

    def check(label: str, expected: int, actual: int) -> None:
        status = "ok" if actual == expected else "FAILED"
        print(f"  [{status}] {label}: expected {expected}, got {actual}")
        if actual != expected:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        proj = os.path.join(tmp, "app.csproj")
        body_path = os.path.join(tmp, "body.md")

        def write(path: str, text: str) -> None:
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(text)

        def run(*args: str) -> int:
            return subprocess.run(
                [sys.executable, GUARD, "--body-file", body_path, *args],
                capture_output=True, text=True).returncode

        applied = dict(core="1.7.0", sentry="6.9.0", efcore="10.0.11",
                       nlog="6.2.0")
        write(proj, CSPROJ.format(**applied))

        write(body_path, body(
            "Updated [Daqifi.Core](https://github.com/daqifi/daqifi-core) "
            "from 1.3.0 to 1.7.0.",
            "Updated [Sentry](https://github.com/getsentry/sentry-dotnet) "
            "from 6.8.0 to 6.9.0."))
        check("applied claims pass", 0, run(proj))

        # THE #130 CASE: the body announces Core 1.7.0, the manifest says 1.3.0.
        write(proj, CSPROJ.format(**{**applied, "core": "1.3.0"}))
        check("unapplied claim fails (the #130 shape)", 1, run(proj))

        # Only SOME of a group's members landing is the same failure — #130 applied
        # four of fourteen, and read as fourteen.
        write(proj, CSPROJ.format(core="1.3.0", sentry="6.9.0",
                                  efcore="10.0.10", nlog="6.1.4"))
        check("partially applied group fails", 1, run(proj))

        write(proj, CSPROJ.format(**applied))

        # A package the body names but nothing pins: the claim cannot be true here.
        write(body_path, body(
            "Updated [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)"
            " from 13.0.3 to 13.0.4."))
        check("claim about an unpinned package fails", 1, run(proj))

        # The two other wordings Dependabot uses.
        write(body_path, body(
            "Bumps [Sentry](https://github.com/getsentry/sentry-dotnet) "
            "from 6.8.0 to 6.9.0."))
        check("'Bumps [X](url)' wording is parsed", 0, run(proj))

        write(body_path, body("Updates `Sentry` from 6.8.0 to 6.9.0"))
        check("backtick 'Updates `X`' wording is parsed", 0, run(proj))

        # The group header of a multi-directory body starts with "Bumps" but states
        # no from/to. Treating it as a claim would invent a package named after the
        # group; ignoring it entirely would leave nothing to check.
        write(body_path, "\n".join([
            "Bumps the minor-and-patch group with 1 update in the /app "
            "directory: [Sentry](https://github.com/getsentry/sentry-dotnet).",
            "",
            "Updates `Sentry` from 6.8.0 to 6.9.0",
            "",
            "<details>",
            "<summary>Release notes</summary>",
            "</details>",
        ]))
        check("multi-directory group header is not a claim", 0, run(proj))

        # Release notes routinely contain their own "Updates `x` from a to b" lines.
        # Counting those invents claims the PR never made — here, a bogus failure.
        write(body_path, body(
            "Updated [Sentry](https://github.com/getsentry/sentry-dotnet) "
            "from 6.8.0 to 6.9.0.",
            notes="Updates `SomeTransitive` from 1.0.0 to 2.0.0\n"
                  "Updated [AlsoNotOurs](https://example.invalid) "
                  "from 3.0.0 to 4.0.0."))
        check("claims inside <details> are ignored", 0, run(proj))

        # Nested <details> (commit lists inside release notes) must not close the
        # outer block early and re-expose changelog lines as claims.
        write(body_path, "\n".join([
            "Updated [Sentry](https://github.com/getsentry/sentry-dotnet) "
            "from 6.8.0 to 6.9.0.",
            "",
            "<details>",
            "<summary>Release notes</summary>",
            "<details>",
            "<summary>Commits</summary>",
            "</details>",
            "Updated [Ghost](https://example.invalid) from 1.0.0 to 2.0.0.",
            "</details>",
        ]))
        check("nested <details> does not leak claims", 0, run(proj))

        # A body with no claims means the parser has gone stale against
        # Dependabot's wording, or this is not a Dependabot PR. Either way the
        # check verified nothing and must not report success.
        write(body_path, "Just some prose with no dependency claims in it.\n")
        check("a body with no claims is an input error", 2, run(proj))

        write(body_path, body(
            "Updated [Sentry](https://github.com/getsentry/sentry-dotnet) "
            "from 6.8.0 to 6.9.0."))

        # Property-valued pins carry no literal version. A manifest holding only
        # those contributes nothing, and on its own leaves nothing to check.
        vendored = os.path.join(tmp, "vendored.csproj")
        write(vendored, VENDORED_CSPROJ)
        check("property-valued pins alone is an input error", 2, run(vendored))
        check("property-valued pins alongside real ones are skipped",
              0, run(proj, vendored))

        check("missing manifest is an input error", 2,
              run(os.path.join(tmp, "nope.csproj")))
        check("no manifests is an input error", 2, run())
        check("--glob with explicit paths is an input error", 2,
              run("--glob", proj))

        missing_body = subprocess.run(
            [sys.executable, GUARD, "--body-file",
             os.path.join(tmp, "nope.md"), proj],
            capture_output=True, text=True).returncode
        check("missing body file is an input error", 2, missing_body)

        no_flag = subprocess.run(
            [sys.executable, GUARD, proj],
            capture_output=True, text=True).returncode
        check("omitting --body-file is an input error", 2, no_flag)

    if failures:
        print(f"\n{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print("\nall cases passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
