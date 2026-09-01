# Contributing to daqifi-avalonia

Thanks for taking the time to contribute!

This is the cross-platform DAQiFi app — one shared library plus four platform heads
(Desktop, Android, iOS). It is a port in progress of the Windows-only WPF app
[daqifi-desktop](https://github.com/daqifi/daqifi-desktop), and most of the device
logic lives in the [daqifi-core](https://github.com/daqifi/daqifi-core) package rather
than here.

## Reporting bugs & requesting features

[Open an issue](https://github.com/daqifi/daqifi-avalonia/issues) with as much detail as
you can: repro steps, expected vs. actual behaviour, which platform you saw it on, the
app version (Help → About, or the title bar), and your device model and firmware version.

**Where does the bug belong?** Discovery, connection, transport, protobuf decoding, and
firmware update are all `Daqifi.Core`'s job — this app calls into it. If a device will not
connect at all, or streams garbage, the issue very often belongs in
[daqifi-core](https://github.com/daqifi/daqifi-core/issues) instead. Anything about
windows, plots, buttons, layout, or export belongs here.

## Submitting code changes

All code changes go through a pull request:

1. Fork the repo (or branch, if you have write access) — `feature/short-description`,
   `fix/short-description`, or `docs/short-description`.
2. Make your changes and add or update tests where there is something to test.
3. Open a PR against `main` describing the change and linking any related issue.
4. CI must pass and the PR needs review before merge.

### Build it before you push — CI builds every platform

`dotnet build` on the Desktop head is not sufficient evidence that a change works. CI
builds the shared library, Desktop, Android, and iOS heads separately, because a change
that compiles for one can easily break another: the heads pin some packages
independently, and the mobile heads are sensitive to workload versions in a way the
desktop head is not.

Requires the **.NET 10 SDK** pinned in `global.json` (`rollForward: disable`), so the
exact version matters. Android and iOS additionally need
`dotnet workload install android ios`, and iOS needs macOS with Xcode — see
[docs/RUNBOOK-macos-ios.md](docs/RUNBOOK-macos-ios.md).

### Lock files are a gate, not a cache

Every project commits a `packages.lock.json`, and CI restores in **locked mode**
(`RestoreLockedMode`, enabled whenever `CI` is set). A lock file that has drifted from its
project's dependencies fails the restore with `NU1004` instead of being quietly
regenerated.

**If you change a `PackageReference`, refresh the lock file in the same commit.** A local
restore updates it for you; commit the result. Forgetting is the single most common way
to get a red build on an otherwise fine change.

### The same package must resolve to the same version in every head

The `Avalonia version lockstep` job compares the resolved dependency graphs of the
Desktop, Android, and iOS heads and fails when a shared package resolves differently
between them. This is deliberately stricter than "it builds": a split graph produces
crashes that only reproduce on one platform, and it is invisible when looking at any
single project. The iOS head pins some Avalonia packages by hand, so it is the usual
source of a split.

### Do not delete the `// @port:` markers

The source carries `// @port:` markers linking symbols back to the upstream WPF app they
were ported from. They look like noise and they are not — they are how the port is tracked
against upstream. Leave them alone unless you are deliberately retiring one.

They reference an internal correspondence map that is not published in this repository, so
the markers are one-way from a contributor's point of view: useful as "this came from
upstream and may have diverged", not something you can follow.

### Tests

There is a small xUnit suite in `Daqifi.Avalonia.Tests`, run by CI on Linux. `dotnet test`
builds that project itself, so it doubles as its compile gate:

```
dotnet test Daqifi.Avalonia.Tests/Daqifi.Avalonia.Tests.csproj
```

Coverage is thin, and much of this app is genuinely hard to test without hardware — a
plot that renders, a device that streams. Please add tests for logic that can be tested in
isolation (parsing, downsampling, scaling, formatting) rather than treating the existing
sparseness as the standard.

### Changes that need hardware

This app is a companion for DAQiFi Nyquist hardware, and a good deal of it cannot be
verified without a device on the network: discovery, connection, streaming, sample rates,
SD logging, firmware update. If your change touches any of those, say in the PR what you
tested it against, and what you could not. "Builds clean, not bench-tested" is a useful
and honest thing to write — quietly untested device changes are not.

### CI guards test themselves

The scripts in `.github/scripts/` enforce repository policy (dependency drift, lock-file
integrity, the Avalonia graph). Each has a `test_*.py` beside it, and CI runs every one of
those self-tests on every push. If you change a guard, update its tests — a guard that
only ever passes is worse than no guard, because it reads as coverage while providing
none.

`.github/dependency-updates/`, `.github/upstream-sync/`, and `.github/merge-queue/` each
carry a README explaining the policy they enforce and why it exists.

### Crash reporting

The app reports crashes to Sentry using a DSN baked into the build. It is not a secret —
the same DSN ships in every released binary, and it grants no read access. Build with
`-p:SentryDsn=` to compile reporting out, or `-p:SentryDsn=<other>` to point at your own
project.

## Security: how we do and don't accept code

**We only ever accept code changes as pull requests against this repository.** A PR gives
reviewers a real diff, runs CI against the change, and ties it to an accountable GitHub
identity.

We do **not** accept patches, "fixes," or libraries attached as `.zip`/binary files in
issue or PR comments — regardless of how convincing or on-topic the surrounding message
is. If you see a comment offering a downloadable file as a fix, please don't run or
extract it, and flag it to a maintainer (or use GitHub's "Report content" option on the
comment) so it can be reviewed and removed.

If you've found a genuine security vulnerability, please report it privately to the
maintainers via [daqifi.com](https://daqifi.com) rather than filing a public issue.
