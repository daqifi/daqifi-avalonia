# Plan divergence follow-up — `Daqifi.Desktop.Logger.CsvLogger`

A plan step was closed with a recorded divergence: `Daqifi.Desktop.Logger.CsvLogger` (plan `20260714T175109-check-c4c63fa`, step `c002`, shard `UI`).

> Reason: Dead code deleted upstream in 4c060cc (superseded by Core CsvExporter); the port mirrors the deletion — file was already Compile-Remove'd on both sides

Every recorded divergence is a SIGNAL that a cross-concept porting idiom may have been missed. Plan-close divergences are the most common source during a drain (#287) — the same loop that surfaced DIV-STREAM-ONESHOT from a device-golden waiver (#203) has to fire here too, or the catalog stops compounding exactly when the most idioms are being discovered.

Before moving on, decide:

- [ ] **Is this divergence a missed cross-concept idiom?** Does the same upstream→downstream difference that made you skip `Daqifi.Desktop.Logger.CsvLogger` recur across other files/views/concepts — name-scope rules, theming/resource lookup, control-template conversion, binding modes, framework capability gaps — such that the next port or re-sync will hit it too?
- [ ] **If YES** — enrich the shard's concept catalog: add/extend a `hal_pairs`/`mechanisms` entry so the idiom surfaces at translate time for every affected unit (and feeds work-order packets, #290). Draft below.
- [ ] **If NO** — it's a genuine one-off: keep the step's divergence rationale current and close this review step (full-capability-parity, #176).

## Draft catalog patch (if YES)

The recorded reason is the first draft of the invariant — generalize it from "why this symbol was skipped" to "the rule every sibling must honor":

```yaml
mechanisms:
  - name: <name the shared mechanism this skip revealed>
    concepts: ["Daqifi.Desktop.Logger.CsvLogger", <siblings that share it>]
    resource: <the shared downstream facility, if any>
    invariant: >
      <generalize: Dead code deleted upstream in 4c060cc (superseded by Core CsvExporter); the port mirrors the deletion — file was already Compile-Remove'd on both sides>
```

