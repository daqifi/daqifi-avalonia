# Merge queue

The point of a merge queue is the failure that no amount of per-PR CI can catch: two
pull requests that each pass against `main` and break once both are in. Instead of
merging a PR's own (already-stale) head, GitHub builds a **merge group** — `main` plus
the queued PRs in order — and merges only if that tentative result is green. It also
means you can queue a stack of PRs and walk away, rather than babysitting
rebase-and-wait for each one.

**Status: the repository side is wired, the GitHub side is blocked on plan.** See
[Enabling it](#enabling-it).

## What is already done

`.github/workflows/build.yml` triggers on `merge_group`. That trigger is not optional:
a queue entry is built on a synthetic `refs/heads/gh-readonly-queue/main/pr-N-<sha>`
ref, and **a required check that lacks a `merge_group` trigger never reports on that
ref**. The entry does not fail fast — it sits until the queue's status-check timeout
expires and is then ejected, which looks like a broken queue rather than a missing
line of YAML.

Two properties of `build.yml` make it safe to build this way, and both are worth
preserving:

- **No job reads pull-request context.** Nothing references
  `github.event.pull_request`, `github.head_ref`, or `github.base_ref`, so every job
  behaves identically on a merge group. A new step that needs any of them must be
  gated on `github.event_name`.
- **`cancel-in-progress` is keyed on `pull_request` only.** A cancelled `merge_group`
  run reports as a *failed* required check, which ejects that PR from the queue and
  forces everything behind it to rebuild. Never broaden that condition to `true`.

## Checks that are safe to require

Require only checks that run on `merge_group`. From `build.yml`:

| Check name | |
| --- | --- |
| `CI script self-tests` | guards' own tests |
| `Library + Desktop head + capture harness` | |
| `Desktop head on macOS` | macOS runner |
| `Android head` | |
| `iOS head` | macOS runner |
| `Avalonia version lockstep` | needs the three head jobs |

**Do not require `Claimed bumps are actually applied`** (`dependabot-claims-check.yml`).
It verifies a Dependabot PR's description against its diff, and a merge group has no
single PR body to verify — it cannot be made merge-queue-aware, so it stays a
pull-request-time check. Requiring it would hang every queue entry until timeout. The
same reasoning applies to any future check whose input is the PR itself rather than
the resulting tree.

## Enabling it

GitHub exposes merge queue only through a branch ruleset or branch protection rule on
`main`, and **neither is available on this repository today**:

```
$ gh api repos/daqifi/daqifi-avalonia/rulesets
Upgrade to GitHub Pro or make this repository public to enable this feature. (HTTP 403)
```

`daqifi` is on the **free** plan and this repository is **private**. Merge queues are
available on public organization repositories, and on private repositories only under
GitHub Enterprise Cloud — the Team plan does not include them for private repos. So
there are two routes:

1. **Make the repository public.** Unblocks merge queue and makes Actions minutes free
   (relevant below). Worth noting that this repo is the private outlier among its own
   dependencies — [`daqifi-desktop`](https://github.com/daqifi/daqifi-desktop), the WPF
   app it ports, and [`daqifi-core`](https://github.com/daqifi/daqifi-core), the library
   it wraps, are both already public. Still a product decision, not a CI one.
2. **Move the org to GitHub Enterprise Cloud.** Note that GitHub **Team** would not
   help: the availability line is "any public repository owned by an organization, or
   ... private repositories owned by organizations using GitHub Enterprise Cloud".

Once one of those is true, in **Settings → Rules → Rulesets** (or branch protection)
for `main`:

- Enable **Require merge queue**.
- Add the six checks above under **Require status checks to pass**.
- **Maximum pull requests to build concurrently**: start at **1–2**, not the default 5
  — see the cost note.
- **Merge method**: `Squash`, to match this repo's history.
- **Maximum PRs to merge / batch size**: `1` while the queue is new. Batching merges a
  group as one unit, so one bad PR fails the whole batch and everything re-forms;
  batch only once green runs are the norm.
- **Build timeout**: the full build is ~6–11 minutes wall clock, so the 60-minute
  default is generous. Leave it.

Also consider turning on **Allow auto-merge** (`Settings → General`), currently off.
With a queue, auto-merge is how a PR joins the queue on approval instead of waiting
for someone to click.

### Cost note

The queue rebuilds *per entry*, so N queued PRs cost roughly N full builds. `build.yml`
has two `macos-latest` jobs (`Desktop head on macOS`, `iOS head`), and macOS runners
bill at **10×** on private repositories. Concurrency 5 on a private repo can burn a
free-plan month of Actions minutes quickly — hence the recommendation to start at 1–2.
If the repository goes public instead, this stops mattering.
