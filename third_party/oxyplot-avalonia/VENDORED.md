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
- `Directory.Build.props` (this directory, not upstream): pins
  `AvaloniaVersion` to the app's 11.3.* and `OxyPlotCoreVersion` to 2.2.0 —
  upstream reads both from `Source/Directory.Build.props` (11.0.0 / 2.1.2),
  which is not part of the vendored subtree.
- `OxyPlot.Avalonia.csproj.DotSettings` dropped.

## Retire when

An official `OxyPlot.Avalonia` NuGet release targets Avalonia 11+ — swap the
ProjectReference back to a PackageReference and delete this directory.
