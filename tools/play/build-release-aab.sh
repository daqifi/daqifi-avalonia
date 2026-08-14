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

echo
echo "Artifact: $AAB"
echo "Signing certificate:"
if unzip -p "$AAB" 'META-INF/*.RSA' 2>/dev/null | keytool -printcert 2>/dev/null | grep -E "^Owner:"; then
  :
else
  echo "  (could not read certificate — verify manually before uploading)" >&2
fi
echo
echo "If the Owner above says 'CN=Android Debug', DO NOT UPLOAD IT."
