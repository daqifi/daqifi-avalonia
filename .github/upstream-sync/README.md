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

**Phase 2 — auto-triage (opt-in, off by default).** If enabled, a second job runs
Claude Code headlessly to pre-classify each commit (ALREADY-PORTED / PORT-AS-IS /
ADAPT / N/A, with port evidence) and posts that as an **advisory comment** on the
digest issue. It is deliberately scoped to *analysis + comment only* — it does **not**
open PRs or push code, so the gated-merge discipline (build → security + code-review →
Qodo → adversarial audit → merge) is preserved for a human/agent to run.

## The watermark

`last-reviewed.sha` holds the last upstream commit that has been reviewed for porting.
The weekly job reports everything newer than it.

**After you finish a triage pass, advance it:** set `last-reviewed.sha` to the upstream
HEAD shown in the digest and commit. That closes the digest until the next real delta.
(This is the piece that was missing before — sync state now lives in the repo instead of
being reconstructed by hand each time.)

## Setup

### Required (Phase 1)

- **Secret `UPSTREAM_SYNC_TOKEN`** — a token that can *read* `daqifi-desktop` (a
  fine-grained PAT with `Contents: read` on `daqifi/daqifi-desktop`, or a GitHub App
  installation token). The default `GITHUB_TOKEN` is scoped to this repo only and cannot
  read another repo, so a checkout of the upstream needs this. Issue creation on *this*
  repo uses the built-in `GITHUB_TOKEN` (`issues: write`) — no extra scope needed there.

### To enable Phase 2 (optional)

Both must be present, or the job skips silently (no API cost):

- **Variable `UPSTREAM_SYNC_AUTOTRIAGE`** = `true` (repo → Settings → Variables).
- **Secret `ANTHROPIC_API_KEY`** — an Anthropic API key. Phase 2 spends API credits on
  each run that has a non-empty delta; `--max-turns` and a Sonnet model keep it bounded,
  and `timeout-minutes` caps runaway jobs.

## Running it by hand

Actions → **Upstream sync check** → **Run workflow**. Tick `force` to rebuild the digest
even when there are no new commits (useful for a first smoke test).
