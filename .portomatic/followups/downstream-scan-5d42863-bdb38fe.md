# Downstream scan — `5d42863..bdb38fe` (4 commits)

Out-of-plan downstream changes with no recorded map reason (#286).
For each file: record WHY it differs from upstream (divergence /
downstream_only), or revert it. `sync scan-downstream --mark` once
everything below is dispositioned.

## UNEXPLAINED: `Daqifi.Avalonia/Daqifi.Desktop/Device/Firmware/HidBootloaderDiscovery.cs`

- commit `6c0827f` — chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)

Map rows touching this file:
- [UI] file row relation=subset
- [UI] symbol Daqifi.Desktop.Device.Firmware.HidBootloaderDiscovery relation=one_to_one

- [ ] **Intentional downstream behavior?** Record it — draft:

```yaml
divergences:
- id: DIV-DS-6c0827f-1
  downstream_path: 'Daqifi.Avalonia/Daqifi.Desktop/Device/Firmware/HidBootloaderDiscovery.cs'
  kind: behavioral
  rationale: >
    <why — e.g. 'chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)'>
  accepted_at: <YYYY-MM-DD>
```

- [ ] **Port-added member on a paired class?** Use a `downstream_only:` member entry on the owning symbol instead.
- [ ] **Accidental drift?** Revert the change.

## UNEXPLAINED: `Daqifi.Avalonia/Daqifi.Desktop/Helpers/VersionHelper.cs`

- commit `6c0827f` — chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)

Map rows touching this file:
- [UI] file row relation=dropped

- [ ] **Intentional downstream behavior?** Record it — draft:

```yaml
divergences:
- id: DIV-DS-6c0827f-2
  downstream_path: 'Daqifi.Avalonia/Daqifi.Desktop/Helpers/VersionHelper.cs'
  kind: behavioral
  rationale: >
    <why — e.g. 'chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)'>
  accepted_at: <YYYY-MM-DD>
```

- [ ] **Port-added member on a paired class?** Use a `downstream_only:` member entry on the owning symbol instead.
- [ ] **Accidental drift?** Revert the change.

## UNEXPLAINED: `Daqifi.Avalonia/Daqifi.Desktop/Loggers/CsvLogger.cs`

- commit `6c0827f` — chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)

Map rows touching this file:
- [UI] file row relation=dropped

- [ ] **Intentional downstream behavior?** Record it — draft:

```yaml
divergences:
- id: DIV-DS-6c0827f-3
  downstream_path: 'Daqifi.Avalonia/Daqifi.Desktop/Loggers/CsvLogger.cs'
  kind: behavioral
  rationale: >
    <why — e.g. 'chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)'>
  accepted_at: <YYYY-MM-DD>
```

- [ ] **Port-added member on a paired class?** Use a `downstream_only:` member entry on the owning symbol instead.
- [ ] **Accidental drift?** Revert the change.

## UNEXPLAINED: `Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LiveGraphPane.axaml`

- commit `6c0827f` — chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)

Map rows touching this file:
- [UI] hal_pair chart_plot_host relation=equivalent

- [ ] **Intentional downstream behavior?** Record it — draft:

```yaml
divergences:
- id: DIV-DS-6c0827f-4
  downstream_path: 'Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LiveGraphPane.axaml'
  kind: behavioral
  rationale: >
    <why — e.g. 'chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)'>
  accepted_at: <YYYY-MM-DD>
```

- [ ] **Port-added member on a paired class?** Use a `downstream_only:` member entry on the owning symbol instead.
- [ ] **Accidental drift?** Revert the change.

## UNEXPLAINED: `Daqifi.Avalonia/Daqifi.Desktop/ViewModels/DaqifiViewModel.cs`

- commit `6c0827f` — chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)

Map rows touching this file:
- [UI] file row relation=subset
- [UI] hal_pair ui_thread_dispatch relation=equivalent
- [UI] symbol Daqifi.Desktop.ViewModels.DaqifiViewModel relation=one_to_one

- [ ] **Intentional downstream behavior?** Record it — draft:

```yaml
divergences:
- id: DIV-DS-6c0827f-5
  downstream_path: 'Daqifi.Avalonia/Daqifi.Desktop/ViewModels/DaqifiViewModel.cs'
  kind: behavioral
  rationale: >
    <why — e.g. 'chore(sync): first upstream sync cycle — bbb34ff → c4c63fa (5 upstream commits)'>
  accepted_at: <YYYY-MM-DD>
```

- [ ] **Port-added member on a paired class?** Use a `downstream_only:` member entry on the owning symbol instead.
- [ ] **Accidental drift?** Revert the change.
