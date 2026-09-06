# HeadlessBench

A system-test rig that boots the **real** Avalonia app headless and drives it against a
**real** DAQiFi board over serial, then asserts on both halves of every check: what the
device/Core layer reports, *and* what the UI actually shows.

Nothing else in the repo does this. The unit suite covers view models and services with
fakes; `tools/parity-audit/AvaloniaCapture` renders screens but never connects to
hardware. HeadlessBench is the only thing that exercises the device → Core → view-model →
rendered-window path end to end.

## What makes it different from a unit test

It goes through the **user's** code path, not a convenient one:

- Connection is `ConnectionDialogViewModel.ConnectManualSerialCommand.ExecuteAsync(...)` —
  the command a user's click invokes — rather than constructing a `SerialStreamingDevice`
  directly. Device registration, the duplicate check, the status string and the hot-plug
  hand-off all run.
- Disconnection is `shell.DisconnectDeviceCommand.Execute(device)` — the command the Devices
  pane binds. `ConnectionManager.Instance.Disconnect(device)` is only one of the four things
  that command does, and `device.Disconnect()` is worse still: it closes the port but leaves
  the device registered, precisely the stale-"Connected" state `CONN-DISC` exists to catch.
- The sampling rate is set through `shell.SelectedStreamingFrequency`, the guarded setter the
  drawer's FREQUENCY control writes, rather than assigning `device.StreamingFrequency`, which
  skips the guard and leaves the shell's own value stale.
- Streaming is driven by toggling `shell.IsLogging`, so the disk-space check, session
  creation and `LoggingFleet.Start` all happen the way they do in the app.
- The Devices and Channels panes are the real pane view models, built the way their own views
  build them — `DevicesPanePrototype.axaml.cs` does `new DevicesPaneViewModel(shell)` and
  `ChannelsPanePrototype.axaml.cs` does `new ChannelsPaneViewModel()`, each in
  `OnLoaded`, and both populate themselves from `ConnectionManager`. So the tile
  rows read live tiles, the channel rows drive `ToggleChannelCommand` / `OpenSettingsCommand`
  and then write the drawer's own bound properties, and `CH-AI` no longer hand-mirrors the
  private `ToggleChannel` it used to copy. Constructing a pane view model *is* the user's
  path; no test-only accessor on the shell was needed.
- SD card listing is `shell.DeviceLogsViewModel.RefreshFilesCommand`, the Logged Data pane's
  REFRESH button on the instance that pane binds, not `device.RefreshSdCardFiles()`.

Every step renders the live window to a PNG and appends one JSON line to
`<out>/results.jsonl`, so a run leaves behind evidence rather than a verdict.

A run that throws part-way through still stops logging and disconnects on the way out, so an
aborted run cannot leave the bench board streaming into a process that has already exited.

## Running it

```bash
# Restore separately — it is deliberately NOT in Daqifi.Avalonia.slnx (see below).
dotnet restore tools/system-test/HeadlessBench/HeadlessBench.csproj
dotnet run   --project tools/system-test/HeadlessBench/HeadlessBench.csproj --no-restore -- \
  --port /dev/cu.usbmodem1101 --out "/tmp/headlessbench-$(date +%Y%m%d-%H%M%S)" \
  --rate 100 --seconds 5
```

**Give every run a fresh `--out`** — the example above stamps the directory with the time.
`results.jsonl` is truncated at startup, so reusing a path silently discards the previous
run's verdicts rather than interleaving them; `shots/` and `appdata/` are *not* cleared, so a
reused directory also mixes two runs' PNGs and two runs' databases.

| flag | meaning | default |
| --- | --- | --- |
| `--port` | serial port of the board (required unless `--scripted`) | — |
| `--out` | run directory for `results.jsonl`, `shots/` and `appdata/` (required) | — |
| `--rate` | streaming frequency in Hz | `100` |
| `--seconds` | how long to stream once the first sample arrives | `5` |
| `--scripted <state>` | test-double mode — **an unimplemented stub today** | — |

Exit codes: `0` all checks passed · `1` at least one `[FAIL]`, or the run threw ·
`2` bad arguments, or an `--out` the rig could not prepare.

### Output

```
<out>/results.jsonl     one JSON object per check: tier, row, check, status, evidence,
                        and optionally artifact + seconds
<out>/shots/*.png       the live window rendered at each step
<out>/appdata/          the app's data directory for this run (see below)
```

### It never touches your real app data

The rig sets `DAQIFI_DATA_DIR` to `<out>/appdata` before boot, so a run cannot write to —
or delete from — the user's real `~/Library/Application Support/DAQiFi`, which holds their
live `DAQiFiDatabase.db`, `DAQifiConfiguration.xml` and logs. It also sets
`DAQIFI_TEST_MODE=1`, which swaps the firewall message box for a no-op and leaves the HID
bootloader watcher unstarted (it takes exclusive HID handles). Neither disables the serial
connection — the hardware path is the point.

## Rows covered

`CONN-USB`, `DEV-INFO`, `DEV-TILE`, `CH-TILE`, `DEV-RATE`, `CH-AI`, `STREAM-AI`,
`LOG-SESSION`, `GRAPH-LIVE`, `CH-DIO`, `CH-PWM`, `SD-LIST`, `CONN-DISC` — plus a `limits`
check on `CH-PWM` and four `unexpected`-check probes (sample-arrival gaps and device-clock
skew, the pin state a channel is left in after PWM is disabled, thread growth across
connect/disconnect, and UI pump latency). Add a row by adding a `Step`; keep the shape —
drive, assert the device, pump, assert the UI, capture, emit.

The full T2/T3 matrix has more rows than this — `DEV-NAME`, `DEV-NET` and `DEV-DEBUG` are
not implemented, for the reasons in Known gaps below. Tracked in #260.

## Known gaps — read before trusting a green run

These are real limits of the current rig, not of the app. Tracked in #260.

- **`--scripted` is a stub**, so every test-double state — empty SD card, dropped device, 500
  sessions, a read-only export destination, a 10-hour session — is unreachable. These are the
  T1 rows that need no hardware, so they are also the ones CI could actually run.
- **`STREAM-AI` fails against fw-3.7.2 and that is not a regression.** The board reports
  ~99.8 Hz by its own timestamps and ~79.7 Hz by wall clock at a set rate of 100. The row's
  companion `unexpected` probe prints both spans precisely so the two layers can be told
  apart; the discrepancy is a long-known firmware timebase issue.
- **`CONN-DISC` does not check `RemoveFirmwareNotification`.** It now drives
  `DaqifiViewModel.DisconnectDeviceCommand` and asserts three of that command's four effects
  (the device leaves both device lists, no channel is left subscribed, `SelectedDevice` is
  cleared). The firmware-notification removal is not asserted.
- **One device, one analog channel, one digital channel, one rate.** The streaming rows run
  against the first analog input of the first connected device at `--rate`; `CH-DIO` and
  `CH-PWM` run against its first PWM-capable digital channel (its first digital channel if
  none is PWM-capable). Multi-device fleets and rate limits are untested. Everything those two
  rows touch — direction, drive state, PWM mode, duty — is state the *board* keeps across a
  host disconnect, so they snapshot all four, disable an inherited PWM before asserting, and
  restore from a `finally` (each step guarded on its own, so one failure cannot skip the steps
  that stop the pin being driven); an aborted run must not leave a shared bench board driving a
  pin. `CH-DIO/cleanup` then reads the restore back and **fails the run** if it did not hold —
  but only `IsPwmEnabled` and `PwmDutyCyclePercent` carry real signal there, because they read
  Core's mirror of the last state it successfully *commanded*, while `IsOutput` and
  `IsDigitalOn` are local properties that echo whatever was assigned. The device layer logs and
  swallows a failed command rather than returning one, so that read-back is as far as the rig
  can see without changing the app for the benefit of its own test harness.
- **`DEV-NAME` is not implemented, and needs `DAQIFI_RESTORE_NAME` alongside it.**
  `SetFriendlyName` sends `SYSTem:DEVice:NAME` *and* `…:NAME:SAVE`, so it writes the board's
  NVM: an interrupted run leaves a shared bench board renamed with nothing to put it back.
  The repair mode is the row's precondition, not a nicety, and it is its own piece of work.
- **`DEV-NET` is not implemented, because there is nothing here it could assert.** The app
  never reads network configuration back from the device; `NetworkConfiguration` is hydrated
  from the status metadata `DEV-INFO` already covers (`HydrateDeviceMetadata`), and the only
  Core read path, `LoadNetworkConfigurationAsync`, is not wired up. Writing configuration is
  `DEV-NET-W`, a T4 row that needs a human.
- **`DEV-DEBUG` is not implemented, deliberately.** Enabling debug mode fires
  `TriggerWifiFirmwareProbe`, whose WINC chip-info query the app itself gates behind debug
  mode because it *"can choke a device with a blank/erased WINC"*. That is not something to
  run unattended against a shared bench board for the sake of a checkbox row.

## Four traps it encodes, for whoever edits it

- **An assertion can be satisfied by a safety net rather than by the path you meant to test.**
  Driving `ConnectionManager.Instance.Disconnect` instead of `DisconnectDeviceCommand` still
  ends the run with zero subscribed channels — `DaqifiViewModel`'s `ConnectedDevices` handler
  sweeps orphaned subscriptions for the auto-removal paths (unplug, WiFi timeout) and catches
  this one on the way past. Measured on the bench, not assumed: of the command's four effects,
  only the `SelectedDevice` clearing actually distinguishes the two call sites. Assert the
  effect that is *unique* to the path, or the row passes either way.
- **Call the property the view binds, not the device method.** `Channel.IsDigitalOn` and
  `SelectedChannel.PwmDutyCyclePercent` are what the tiles bind;
  `device.SetChannelOutputValue` / `device.SetChannelPwmDutyCycle` send the SCPI write
  *without* updating the bound property. Probing the device method reads a stale value and
  reports a bug that is not there.
- **Never block on an async command.** Headless, `Dispatcher.UIThread` *is* the calling
  thread, so `.GetAwaiter().GetResult()` on a command's `Task` deadlocks forever. Start the
  task, then `PumpUntil` it completes.
- **A tile you are holding may already have been thrown away.** Anything that re-shelves a
  channel — flipping its direction, enabling PWM — posts `ChannelsPaneViewModel.Rebuild` at
  Background priority, which disposes every tile and builds new ones. Read a tile through
  `FindTile` after each such change rather than reusing the reference from before it, or the
  row asserts against a dead object that will never update again. The same goes for a command
  that is already running: `DeviceLogsViewModel` fires an SD refresh of its own when a device
  becomes selected, and a second `RefreshFilesCommand.ExecuteAsync` while the first is in
  flight is dropped, so `SD-LIST` waits for `CanExecute` before it drives the button.

## Two build constraints it shares with `AvaloniaCapture`

- **Not in `Daqifi.Avalonia.slnx`**, like every other project under `tools/` and
  `third_party/`. Restore and build it by path.
- **Never build it with an explicit `-r`.** A RID-specific restore rewrites
  `packages.lock.json` to hold that one RID, and CI's locked restore then fails `NU1004`
  (#85). The long note at the top of `Directory.Build.props` has the detail.

CI compiles it (the `desktop` job in `.github/workflows/build.yml`) but never runs it —
running needs a board. The compile is what stops the app's internals moving out from under
it unnoticed.
