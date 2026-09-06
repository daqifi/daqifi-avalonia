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

**Go through `run.sh`, not a bare `dotnet run`.** It builds `-c Release`, and that is
load-bearing: a **Debug** capture cannot reproduce the committed baseline on any host at
any commit, because `MobileShellView` appends `AppVersion.ShortBuildMetadata` to the
version line under `#if DEBUG` and that metadata is the git commit SourceLink stamps into
`InformationalVersion`. The mobile stream screens then carry the short SHA of whatever
commit built them. It presents as a regression rather than as a mistake — a couple of
screens differ by a tenth of a percent of their pixels while every other screen is
byte-identical, and a Debug capture is self-consistent, so repeating it confirms the
difference instead of exposing it. The tell is that the differing pixels are a short hex
string beside the version number (found from the wrong end in #262).

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
pull request, additionally asserts the size of the capture set independently of the
harness's own `ExpectedScreens` list (the workflow states the number itself), and uploads
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

**The fourth was a settle problem again**, and it is the reason `SettledFrame` now wants
**three** identical frames rather than two (#253). Spacing the samples makes "nothing
changed" evidence only while the thing that might be moving moves faster than a pixel per
interval — and an *eased* transition does not, at its end. The three right-hand drawers open
by animating the `SplitView` pane's width to `OpenPaneLength=380`, and the last 1% of that
ease crawls: one 50 ms gap can pass with every pixel rounding to the same value. Instrumented
immediately after the capture returned, the pane came back `340, 0, 380, 447` on most runs
and `344, 0, 376, 447` on others — settled, saved, and four pixels short of open, which
re-lays out every glyph inside the pane and moves **7,063** of its pixels. It flipped about
one run in three, on the minimum-size drawer screens that first ran into it; the full-size
ones have the same shape and have simply been landing past the end of the ease. Requiring the
agreement to hold across two intervals instead of one fixed it, and moved **no** bytes: all
34 screens are identical to the pre-fix capture, so the rule removed a chance of saving the
wrong frame rather than changing which frame is right.

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
| `dialog-firmware-uploading` | the bootloader dialog's upload scrim and its Cancel button (#241) |

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

## Window size, and the theme the whole manifest assumes

Two things every hash here silently depended on until #253, both of which the harness now
states out loud.

### The minimum window size (`desktop-min-*`)

The desktop sweep runs **twice**: once at 1440x900, and once at the window's own declared
minimum. `CaptureDesktop` reads that minimum off `MainWindow` (`MainWindow.axaml`:
`MinWidth="720" MinHeight="480"`) rather than restating it, so changing the AXAML
re-renders these screens and the baseline check reports it, instead of the two quietly
disagreeing. A window that declares no minimum arrives as `0x0` and fails the run — a
capture at a size the app does not name would gate nothing but the harness's own opinion,
which is also why there is no third, invented size.

The minimum is where clipping and overlap happen, and it had never been rendered: a green
visual gate meant "1440x900 did not change". It is a desktop-only sweep, because it is the
desktop `Window` that carries the constraint — the mobile shell is already captured at the
Galaxy A16's true logical size, and each dialog at the size its own AXAML declares.

Worth knowing before reading a `desktop-min-*` diff: the flyouts are one `SplitView` with a
fixed `OpenPaneLength="380"`, so at 720 wide the pane covers 380 of the window and the
pane's own content does **not** reflow with width. Measured when these were recorded, the
pane region of `desktop-min-6` and `desktop-min-8` was pixel-identical to the 1440-wide
capture's; `-7` and `-9` differed, because their empty states are vertically centred and
the window is 480 tall rather than 900. All nine are captured anyway — "the desktop set, at
both sizes" is a rule that needs no per-screen re-derivation and stays right when a flyout
later grows content that does reflow, which a curated subset would not.

### The theme pin (no light-theme screens, and why)

Every screen in this manifest is a **Dark** rendering. That is a property of the app, not of
the harness: `App.axaml` pins `RequestedThemeVariant="Dark"`, `DesignTokens.axaml` ships
`Dark` and `Default` carrying identical dark values and **no `Light` dictionary**, and no
view-model, view or setting exposes a theme switch. There is no light theme to gate.

The harness can nonetheless *render* one — it is one property assignment, and #253 measured
what comes back. With the pin removed the app follows the host, and this headless platform
reports **Light**: the resulting capture is byte-identical to one with `ThemeVariant.Light`
forced, and **10 of the 25 screens then in the manifest changed** — `desktop-8`,
`desktop-9`, `dialog-connect-wifi-scanning`, `dialog-export-configure`,
`dialog-firmware-uploading`, and all five mobile Stream/Storage/Settings screens. Those are
the surfaces built from Fluent's stock control chrome rather than from `DesignTokens.axaml`
brushes, and Fluent's chrome is the half of the app that *has* a light variant. The app's
own tokens do not, so what comes out is a hybrid — light controls on dark panels — that no
user can reach while the pin holds.

So there are no light baselines, and adding them would be worse than adding nothing:
it would freeze an unreachable hybrid as "correct" and double the gate's flake surface for
it. What the harness does instead is `RequireThemePinnedDark()`, which fails the run if
`RequestedThemeVariant` or `ActualThemeVariant` is anything but `Dark` and prints the host's
own variant beside them for context. That catches the one edit that makes the hybrid
reachable — deleting an attribute from `App.axaml`, which breaks no build and fails no other
test — for zero screens and zero run time. If the app ever grows a real light variant, that
assert is what to replace with light captures, and the ten screens above are the ones that
will move.

## Baselines

`baselines/<os>-<arch>.sha256` records the SHA-256 of every Avalonia screen the harness
produces, for one host — a dated reference point for "has anything moved". The count is
deliberately not restated here: it is `ExpectedScreens` in `AvaloniaCapture/Program.cs`,
and the workflow's own `expected=` in `.github/workflows/build.yml`, which are the two
places that are checked against reality on every run. `./run.sh --check-baseline`
captures once and verifies against the one for the current host, failing on a changed
screen, a missing one, and one the baseline does not list (`shasum -c` only checks the
names it was given, so the extra-file direction is checked separately).

Seven screens were re-recorded 2026-09-05 (macOS 26.5, Apple silicon, .NET SDK 10.0.302) for
the empty-state consolidation: the five desktop panes that hand-rolled the badge/title/sentence
pattern with literal attributes — Devices, Channels, Logged Data, Profiles and Device Logs — now
use the app-wide `emptyBadge`/`emptyTitle`/`emptyBody` classes the mobile panes and both flyouts
already use. It is a **visual** change, not a pure refactor, and was taken deliberately: the
title is SemiBold at `FontSizeSmall` rather than Bold at a hardcoded 11, the body is
`TextSecondary` at `FontSizeBody` rather than Light `TextPrimary`, the badge loses a 1px
`BorderDim` rim, and `emptyBody`'s `MaxWidth` went 360 → 420. Four desktop screens moved
(`desktop-2`, `-3`, `-4`, `-5`) — four and not five because Device Logs is a pivot *inside* the
Logged Data pane and no screen in this manifest selects it, so that fifth conversion is the one
part of the change the visual gate does not cover and was checked by rendering it by hand.
Three mobile **landscape** screens moved too
(`mobile-landscape-2`, `-3`, `-4`), because 820 logical px is wider than either `MaxWidth` cap
and so the cap is what binds there; portrait is 384 and its own content width binds first, which
is why no portrait screen moved. The other eighteen are byte-for-byte unchanged against the
pre-change capture, and that capture reproduced the then-committed manifest on all 25 — so the
attribute soup is what was shipping rather than an artifact of the harness. Five
`--determinism` runs agreed 25/25 before the change and five after.

The recording is **not** single-host. On the PR that made it the `macos-latest` runner — macOS
**26.6.2** (25G83), arm64, image `macos26/20260831.0337.3`, a different machine on a different
macOS build from the 26.5 Mac that recorded it — passed its own five-run determinism check
25/25 and then reproduced this manifest, all seven new hashes included. So the shift these
seven screens record is a property of the change rather than of the host that captured it.

The two unchanged screens worth naming are `desktop-7-notifications-flyout` and
`desktop-9-summary-flyout`: both render their empty states through these same classes as of
#258 and #249, and both are byte-identical across the change. That is the measurement behind
the claim that the `MaxWidth` bump is inert at flyout width — the pane is narrower than either
cap — rather than an assumption about it.

`mobile-landscape-3-storage.png` was captured **after** #252 (PR #265) landed, and is the one
line here that had to be. The two changes collided on it: #252 removes the pane heading and a
48px margin from the same view whose empty-state sentence this re-flows, so the hash each had
recorded independently described a rendering that would not exist once both were in. Measured
rather than assumed — the pre-#252 capture of that screen does not match the post-#252 one, and
a line-level merge of the two manifests would have been green on neither side. The whole
recording above was therefore taken on the merged tree, against a base that already contained
#252 and #262. `mobile-portrait-3-storage` is #252's alone: portrait is inert for this change,
so it is byte-identical here and keeps the hash #252 gave it.

The general rule this is an instance of: a hash records a rendering of a **base commit**, not of
a diff. Two PRs that re-record the same screen cannot be merged line-wise, and neither can a PR
whose branch has fallen behind — every gate here stays green on a stale base, because the
manifest in the tree is stale in exactly the same way.

`macos-arm64.sha256` was extended 2026-09-05 for #241 (macOS 26.5, Apple silicon, .NET SDK
10.0.302) with `dialog-firmware-uploading.png` — the bootloader dialog's upload scrim, whose
new Cancel button is reachable only through a reflection binding and so is invisible to every
other check in the repo. Five `--determinism` runs agreed 25/25, and the same capture
reproduced the previous manifest on **all 24** existing hashes, byte for byte: the recording
is purely additive, and the change that motivated it moved nothing that was already gated.

`desktop-7-notifications-flyout.png` alone was re-recorded 2026-09-05 for #250 (macOS 26.5,
Apple silicon, .NET SDK 10.0.302), which gives the notifications flyout the named empty state
every other pane already has instead of a blank panel. **One** hash moved: the other
twenty-three are byte-for-byte unchanged against the pre-fix capture. The pre-fix capture
matched the then-committed manifest on all 24 — so the blank panel is what was shipping,
rather than an artifact of the harness — and five `--determinism` runs agreed 24/24 after the
change.

Those two recordings were made independently in separate open PRs and both counts above
are as-of that PR: after both land the manifest lists **25** screens, `desktop-7` carries
#250's post-fix hash, and #241's addition is the twenty-fifth line. The two changes touch
different lines of `macos-arm64.sha256` — one appends, one rewrites — so the merged
manifest is the union, and `--check-baseline` on the merged tree is 25/25.

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

`desktop-2-loggeddata.png` alone was re-recorded 2026-09-05 for #251 (macOS 26.5, Apple
silicon, .NET SDK 10.0.302), which stops collapsing the Logged Data plot when no session is
selected, so the region draws the same empty labelled axes Live Graph already draws instead
of a featureless black rectangle. **One** hash moved: the other twenty-three are
byte-for-byte unchanged against the pre-fix capture, and the pre-fix capture matched the
then-committed manifest on all 24 — so the void is what was shipping rather than an artifact
of the harness. Five `--determinism` runs agreed 24/24 after the change. Deliberately placed
below the #228 paragraph rather than at the top of this list: #250 was re-recording
`desktop-7` in a separate open PR at the same time, and the two entries land in different
hunks this way. Both touch `macos-arm64.sha256`, but on different lines.

`mobile-landscape-3-storage.png` **and** `mobile-portrait-3-storage.png` were re-recorded
2026-09-05 for #252 (macOS 26.5, Apple silicon, .NET SDK 10.0.302), which stops the mobile
Storage pane pushing its empty state below the fold. **Two** hashes moved, and they are the
two orientations of the one view that changed — every other screen in the manifest is
byte-for-byte unchanged against the pre-fix capture, and that pre-fix capture matched the
then-committed manifest on every screen it lists, so the clipped empty state is what was
shipping rather than an artifact of the harness. Five `--determinism` runs agreed after the
change. This is the first entry in this list to move **two** screens, and the pair is the
point rather than a slip: the fix is orientation-agnostic — nothing about it is gated on
landscape — so the portrait rendering of the same pane necessarily moves with it, and a
recording that touched only the landscape screen would be recording half of what changed.
The recording is **not** single-host: on the PR that made it the `macos-latest` runner — macOS
**26.6.2** (25G83), arm64, image `macos26/20260831.0337.3` — passed its own five-run
determinism check and then reproduced this manifest, both new hashes included.
Deliberately placed between the #251 and #213 paragraphs: three other open PRs were writing
to this file at the same time, at the head of this list and at its foot, and this position
keeps every entry in a hunk that does not touch the others. No screen count is stated above
because one of those PRs adds a screen; the counts in the neighbouring paragraphs are true
as of the recordings they describe.

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

`desktop-9-summary-flyout.png` alone was re-recorded 2026-09-05 for #249 (macOS 26.5, Apple
silicon, .NET SDK 10.0.302), which takes the Log Summary flyout off the last MahApps-blue
GroupBox header left in the app and onto the full-bleed section, grey caps label and pill
button its sibling Plot Settings flyout already uses, and gives its device region a named
empty state instead of two thirds of a pane of bare panel. **One** hash moved: the other
twenty-three, every mobile and dialog screen included, are byte-for-byte unchanged against
the pre-fix capture, and that pre-fix capture matched the then-committed manifest on all 24
— so the unstyled pane is what was shipping rather than an artefact of the harness. Five
`--determinism` runs agreed 24/24 after the change. The recording is **not** single-host: on
the PR that made it the `macos-latest` runner — macOS **26.6.2** (25G83), arm64, image
`macos26/20260831.0337.3`, a newer OS build than either this manifest's recording Mac or the
runner that confirmed the #228 recording — passed its own five-run determinism check 24/24 and
then reproduced this manifest, new hash included. Deliberately placed at the foot of this list
rather than at its head: three other open PRs were writing to this file at the same time (one
adding a screen, two re-recording `desktop-7` and `desktop-2`), and this position keeps all
four entries in hunks that do not touch. In the manifest itself the four are on different
lines for the same reason.

`macos-arm64.sha256` was extended 2026-09-05 for #253 (macOS 26.5, Apple silicon, .NET SDK
10.0.302) with the nine `desktop-min-*` screens — the desktop sweep repeated at
`MainWindow`'s own declared `MinWidth=720 MinHeight=480`, see [Window
size](#window-size-and-the-theme-the-whole-manifest-assumes). The recording is **purely
additive**: every screen the manifest already listed is byte-for-byte unchanged against a
capture taken from the unmodified tree (first at `acc5886`, and re-confirmed at `84ceb05`
after the rebase described below), which is the evidence that adding a
second sweep — and the settle-rule change it needed — moved nothing that was already gated.
Six `--determinism` runs agreed on all of them afterwards, against a pre-fix run in which
`desktop-min-7` and `desktop-min-9` flipped about one run in three. The nine new lines land
in one contiguous block between `desktop-9-*` and `dialog-*`, touching no existing line.
Deliberately placed at the foot of this list: three other open PRs were writing near the head
of the manifest at the time.

The recording is **not** single-host: on the PR that made it the `macos-latest` runner — macOS
**26.6.2** (25G83), arm64, image `macos26/20260831.0337.3`, a newer OS build than the recording
Mac's 26.5 — passed its own five-run determinism check **34/34** and then reproduced this
manifest, all nine new hashes included. So the settle-rule change reproduces across machines
too, which matters more here than for a pure re-recording: a rule about *when* a frame is still
enough to keep is exactly the kind of thing that could have been tuned to one machine's timing.

**Four of those nine lines were then re-recorded 2026-09-06 against `84ceb05`** (macOS 26.5,
Apple silicon, .NET SDK 10.0.302 — the same host as the entry above), and the reason
is the rule this section opens with, arriving for real: a hash records a rendering of a **base
commit**, not of a diff. The nine were first captured at `acc5886`; the empty-state
consolidation (#276) landed on `main` afterwards and restyled the five desktop panes, so
`desktop-min-2-loggeddata`, `-3-channels`, `-4-devices` and `-5-profiles` — the four
minimum-size screens that photograph four of those panes — described a rendering that no longer
existed. Nothing warned: `git merge` is clean (the two manifest hunks do not touch), the
mergeability badge is computed pairwise against `main`, and the branch's own green CI run
predates the merge. **`--check-baseline` on the merged tree is what found it**, failing on
exactly those four and passing the other thirty.

The delta is #276's change and nothing else, and the set correspondence is the proof: the four
full-size panes #276 re-recorded (`desktop-2`, `-3`, `-4`, `-5`) map one-to-one onto the four
minimum-size screens that moved, and the five desktop screens #276 left alone map onto the five
`desktop-min-*` that stayed byte-identical. Measured on the two screens where both sizes were
captured either side of the merge, the pixel delta is *the same count at both sizes* —
`desktop-2`/`desktop-min-2` differ by 3,733 px, `desktop-3`/`desktop-min-3` by 6,280 px — which
is what a recolour and a font-size step on centred content should do when the window shrinks
around it. Nothing clips, overlaps or falls below the fold at 720x480; the visible change is
Logged Data's sentence going 13 → 15px and `TextPrimary` → `TextSecondary`, with room to spare.
Five `--determinism` runs agreed **34/34** on the merged tree before the four were re-recorded
from that run's `r1`, and the four hashes reproduce a second host's independent capture of the
same tree byte for byte.

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
of the macOS job — which is what will settle the same question for the seven dialog screens
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
