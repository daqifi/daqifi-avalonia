# Parity ledger: what is still only possible in daqifi-desktop

**Measured 2026-09-03** against daqifi-desktop `36995fe` (upstream `main`) and this
repo at `5414c07`. **Re-measured 2026-09-04** at the binding level, against the same
upstream `36995fe` — still `identical` to upstream `main` — after the 2026-09-03
method was shown to be measuring the wrong thing.

## Verdict

**Nothing.** Every user-reachable capability in the WPF app has a counterpart in
this port. Feature parity is no longer what blocks retiring daqifi-desktop —
shipping and validation are.

Five independent signals were swept. On 2026-09-04 one of them was found to be
measuring the wrong thing and was replaced by a stronger one; the other four are
unchanged. All five return zero user-reachable gaps. The triage that got there is
below, so the next pass can check the reasoning rather than repeat the sweep.

## Correction, 2026-09-04: declared is not bound

The 2026-09-03 row "67 command bindings upstream, **0 absent** downstream" was
**true but not evidence for what it was used for**, and the verdict above was
resting partly on it. It came from a *symbol* diff: it asked whether
each upstream command exists downstream as a declaration. A command can be
declared downstream and bound by **no view at all**, and to a symbol diff that is
indistinguishable from a command a user can press. Declared ≠ bound ≠ reachable.

`coverage.py --bindings` now asks the binding question directly, by reading the
XAML on both sides rather than the C#. Re-run at the same upstream revision:

| | measured |
|---|---|
| Member names bound in WPF `.xaml` | **321** |
| …bound by no `.axaml` downstream | **4**, all triaged below as non-gaps |
| Commands the port declares | **98** |
| …bound by no `.axaml` | **10**; 4 reachable another way, **6 dead** |
| `[ObservableProperty]` state the port declares | **194** |
| …bound by no `.axaml` | **38**, of which 8 are never read in C# either |

**The parity conclusion survives, and is now better founded**: the 4 upstream-bound
names with no downstream binding are `FlyoutWidth`/`FlyoutHeight` (MahApps `Flyout`
sizing, which Avalonia's `SplitView` does not have), `ElapsedTime` (dropped on
purpose, with the reasoning at `SummaryLogger.cs`), and `Port` — the container of
upstream's `Port.PortName`, which the port binds flattened as `PortName`.

**What the binding check found instead is on the port's own side**, and it is a
different defect class: six commands and a pile of observable state that nothing
can reach. Filed as #241–#243. **None of them is a parity gap.** Every one is
either dead upstream too — this port faithfully copied the WPF app's dead MVVM
surface — or a port-only addition that was never wired up. All **48** unbound
names were checked back against upstream: **none of them is bound in any WPF
`.xaml` either**, and 9 do not appear in upstream's C# at all.

### The unreachable surface, ranked by user impact

Ranked by what a user loses, not by how many lines it is. Nothing here is a
regression against the WPF app; the top two are capabilities **neither** app
offers, which is why they rank above the merely dead code below them.

| # | What | Where | Filed |
|---|---|---|---|
| 1 | **A firmware upload cannot be cancelled.** `CancelUploadFirmwareCommand` and `CancelFirmwareUploadCommand` both exist, both work, and no view binds either. The upload scrim is modal and can run for minutes. | `FirmwareDialogViewModel.cs:337`, `DaqifiViewModel.cs:983` | #241 |
| 2 | **The WiFi-firmware update reports progress to nobody.** `FirmwareUpdateStatusText` is assigned 18 times (15 in `FirmwareUpdateCoordinator`, 3 in the view-model) and read nowhere; `DaqifiViewModel.IsFirmwareUploading` gates `CanExecute` but drives no visual. | `DaqifiViewModel.cs:188` | #241 |
| 3 | **Dead commands with a live duplicate.** `RemoveChannelCommand` re-implements what `ChannelsPaneViewModel` and `MobileShellViewModel` already do; `ToggleDebugModeCommand` duplicates the bound `IsDebugModeEnabled` toggle; `ShutdownCommand` wraps a method nothing calls in either app. | `DaqifiViewModel.cs` 1031 / 2862 / 1069 | #242 |
| 4 | **Observable state nothing reads.** 8 `[ObservableProperty]` fields are assigned and never read back, in C# or XAML — `ExportProgressText`, `LoggedSessionName`, `IsDeviceSettingsOpen`, `Flag`, `DeviceResponse`, `HasDigitalData`, `MessageType`, `SelectedProfileDevices`. Each one costs a generated property, a change notification and a reader's attention. | various | #243 |

**#241 is deliberately not fixed here.** PR #239 is open against
`DaqifiViewModel`'s cancellation ownership; a Cancel button written against `main`
would bind a surface that is about to move.

Two corrections of record that came out of the same pass:

- The finding that reopened this file was reported as "the WPF `FirmwareFlyout.xaml`
  Cancel button was never ported". **There is no `FirmwareFlyout.xaml` upstream.**
  `Daqifi.Desktop/View/Flyouts/` at `36995fe` holds `LiveGraphFlyout`,
  `NotificationsFlyout` and `SummaryFlyout` and nothing else; the file exists only in
  branch worktrees under the sibling checkout's `.claude/worktrees/`, which is why a
  `find` over that checkout appears to show it. Upstream's own
  `CancelFirmwareUploadCommand` is bound by nothing either. **Read the sibling
  repo at the watermark, not at whatever its worktrees are on.**
- Three `@port:` backlinks name upstream symbols that do not exist at `36995fe`:
  `DaqifiViewModel.RemoveChannel`, `.ToggleDebugMode`, and — as `[RelayCommand]` —
  `.Shutdown`, which upstream declares as a plain method. A backlink to a
  nonexistent symbol cannot be validated by the declared-level diff, which only
  ever reads backlinks in the other direction.

## Why this file exists

The previous ledger lived in `.portomatic/map/*.yaml` (`UI.yaml`, `Common.yaml`,
`Mobile.yaml`), whose per-file `relation:` field carried the gap signal. **That
directory no longer exists in either checkout**, so the old `subset` /
`not_yet_ported` counts cannot be refreshed or even re-read. This file plus
`tools/parity-audit/coverage.py` replace it with something reproducible from the
two working trees alone.

## Upstream drift is zero

```
$ cat .github/upstream-sync/last-reviewed.sha
36995fedb291337cc681b17aefd32c6fa96c3d86
$ gh api repos/daqifi/daqifi-desktop/compare/36995fe...main --jq .status
identical
```

The watermark is already at upstream `main`: **0 commits behind**. daqifi-desktop
is not moving, so the gap set is fixed rather than a moving target. This is the
single most important fact for planning — a parity pass no longer has to re-measure
drift before it can trust anything else.

## The five signals

| Signal | Result |
|---|---|
| Views | WPF 22 XAML files → every one has an `.axaml` counterpart. Port adds 8 mobile views, 1 dialog, 1 resource dictionary. |
| ~~Command bindings~~ | ~~67 distinct `Command=` bindings upstream, **0 absent** downstream; 13 new downstream.~~ **Superseded** — this measured declarations, not bindings. See the correction above. |
| Bound members (2026-09-04) | 321 member names bound in WPF `.xaml`, **4** bound by no `.axaml` downstream — all triaged as non-gaps. Reproduce with `coverage.py --bindings`. |
| Types | 167 upstream types, **7** with no `@port:` backlink — all triaged below as non-gaps. |
| Members | 646 upstream public/internal members, **20** with no backlink and no same-named symbol downstream — all triaged below. |
| User-visible labels | 9 upstream strings absent downstream, all renames/redesigns where the port is equal or richer. |

Two signals earlier passes relied on are now spent and should not be re-run:

- **`NotImplementedException`** is down to 7 files, all XAML value converters whose
  `ConvertBack` correctly throws for a one-way binding. PR #163 established this.
- **`@port:` markers** (153 files) are *symbol backlinks*, not gap descriptions —
  they read `// @port: <upstream fully-qualified symbol>`. Treating their count as a
  work queue was a false premise; their real value is enabling the coverage diff below.

## Triage: the 27 unbacklinked symbols

Reproduce with `python3 tools/parity-audit/coverage.py --names`.

**WPF-only scar tissue, correctly dropped (5).** Avalonia binds `IsVisible` to a
`bool` directly, so the visibility converters have no downstream role, and
`TileBrushes` is a WPF `Brush` resource holder.

`InvertedBoolToVisibilityConverter`, `BooleanConverter`, `BooleanToVisibilityConverter`,
`TileBrushes`, `TileBrushes.Frozen`

**Renamed downstream (1).** `Models.DebugDataHistory` → `DebugDataCollection`
(`Daqifi.Avalonia/Daqifi.Desktop/Models/DebugDataModel.cs`), same 100-entry ring and
`AddEntry`/`Clear` surface. Note its backlink names an upstream symbol that does not
exist, which is why the diff flags it.

**Refactored, capability intact (3).**

- `LoggingManager.ParseProfiles` — profile XML parsing moved into a dedicated
  `ProfileXmlStore`. Profiles load, activate, save and delete downstream.
- `AbstractStreamingDevice.GetSdCardParseConfiguration` — became
  `SdCardSessionImporter.BuildDeviceConfiguration`, so the config is built where it is
  consumed instead of every device carrying the method.
- `Logger.ChannelBuffer` / `SummaryBuffer` and their 13 `*Ticks` accumulators — the
  port's `SummaryLogger` keeps the same statistics but stores and presents them in
  milliseconds rather than raw ticks.

**Dead state upstream: written, never read, never surfaced (4).** Dropping these is
the "port the capability, leave the scar tissue" rule working as intended.

- `SummaryBuffer.HasRollover` — assigned from `dataSample.Rollover`, then read by
  nothing. (`Rollover` itself *is* ported, on `DeviceMessage`.)
- `DeviceMessage.AnalogChannelCount` / `DigitalChannelCount` — only ever assigned `0`.
- `ChannelBuffer.FirstSampleTicks` / `LastSampleTicks` and siblings — internal to the
  discarded buffer type above.

**A defensive guard the port dropped (1).** `SessionDataRepository.FALLBACK_CHANNEL_COLOR`
(`"#FF808080"`) — upstream substitutes it when a persisted sample row has no colour, so
`OxyColor.Parse` cannot throw and abort a whole session load. The port passes `g.Color`
straight through with no `??`
(`Daqifi.Avalonia/Daqifi.Desktop/Loggers/SessionDataRepository.cs:164`).

Recorded as a difference, **not filed as a gap** — but state the reason narrowly, because
the obvious argument is wrong. Both repos share the same
`20250812090000_InitialSQLiteMigration` declaring `Color` as `nullable: false`, and it is
tempting to conclude a null "cannot be produced". That over-claims: `nullable: false`
constrains the table *as the migration creates it*, and SQLite does not re-validate rows
already sitting in a database file. Upstream says as much directly above the constant —
"Color is nullable in the Samples table (legacy/imported rows can omit it)" — so upstream's
guard is aimed at **pre-existing databases**, not at its own writes.

What actually holds is narrower, and it is enough:

- Both apps' *current* write paths always set a colour. `DataSample.Color` is a
  non-nullable `string` in both (upstream defaults it to `string.Empty`), and the port's
  SD-card importer assigns one on every row, falling back to `#D32F2F` / `#757575`.
- Neither app guards the **empty string**, which `OxyColor.Parse` rejects just as hard as
  null — so on the colourless-row scenario the two apps are closer to equal-risk than the
  `??` suggests.

The residual case is a database written before the column was required. There the WPF app
renders grey and this port would throw on session load, aborting the whole load rather
than one series. That is robustness against legacy data, not a capability the WPF app
offers a user and this one does not — so for *parity* purposes it stays a difference.

It is still a reliability defect, so it is **filed as #231** rather than left to wait for
a report. Note the fix is not the bare `?? "#FF808080"` this ledger first suggested:
`OxyColor.Parse` rejects the empty string just as hard as null, and
`PlotLogger.cs:271` calls `.ToLowerInvariant()` on the value first, so null throws there
before `Parse` is even reached. The guard has to normalise both —
`string.IsNullOrWhiteSpace(g.Color) ? FALLBACK_CHANNEL_COLOR : g.Color` where the value
enters `SessionChannelInfo`, which covers all three `Parse` call sites from one place.

## Label differences, all in the port's favour

Nine upstream strings have no downstream match. None is a lost capability:

- **Log Summary flyout** — a redesign, not a subset. Upstream's `Delta (ticks)` /
  `Latency (ticks)` / `Elapsed Time (ms)` / `Sample Size` become `Interval (ms)` /
  `Latency (ms)` / `Samples` / `Refresh Every (msgs)`, and the port **adds**
  `Host clock`, `Device clock`, `Total Samples/s` and `Out of order`.
- **SD card pane** — upstream's `USB REQUIRED` / "Connect a device over USB to browse
  its SD card." become `NO DEVICE` / "Connect a device over USB **or Wi-Fi**…",
  which is correct because the port can reach the SD card over Wi-Fi. The port also
  **adds** a `DEVICE BUSY` state upstream lacks.
- **Debug window** — `CLEAR` vs `Clear`, a casing difference.

There are no keyboard accelerators upstream (`KeyBinding` appears zero times), so
none can be missing.

## What actually blocks retiring daqifi-desktop

Ranked by what stands between today and switching users over. Parity does not appear
on this list, which is the point.

| Rank | Item | Actionable on this Mac? |
|---|---|---|
| 1 | **#102** iOS `UseInterpreter` → AOT-safe EF Core | **Yes** — code change; the iOS head builds here and a simulator is available |
| 2 | **#98** Play Store submission | No — needs the org Play account |
| 3 | **#109** Apple Developer account | No — external, days of lead time; gates all iOS/macOS shipping |
| 4 | **#115 / #116** device matrix and long-duration soak | No — needs physical devices |
| 5 | **#104** iOS Wi-Fi discovery on real hardware | No — needs #109 plus a device |
| 6 | **#89** visual parity gate | **Partly** — see below |
| 7 | **#100** Sentry IP scrubbing, second owner | No — account admin |
| 8 | **#208 / #210** DiskSpaceMonitor bug, test-pool race | **Yes** — ordinary code work |

### #89 is further along than its body says

Its two asks were "make the harness runnable on `osx-arm64`" and "record a macOS
baseline". **Both are done** — PR #179 unpinned `RuntimeIdentifier=win-x64`, and
`tools/parity-audit/baselines/macos-arm64.sha256` is committed. What remains is only
the *WPF* leg, which needs Windows because WPF does. The issue body should be
narrowed to that so it stops reading as available work.

## The Avalonia leg was captured and matches its baseline

```
DOTNET=~/.dotnet/dotnet tools/parity-audit/run.sh <out>
tools/parity-audit/run.sh --check-captured <out>/avalonia
```

All **24** screens captured and every one is byte-identical to the committed
`baselines/macos-arm64.sha256`. So the port boots headless on macOS, every pane and
dialog renders, and the rendering is stable run-to-run on this host.

That capture surfaced one genuine user-visible layout defect, filed separately: in
the Log Summary flyout's SETTINGS box the label `Refresh Every (msgs)` is clipped to
`Refresh Every (ms` and the **Reset** button overlaps the status toggle's `Stopped`
text. The grid is `ColumnDefinitions="150,*,75"` with a fixed 150px first column too
narrow for the label at this font
(`Daqifi.Avalonia/Daqifi.Desktop/View/Flyouts/SummaryFlyout.axaml:72`). It is in the
committed baseline, so it ships today. It is a defect in a redesigned surface, not a
missing capability — no WPF capture exists to compare against.

## Limits of this ledger

State these rather than round up:

- **No WPF-side visual capture was taken.** `WpfCapture` requires Windows. Every
  visual statement here comes from reading XAML, or from the Avalonia leg alone.
  A one-sided capture is not a parity comparison.
- **The coverage diff is regex-based**, over declaration syntax rather than a
  compiled symbol table. It finds candidates; each still needs reading by hand. Its
  first revision omitted four modifiers and so lifted the denominator from 606 to 646
  when they were added: `const` accounted for **26** members, `abstract` 9 and
  `required` 5 (`volatile` 0). The `const` miss was the one that mattered — it hid the
  one real difference in the sweep (the fallback colour above). Treat the member regex
  as the part most likely to be wrong.
- **The parser's remaining blind spots were measured, not assumed.** `MEMBER_RE`
  requires a declaration to end in `(`, `{` or `=`, and takes the name from before any
  `<`, so it never sees plain fields and events declared with `;`, generic methods,
  explicit interface implementations, or `abstract` methods ending in `;`. Widening it
  to cover all four surfaces **39** further upstream symbols. Exactly **one** is absent
  from the port by name — `DialogService.ServiceLocator.RegisterSingleton<,>` — and that
  is a refactor, not a gap: the port replaced the hand-rolled locator with
  `Microsoft.Extensions.DependencyInjection` (`ServiceCollection` / `AddSingleton` in
  `Daqifi.Avalonia/Daqifi.Desktop/App.cs`). **The verdict is unchanged with the blind
  spots covered.** `test_coverage.py` pins these forms so the boundary stays visible.
  `protected` members remain out of scope by design.
- **Behaviour behind a matching name is not verified.** A command that exists
  downstream is not proof it does the same thing. This ledger establishes that the
  *surface* is complete, not that every code path matches. Since 2026-09-04 that
  caveat is doubly true, because a matching surface has now been shown not even to
  guarantee matching *reachability* — six commands the port declares are bound by
  nothing at all.
- **`--bindings` corrected its own denominator four times on its first run**, and
  every one of those was a silent under- or over-count, not a crash: inline
  `[ObservableProperty]` declarations, `RelativeSource TemplatedParent`,
  element-syntax `<Binding Path="…"/>`, and two view-models sharing a property
  name. None changed a finding, which is the only reason the counts above are
  worth quoting — but re-run it rather than quoting them from here.
- **`--bindings` reads markup, not a compiled binding graph**, so it knows names and
  not types. Matching is on the bare identifier of every path segment, which is
  deliberately generous — upstream's `Port.PortName` and the port's flattened
  `PortName` should agree — but it means a member bound downstream on a *different*
  view-model counts as bound. `DaqifiViewModel.IsFirmwareUploading` is the live
  example: nothing binds it, yet the identifier is bound in `FirmwareDialog.axaml`
  against `FirmwareDialogViewModel`, so section A cannot see it and section C does not
  list it. Section A alone would not have found the control this mode exists for.
  Four names are declared by two view-models each — `IsSettingsOpen`,
  `HasConnectedDevice`, `IsLoggingActive`, `IsSelected` — so one being bound clears
  both. B and C do list every *declaration* separately, which is what makes those
  duplicates visible at all; the reachability verdict beside them is still per-name.
- **Reachability is not the same as usefulness.** `--bindings` proves a view names
  the member. It cannot tell you the handler behind it does anything, or that the
  control is ever enabled — the next two classes down. Those still need reading.
