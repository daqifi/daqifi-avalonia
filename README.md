# daqifi-avalonia

Cross-platform DAQiFi application built on [Avalonia UI](https://avaloniaui.net/),
targeting **Windows, Linux, macOS, Android, and iOS** from one codebase.

DAQiFi is a front end for [DAQiFi Nyquist](https://daqifi.com/) wireless
data-acquisition hardware: discover a device on your network, configure its channels,
watch signals live, and record them to CSV.

> **This app requires DAQiFi Nyquist hardware.** It is a companion for a physical
> device, not a standalone measurement tool, and does nothing useful without a Nyquist
> on the network.

## Status

**Beta.** The current release is
[v3.3.0-beta.1](https://github.com/daqifi/daqifi-avalonia/releases).

Maturity differs sharply by platform, so it is worth being specific:

- **Windows / Android** — the most exercised. Discovery, connection, channel
  configuration, live plotting, and CSV recording have been run against real Nyquist
  hardware.
- **macOS** — builds, launches, and renders; some Windows-only code paths in the port
  are still marked unverified in the runbook.
- **iOS** — builds and deploys, but discovery has **not** been validated against real
  hardware on a real device. The simulator cannot test it, because the network
  behaviour is the whole risk. Treat iOS as unproven.

This is a port in progress, not a finished rewrite. Expect rough edges: some code paths
still throw `NotImplementedException`, and `// @port:` markers throughout the source
mark work that has not been reconciled with the app it ports. Treat it accordingly.

Those `// @port:` markers reference an internal correspondence map (`.portomatic/`)
that tracks this port against upstream file by file. It is working state for the
porting tool rather than part of the application, so it is not published here — the
markers are still useful as "this symbol came from upstream and may have diverged",
but the map they index is not in this repository.

### Projects

| Project | Target framework | Notes |
| --- | --- | --- |
| `Daqifi.Avalonia` | `net10.0` | Shared library — views, view models, device logic |
| `Daqifi.Avalonia.Desktop` | `net10.0` | Windows, Linux, macOS |
| `Daqifi.Avalonia.Android` | `net10.0-android36.0` | minSdk 29 |
| `Daqifi.Avalonia.iOS` | `net10.0-ios` | |

## Where this fits

- **[daqifi-desktop](https://github.com/daqifi/daqifi-desktop)** — the original WPF,
  Windows-only application. This repository is a port of it, and deliberately keeps the
  upstream `Daqifi.Desktop.*` namespaces so the two stay diffable.
- **[daqifi-core](https://github.com/daqifi/daqifi-core)** — device discovery,
  transport, protobuf, and firmware-update logic all come from this NuGet package
  (currently pinned to `1.7.0`). That logic is *referenced, not ported*; bugs in device
  communication usually belong there rather than here.

## Building

Requires the **.NET 10 SDK**, pinned in `global.json` to `10.0.302` with
`rollForward: disable` — the exact version matters, because the mobile heads are
sensitive to workload versions.

```bash
dotnet build Daqifi.Avalonia.Desktop/Daqifi.Avalonia.Desktop.csproj
dotnet run   --project Daqifi.Avalonia.Desktop
```

Android and iOS need their respective workloads (`dotnet workload install android ios`),
and iOS needs macOS with Xcode. See [docs/RUNBOOK-macos-ios.md](docs/RUNBOOK-macos-ios.md)
for the macOS and iOS specifics, including what device deployment requires.

Every project commits a `packages.lock.json`, and CI restores in **locked mode** — a
lock file that has drifted from its project's dependencies fails the build rather than
being silently regenerated. If you change a `PackageReference`, refresh the lock file in
the same commit.

## Repository layout

- `Daqifi.Avalonia/` — the shared application: views (`.axaml`), view models, and the
  device layer that wraps Daqifi.Core
- `Daqifi.Avalonia.Desktop/`, `.Android/`, `.iOS/` — the platform heads
- `third_party/oxyplot-avalonia/` — a vendored build of OxyPlot's Avalonia bindings
  (MIT; see `third_party/oxyplot-avalonia/LICENSE` and `VENDORED.md` for why it is
  vendored rather than referenced)
- `docs/` — runbooks and design notes
- `tools/` — build and release tooling, including the parity-audit harness used to
  compare this app's UI against the upstream WPF app
- `.github/` — CI workflows and the guard scripts they run. The policy directories
  (`dependency-updates/`, `upstream-sync/`, `merge-queue/`) each carry a README
  explaining what they enforce and why

## Privacy

The app talks only to your Nyquist over your local network. There are no accounts, no
ads, and no advertising or analytics tracking. It sends anonymous crash diagnostics
(via Sentry) so defects can be found and fixed; no personal information is attached.
Crash reporting can be compiled out with `-p:SentryDsn=`.

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). CI builds
every head on every pull request, so expect the build to tell you quickly if a change
breaks a platform you did not test.

## License

[MIT](LICENSE) — the same license as
[daqifi-desktop](https://github.com/daqifi/daqifi-desktop) and
[daqifi-core](https://github.com/daqifi/daqifi-core).

## Support

Questions about the hardware or the app: [daqifi.com/contact](https://daqifi.com/contact)
