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
- Disconnection is `ConnectionManager.Instance.Disconnect(device)`, which is what the
  Devices pane calls. Calling `device.Disconnect()` closes the port but leaves the device
  registered, which is precisely the stale-"Connected" state the `CONN-DISC` row exists to
  catch.
- Streaming is driven by toggling `shell.IsLogging`, so the disk-space check, session
  creation and `LoggingFleet.Start` all happen the way they do in the app.

Every step renders the live window to a PNG and appends one JSON line to
`<out>/results.jsonl`, so a run leaves behind evidence rather than a verdict.

## Running it

```bash
# Restore separately — it is deliberately NOT in Daqifi.Avalonia.slnx (see below).
dotnet restore tools/system-test/HeadlessBench/HeadlessBench.csproj
dotnet run   --project tools/system-test/HeadlessBench/HeadlessBench.csproj --no-restore -- \
  --port /dev/cu.usbmodem1101 --out "/tmp/headlessbench-$(date +%Y%m%d-%H%M%S)" \
  --rate 100 --seconds 5
```

**Give every run a fresh `--out`.** `results.jsonl` is **appended**, never truncated, and
the rig does not refuse a directory that already has one. Reuse the same path twice and
the file holds two runs' verdicts with nothing marking the boundary — which is why the
example above stamps the directory with the time.

| flag | meaning | default |
| --- | --- | --- |
| `--port` | serial port of the board (required unless `--scripted`) | — |
| `--out` | run directory for `results.jsonl`, `shots/` and `appdata/` (required) | — |
| `--rate` | streaming frequency in Hz | `100` |
| `--seconds` | how long to stream once the first sample arrives | `5` |
| `--scripted <state>` | test-double mode — **an unimplemented stub today** | — |

Exit codes: `0` all checks passed · `1` at least one `[FAIL]`, or the run threw ·
`2` bad arguments.

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

`CONN-USB`, `DEV-INFO`, `CH-AI`, `STREAM-AI`, `LOG-SESSION`, `GRAPH-LIVE`, `CONN-DISC`, plus
three `unexpected`-check probes (sample-arrival gaps and device-clock skew, thread growth
across connect/disconnect, and UI pump latency). Add a row by adding a `Step`; keep the
shape — drive, assert the device, pump, assert the UI, capture, emit.

## Known gaps — read before trusting a green run

These are real limits of the current rig, not of the app. Tracked in #260.

- **`CONN-DISC` is one layer below the user's click.** It calls
  `ConnectionManager.Instance.Disconnect(device)`, but a user's disconnect runs
  `DaqifiViewModel.DisconnectDeviceCommand`, which *also* unsubscribes every active channel
  from `LoggingManager`, calls `RemoveFirmwareNotification`, and clears `SelectedDevice`.
  So `CONN-DISC` can pass while that cleanup is broken, and the channel this rig subscribed
  in `CH-AI` is still subscribed when the run ends. (The connect side does not have this
  problem — it goes through the dialog's own command.)
- **`LOG-SESSION` does not check the database.** It asserts only that a sample arrived and
  that the logging toggles returned to false. Session finalization is a fire-and-forget
  task the rig neither awaits nor validates, so a persistence failure still reports a pass.
  The emitted evidence string says so too.
- **No cleanup on the exception path.** `RunHardwareSequence` stops logging and disconnects
  only on the normal path; an exception partway through leaves the board streaming until
  the process exits.
- **Setup is unguarded.** Creating `--out`, setting the environment and registering icons
  all run before the try block, so a bad path or a permission error crashes with a stack
  trace rather than the documented exit codes.
- **`--scripted` is a stub**, so every test-double state is unreachable.

## Two traps it encodes, for whoever edits it

- **Call the property the view binds, not the device method.** `Channel.IsDigitalOn` and
  `SelectedChannel.PwmDutyCyclePercent` are what the tiles bind;
  `device.SetChannelOutputValue` / `device.SetChannelPwmDutyCycle` send the SCPI write
  *without* updating the bound property. Probing the device method reads a stale value and
  reports a bug that is not there.
- **Never block on an async command.** Headless, `Dispatcher.UIThread` *is* the calling
  thread, so `.GetAwaiter().GetResult()` on a command's `Task` deadlocks forever. Start the
  task, then `PumpUntil` it completes.

## Two build constraints it shares with `AvaloniaCapture`

- **Not in `Daqifi.Avalonia.slnx`**, like every other project under `tools/` and
  `third_party/`. Restore and build it by path.
- **Never build it with an explicit `-r`.** A RID-specific restore rewrites
  `packages.lock.json` to hold that one RID, and CI's locked restore then fails `NU1004`
  (#85). The long note at the top of `Directory.Build.props` has the detail.

CI compiles it (the `desktop` job in `.github/workflows/build.yml`) but never runs it —
running needs a board. The compile is what stops the app's internals moving out from under
it unnoticed.
