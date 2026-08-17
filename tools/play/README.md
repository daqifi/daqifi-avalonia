# Releasing to Play

## Before promoting anything to production

**Check which commit the artifact on the track was built from.** Play shows a versionCode, not a
commit, so the two drift silently and the drift is invisible in the Console.

The first internal-testing upload (**versionCode 1**) was built from `91338e4`. Thirteen commits
landed afterwards, so that artifact is missing — among other things — the fix that makes Sentry
initialise at all on Android (`d3a3511`). Promoting it would ship an app with **no crash
reporting**, while the Play Data safety form declares that crash logs are collected. It also
predates every defect the pre-merge review found: the foreground-service leak on a silent drop, a
watchdog that could disconnect a healthy stream, a guaranteed crash when foreground promotion
fails, and the render-path allocation churn.

**versionCode 1 must not go to production.** Build a fresh artifact from `main` and upload it as
versionCode 2.

## Building the signed bundle

```bash
bash tools/play/build-release-aab.sh
```

Reads the keystore password from `~/keystores/.daqifi-upload.pw` so it never reaches the process
list or shell history, refuses to run without the keystore, refuses a debug-signed or unreadable
certificate, and refuses to guess when more than one signed bundle is present.

## The publishing credential

Created 2026-08-16. A **dedicated** Google Cloud project, deliberately not one of the existing
DAQiFi/Tacuna projects — a leaked publishing key should not also reach Gemini, Search Console, or
(most importantly) the `tacuna-mailbox-dwd` service account, which holds domain-wide Gmail
delegation.

| | |
|---|---|
| Cloud project | `daqifi-play-publishing` ("DAQiFi Play Publishing") |
| Service account | `play-publisher@daqifi-play-publishing.iam.gserviceaccount.com` |
| API enabled | `androidpublisher.googleapis.com` |
| Key file | `~/keys/play-sa.json` (mode 0600, outside the repo tree so it cannot be committed) |

The service account holds **no GCP IAM roles at all**. Its only authority is what Play Console
grants it, which keeps the blast radius to store releases.

To recreate it from scratch:

```bash
gcloud projects create daqifi-play-publishing --name="DAQiFi Play Publishing"
gcloud services enable androidpublisher.googleapis.com --project=daqifi-play-publishing
gcloud iam service-accounts create play-publisher --project=daqifi-play-publishing \
    --display-name="Play Publisher"
umask 077 && gcloud iam service-accounts keys create ~/keys/play-sa.json \
    --iam-account=play-publisher@daqifi-play-publishing.iam.gserviceaccount.com
```

Then, in the Play Console — **neither step has an API, both are one-time**:

1. **Setup → API access** → link the `daqifi-play-publishing` Cloud project. Requires the Play
   Console **account owner**; an admin cannot do it.
2. **Users and permissions** → invite `play-publisher@…` → grant, for `com.daqifi.app`, *View app
   information* and *Release apps to testing tracks* (add *Release to production* only when the
   full rollout is driven from here).

Reading the failure from `--check` tells you which half is missing:

| result | meaning |
|---|---|
| `HTTP 401` | key wrong/revoked, or the API not enabled on the project |
| `HTTP 403` | key is good; Play Console has not granted it access yet (step 2, or still propagating) |
| `HTTP 404` | authorised, but no app with this package — it must be created in the Console first |

Permission changes usually apply within minutes but can lag. Re-run `--check` rather than guessing.

## Uploading

```bash
# verify credentials and permissions without touching the store
python3 tools/play/publish.py --key ~/keys/play-sa.json --check

# full dry run: uploads into a throwaway Play "edit", then discards it
python3 tools/play/publish.py --key ~/keys/play-sa.json --aab <path>

# publish
python3 tools/play/publish.py --key ~/keys/play-sa.json --aab <path> --track internal --commit
```

Always dry-run first. The whole flow — auth, upload, track assignment — runs against a staging
edit that is then deleted, so credentials and the bundle are verified end to end with nothing
reaching the store.

## Recording what shipped

After each upload, note the versionCode **and the commit SHA** it was built from. That mapping is
the only thing that answers "is the build on the track current?" without guessing, and it is the
question this file exists because of.

| versionCode | commit | track | notes |
|---|---|---|---|
| 1 | `91338e4` | — | superseded 2026-08-16; Sentry inert, pre-review defects. **Never promote.** |
| 2 | `8117d38` | internal | 2026-08-16. First build with working crash reporting: breadcrumb trail, device context, offline envelope cache. |
| 3 | _(pending)_ | | supersedes 2 — fixes the crash-on-launch a second activity instance caused (DAQIFI-DESKTOP-1Y). |
