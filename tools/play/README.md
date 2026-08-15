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
| 1 | `91338e4` | internal | **do not promote** — Sentry inert, pre-review defects |
| 2 | _(pending)_ | | build from `main` |
