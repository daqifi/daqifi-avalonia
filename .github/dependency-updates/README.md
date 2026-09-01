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

Four changes. The first three each cover a failure the others do not; the fourth
covers the one head none of them can reach.

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

Three deliberate choices:

- **Every project that pins the package is checked, not just one.**
  `Avalonia.Fonts.Inter` is pinned by three projects here, so a grouped
  multi-directory PR that moved one and left another behind would otherwise pass on
  the strength of the one it did move.
- **"Every project" means every project Dependabot manages** — the `directories`
  list in `dependabot.yml`, read at check time. This matters in both directions: the
  iOS head is not in that list, so its pins drift on purpose and must not fail a PR
  that could never have touched them; and if the list later grows, the check grows
  with it instead of silently keeping the old scope. A config it cannot read is exit
  2, not a silently widened scope.
- **Lock files are evidence, never grounds for failure.** A transitive-only update —
  the shape a security bump takes — moves a lock file and nothing else, so a claim
  about a package no in-scope project pins is satisfied by a lock-file resolution.
  They cannot fail a claim, because a legitimate single-directory PR leaves the
  *other* directories' lock files recording the old transitive version (#93 changed
  only `Daqifi.Avalonia/`), and failing on that would reject good PRs. This cannot
  hide the #130 case: there the lock file recorded `Daqifi.Core` at 1.3.0 right
  alongside the csproj, because the bump landed nowhere. "Legitimate" here means the
  PR is honest about what it changed — those stale sibling lock files still fail the
  build, which is a different problem with a different fix: see "Why a Dependabot PR
  that touches `/Daqifi.Avalonia` needs a lock-file commit" below.

It is also one-directional: it asserts every claim was applied, not that every
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

### 4. `check_core_drift.py` again, pointed at the iOS head

Same workflow, second job. `/Daqifi.Avalonia.iOS` is outside Dependabot's reach —
see below — so nothing will ever open a PR for its `Avalonia.iOS` and
`Avalonia.Fonts.Inter` pins. The job compares both against nuget.org weekly and
files its own rolling issue naming the file to edit.

The script needed no changes for this: it already takes `--package` and explicit
manifest paths. The job passes the iOS csproj by path rather than using `--glob`,
so a report titled *"the iOS head is behind"* can only ever be about the iOS head.

This is a second line, not the first. `check_avalonia_versions.py` already fails the
build when the heads' Avalonia graphs split, which is exactly what an un-bumped iOS
head causes. But it fires after a full macOS iOS build and only once someone has
already opened the bump that moved the other heads — so the first person to learn is
whoever is debugging a red build on a Dependabot PR. This fires earlier, on a
schedule, and says which file to edit.

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

`refresh_lock_files.sh` is not a guard — it is a remedy, and the only shell script
here — but it follows the same shape for the same reason: its refusals are the part
that matters, so `test_refresh_lock_files.py` drives it with a stub `dotnet` and
asserts it exits 2 without writing. That test is picked up by the same `scripts` job,
which globs `test_*.py` regardless of what the thing under test is written in.

## Why the iOS head stays out of `directories`

`/Daqifi.Avalonia.iOS` is not in `dependabot.yml`'s `directories`. That began as an
oversight — the head was added after the Dependabot config was written, and the
config's comment enumerated two deliberate exclusions without it. Adding it was
tried, and it does not work; the comment there now records the exclusion as
deliberate and says why.

Dependabot's NuGet updater image installs .NET SDKs and **no workloads** (see
`nuget/Dockerfile` in `dependabot/dependabot-core`), and this head targets
`net10.0-ios`. That stops the updater twice, independently. Both were reproduced
against SDK 10.0.302 — the version `global.json` pins — using a workload the test
machine lacks:

| Stage | What Dependabot runs | Result with no workload |
| --- | --- | --- |
| Discovery | `DependencyDiscovery.props` sets `DesignTimeBuild=true` and `TargetPlatformVersion=0.0` | `NU1012` — the TFM does not spell its platform version out |
| Lock file | `LockFileUpdater.cs` runs a plain `dotnet restore --force-evaluate` | `NETSDK1147` — no `DesignTimeBuild` there, so nothing suppresses it |

`DesignTimeBuild=true` genuinely does suppress `NETSDK1147` during discovery — the
SDK's `_CheckForMissingWorkload` target is conditioned on it — which is why the
Android head clears that first stop: `net10.0-android36.0` carries its platform
version in the TFM, so it never hits `NU1012`. `net10.0-ios` does not carry one; the
workload supplies `26.5`, which is why `packages.lock.json` here is keyed
`net10.0-ios26.5` while the csproj says only `net10.0-ios`.

Two things follow, both worth knowing before anyone tries this again:

- **Pinning the iOS TFM to `net10.0-ios26.5` is not the fix it looks like.** It
  clears the discovery stop and leaves the lock-file one exactly where it was.
- **The failure would not even be a failed job.** `LockFileUpdater` logs its error
  and returns rather than aborting the update, so adding the directory would produce
  a weekly PR carrying a bumped csproj and an untouched lock file — `NU1004` under
  the locked-mode restore in `Directory.Build.props`.

Watched instead by guard 4 above.

### The same exposure, on a head that IS managed

`/Daqifi.Avalonia.Android` is in `directories`, and its lock file is subject to that
same `LockFileUpdater` failure — its TFM rescues discovery, not the restore. So a bump
to one of its own two pins would arrive with a bumped csproj and a stale
`Daqifi.Avalonia.Android/packages.lock.json`, and nothing would say so but a line in
the updater log. Both of those packages have sat at 12.1.1 since 2026-07-30, so
Dependabot has had nothing to open.

That has never bitten **on its own**. It has never been the reason the Android head is
red either, because a much broader version of the same problem gets there first — see
the next section, which is also where the decision this section used to defer now
lives.

## Why a Dependabot PR that touches `/Daqifi.Avalonia` needs a lock-file commit

Not just the Android one, and not because of a workload. Any Dependabot PR that bumps
a package in the shared library arrives red and cannot merge without a lock-file commit
— and that is 23 of the 28 packages this repo manages, including every one whose name
does not start with `Avalonia.`.

Six `packages.lock.json` files are committed. Five belong to the app's own projects
(the vendored `third_party/oxyplot-avalonia` carries the sixth and is not affected by
any of this), and four of those five reach their packages through a `ProjectReference`
to `Daqifi.Avalonia`. A lock file records the full transitive closure, so the shared
library's dependencies are written into all five — `Sentry`, for one, is `Direct` in
`Daqifi.Avalonia/packages.lock.json` and `Transitive` in the Desktop, Android, iOS and
`AvaloniaCapture` files. So is every other package the library pins.

Dependabot rewrites the lock file **only in the directory whose manifest it edited**.
A one-line bump in `Daqifi.Avalonia/Daqifi.Avalonia.csproj` therefore invalidates five
lock files and refreshes one, and `RestoreLockedMode` fails the other four:

```
error NU1004: The project references daqifi.avalonia whose dependencies has changed.
The packages lock file is inconsistent with the project dependencies so restore can't
be run in locked mode.
```

This is the current state of the repo, not a forecast. [#96] is the clean case — one
`NCalcSync` bump, nothing else wrong, `NU1004` on both heads it builds. [#130] fails
the same way on the Android and iOS heads (it also has an unrelated `NU1605`:
`EFCore.BulkExtensions.Sqlite` pulls `Microsoft.EntityFrameworkCore.Relational` 10.0.11
against a 10.0.10 pin, which no lock refresh will fix). Both PRs' diffs show the shape:
two files, both under `Daqifi.Avalonia/`.

Reproduced on the pinned SDK by doing exactly what Dependabot does — bump `Sentry`
6.8.0 → 6.9.0 in the shared library, refresh only its own lock file, then restore each
head in locked mode. All four fail `NU1004`; running the script below makes all four
pass.

**`check_avalonia_versions.py` is not a safety net for this.** The `avalonia-graph` job
`needs: [desktop, android, ios]`, so when those fail `NU1004` it is **SKIPPED** — which
is what the checks on [#130] show. It is a real gate against a split graph and it
cannot fire on a Dependabot PR, because on a Dependabot PR it never runs.

**Which PRs escape.** `Daqifi.Avalonia` is the only managed project with dependents;
the Desktop, Android, iOS and `AvaloniaCapture` heads are leaves. So a PR confined to a
leaf — the five packages that live nowhere else, `Avalonia.Desktop`, `Avalonia.Android`,
`Avalonia.Headless`, `Avalonia.Skia`, `Avalonia.HarfBuzz` — stales no other lock file
and needs no refresh. Two caveats: an Android-head-only PR fails a *different* way (the
workload problem above leaves its own lock file stale), and a leaf PR that moves an
Avalonia package in one head and not another is exactly what `check_avalonia_versions.py`
is for — which does run there, because those jobs get far enough to upload a graph.

### The remedy

```bash
.github/scripts/refresh_lock_files.sh
```

Run it and commit the result onto the Dependabot branch. It will not leave a partial
refresh behind, by either route:

- **Before it starts**, it checks for the pinned SDK and for both the `android` and
  `ios` workloads, and exits 2 naming what is missing. In practice that means a Mac
  with `dotnet workload install android ios`.
- **Once it starts**, it snapshots every lock file, and a restore that fails partway —
  a genuine `NU1605` conflict, say — rolls all of them back and exits 1. Otherwise the
  projects restored before the failure would keep their new lock files, which is the
  same half-refreshed set arriving by a different door and looking like an ordinary
  diff on the way out. The snapshot is of the working tree, not `HEAD`, so Dependabot's
  own lock-file edit survives the rollback.

`test_refresh_lock_files.py` holds it to both with a stub `dotnet`: four refusal cases,
plus a stub that mutates lock files and fails on the third restore. That group carries a
control case whose stub never fails, because a rollback test whose stub never wrote
anything would pass without proving anything.

Locked mode is not in the way: `--force-evaluate` is what overrides `RestoreLockedMode`,
which is what NuGet's own `NU1004` text tells you to reach for. Verified with `CI=true`
and `RestoreLockedMode` evaluating `true` — a plain restore of a stale head fails
`NU1004`, and the same restore with `--force-evaluate` regenerates it. So the script
works as-is inside a workflow, should the automation below ever get built.

It iterates projects rather than restoring `Daqifi.Avalonia.slnx`, because
`tools/parity-audit/AvaloniaCapture` is not in the solution and a solution-level restore
would silently miss it. It never passes `-r`, which would prune the lock file to a
single RID (see the warning at the top of `Directory.Build.props`).

`--force-evaluate` is also the only thing that catches a lock file that is *stale* rather
than drifted. NuGet compares ids case-insensitively when it decides an existing lock file
is consistent, so a file whose entries are merely spelled differently from what a fresh
derivation produces satisfies locked mode, survives the post-restore `git diff`, and stays
stale indefinitely. [#106] is what that cost: the vendored project's key derived as
`oxyplot.avalonia` on macOS and `Oxyplot.Avalonia` on Linux, so running this script on a
Mac produced a one-line diff nobody could tell from churn. The cause is gone (see
`third_party/oxyplot-avalonia/VENDORED.md`), and all three build jobs now re-derive their
own head, so a key that is host-dependent again fails in CI rather than on a laptop.

### Why `/Daqifi.Avalonia.Android` stays in `directories`

The alternative considered was dropping it the way the iOS head is dropped, and
covering it with `check_avalonia_versions.py`. That was rejected on the evidence above:

- It would fix nothing. The Android head's `NU1004` comes from the `ProjectReference`
  closure, not from `Avalonia.Android` or `Avalonia.Fonts.Inter`. The proof is the iOS
  head, which is *already* out of `directories` and fails identically on [#130].
- The guard offered as cover does not run on the PRs in question, per above.
- It would cost the only automated watch on those two pins.

So the Android head stays managed, its workload exposure is recorded rather than
worked around, and the lock-file refresh is a documented manual step on every PR
that touches the shared library.

### The real fix, not done here

A workflow that runs the refresh script on `dependabot[bot]` PRs and pushes the result,
so a Dependabot PR arrives green. It needs write access to the PR branch and a macOS
runner for the iOS head, which bills at 10× — a decision worth making deliberately
rather than smuggling into this change. Tracked against [#132], which is about exactly
this class of silent Dependabot failure.

[#93]: https://github.com/daqifi/daqifi-avalonia/pull/93
[#95]: https://github.com/daqifi/daqifi-avalonia/pull/95
[#96]: https://github.com/daqifi/daqifi-avalonia/pull/96
[#106]: https://github.com/daqifi/daqifi-avalonia/issues/106
[#130]: https://github.com/daqifi/daqifi-avalonia/pull/130
[#131]: https://github.com/daqifi/daqifi-avalonia/pull/131
[#132]: https://github.com/daqifi/daqifi-avalonia/issues/132
