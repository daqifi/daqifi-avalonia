# daqifi-avalonia

Cross-platform DAQiFi application built on [Avalonia UI](https://avaloniaui.net/),
targeting **Windows / Linux / macOS / Android / iOS**.

This is an agentic port of [daqifi-desktop](https://github.com/daqifi/daqifi-desktop)
(WPF, Windows-only) driven by [portomatic](https://github.com/daqifi/portomatic).
Port scope, divergences, and acceptance criteria are tracked in
[portomatic #221](https://github.com/daqifi/portomatic/issues/221).

## Structure

- `Daqifi.Avalonia/` — the Avalonia app (ports upstream `Daqifi.Desktop/`)
- `.portomatic/` — correspondence map shards, plans, and UI concept catalog
  (the port's source of truth; see the
  [UI catalog](https://github.com/daqifi/portomatic/blob/main/docs/ui-catalog-wpf-avalonia.md))

Device discovery, transport, protobuf, and firmware-update logic come from the
[Daqifi.Core](https://github.com/daqifi/daqifi-core) NuGet package — referenced,
not ported.

## Status

Green-field scaffold stage. Stubs carry `// @port:` markers and throw
`NotImplementedException` until their apply-plan steps land.
