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
headless window at phone portrait (412×892) and landscape (1000×560) sizes and
navigated by raising `Click` on the named nav buttons (`NavChannels`/`RailChannels`…).
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
- Faithful pixels need `UseSkia()` + `UseHeadless(new AvaloniaHeadlessPlatformOptions
  { UseHeadlessDrawing = false })`; capture with `window.CaptureRenderedFrame()`.

## Limitations

- Empty-state only (no device). **Connected-state** parity (e.g. does the mobile
  landscape Live Graph show the plot while streaming — issue #10) needs a real
  device capture; the mobile shell here is rendered headless at phone sizes, not
  on an actual phone (no Android system chrome, real fonts, or touch targets).
