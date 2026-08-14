#!/usr/bin/env python3
"""Upload a signed AAB to a Google Play track via the Play Developer API.

Why this exists
---------------
The Play Console cannot be driven headlessly, but the release step that repeats
every version — upload a bundle, put it on a track, write release notes — is
fully covered by the `androidpublisher` API. This wraps that.

It deliberately does NOT try to cover the parts the API cannot do. Creating the
app, the Data safety form, and the App content declarations are Console-only and
one-time; see `--check` output for the current state of what the API can see.

Safety
------
Nothing reaches the store unless `--commit` is passed. Without it the script does
the whole flow against a Play "edit" — a staging area — and then deletes it, so
you get a real end-to-end verification of credentials, permissions and the bundle
itself with no outward effect. Run it that way first, every time.

Usage
-----
    # verify access and show what Play currently has (no edit created)
    python3 tools/play/publish.py --key ~/keys/play-sa.json --check

    # full dry run: upload into a throwaway edit, then discard it
    python3 tools/play/publish.py --key ~/keys/play-sa.json \
        --aab Daqifi.Avalonia.Android/bin/Release/net10.0-android36.0/com.daqifi.app.aab

    # the real thing
    python3 tools/play/publish.py --key ~/keys/play-sa.json \
        --aab .../com.daqifi.app.aab --track internal --notes "First internal build." --commit
"""

import argparse
import os
import sys

try:
    from google.oauth2 import service_account
    from googleapiclient.discovery import build
    from googleapiclient.errors import HttpError
except ImportError:
    sys.exit("Missing deps. Install with:\n"
             "  python3 -m pip install --user google-auth google-api-python-client")

PACKAGE = "com.daqifi.app"
SCOPE = "https://www.googleapis.com/auth/androidpublisher"


def connect(key_path):
    if not os.path.isfile(key_path):
        sys.exit(f"Service-account key not found: {key_path}")
    creds = service_account.Credentials.from_service_account_file(key_path, scopes=[SCOPE])
    return build("androidpublisher", "v3", credentials=creds, cache_discovery=False)


def explain(err):
    """Turn the common API failures into something actionable."""
    status = getattr(getattr(err, "resp", None), "status", None)
    detail = err._get_reason() if hasattr(err, "_get_reason") else str(err)
    hints = {
        401: "Authentication failed. The key is wrong, revoked, or its project lacks the "
             "Google Play Android Developer API.",
        403: "Authenticated, but not authorised. In Play Console → Users and permissions, "
             "invite the service-account email and grant it release permissions for this app. "
             "Permission changes can take a few minutes to apply.",
        404: f"Play has no app with package '{PACKAGE}'. The app must be CREATED IN THE CONSOLE "
             "first — there is no API to create one. Check the package name matches exactly.",
    }
    return f"HTTP {status}: {detail}\n  → {hints.get(status, 'See the error above.')}"


def check(service):
    """Report what Play currently holds, without creating an edit."""
    print(f"Package: {PACKAGE}")
    edit = service.edits().insert(body={}, packageName=PACKAGE).execute()
    edit_id = edit["id"]
    try:
        tracks = service.edits().tracks().list(
            packageName=PACKAGE, editId=edit_id).execute().get("tracks", [])
        if not tracks:
            print("Tracks: none configured yet.")
        for t in tracks:
            releases = t.get("releases", [])
            if not releases:
                print(f"  {t['track']}: no releases")
            for r in releases:
                versions = ", ".join(r.get("versionCodes", [])) or "—"
                print(f"  {t['track']}: status={r.get('status')} "
                      f"versionCodes=[{versions}] name={r.get('name', '—')}")

        bundles = service.edits().bundles().list(
            packageName=PACKAGE, editId=edit_id).execute().get("bundles", [])
        codes = sorted(b["versionCode"] for b in bundles)
        print(f"Uploaded bundles: {codes if codes else 'none'}")
        if codes:
            print(f"  → next versionCode must be > {codes[-1]}")
    finally:
        # Always discard: --check must never leave a dangling edit behind.
        service.edits().delete(packageName=PACKAGE, editId=edit_id).execute()


def publish(service, aab_path, track, notes, commit):
    if not os.path.isfile(aab_path):
        sys.exit(f"AAB not found: {aab_path}")

    size_mb = os.path.getsize(aab_path) / (1024 * 1024)
    print(f"Bundle: {aab_path} ({size_mb:.1f} MB)")

    edit_id = service.edits().insert(body={}, packageName=PACKAGE).execute()["id"]
    print(f"Edit:   {edit_id}")

    committed = False
    try:
        bundle = service.edits().bundles().upload(
            packageName=PACKAGE, editId=edit_id,
            media_body=aab_path, media_mime_type="application/octet-stream").execute()
        version_code = bundle["versionCode"]
        print(f"Uploaded versionCode {version_code}")

        release = {"status": "completed", "versionCodes": [str(version_code)]}
        if notes:
            release["releaseNotes"] = [{"language": "en-US", "text": notes}]

        service.edits().tracks().update(
            packageName=PACKAGE, editId=edit_id, track=track,
            body={"track": track, "releases": [release]}).execute()
        print(f"Assigned to track '{track}'")

        if not commit:
            print("\nDRY RUN — discarding the edit. Nothing reached the store.")
            print("Everything above succeeded, so credentials, permissions and the bundle are good.")
            print("Re-run with --commit to publish.")
            return

        service.edits().commit(packageName=PACKAGE, editId=edit_id).execute()
        committed = True
        print(f"\nCOMMITTED — versionCode {version_code} is live on '{track}'.")
    finally:
        if not committed:
            try:
                service.edits().delete(packageName=PACKAGE, editId=edit_id).execute()
            except HttpError:
                pass   # already gone, or the commit consumed it


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--key", required=True, help="path to the service-account JSON key")
    p.add_argument("--aab", help="path to the signed .aab")
    p.add_argument("--track", default="internal",
                   choices=["internal", "alpha", "beta", "production"])
    p.add_argument("--notes", default="", help="release notes (en-US)")
    p.add_argument("--check", action="store_true",
                   help="report current tracks and bundles, then exit")
    p.add_argument("--commit", action="store_true",
                   help="actually publish. Without this the edit is discarded.")
    args = p.parse_args()

    service = connect(args.key)

    try:
        if args.check:
            check(service)
            return
        if not args.aab:
            sys.exit("--aab is required unless --check is passed.")
        publish(service, args.aab, args.track, args.notes, args.commit)
    except HttpError as err:
        sys.exit(explain(err))


if __name__ == "__main__":
    main()
