# Why `Daqifi.Core` drifted for five weeks, and what now catches it

`Daqifi.Core` is the package this app wraps — it decides how the app talks to
hardware. Its pin sat at **1.3.0 from 2026-07-24 to 2026-08-31** while Core shipped
1.4.0, 1.5.0, 1.6.0 and 1.7.0. Nothing surfaced that. This file records what
actually happened, because the obvious explanation was wrong and the real one is
easy to walk into again.

## What actually happened

The starting assumption ([#132]) was that Dependabot had **never** opened a PR for
`Daqifi.Core` — reading the 12 Dependabot PR titles in this repo's history finds
Avalonia, Sentry, NCalcSync and grouped chores, and no Core. That is true of the
titles. It is not true of the PRs.

| Date | What Dependabot did with `Daqifi.Core` |
| --- | --- |
| 2026-08-03 | [#93] bumped Core **1.3.0 → 1.4.0**, titled *"Bump the minor-and-patch group with 3 updates"*. |
| 2026-08-10 | Auto-closed #93 as superseded by [#95] (Core **1.3.0 → 1.5.0**), titled *"Bump Avalonia.Controls.DataGrid and 2 others"*. |
| 2026-08-17 | Auto-closed #95 — *"Looks like these dependencies are updatable in another way"* — and **opened no replacement**. |
| 2026-08-24 | Nothing. Core 1.7.0 published that day. |
| 2026-08-31 | [#130]'s body announces Core **1.3.0 → 1.7.0**. Its diff does not touch Core. |

Three distinct failures, none of which look like a failure from the outside:

1. **A grouped PR is titled after the group, not its members.** Core was in two PRs
   and named in neither title. Scanning a PR list — the natural way to ask "has
   Core been bumped?" — returns nothing, indistinguishable from a package with no
   updates.
2. **Dependabot closed its own Core bump and opened nothing in its place.** From
   2026-08-17 to 2026-08-31 there was no open PR mentioning Core at all.
3. **A Dependabot PR body can announce a bump the PR never made.** #130 lists 14
   updates; its diff applies **4**. Core, `System.IO.Ports`, `System.Management`,
   `Microsoft.Data.Sqlite`, `Microsoft.EntityFrameworkCore`,
   `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.Extensions.Http`, `NLog`,
   `System.Configuration.ConfigurationManager` and `Avalonia.Controls.DataGrid` are
   all described as updated and are all still at their old versions in the csproj.
   This is the worst of the three, because the PR reads as proof the dependency
   moved.

## The theory that was wrong

#132 proposed that a Core-only bump could not resolve: Core 1.7.0 raises its floor
on `System.IO.Ports` and `System.Management` to 10.0.11, both pinned directly in
`Daqifi.Avalonia.csproj`, so bumping Core alone fails `NU1605: Detected package
downgrade`. That is real, and it is why [#131] had to move all three pins together.

It does not explain the history. **Core 1.4.0, 1.5.0 and 1.6.0 all depend on
`System.IO.Ports` / `System.Management` 10.0.10** — exactly what was pinned. Only
1.7.0 raised the floor. There was no downgrade conflict on 2026-08-03 or
2026-08-10, and Dependabot demonstrably did produce working bumps on both dates.

Worth keeping in mind anyway: the next time Core raises a floor on a directly
pinned transitive, a Core-only PR will not resolve. The guards below are what makes
that visible instead of silent.

## What now catches it

Three changes, each covering a failure the others do not.

### 1. `Daqifi.Core` is excluded from the `minor-and-patch` group

`.github/dependabot.yml`. Ungrouped, Core gets its own PR titled *"Bump Daqifi.Core
from X to Y"* every time. It cannot be mistaken for someone else's chore in a PR
list, and it cannot be dropped from a group whose other members moved.

This is the fix for failure 1. It does nothing for 2 or 3 — Dependabot can still
close its own PR, and can still open one that over-claims.

### 2. `check_dependabot_claims.py` — the PR must have made what it says

`.github/workflows/dependabot-claims-check.yml`, on every Dependabot PR. Parses the
`Updated X from A to B.` claims out of the body and fails if any claimed version is
recorded neither in a `.csproj` nor in a `packages.lock.json`.

Fix for failure 3. Verified against every Dependabot PR in this repo's history: it
passes #93, #95 and #96, and fails #130 on all ten unapplied claims.

Two deliberate choices:

- **Lock files count as evidence.** A transitive-only update — a security bump to
  something no csproj names — moves the lock file and nothing else, and demanding a
  csproj change would fail exactly the PRs it is most costly to block. This does not
  weaken the check: in #130 the lock file recorded `Daqifi.Core` at 1.3.0 right
  alongside the csproj, because the bump landed nowhere.
- **It is one-directional.** It asserts every claim was applied, not that every
  change was claimed, because Dependabot legitimately touches things the body never
  mentions. The reverse check would fire on every PR.

### 3. `check_core_drift.py` — the pin versus nuget.org, weekly

`.github/workflows/core-drift-check.yml`, Mondays after Dependabot's own run.
Compares the committed Core pin against the flat-container index on nuget.org and
files a single rolling issue listing every release not taken up, closing it when
the pin catches up.

Fix for failure 2, and the backstop for everything else: it does not depend on
Dependabot having done anything. Dependabot can open nothing, close its own PR, or
open one that lies, and this still reports the truth.

## Conventions these follow

Both guards are ordinary `.github/scripts` Python with a `test_*.py` beside them,
run with no test framework, following `check_avalonia_versions.py`. They share its
exit-code contract, which exists so a caller can tell a real finding from a broken
invocation:

| Code | Meaning |
| --- | --- |
| 0 | the check passed |
| 1 | a genuine violation |
| 2 | the check could not run — bad input, unreadable file, nothing matched |

**Exit 2 must never be treated as a pass.** A guard that cannot run and reports
success is the exact failure mode this whole file is about: silence that looks like
health. Both workflows fail the job on 2.

Every self-test in `.github/scripts` also runs on every push and PR via the
`scripts` job in `build.yml` — without it, these two guards would only be exercised
on a Dependabot PR or on a Monday, and a regression could sit unnoticed for a week.

## Known gap

`/Daqifi.Avalonia.iOS` is not in `dependabot.yml`'s `directories`, so the iOS head's
`Avalonia.iOS` and `Avalonia.Fonts.Inter` pins are unmanaged and will drift behind
the other heads. The config's comment enumerates two deliberate exclusions and the
iOS head is not one of them — it was added after the Dependabot config was written.
Adding it needs a check that Dependabot's updater can restore a `net10.0-ios`
project at all, which is why it is not fixed here.

[#93]: https://github.com/daqifi/daqifi-avalonia/pull/93
[#95]: https://github.com/daqifi/daqifi-avalonia/pull/95
[#130]: https://github.com/daqifi/daqifi-avalonia/pull/130
[#131]: https://github.com/daqifi/daqifi-avalonia/pull/131
[#132]: https://github.com/daqifi/daqifi-avalonia/issues/132
