# Parity-audit visual capture tooling

Renders the **original WPF app** (`daqifi-desktop`) and the **Avalonia port**
(desktop + Android mobile shell) to PNGs and builds side-by-side montages, so we
can compare the port against the original screen-by-screen and track UI-parity
gaps.

Used for the 2026-07-22 parity audit → issues **#5–#14**.

## What it captures

| Harness | App | How | Output |
|---|---|---|---|
| `AvaloniaCapture/` | the Avalonia port | boots the **real** app headless (Skia, no display) and drives the view-models | `<out>/avalonia/desktop-*.png`, `mobile-{portrait,landscape}-*.png` |
| `WpfCapture/` | the original WPF app | runs the **real** app off-screen, captures via `RenderTargetBitmap` | `<out>/wpf/wpf-*.png` |
| `montage.py` | — | pairs them left/right with labels | `<out>/montage/*.png` |

Both harnesses render **without a device connected** (empty states), driving the
UI by reflection — desktop tabs via `SelectedIndex` (0–4) and flyouts via the
VM booleans (`IsAppSettingsOpen`, `IsNotificationsOpen`,
`IsLiveGraphSettingsOpen`, `IsLogSummaryOpen`); the mobile shell is hosted in a
headless window at the Galaxy A16's true logical content size — portrait
(384×800) and landscape (820×360) — and navigated by raising `Click` on the named
nav buttons (`NavChannels`/`RailChannels`…).
`DAQIFI_TEST_MODE=1` is set so no modal dialogs / firewall prompts appear and the
per-user data dir is used.

## Requirements

- **Windows .NET 10 SDK** at `C:\Program Files\dotnet\dotnet.exe`. The harnesses
  are driven with the *Windows* dotnet (from WSL): win-x64 Skia native + fonts are
  reliable there, and WPF requires Windows.
- The sibling repo **`daqifi-desktop`** checked out next to this one
  (`GitHub/daqifi-desktop`), for the WPF leg. If it lives elsewhere, edit the
  `ProjectReference` in `WpfCapture/WpfCapture.csproj`.
- **python3 + Pillow** in WSL for the montages (`pip install pillow`).

## Run

```bash
# from WSL, in this directory
./run.sh                # writes to ./out (gitignored)
./run.sh /some/out/dir  # or a custom out dir
```

`run.sh` runs all three steps. To run a single harness directly:

```bash
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
"$DOTNET" run --project AvaloniaCapture/AvaloniaCapture.csproj -c Release -- "C:\\path\\to\\out\\avalonia"
"$DOTNET" run --project WpfCapture/WpfCapture.csproj      -c Release -- "C:\\path\\to\\out\\wpf"
python3 montage.py /mnt/c/path/to/out
```

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
  { UseHeadlessDrawing = false })`; capture with `window.CaptureRenderedFrame()`.

## Side effects (both harnesses boot the REAL app)

Faithful captures require booting the real app's DI + services, so a capture run has the
same side effects as launching the app once. Two to be aware of:

- **Shared per-user data dir / DB.** `DAQIFI_TEST_MODE=1` is set, but the app's data dir
  resolves to `%LocalAppData%\DAQiFi` for any un-elevated run (test or not), so the harness
  reads/migrates the **same** `DAQiFiDatabase.db` a normal dev run uses. Running a capture
  before you first launch the app can apply a pending EF migration to your real DB, and
  running it while your own debug instance holds the DB open can surface as "database is
  locked". Nothing is destroyed, but it is not isolated. (A proper `DAQIFI_DATA_DIR`-style
  override belongs in the app — see the follow-up issue.)
- **Bootloader HID hold (low).** The app starts `BootloaderWatcher`, which takes an
  *exclusive* HID handle on any device sitting in HID-bootloader mode. It's released on
  the harness's `desktop.Shutdown()` (a couple of seconds), but **don't run a capture at
  the exact moment another tool is flashing a device over the HID bootloader** — the two
  would contend for the handle.

## Limitations

- Empty-state only (no device). **Connected-state** parity (e.g. does the mobile
  landscape Live Graph show the plot while streaming — issue #10) needs a real
  device capture; the mobile shell here is rendered headless at phone sizes, not
  on an actual phone (no Android system chrome, real fonts, or touch targets).
