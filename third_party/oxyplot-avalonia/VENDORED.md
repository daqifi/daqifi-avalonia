# Vendored: OxyPlot.Avalonia

- Upstream: https://github.com/oxyplot/oxyplot-avalonia (`Source/OxyPlot.Avalonia/`)
- Commit: `9913a97d79a75585abd0ee5b417739ac52bca4c6` (master, 2024-08-03)
- License: MIT (see LICENSE, copied from the upstream repo root)

## Why vendored

The latest NuGet release (`OxyPlot.Avalonia 2.1.0`) targets **Avalonia 0.10.11**
and fails at runtime under Avalonia 11 with
`FileNotFoundException: Avalonia.Visuals, Version=0.10.11.0` the moment a
`PlotView` is constructed (caught by the first `dotnet run` smoke test through
the ported App host). Upstream master has had Avalonia 11 support since 2023
but has never been published to NuGet, and the Avalonia-11 packages that do
exist on NuGet are low-download third-party forks. Building the official
source pinned at a commit is the safer supply chain.

## Local modifications

- `OxyPlot.Avalonia.csproj`: `GeneratePackageOnBuild` True → False (we consume
  it as a ProjectReference, not a package).
- `OxyPlot.Avalonia.csproj`: `<PackageId>Oxyplot.Avalonia</PackageId>` dropped
  (#106). Upstream's value differs from the project's own name only in the case
  of one letter — `Oxyplot` vs `OxyPlot` — and NuGet resolved that mismatch
  differently on a case-insensitive filesystem than on a case-sensitive one, so
  the project's key in every `packages.lock.json` came out `oxyplot.avalonia`
  when derived on macOS and `Oxyplot.Avalonia` when derived on Linux. The
  committed files could satisfy one host or the other, never both. With the
  property gone the id falls back to `AssemblyName`, which is already
  `OxyPlot.Avalonia`, and both hosts derive the same key. Nothing is lost: this
  is never packed (see above) and is referenced by path, so the property had no
  reader. Restore it only alongside `GeneratePackageOnBuild`, and expect the
  lock-file split to come back with it.
- `Directory.Build.props` (this directory, not upstream): pins
  `AvaloniaVersion` to the app's exact version (currently **12.0.5**) and
  `OxyPlotCoreVersion` to 2.2.0 — upstream reads both from
  `Source/Directory.Build.props` (11.0.0 / 2.1.2), which is not part of the
  vendored subtree. Also sets `RestorePackagesWithLockFile` (this subtree has
  its own `Directory.Build.props`, so it does not inherit the repo-root one)
  and pins `AvaloniaUseCompiledBindingsByDefault=false` — see below.
- `OxyPlot.Avalonia.csproj.DotSettings` dropped.

### Avalonia 12 support (applied 2026-07-28, port issue #71)

Upstream has no Avalonia 12 release, but
[upstream PR #74](https://github.com/oxyplot/oxyplot-avalonia/pull/74)
("added support for avalonia 12") does. Its library changes apply cleanly here
because **the vendored pin `9913a97` is that PR's merge base** — upstream master
has not moved since 2024-08. Applied:

- `OxyPlot.Avalonia.csproj`: `netstandard2.0` → `net10.0`. Avalonia 12 dropped
  .NET Standard entirely.
- `PlotBase.cs`: added `using global::Avalonia.Input.Platform;` —
  `IClipboard.SetTextAsync` moved off the interface to an extension method.

Deliberately **not** taken from PR #74: its `Themes/Default.axaml` hunk adding
`x:DataType="oxy:TrackerHitResult"` to the tracker `ControlTemplate`s. Avalonia
12 flips `AvaloniaUseCompiledBindingsByDefault` to true, which is what makes
those `{Binding Position}` bindings need a data type; pinning the property false
in this directory's `Directory.Build.props` has the same effect in one line and
keeps the vendored source a smaller diff from upstream master, which matters for
the next re-sync. If PR #74 ever merges upstream, prefer adopting its version
wholesale at that point.

Verified after the upgrade: `PngExporter` (the app's session-PNG export, which
PR #74 does **not** cover — it renders off the visual tree via
`RenderTargetBitmap.Render` + `Save`) produces a valid 25 KB PNG under 12.0.5,
and `PlotView` draws live series on a real device at 16 ch @ 100 Hz (port #72).

## Retire when

An official `OxyPlot.Avalonia` NuGet release targets the Avalonia version this
app is on — swap the ProjectReference back to a PackageReference and delete this
directory. As of 2026-07-28 the latest release (2.1.0) still targets Avalonia
0.10, and upstream has published nothing for 11 either, so this is not close.
