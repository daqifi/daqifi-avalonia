# Runbook — macOS and iOS heads for daqifi-avalonia

**For:** Tyler (Mac required)
**Repo:** `daqifi/daqifi-avalonia`
**Engine:** `daqifi/portomatic`

You are adding the last two of five target platforms. Windows, Linux and Android
are done and shipping.

---

## Read this first: what is and is not already done

**macOS builds and runs today.** Verified on 2026-08-01 on an Apple-silicon Mac
with .NET SDK 10.0.203 — **a historical record, from before `global.json` pinned
the SDK.** Do not install 10.0.203 to reproduce it: with `rollForward: disable`
that now fails with *"A compatible .NET SDK was not found"* before any build
runs. Use the pinned SDK from §0; the result below still holds.

```
dotnet build Daqifi.Avalonia.Desktop/Daqifi.Avalonia.Desktop.csproj \
  -c Release -r osx-arm64 --self-contained false
→ 0 Error(s), 296 Warning(s), 23s
→ bin/Release/net10.0/osx-arm64/Daqifi.Avalonia.Desktop: Mach-O 64-bit executable arm64
```

The app launches, the window renders, and `DAQifiAppLog.log` reports
`Plot render tick active (first tick fired)`.

**macOS needs no new project.** `Daqifi.Avalonia.Desktop` already declares
`osx-x64;osx-arm64` in its `RuntimeIdentifiers` — written during the
shared-project split (`4489f28`) — and that declaration now has a build behind
it. Note that CI still does not: all three jobs (`desktop`, `android`,
`avalonia-graph`) run on `ubuntu-latest`, so there is **no macOS runner** and
nothing stops a macOS regression from landing.

So the remaining macOS work is not "will it compile" — it is:

- **Runtime behaviour under QA** (1.2). Compiling is not rendering; the
  PlotView and icon checks are the real gate.
- **Windows-only code paths that degrade rather than crash** (1.3).
- **A macOS CI job**, so this does not silently rot. Its own ticket.

Budget: less than the day originally planned for bring-up, more than zero for QA.

**iOS is a genuinely new head**, mirroring `Daqifi.Avalonia.Android`. It needs a
new project, a platform entry point, platform-service registrations, and an
Apple provisioning story. Budget: several days, and the device-connectivity
question below may reshape the scope.

Do macOS first. It de-risks the shared library on Apple platforms before you take
on iOS-specific work, and if the Desktop head has a macOS problem you want to
know that before it is tangled up with a new head.

---

## 0. One-time setup

```bash
git clone https://github.com/daqifi/daqifi-avalonia
git clone https://github.com/daqifi/daqifi-desktop      # upstream, needed as a sibling
git clone https://github.com/daqifi/portomatic

cd portomatic
python3 -m venv .venv && .venv/bin/pip install -e '.[dev,treesitter,visual]'
```

Install the .NET SDK **version pinned in `global.json`** — currently `10.0.302`, with
`rollForward: disable`, so nothing else will do. That is not fussiness: the SDK's
bundled runtime version decides `Microsoft.NET.ILLink.Tasks`, an *implicit* package
reference on the trimming heads which the lock files record. A different SDK therefore
fails locked-mode restore with `NU1004` before it fails anything you would recognise as
a real problem. A floating SDK is what took CI down in #107.

Check with `dotnet --version`; if it disagrees with `global.json`, install that exact
version rather than editing the pin.

Then, **for the iOS head only**:

```bash
dotnet workload install ios
```

> **This command can fail and still exit 0.** If your dotnet sits under a
> root-owned prefix — the Homebrew cask uses `/usr/local/share/dotnet`, but the
> prefix varies by install method — it prints `Inadequate permissions. Run the
> command with elevated privileges.` and then **returns success**. It needs
> `sudo`, and it needs an absolute path, because root's `PATH` will not
> necessarily contain your dotnet. Find yours, and **look at what it prints**:
>
> ```bash
> command -v dotnet
> ```
>
> then elevate that exact path:
>
> ```bash
> sudo /the/path/it/printed workload install ios
> ```
>
> Two steps on purpose. Do **not** collapse them into
> `sudo "$(command -v dotnet)" …` — that runs whatever your `PATH` resolves
> first as root, sight unseen, which is a bad habit to write into a runbook even
> where the box is trusted. It also misbehaves when `dotnet` is a shell alias or
> function, where `command -v` prints a definition rather than a path.
>
> Either way, verify the artifact rather than the exit code: `dotnet workload
> list` must actually list `ios`. This is the same trap as the one at the bottom
> of this document, caught in the wild.

macOS needs no workload — the Desktop head is plain `net10.0` published to an
`osx-arm64` RID, not `net10.0-maccatalyst`. Don't install `maccatalyst` unless
you deliberately decide to add a Catalyst head, which is not in scope here.

### ⚠ You have to create `.portomatic/project.yaml` — it is not in the clone

**`.portomatic/project.yaml` is gitignored** (`.gitignore`, under
*"portomatic local state (map/ and plans/ ARE committed)"*). It is per-machine
config, so a fresh clone does **not** have it and no `git pull` will produce it.
`.portomatic/map/`, `plans/`, `suites/` and `sync_state.yaml` **are** committed —
only `project.yaml` (plus `reports/`, `research/`, `cache/`) is local.

Create it before running any portomatic command. It holds absolute paths to your
two clones:

```yaml
upstream_root: /Users/<you>/GitHub/daqifi-desktop
downstream_root: /Users/<you>/GitHub/daqifi-avalonia
```

Do not copy the values from the Windows box — they are WSL paths
(`/mnt/c/Users/...`) and mean nothing here. If portomatic ships an init/bootstrap
command or an example file, prefer that over hand-writing; otherwise run
`doctor` (below) and let its complaints tell you which keys are missing.

There is also an `android` target carrying an `AndroidSdkDirectory`. Either point
it at your own SDK or drop the target from your local file — you are not building
Android on this machine.

**Nothing in this file is ever committed**, so there is no "do not commit the
path edits" hazard — git already ignores it. That also means it is invisible to
review: if a portomatic command behaves oddly, this file is the first thing to
check and the last thing anyone else can see.

> The same applies to the `dotnet.targets` edit below — `dotnet.targets` lives
> in `.portomatic/project.yaml`, so it is a **local** edit on your machine only
> and no one else will ever see it. Adding the iOS head to
> `Daqifi.Avalonia.slnx` (§2.2) *is* a committed change.

### The two target blocks you need

Append these to `dotnet.targets` now — this is the one place they are written
out, and §2.2's "add the head to `dotnet.targets`" refers back here rather than
asking for a second edit. Add the `osx-arm64` block today; the `ios` block only
matters once you start PART 2, but adding both now costs nothing and arms
`doctor` to tell you what iOS will need.

Both were validated against portomatic's own
loader (`portomatic.config.load_project`) rather than written from memory — the
schema is `extra="forbid"`, so a mistyped key fails loudly instead of being
silently ignored:

```yaml
  - name: osx-arm64
    rid: osx-arm64
    configuration: Release
    self_contained: true
  - name: ios
    tfm: net10.0-ios
    configuration: Release
    project: Daqifi.Avalonia.iOS/Daqifi.Avalonia.iOS.csproj
```

Desktop targets take a **`rid`**; mobile heads take a **`tfm`** plus their own
`project`, because a mobile head is a different csproj rather than a different
RID of the same one — compare the existing `android` entry.

Adding the `ios` target is what arms `doctor`'s iOS checks — they are driven off
this matrix, so until the target exists `doctor` has nothing to say about iOS.

**On your Mac, having done §0's workload install, the line you want is:**

```
✓ dotnet workload: ios: installed
```

`doctor` marks status with a glyph — `✓` ok, `⚠` warn, `✗` fail, `·` skipped —
not a word. Grep for the check *name* (`dotnet workload: ios`), never for a
status token.

If you instead get the `⚠` form, then §0's `dotnet workload install ios` did not
take. That is a real finding, not expected noise; run the fix hint `doctor`
prints under it:

```
⚠ dotnet workload: ios: required by the dotnet target matrix but not installed
   → dotnet workload install ios
```

There is a second warning you will **not** see on a Mac, and should not go
hunting for when it fails to appear:

```
⚠ dotnet ios host: iOS targets need a macOS host for full builds; this host can 
only do compile-level checks
```

It fires only *off* Mac — it is what the Windows/WSL box reports, which is where
these two `⚠` blocks were captured, verbatim, by adding the `ios` target to a
throwaway copy of `project.yaml` and running `doctor`. (The mid-sentence break
is Rich's 80-column wrap, reproduced as-is rather than tidied — including the
trailing space after `can`, which is Rich's and is deliberate here, not stray.
`cat -A` on the real output shows `…this host can $`. Please don't strip it;
what is printed here is what you can actually match against.) On darwin it is
suppressed
entirely. The gate is a `sys.platform != "darwin"` check in portomatic's
dotnet-workload check; find it with `grep -n 'darwin' src/portomatic/doctor.py`
rather than by line number, since §0 clones portomatic unpinned and any line
quoted here would drift.

Sanity check:

```bash
cd portomatic
.venv/bin/python -m portomatic doctor --downstream ../daqifi-avalonia
```

Expect some `fail` lines for toolchains you do not have (Android SDK). What
matters is that it finds the project and reports no *critical* failures for the
.NET toolchain.

---

## PART 1 — macOS head

### 1.1 Build it

```bash
cd daqifi-avalonia
dotnet build Daqifi.Avalonia.Desktop/Daqifi.Avalonia.Desktop.csproj \
  -c Release -r osx-arm64 --self-contained false
```

**This succeeds as-is.** 0 errors, ~23s, producing an arm64 Mach-O executable.

An earlier draft of this runbook predicted friction from three Windows-shaped
properties on the Desktop head:

```xml
<OutputType>WinExe</OutputType>
<BuiltInComInteropSupport>true</BuiltInComInteropSupport>
<ApplicationManifest>app.manifest</ApplicationManifest>
```

**None of them break the build.** The SDK ignores all three for a non-Windows
RID — `WinExe` only suppresses the console window on Windows, and the manifest
and COM properties are simply not consumed. **Do not preemptively make them
conditional.** There is no bug to fix here, and the RID-conditioned
`ApplicationManifest` that draft proposed would have been a change with no
observable effect.

(If you ever *do* need to condition one, condition on the **target RID**
(`$(RuntimeIdentifier)`), never `$([MSBuild]::IsOSPlatform('Windows'))` — the
latter tests the build host, which is wrong the moment CI cross-compiles a
Windows build from `ubuntu-latest`, as this repo's does.)

> **Trap: lock-file churn.** A build with an explicit `-r` rewrites
> `packages.lock.json` files to reflect that RID. Committing the result can break
> CI with `NU1004: the project's runtime identifiers have changed`, and in a diff
> it looks like harmless churn. On Windows this has rewritten 1004 lines across
> three lock files.
>
> On macOS the osx-arm64 build is milder — one file, two lines, *adding* a RID
> section rather than dropping others:
>
> ```diff
>    third_party/oxyplot-avalonia/OxyPlot.Avalonia/packages.lock.json
> -    }
> +    },
> +    "net10.0/osx-arm64": {}
> ```
>
> Still do not commit it. After any `-r` build run `git status`; if a
> `packages.lock.json` is modified, **revert it — do not stage it.**
> `dotnet restore` without `-r` (or with `--force-evaluate`) restores the full
> set. CI guards this (`.github/workflows/build.yml`, "Lock files must be
> unchanged by restore"), so it cannot land silently — but it will waste a cycle.

### 1.2 Run and QA it

There is no macOS QA checklist yet — you are writing it. Mirror the desktop list
that was run on Windows in
[#72 — Avalonia 12 visual + device acceptance](https://github.com/daqifi/daqifi-avalonia/issues/72),
because those are the paths the Avalonia 12 migration actually touched:

- [ ] app launches; nav rail renders and all five panes open —
      **Live Graph, Logged Data, Channels, Devices, Profiles**
- [ ] icons render throughout (Optris icon pack — a font/glyph provider is a
      classic per-platform failure)
- [ ] WiFi discovery finds a bench Nyquist; connect works
- [ ] enable 16 channels → stream → **PlotView actually draws series pixels**
- [ ] minimap pan/zoom
- [ ] session PNG export opens and is non-blank
- [ ] DeviceLogs copy-to-clipboard (macOS clipboard is a distinct implementation)
- [ ] window maximize/restore round-trip
- [ ] file dialogs (`IStorageProvider`) open natively

**The PlotView check is not a formality.** If the OxyPlot theme `StyleInclude`
fails to apply, `PlotView.OnApplyTemplate` silently no-ops and the plot renders
**nothing, with no exception**. Confirm you see actual plotted lines.

### 1.3 Windows-only code paths — what is confirmed, and what is not

**Already guarded correctly — do not go hunting these:**

- `SetThreadExecutionState` (a `kernel32` P/Invoke) is behind
  `OperatingSystem.IsWindows()` at `AbstractStreamingDevice.cs:1296` and `:1346`.
  That is a runtime check, so it covers macOS properly. This threw a
  `DllNotFoundException` on Android once; the fix generalises.

**Confirmed on macOS — guarded, but degrades silently:**

- `ConnectionManager`'s constructor (`ConnectionManager.cs:173`) builds a
  `ManagementEventWatcher` (WMI, Windows-only) inside a `try` whose
  `catch (Exception ex)` only *logs*. Observed verbatim in
  `DAQiFi/Logs/DAQifiAppLog.log` on first launch:

  ```
  LEVEL=ERROR: Failed to initialize ManagementEventWatcher: System.Management
  currently is only supported for Windows desktop applications.
  System.PlatformNotSupportedException
     at System.Management.WqlEventQuery..ctor(String queryOrEventClassName)
  ```

  The app does not crash — `ConnectionManager` is a static singleton, so an
  uncaught throw here would be a fatal `TypeInitializationException` and the
  catch is load-bearing. But USB hotplug-removal detection is **dead on macOS
  with no user-visible signal**. Decide whether that is acceptable for macOS v1
  or needs a `DeviceWatcher` implementation (there is already a
  `Services/DeviceWatcher/` abstraction with `WmiDeviceWatcher` and
  `NoOpDeviceWatcher` — a macOS watcher belongs there, and `ConnectionManager`
  should be routed through it rather than constructing WMI directly).

**Fixed in this PR — hardcoded path separators:**

- `DaqifiSettings.cs` and `LoggingManager.cs` built their XML paths as
  `AppDirectory + "\\DAQifi...xml"`. A backslash is a legal *filename* character
  on macOS, so this did not fail — it silently created files literally named
  `DAQiFi\DAQifiConfiguration.xml` and `DAQiFi\DAQifiProfilesConfiguration.xml`
  **next to** the data directory while the database and logs went correctly
  *inside* it. Settings and profiles were therefore split away from the rest of
  the app data. Now `Path.Combine`, which is byte-identical on Windows.
  If you have already run a pre-fix build, delete the two stray backslash-named
  files from `~/Library/Application Support/`; settings regenerate at defaults.

**Unverified — check these yourself:**

- `System.IO.Ports` (serial/USB). It exists on macOS, but device paths differ
  (`/dev/cu.usbmodem*` rather than `COM*`). I did not trace the enumeration code,
  so treat serial-over-USB on macOS as an open question, not a working feature.
- Firewall / privacy prompts. macOS will ask for local-network access on first
  discovery. Grant it — and note in the QA record that dismissing it produces a
  **silent** discovery failure, which is a support burden worth documenting.

Record every real difference as a divergence, not a code tweak, if upstream
behaviour genuinely cannot be reproduced. The standing rule in this project: a
coverage gap becomes a ticket, never a rationale tweak.

---

## PART 2 — iOS head

### 2.1 Decide the connectivity story FIRST

Before writing code, settle this, because it determines whether the head is worth
building as more than a shell:

- **No USB host, and this is already a recorded decision.** Divergence
  **DIV-UI-003** (`.portomatic/map/UI.yaml`, accepted 2026-07-06) states it
  outright: *"System.IO.Ports does not exist on iOS and is unusable as-is on
  Android — mobile is WiFi/TCP + UDP discovery ONLY. Desktop keeps serial."* So
  this is settled, not something to re-litigate. Concretely,
  `MobileUsbConnector.Current` (a settable static at
  `Daqifi.Avalonia/Services/IMobileUsbConnector.cs:43`) stays `null`, and
  `IsAvailable => Current is not null` makes the UI hide the affordance for free.
  **Leave it unset.**
- **UDP discovery needs an Apple entitlement.** Discovery is UDP on port
  **30303** (`ConnectionDialogViewModel.cs:272`, `new WiFiDeviceFinder(30303)`).
  iOS requires the **Local Network** privacy permission
  (`NSLocalNetworkUsageDescription`) and, for true multicast, the
  `com.apple.developer.networking.multicast` entitlement — **which requires
  approval from Apple.** Without it, discovery may work only for subnet-directed
  traffic, or not at all.

Android hit the mirror image of this: broadcast to `255.255.255.255` was dropped
by the OS, and the fix was subnet-directed broadcast plus a `MulticastLock`. Read
`Daqifi.Avalonia/Services/INetworkDiscoveryScope.cs` (the
`NetworkDiscoveryScope.Current` static hook) and
`Daqifi.Avalonia.Android/MulticastDiscoveryScope.cs` before designing the iOS one
— that file is the whole pattern you are mirroring.

**If the multicast entitlement is not obtainable, say so early.** A manual
connect-by-IP path already exists in the mobile shell — the `ManualIp` `TextBox`
at `MobileShellView.axaml:141`, labelled *"Or connect by IP (data port 9760)"* —
and may be the honest iOS story. (Its `PlaceholderText` is a hardcoded
`192.168.1.234`. That is a static placeholder, not leaked device state; I filed a
bug against it once and was wrong.)

### 2.2 Create the head

Mirror `Daqifi.Avalonia.Android` exactly — it is the template:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
    <!-- Starting value only — let the SDK/Avalonia.iOS tell you its real floor
         and raise it rather than guessing. Android's equivalent is 23. -->
    <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationId>com.daqifi.avalonia</ApplicationId>
    <ApplicationVersion>1</ApplicationVersion>
    <ApplicationDisplayVersion>3.3.0</ApplicationDisplayVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia.iOS" Version="12.1.1" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Daqifi.Avalonia\Daqifi.Avalonia.csproj"
                      UndefineProperties="RuntimeIdentifier;SelfContained" />
  </ItemGroup>
</Project>
```

> **`UndefineProperties` is load-bearing, not decoration.** The shared app dll is
> platform-neutral. Letting the head's RID flow into its build RID-flavours the
> `obj` tree and breaks Avalonia's XamlIl task with `AVLN9999`. The Android head
> carries the same attribute for the same reason.

**Pin `Avalonia.iOS` to the exact version the other heads use** (currently
`12.1.1`). CI enforces this: `.github/scripts/check_avalonia_versions.py` fails
the build if any head resolves a different Avalonia version, because a split
graph is silent at compile time and throws `MissingMethodException` at runtime.

Then add the head to `Daqifi.Avalonia.slnx` (a committed change). Its
`dotnet.targets` entry is the `ios` block from §0 — if you added it there, this
step is already done and needs no second edit.

### 2.3 Entry point and platform services

Android's `MainActivity` / `MainApplication` split is the model:

- `App.axaml.cs:29` already branches on `ISingleViewApplicationLifetime` (the
  desktop arm is `IClassicDesktopStyleApplicationLifetime` at `:21`). **That
  branch is shared and platform-neutral — iOS should need no change there.**
  Verify rather than assume, but do not start by rewriting it.
- Register platform hooks before any view loads, as `MainActivity.OnCreate`
  does: `NetworkDiscoveryScope.Current = <your iOS scope>`. Leave
  `MobileUsbConnector.Current` unset (see 2.1).
- Read the comment block in `MainActivity.cs` about Context lifetime — the iOS
  analogue is avoiding a retained view controller.

### 2.4 Build, deploy, QA

```bash
dotnet build Daqifi.Avalonia.iOS/Daqifi.Avalonia.iOS.csproj -c Release
```

Device deployment needs an Apple Developer account and a provisioning profile.
The simulator is fine for UI work but **cannot validate discovery** — the network
behaviour is the whole risk, so a real device on the same LAN as a bench Nyquist
is required before calling it done.

QA mirrors the Android list in
[#72](https://github.com/daqifi/daqifi-avalonia/issues/72): boots, WiFi discovery
finds devices, **tap-to-connect works**, stream + plot 16 ch @ 100 Hz renders,
`i:Icon` glyphs render, TextBox placeholder shows.

---

## Two traps in the build files that waste an afternoon

Both of these cost me real time on this repo, and neither produces an error
message that points at the cause.

**1. `--` is illegal inside an XML comment.** Writing something like
`-r/--runtime` in a `Directory.Build.props` or `.csproj` comment makes MSBuild
fail to load *the entire file* with `MSB4024`. The properties it defines then
silently do not apply, so a restore can still print "OK" while locked mode was
never armed. I did this twice. Write `-r / -runtime` or reword.

**2. Verify the artifact, not the exit code.** `cmd >/dev/null 2>&1 && echo OK`
reports OK for a silently degraded run. Check the evaluated value — e.g.
`dotnet msbuild -getProperty:RestoreLockedMode` — or the file that should exist.

And the WSL-specific one, which becomes a **macOS-specific** one for you: when a
check surprises you, re-verify the instrument before diagnosing the code. The
fastest way is an A/B against a known-good baseline (`git worktree` on a
pre-change commit) run *identically*. I once filed a ticket blaming a migration
for breaking a harness; the A/B proved the harness fine and turned up a real
18-screen visual diff instead.

---

## Working conventions in these repos

These are enforced, not stylistic:

1. **Conventional Commits** on every commit, PR and issue title
   (`feat:`, `fix:`, `chore(deps):`).
2. **Never push straight to `main`.** Branch, PR, and let CI run.
3. **CI must be green.** `.github/workflows/build.yml` builds every head, enforces
   NuGet lock files in locked mode, verifies the lock files are unchanged by
   restore, and checks Avalonia version lockstep across heads.
4. **A coverage gap becomes a ticket, not a rationale tweak.** If you cannot
   verify something, say so explicitly and file it.
5. **A gate that has only ever passed is not a gate.** If you add a check, show it
   failing on input it should reject before you trust it. This is
   [portomatic #386 — every gate must ship a demonstrated failing case](https://github.com/daqifi/portomatic/issues/386),
   and it exists because a run of separate checks in this project reported
   success while verifying nothing — including, twice, checks written
   specifically to prevent that.

---

## portomatic commands you will actually use

Every command below was run against this real project on 2026-08-01, and the
notes are what it actually did — not what the `--help` text implies.

```bash
P=".venv/bin/python -m portomatic"          # from the portomatic checkout
D="--downstream ../daqifi-avalonia"

$P doctor $D     # toolchains, grammars, workloads. Exits 1 if any are missing —
                 # expect that, since you will not have the android workload.
$P status $D     # port progress: sync watermark, shards, projection state.
                 # Also exits 1; read the output, not the exit code.
$P check $D      # classify upstream changes since the watermark.
                 # NOT read-only: writes a plan under .portomatic/plans/ and
                 # bumps last_check_at in .portomatic/sync_state.yaml. Check
                 # `git status` afterwards and revert if you were only looking.
```

**`portomatic coverage` does not apply here.** It is Python-only and exits 2 on
this project with *"no Python package found under . or src"*. Ignore it; the C#
equivalent is the symbol pairing in `status`.

### The visual parity gate — needs PR #388 merged first

`portomatic differential visual` is **not on `main` yet**. It lives on
`feat/visual-differential-384` ([PR #388](https://github.com/daqifi/portomatic/pull/388)).
If `differential --help` shows only `lift / specs / gen / compare`, you have a
build without it — wait for the merge or check out the branch.

Once it lands (needs the `visual` extra for Pillow):

```bash
$P differential visual <baseline-dir> <candidate-dir> --determinism
$P differential visual <baseline-dir> <candidate-dir> --threshold 0.001 --diff-out /tmp/diffs
```

**Always run `--determinism` first.** It captures the same side twice and
requires byte-identity. A harness that races its own animations produces
differences indistinguishable from a real regression — on this project that
produced a **65.6%** false positive that looked exactly like a catastrophic
visual break.

`--diff-out` must be a directory separate from both capture sets; the tool now
refuses an overlapping one. That guard originally compared paths case-sensitively
and **your Mac is the platform where that mattered** — APFS is case-insensitive by
default, so `--diff-out CAPS` against a capture dir `caps` slipped through and
overwrote the captures, self-concealingly (the amplified black-vs-white diff is
solid white, so the rerun compared white against white and reported "identical").
Fixed in #388 via `samefile` identity. Mentioned because if you are ever on an
older build, that trap is live and silent.

> **The capture harness does not run on your Mac yet.** Both legs of
> `tools/parity-audit` are pinned to `RuntimeIdentifier=win-x64` and its README
> requires the *Windows* .NET SDK at `C:\Program Files\dotnet\dotnet.exe`
> (`WpfCapture` genuinely needs Windows; `AvaloniaCapture` is pinned only because
> win-x64 Skia natives were the reliable option from WSL). So you can build and
> run the macOS app, but you cannot produce macOS captures to feed the visual
> gate without first unpinning `AvaloniaCapture`. That is its own ticket, not
> something to improvise mid-QA.

---

## Where to ask, and what to read first

- Port issues: `daqifi/daqifi-avalonia`
- Engine issues: `daqifi/portomatic`

Worth reading before you start, for why things are the way they are:

- [daqifi-avalonia #70 — the coordinated Avalonia 11.3 → 12.1 migration meta](https://github.com/daqifi/daqifi-avalonia/issues/70)
  (closed) — everything that moved, and why.
- [daqifi-avalonia #72 — Avalonia 12 visual + device acceptance](https://github.com/daqifi/daqifi-avalonia/issues/72)
  (closed) — the QA checklist actually run on Windows and Android. **This is the
  list you are mirroring on macOS and iOS.**
- [daqifi-avalonia #79 — parity captures are not hermetic](https://github.com/daqifi/daqifi-avalonia/issues/79)
  (closed as invalid) — worth 30 seconds purely as a caution: it was filed on a
  wrong diagnosis. The "leaked device state" was a hardcoded placeholder.

## Confidence, by section

This runbook was drafted from the Windows/Linux side and then partly corrected by
running it on an Apple-silicon Mac (2026-08-01, .NET SDK 10.0.203 — the SDK of
the day, before `global.json` pinned it; §0 has the version to actually install).
Trust it accordingly:

| Section | Status |
|---|---|
| portomatic commands | **Executed** against this project |
| §1.1 macOS build | **Executed** — succeeds; the predicted `ApplicationManifest` friction did not happen |
| §1.1 lock-file churn | **Reproduced** on macOS (one file, two lines) |
| §1.3 WMI degradation | **Reproduced** — exact log line quoted |
| §1.3 path-separator bug | **Found by running it**; fixed in this PR |
| §1.2 QA checklist | **Not run.** The app launches and the plot tick fires; nothing beyond that is verified — no device, no discovery, no export |
| §1.3 `System.IO.Ports` on macOS | **Not verified** |
| PART 2 (iOS) | **Not verified at all.** Written from the Android head by analogy, on a machine with no iOS SDK |

An earlier revision of this document opened by asserting nobody had ever built or
run this on a Mac. That was wrong — the app log already had a run recorded from
2026-07-25. Corrected here, and worth noting as a reminder that this runbook's
weakest claims are the confident ones about untested things.

If something here is wrong, saying so is more useful than working around it.
