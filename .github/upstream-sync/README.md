# Upstream sync check

This port ([`daqifi-avalonia`](https://github.com/daqifi/daqifi-avalonia)) tracks the
upstream WPF app [`daqifi-desktop`](https://github.com/daqifi/daqifi-desktop). The two
are co-developed closely, so upstream fixes are often already present here — but not
always. `.github/workflows/upstream-sync-check.yml` runs **weekly** to surface any
upstream commits that still need triage, so nothing quietly falls through.

## What it does

**Phase 1 — digest (always on).** Each Monday it diffs `daqifi-desktop@main` against
the **watermark** in `last-reviewed.sha` and, if there are new commits, opens or updates
one rolling issue labelled [`upstream-sync`](https://github.com/daqifi/daqifi-avalonia/labels/upstream-sync)
listing them with a triage checklist. No new commits → it does nothing. It never
auto-files per-commit tickets (most commits are already ported; that would just be
spam) — it produces **one digest for a human or agent to triage**.

**Phase 2 — supervised auto-triage (label-driven).** The digest issue carries the
`upstream-sync` label; a **supervised** agent picks it up and runs the gap-analysis
(grep the port for each commit's symbols → ALREADY-PORTED / PORT-AS-IS / ADAPT / N/A)
and then the gated port PR flow (build → security + code-review → Qodo → adversarial
audit → merge). That agent is the maintainer's **local, gated Claude Code session**
(e.g. via the `daqifi-loop` skill) — an environment with real guardrails and a human in
the loop.

Auto-triage is deliberately **not** run as an LLM step inside CI. A model reading
untrusted upstream diffs, with shell access, next to live workflow credentials is the
classic prompt-injection "lethal trifecta": a malicious upstream diff could instruct the
model to read a persisted token and exfiltrate it into the (world-readable) digest
comment. Keeping the model out of CI removes that surface entirely while preserving the
capability where it's safe. (If you ever want in-CI pre-classification anyway, it must be
built as a split design — a no-write LLM job whose output a *separate, non-LLM* step
posts, with `persist-credentials: false` and no PAT/GITHUB_TOKEN in the model's env — and
even then the residual is worth a deliberate decision.)

## The watermark

`last-reviewed.sha` holds the last upstream commit that has been reviewed for porting.
The weekly job reports everything newer than it.

**After you finish a triage pass, advance it:** set `last-reviewed.sha` to the upstream
HEAD shown in the digest and commit. That closes the digest until the next real delta.
(This is the piece that was missing before — sync state now lives in the repo instead of
being reconstructed by hand each time.)

## Setup

The workflow needs exactly one secret:

- **Secret `UPSTREAM_SYNC_TOKEN`** — a token that can *read* `daqifi-desktop` (a
  fine-grained PAT with `Contents: read` on `daqifi/daqifi-desktop`, or a GitHub App
  installation token). The default `GITHUB_TOKEN` is scoped to this repo only and cannot
  read another repo, so the upstream checkout needs this. The checkout uses
  `persist-credentials: false` so the token is not left behind in `.git/config`. Issue
  creation on *this* repo uses the built-in `GITHUB_TOKEN` (`issues: write`) — no extra
  scope needed there.

Phase 2 (triage) needs **no** CI secret or variable — it happens in your local gated
Claude Code session against the labelled digest issue (see above).

## Running it by hand

Actions → **Upstream sync check** → **Run workflow**. Tick `force` to rebuild the digest
even when there are no new commits (useful for a first smoke test).
