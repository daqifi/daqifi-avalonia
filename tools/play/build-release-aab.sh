#!/usr/bin/env bash
# Build the signed release AAB for Play upload.
#
# Wraps the two things that are easy to get wrong by hand:
#
#   1. AndroidSdkDirectory must be passed explicitly. Neither ANDROID_HOME nor
#      ANDROID_SDK_ROOT is exported on the build box, so omitting it fails with
#      XA5300 before signing is even attempted.
#   2. Without a keystore the SDK silently signs with a throwaway debug key
#      (CN=Android Debug). Uploading that as the FIRST bundle would permanently
#      set Play's upload certificate to a machine-local key that is not backed
#      up anywhere. This script refuses to produce an unsigned/debug build.
#
# The password is read from a file rather than taken as an argument, so it never
# lands in the process list or shell history.
set -euo pipefail

KEYSTORE="${DAQIFI_KEYSTORE:-$HOME/keystores/daqifi-upload.jks}"
PWFILE="${DAQIFI_KEYSTORE_PWFILE:-$HOME/keystores/.daqifi-upload.pw}"
SDK="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-/home/user/android-sdk}}"
DOTNET="${DOTNET:-$HOME/.dotnet-linux/dotnet}"
PROJ="Daqifi.Avalonia.Android/Daqifi.Avalonia.Android.csproj"

[[ -f "$KEYSTORE" ]] || { echo "No keystore at $KEYSTORE" >&2; exit 1; }
[[ -f "$PWFILE"   ]] || { echo "No password file at $PWFILE" >&2; exit 1; }
[[ -d "$SDK"      ]] || { echo "No Android SDK at $SDK" >&2; exit 1; }

export DAQIFI_KEYSTORE_PASS
DAQIFI_KEYSTORE_PASS="$(<"$PWFILE")"

# Marker for the freshness check below. Anything the build does not regenerate will be older
# than this.
STAMP=$(mktemp)

"$DOTNET" build "$PROJ" -c Release \
  -p:AndroidSdkDirectory="$SDK" \
  -p:AndroidSigningKeyStore="$KEYSTORE"

# Require EXACTLY one signed bundle. `find … | head -1` picks by filesystem traversal order,
# so a stale artifact from an earlier build silently wins — and the thing selected here is what
# gets uploaded to Play. This is not hypothetical: during the first release both a stale
# `bin/Release/net10.0-android/` tree (from before the TFM was pinned) and an old debug-signed
# artifact sat alongside the real one, and `head -1` chose between them by luck.
mapfile -t AABS < <(find Daqifi.Avalonia.Android/bin/Release -name "*-Signed.aab" | sort)
if [[ ${#AABS[@]} -eq 0 ]]; then
  echo "Build finished but produced no signed .aab" >&2
  exit 1
fi
if [[ ${#AABS[@]} -gt 1 ]]; then
  echo "Refusing to guess: ${#AABS[@]} signed bundles under bin/Release —" >&2
  printf '  %s\n' "${AABS[@]}" >&2
  echo "Delete the stale ones (or 'dotnet clean') and re-run, so the upload is unambiguous." >&2
  exit 1
fi
AAB="${AABS[0]}"

# Refuse a stale artifact. The build succeeding does NOT guarantee it produced a new bundle — an
# up-to-date incremental build is a successful no-op, and the previous run's .aab is still sitting
# at the same path. Every other check here would pass on it: it is signed with the right key and
# there is exactly one. This is precisely how versionCode 1 nearly went to production carrying a
# 13-commit-old build, so the freshness of the thing being uploaded is checked, not assumed.
if [[ ! "$AAB" -nt "$STAMP" ]]; then
  echo "REFUSING: $AAB is older than this build — the build did not regenerate it." >&2
  echo "Run 'dotnet clean' (or delete bin/Release) and try again." >&2
  rm -f "$STAMP"
  exit 1
fi
rm -f "$STAMP"

echo
echo "Artifact: $AAB"
# Fail rather than warn. This script exists to stop a debug-signed bundle reaching Play, where
# the first upload permanently fixes the upload certificate — a warning printed above a 300-line
# build log is not a control, it is a hope. Unreadable is also a failure: if the certificate
# cannot be checked, the one guarantee this script offers has not been established.
OWNER=$(unzip -p "$AAB" 'META-INF/*.RSA' 2>/dev/null | keytool -printcert 2>/dev/null | grep -m1 '^Owner:')
if [[ -z "$OWNER" ]]; then
  echo "Could not read the signing certificate from $AAB — refusing to vouch for it." >&2
  exit 1
fi
if [[ "$OWNER" == *"CN=Android Debug"* ]]; then
  echo "REFUSING: $AAB is DEBUG-signed ($OWNER)." >&2
  echo "Uploading it would permanently set Play's upload certificate to a throwaway key." >&2
  exit 1
fi
echo "Signing certificate: $OWNER"

# Print the versionCode. Play shows this number and not a commit, so it is the only identifier
# shared between what is built here and what appears on a track — say it out loud at build time
# rather than leaving the operator to infer it. Record it against the commit in
# tools/play/README.md after uploading.
# NOTE: a SINGLE -getProperty returns the bare value ("2"); it only emits JSON when several
# properties are requested. Parsing the JSON form here silently produced an empty string.
VC=$("$DOTNET" msbuild "$PROJ" -getProperty:ApplicationVersion -p:AndroidSdkDirectory="$SDK" 2>/dev/null | tr -d '[:space:]')
echo "versionCode:         ${VC:-unknown}"
echo "built from commit:   $(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
