# Parity-audit visual capture tooling

Renders the **original WPF app** (`daqifi-desktop`) and the **Avalonia port**
(desktop + Android mobile shell) to PNGs and builds side-by-side montages, so we
can compare the port against the original screen-by-screen and track UI-parity
gaps.

Used for the 2026-07-22 parity audit → issues **#5–#14**.

## What it captures

| Harness | App | How | Output |
|---|---|---|---|
| `AvaloniaCapture/` | the Avalonia port | boots the **real** app headless (Skia, no display) and drives the view-models | `<out>/avalonia/desktop-*.png`, `mobile-{portrait,landscape}-*.png`, `dialog-*.png` |
| `WpfCapture/` | the original WPF app | runs the **real** app off-screen, captures via `RenderTargetBitmap` | `<out>/wpf/wpf-*.png` |
| `montage.py` | — | pairs them left/right with labels | `<out>/montage/*.png` |

`coverage.py` in this directory is the **non**-visual half of the same question:
it diffs the two source trees rather than their pixels, at the symbol level by
default and at the *binding* level with `--bindings`. Nothing else here depends
on it; see its module docstring and `docs/parity-ledger.md`.

Both harnesses render **without a device connected** (empty states), driving the
UI by reflection — desktop tabs via `SelectedIndex` (0–4) and flyouts via the
VM booleans (`IsAppSettingsOpen`, `IsNotificationsOpen`,
`IsLiveGraphSettingsOpen`, `IsLogSummaryOpen`); the mobile shell is hosted in a
headless window at the Galaxy A16's true logical content size — portrait
(384×800) and landscape (820×360) — and navigated by raising `Click` on the named
nav buttons (`NavChannels`/`RailChannels`…); and since #213 the **dialogs** are
constructed directly with their real view-models and seeded per state, see
[Dialogs](#dialogs).
`DAQIFI_TEST_MODE=1` is set so no modal dialogs / firewall prompts appear and the
per-user data dir is used.

## Requirements

The two legs do **not** have the same requirements, and that is the whole shape of
this tool: `WpfCapture` boots the original WPF app and therefore needs Windows at
all, while `AvaloniaCapture` is RID-less and runs on whatever host you are sitting
at. So there are two supported host shapes.

**WSL — both legs, i.e. an actual parity comparison:**

- **Windows .NET 10 SDK** at `C:\Program Files\dotnet\dotnet.exe`. Both harnesses
  are driven with the *Windows* dotnet from WSL, because WPF requires Windows.
- The sibling repo **`daqifi-desktop`** checked out next to this one
  (`GitHub/daqifi-desktop`), for the WPF leg. If it lives elsewhere, edit the
  `ProjectReference` in `WpfCapture/WpfCapture.csproj`.
- **python3 + Pillow** in WSL for the montages (`pip install pillow`).

**macOS / Linux — the Avalonia leg only:**

- A .NET 10 SDK that satisfies the repo's `global.json` (pinned, `rollForward:
  disable`). `run.sh` uses `dotnet` from `PATH`; if that one cannot satisfy the
  pin, point it at one that can: `DOTNET=~/.dotnet/dotnet ./run.sh`.
- Nothing else. No Pillow, no sibling repo — the WPF leg and the montages are
  skipped, loudly, and the script says so rather than printing "Done" over a
  half-run comparison.

This is what closed #89: the harness used to pin `RuntimeIdentifier=win-x64`, so
the visual gate had **no macOS coverage at all**. It now has a macOS capture leg.
It still has no macOS-vs-WPF comparison, because there is no WPF anywhere but
Windows — see [Limitations](#limitations).

## Run

```bash
# from WSL, in this directory — both legs + montages
./run.sh                # writes to ./out (gitignored)
./run.sh /some/out/dir  # or a custom out dir

# from macOS/Linux — Avalonia leg only, into <out>/avalonia
DOTNET=~/.dotnet/dotnet ./run.sh /some/out/dir

# prove the capture is deterministic on this host BEFORE trusting any comparison
./run.sh --determinism /some/out/dir                       # 5 runs
DETERMINISM_RUNS=10 ./run.sh --determinism /some/out/dir    # more, if you are chasing a flake

# then: capture once and diff every screen against the committed baseline
./run.sh --check-baseline /some/out/dir

# or run that same comparison over captures you already have — which is what CI does
# with the determinism run's own first set, so the two checks share one capture
./run.sh --check-captured /some/out/dir/determinism/r1
```

Every mode fails non-zero on anything it cannot vouch for, including an **incomplete**
capture. `AvaloniaCapture` declares the screens it is contracted to produce
(`ExpectedScreens`) and fails the run if any is absent or if it wrote one that is not on
the list — because several capture sites can decline to fire and only print `[SKIP]`, and
a screen dropped that way vanishes from every downstream comparison too. A determinism
run would then report a clean `17/17` and a baseline check would never look for the
missing name. A gate that quietly shrinks its own scope is worse than no gate.

### Always run `--determinism` on a host you have not captured from before

It captures the Avalonia side N times into `<out>/determinism/r1..rN` and requires
every PNG to be byte-identical to the first run's, failing non-zero if any differs,
is missing, or if there is nothing to compare at all.

Since #191 this is also a **CI gate**: the `Desktop head on macOS` job runs it on every
pull request, additionally asserts that the capture produced all 24 screens, and uploads
`<out>/determinism/` as an artifact when it fails, so the differing PNGs are in hand.

Since #188 the **baseline is gated there too, over the same captures**: the job runs
`--determinism` first, and then points `--check-captured` at `determinism/r1` — one of
the sets every other run was just shown to byte-match. So a green macOS job says both
"this runner is stable" and "this runner reproduces the bytes recorded on a developer's
Mac", about one set of bytes, for the cost of the captures that had already happened.

The order is load-bearing rather than tidy. A baseline check on a host whose captures
race is a machine for manufacturing differences that are not regressions — which is
exactly why the baseline half stayed out of CI until #202 removed the last such race.

That artifact also carries each pass's `.capture-determinism-N.log`, copied in under
`capture-logs/` with the leading dot dropped. The job has to copy them because
`run_capture` writes them at the output *root*, one level above the directory being
uploaded, and as dotfiles — so nothing under the upload's `path:` matched them. Without
that copy the one failure with nothing else to go on, a pass that dies before writing a
single PNG, produced an artifact with no files in it at all.

This is not ceremony. Avalonia's animation clock advances with wall time, so whether
a fade-in has finished when the shutter opens is a race, and a racing harness
produces differences indistinguishable from a real regression: on this project two
runs of the *same binary at the same commit* once differed on **65.6%** of pixels,
which read as a catastrophic visual break and was a half-finished fade.
`AvaloniaCapture` refuses to save a frame it cannot prove is still (`SettledFrame`),
and the per-screen `settled in N round(s)` line reports how long that took.

**The first macOS run of this mode found two ways that settle loop could still lose
the race**, both fixed in the same change that unpinned the RID:

- Consecutive frames were compared with no time between them, so a transition sitting
  at 20% opacity produced two identical samples and the loop stopped there. One run in
  eight saved `mobile-portrait-3-storage` with **81% of its pixels** wrong — the whole
  pane at the wrong opacity, reported as `settled`. Fixed by spacing the samples
  (`SettleSampleInterval`).
- The three right-hand drawers are one `SplitView`, and the sweep opened the next one
  while the previous close was still animating. That left a one-pixel-wide flip on the
  pane edge, 50/50 per run.

Both are in `Program.cs` with the measurements attached. The lesson generalises: a
settle loop is only as good as the evidence that it settled, and "two samples agreed"
is not evidence unless something could have changed between them.

**The third one was not a settle problem at all** (#201), and it is why `Encode` no
longer calls `CaptureRenderedFrame`. That reads the headless *framebuffer* — one
persistent surface the compositor updates in place, a dirty region at a time — so what
came back depended on the sequence of partial redraws that built it, not only on what
the window looked like. A settle loop cannot see that: a picture assembled by one
sequence of redraws is exactly as *still* as the same picture assembled by another.
Concretely, with the `SplitView` pane open the column immediately left of it (x=1059,
the 1440-minus-380 edge) was covered by more than one redraw, and 8-bit quantisation
between passes left ±1 on the six pixels per screen where a content divider crosses it
— on three screens, always the same values, sub-perceptual, and enough to fail byte
identity **~1 capture run in 5 on `macos-latest`** while never failing in 40 on the
baseline Mac. Waiting longer made it worse, because more time means more redraws.

`Encode` now renders the window into a fresh `RenderTargetBitmap`, which starts blank
and is drawn once, so the bytes are a function of the visual tree and nothing else.
Switching left 15 of the 18 screens byte-identical; the three that moved are the
`SplitView` flyouts, by those 18 pixels, and their new hashes are the ones CI had been
intermittently producing all along. The settle loop stays — it fixes a tree that is
genuinely still moving, which this does nothing about.

**Why five runs and not two.** Both defects above were roughly coin-flips per run, and
two runs miss a 50/50 flip half the time; five gets that to ~6%, ten to ~0.2%, at about
15 s a run. portomatic's own `--determinism` captures twice — this is the same check at
the sample size the failures here actually call for.

The equivalent engine-side gate is `portomatic differential visual <a> <b>
--determinism` (portomatic #388), which also does the screen-by-screen comparison
against a candidate set. `--determinism` here is the dependency-free version of the
same check, for confirming a host before you feed captures to anything.

### Output paths under WSL (read this before hunting for missing PNGs)

Under WSL the harnesses are launched with the **Windows** dotnet, so they execute on
the Windows .NET runtime through interop and `Path.GetFullPath` resolves a
Linux-style argument against a Windows root:

| you pass | files actually land in | Linux sees them at |
|---|---|---|
| `/tmp/shots` | `C:\tmp\shots` | `/mnt/c/tmp/shots` |
| `C:\tmp\shots` | `C:\tmp\shots` | `/mnt/c/tmp/shots` |

This cost an hour once (#74): the tool reported 18 successful captures while
`ls /tmp/shots` showed an empty directory. `AvaloniaCapture` now prints the
**resolved** directory up front, flags the rewrite when it happens, and reports
the byte count of every file it writes — so that particular silent no-op is no
longer possible. Pass a Windows-style path when you want both sides to agree on
one location.

Note that `ls` hides dotfiles: the isolated `.appdata` directory (throwaway DB +
logs) is there even when a plain `ls` of the output dir suggests otherwise.

`run.sh` runs all three steps. To run a single harness directly:

```bash
# WSL
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
"$DOTNET" run --project AvaloniaCapture/AvaloniaCapture.csproj -c Release -- "C:\\path\\to\\out\\avalonia"
"$DOTNET" run --project WpfCapture/WpfCapture.csproj      -c Release -- "C:\\path\\to\\out\\wpf"
python3 montage.py /mnt/c/path/to/out

# macOS / Linux
~/.dotnet/dotnet run --project AvaloniaCapture/AvaloniaCapture.csproj -c Release -- /path/to/out/avalonia
```

**Never pass `-r` to a build or restore of `AvaloniaCapture`.** An explicit-RID
restore rewrites its `packages.lock.json` to that single RID and the next CI restore
fails `NU1004`; `Directory.Build.props` documents this at length. The project is
RID-less on purpose and resolves each host's native assets through `deps.json` at run
time.

## Dialogs

Every `Window` the app shows through `IDialogService` used to be **outside the visual gate
entirely** — the harness rendered the desktop panes, the drawers and the mobile shell, and
constructed no dialog at all (#213). That mattered more here than it would elsewhere,
because **XAML in this repo is not compile-checked**: the views carry no `x:DataType`, so
bindings resolve by reflection at run time. A green build cannot tell you whether a new
`TextBlock` renders, whether its binding resolves, or whether a `StaticResource` brush
exists. Three changes in a row (#209, #218, #219) therefore shipped with an explicit
"I did not render this dialog" disclosure.

`CaptureDialogs()` in `AvaloniaCapture/Program.cs` closes that. It runs **last**, after the
desktop and mobile phases, and captures one PNG per seeded state:

| screen | what it pins |
|---|---|
| `dialog-connect-wifi-scanning` | the empty state every open of the connect dialog starts in |
| `dialog-connect-usb-idle` | USB tab with one discovered device, no error |
| `dialog-connect-usb-error` | the same, plus `SerialConnectError` (#207/#209) |
| `dialog-connect-manual-usb-error` | Manual USB tab with `ManualPortError` |
| `dialog-export-configure` | the export dialog's configuration form |
| `dialog-export-failed` | its result state with `ExportSucceeded` false |

### Adding one

Append a `DialogScreen` to `DialogScreens()`, and add its name to `ExpectedScreens` — the
two lists are checked against each other in both directions, so forgetting either fails the
run rather than silently shrinking the gate. Then re-record the baseline (below) and bump
`expected=` in the macOS determinism step of `.github/workflows/build.yml`, which states the
screen count independently on purpose.

```csharp
yield return new DialogScreen
{
    Name = "dialog-something-state",
    Size = (560, 500),                       // the size the window's own AXAML declares
    Build = () => new SomeDialog { DataContext = SeededViewModel() },
    Prepare = w => SelectTab(w, "dialog-something-state", "USB"),   // needs a realized tree
    Inspect = w => RequireRenderedText(w, "dialog-something-state", "SomeAutomationId", Text),
};
```

Four rules, each of which cost someone real time:

- **Never start discovery.** See [Side effects](#side-effects-both-harnesses-boot-the-real-app).
  Seed the bound collection directly, with fixed literal values — anything a real finder,
  the clock or the environment produced would land in the PNG and the baseline would then be
  per-machine.
- **An indeterminate `ProgressBar` never settles**, so a dialog containing a visible one
  fails `SettledFrame` after all 40 rounds with a message that says nothing about progress
  bars. The connect dialog puts one behind a "Scanning…" overlay on three of its five tabs,
  so this is the *default* state of the dialog most likely to be captured next.
  `SettleIndeterminateProgress` now fails loudly and **by name** instead, and offers two
  remedies: **preferred**, seed the state that hides the overlay (a device in the list, and
  `HasNoSerialDevices = false`) — nothing is faked, that is a state the app really has; or
  set `FreezeIndeterminateProgress` when the scanning state *is* the subject, which stops
  the animation and logs that it did, because a frozen bar renders as its empty track rather
  than as the moving indicator a user sees. The settle rule itself is never loosened for
  everything: CI has caught a real six-pixel race with it (#201).
- **Assert the element, not just the picture.** A `TextBlock` whose binding silently failed
  to resolve and one that rendered an empty string are indistinguishable in a screenshot,
  and with no `x:DataType` the first is exactly what a renamed view-model member produces.
  `RequireRenderedText` reads it back out of the visual tree by
  `AutomationProperties.AutomationId` and prints type, visibility, bounds, foreground brush
  and text, separately from the pixels.
- **Select tabs by header, not by index** (`SelectTab`). An index is silently wrong the
  moment a tab is inserted before it, and the wrong tab saved under the right filename
  passes every check this tool has while being a picture of something else.

### Not covered: the autosizing alert family

`ErrorDialog`, `SuccessDialog`, `DuplicateDeviceDialog` and `MessageDialog` are
`SizeToContent`, and they are **deliberately absent**, because headless does not size them
the way a real window does. Measured: `DuplicateDeviceDialog` declares
`MinWidth="480" MinHeight="280"`, and headless its `Bounds`, `ClientSize`, `DesiredSize` and
`Width`/`Height` all come back **460×248** — under its own stated minimum. Nothing in the
headless windowing stack applies a window's min/max the way a platform window does, so a
capture at that size would be a picture of a dialog no user has, and pinning an arbitrary
size instead would photograph a layout the app never produces. Every screen this harness
takes is at a size the harness itself set, and that stays true. Covering this family needs
either a headless window that honours the constraints or a decision about what the "real"
size is; it is not a matter of adding another `DialogScreen`.

`FirmwareDialog` is absent for a different reason: `FirmwareDialogViewModel`'s constructor
starts a network fetch (`LoadFirmwareOptionsAsync`) that populates a bound `ComboBox`
asynchronously, so its rendered state is a race against github.com. Capturing it needs an
injected stub `IFirmwareDownloadService`, which makes the picture a picture of a stub.

## Baselines

`baselines/<os>-<arch>.sha256` records the SHA-256 of all 24 Avalonia screens for one
host — a dated reference point for "has anything moved". `./run.sh --check-baseline`
captures once and verifies against the one for the current host, failing on a changed
screen, a missing one, and one the baseline does not list (`shasum -c` only checks the
names it was given, so the extra-file direction is checked separately).

`desktop-7-notifications-flyout.png` alone was re-recorded 2026-09-05 for #250 (macOS 26.5,
Apple silicon, .NET SDK 10.0.302), which gives the notifications flyout the named empty state
every other pane already has instead of a blank panel. **One** hash moved: the other
twenty-three are byte-for-byte unchanged against the pre-fix capture. The pre-fix capture
matched the then-committed manifest on all 24 — so the blank panel is what was shipping,
rather than an artifact of the harness — and five `--determinism` runs agreed 24/24 after the
change.

`desktop-9-summary-flyout.png` alone was re-recorded 2026-09-03 for #228 (same host and
environment as the #213 line below), which unclips the Log Summary SETTINGS label and
moves Reset off the status toggle. **One** hash moved: the other twenty-three, including
every mobile and dialog screen, are byte-for-byte unchanged against the pre-fix capture —
which is the evidence that a fix inside one flyout's grid did not reach anything else.
Five `--determinism` runs agreed 24/24 before the change and five after, and the pre-fix
capture matched the then-committed manifest on all 24, so the recorded bytes are the ones
that were shipping. The recording is **not** single-host: on the PR that made it, the
`macos-latest` runner (macOS 26.5.2 / 25F84, `macos26/20260728.0273.1`) passed its own
five-run determinism check 24/24 and then reproduced this manifest, new hash included —
so the cross-machine agreement described further down holds for the re-recorded screen
too, rather than being inherited from the recording that preceded it.

`macos-arm64.sha256` was extended 2026-09-03 for #213 (macOS 26.5 build 25F71, Apple
silicon, .NET SDK 10.0.302, Avalonia 12.1.1) with the six `dialog-*` screens, after 10
consecutive `--determinism` runs on that host agreed 24/24. That recording is **purely
additive**: all eighteen existing hashes are byte-for-byte unchanged, which is the evidence
that adding the dialog phase moved nothing that was already gated.

It was re-recorded 2026-09-02 for #201 on the same host, after 20 consecutive
`--determinism` runs. Fifteen of the eighteen hashes were unchanged from the 2026-09-01
recording against `6834469`; the three `SplitView` flyouts moved by the six pixels each that
#201 is about. No other host has a baseline **file**, and a second Apple-silicon host does not
want one: it wants to match this one, which since #188 is what CI checks on every pull
request. The mode still tells you how to record a baseline for a genuinely new OS+arch,
and still refuses to invent a comparison without it.

**A mismatch is a prompt, not a verdict** — re-read that environment line first. In CI
the same applies with one addition: the baseline step prints the runner's macOS build and
image version on **every** run, green ones included, so a red one can be read against the
last green one instead of guessed at. In order of likelihood, red means a UI change whose
commit did not re-record the manifest, a real visual regression, or the `macos-latest`
image having moved under us — the third being the one your own Mac cannot reproduce.
There is a fourth, and it is the interesting one: a red that does **not** reproduce is a
capture race that survived five determinism samples (which miss a 50/50 per-run flip
about 6% of the time), i.e. a fourth instance of the defect class #179 and #201 each
turned out to be. That wants a ticket, not a re-run. The
PNGs themselves are deliberately *not* committed: the harness is deterministic, so they
are exactly regenerable from any commit, and 640 KB of binaries per recording would be
permanent git weight for data that has a one-command source. Re-record after an intended
UI change with `shasum -a 256 *.png` from the capture dir, in the same commit as the
change, and update the environment line above with it.

**Record a baseline only after `--determinism` passes on that host.** A baseline taken
from a host that races its own animations bakes one arbitrary frame in as the truth.

**Cross-*machine* reproducibility is measured now, and it holds** (#188). A GitHub
`macos-latest` runner — a different Apple-silicon machine on a different macOS build
(26.5.2 / 25F84, image `macos26`, against 26.5 / 25F71 on the recording Mac) —
reproduced all eighteen hashes byte for byte, and does so on every pull request as part
of the macOS job — which is what will settle the same question for the six dialog screens
added in #213. So the manifest's OS+arch name is earned rather than assumed, and it
stays earned: the day two Apple-silicon hosts stop agreeing, a red tick says so.

Why that works is worth knowing, because it is what makes the name reasonable in the
first place: the render is headless *software* Skia with an embedded Inter font and
HarfBuzz shaping, every piece of it from a pinned package, so very little about the host
is on the path to a pixel.

Two hosts is still not every host. If `--check-baseline` fails on a **third** machine,
the environment line above is the first thing to compare — but "the UI regressed" is now
a likelier reading than it was, because a mismatch has to explain why two machines
agreed and yours does not.

## Implementation notes / gotchas

These cost real time to discover — leaving them here:

- **`IClassicDesktopStyleApplicationLifetime` is not user-implementable** (Avalonia
  seals it with an internal member). Use the real `ClassicDesktopStyleApplicationLifetime`
  from `Avalonia.Desktop` and `SetupWithLifetime(...)` — windowing stays headless as
  long as you never call `UsePlatformDetect`.
- **The original app's assembly name is `DAQiFi`**, not `Daqifi.Desktop` (that's only
  the C# namespace). Pack `;component` URIs therefore use `/DAQiFi;component/...`.
- **`Application.ResourceAssembly` can't be set after WPF init** (throws). The original
  `App.xaml` merges an entry-relative `"/Resources/DesignTokens.xaml"`; when the app is
  *hosted* from this harness the entry assembly is the harness, so `WpfCapture/Resources/DesignTokens.xaml`
  is a shim that re-merges the real dictionary by explicit `/DAQiFi;component/...` URI.
- **Every leading-slash pack URI in a window the app shows during startup resolves into
  `WpfCapture`, not `DAQiFi`.** Two exist: `App.xaml`'s `/Resources/DesignTokens.xaml`
  (shimmed above) and `MigrationStatusWindow.xaml`'s `<Image Source="/Images/DAQiFi.png">`,
  which pops whenever the DB has pending migrations — deterministic on a fresh
  `%LocalAppData%\DAQiFi` DB, or once the port's applied migrations lag the sibling's.
  `WpfCapture.csproj` links the real `Images/DAQiFi.png` in at that logical path so the
  window loads instead of crashing the leg with "Cannot locate resource images/daqifi.png".
  If the app grows a third startup-window entry-relative URI, shim it here too.
- Faithful pixels need `UseSkia()` + `UseHeadless(new AvaloniaHeadlessPlatformOptions
  { UseHeadlessDrawing = false })`. Capture with a fresh `RenderTargetBitmap`, **not**
  `window.CaptureRenderedFrame()` — that one reads the headless framebuffer, which the
  compositor updates in place a dirty region at a time, so its contents depend on the
  redraw history and not only on the tree (#201, and the long note on `Encode`).
- **`AvaloniaCapture` declares no `RuntimeIdentifier`, and that is load-bearing.** A
  pinned RID does not merely bias the pixels — it makes the harness unrunnable on
  every other host, because the build produces that platform's apphost and only that
  platform's native assets. RID-less, NuGet still lays every package's natives out
  under `runtimes/<rid>/native` and the host picks the right ones through `deps.json`,
  so one source tree captures on macOS, Linux and Windows from the same `dotnet run`.

## Side effects (both harnesses boot the REAL app)

Faithful captures require booting the real app's DI + services, so a capture run has the
same side effects as launching the app once. The Avalonia harness is isolated from your
real state (both gaps below were closed in issue #18):

- **Isolated data dir / DB.** The harness sets `DAQIFI_DATA_DIR` to a throwaway `.appdata`
  subdirectory of the run's output dir, which the app honors before migrating, so a capture run
  reads/migrates an isolated DB and never touches the `%LocalAppData%\DAQiFi\DAQiFiDatabase.db`
  a normal dev run uses. (It's cleaned with the rest of the output dir; delete it to reset.)
- **No hardware discovery.** Under `DAQIFI_TEST_MODE=1` the app skips starting
  `BootloaderWatcher`, so a capture no longer takes an *exclusive* HID handle on a device
  sitting in bootloader mode — it's safe to run alongside a HID-bootloader flash. Serial and
  WiFi discovery are not running either, and **this is load-bearing, not incidental** — a
  capture that saw a real device would render its serial number or IP, and that screen would
  then differ run to run for reasons that have nothing to do with the UI. Re-checked on macOS
  on 2026-09-01 with a board attached on `/dev/cu.usbmodem1101`: `desktop-4-devices.png` shows
  the empty "NO DEVICES CONNECTED" state, and all 18 screens were byte-identical across twelve
  runs.

  What guarantees it is that **discovery is a separate call from construction**.
  `ConnectionDialogViewModel`'s constructor starts nothing; `StartConnectionFinders()` does,
  and its only caller is `DaqifiViewModel.ShowConnectionDialogAsync`. Since #213 the harness
  *does* construct `ConnectionDialog` (see [Dialogs](#dialogs)) — so the guarantee is no
  longer "the harness never touches that dialog", it is that **no capture scenario may call
  `StartConnectionFinders()` or otherwise start a finder**. A real `SerialDeviceFinder` opens
  every DAQiFi VID/PID COM port on the machine. Scenarios seed `AvailableSerialDevices`
  directly instead, with fixed literal values. (Constructing a `SerialStreamingDevice` is
  inert: its `SerialPort` is constructed, never opened.)

> Note: the **WPF** harness boots the sibling `daqifi-desktop` app, which does not have these
> overrides, so it still shares the real per-user DB. Prefer running it before your first
> normal launch of the day, or against a throwaway `%LocalAppData%\DAQiFi`.

## Limitations

- **No WPF side off Windows, so no cross-platform parity comparison off Windows.**
  The macOS/Linux run produces the Avalonia half only. That half is a real regression
  baseline — capture, re-capture, compare — but it is not the WPF-vs-Avalonia diff
  this directory is named for. Producing that still requires a Windows host, and no
  amount of work on `AvaloniaCapture` changes it: `WpfCapture` targets
  `net10.0-windows` and hosts the sibling WPF app.
- Empty-state only (no device). **Connected-state** parity (e.g. does the mobile
  landscape Live Graph show the plot while streaming — issue #10) needs a real
  device capture; the mobile shell here is rendered headless at phone sizes, not
  on an actual phone (no Android system chrome, real fonts, or touch targets).
