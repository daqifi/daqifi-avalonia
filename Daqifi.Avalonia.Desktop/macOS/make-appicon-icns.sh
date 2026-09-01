#!/bin/bash
#
# Regenerates AppIcon.icns, the macOS app-bundle icon, from the shared 1024px app artwork.
#
# The .icns is COMMITTED rather than built. `iconutil` and `sips` are macOS-only, and an
# osx-arm64 publish is a perfectly ordinary thing to do from a Linux box; making the bundle
# depend on Apple tooling would turn a cross-publish into a silently icon-less app. A
# committed binary that any host can copy keeps MacAppBundle.targets host-independent.
#
# The source is the iOS head's app icon, not a copy of it, because that PNG *is* the shared
# artwork - the same lockup the Android, Windows and iOS heads use, produced by
# daqifi-design-tokens/assets/app-icons. Do not hand-edit either file: change the source art,
# re-run that generator, then re-run this.
#
# Known cosmetic gap: the source is a full-bleed square drawn for iOS, which masks icons to a
# rounded rect. macOS does not mask, so the Dock shows a hard-edged square where every other
# app shows a squircle. Fixing that means a macOS-specific variant from the design-token
# generator; it is artwork, not packaging, and is deliberately not invented here.
#
# Usage:  Daqifi.Avalonia.Desktop/macOS/make-appicon-icns.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="$here/../../Daqifi.Avalonia.iOS/Assets.xcassets/AppIcon.appiconset/AppIcon-1024.png"
out="$here/AppIcon.icns"

if [ ! -f "$src" ]; then
  echo "source artwork not found: $src" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
iconset="$work/AppIcon.iconset"
mkdir -p "$iconset"

# The ten entries `iconutil` expects. Names are load-bearing: iconutil derives each icon's
# type from the filename, and an unrecognised name is skipped without complaint.
for spec in \
  16:icon_16x16.png \
  32:icon_16x16@2x.png \
  32:icon_32x32.png \
  64:icon_32x32@2x.png \
  128:icon_128x128.png \
  256:icon_128x128@2x.png \
  256:icon_256x256.png \
  512:icon_256x256@2x.png \
  512:icon_512x512.png \
  1024:icon_512x512@2x.png
do
  px="${spec%%:*}"
  name="${spec#*:}"
  sips -s format png -z "$px" "$px" "$src" --out "$iconset/$name" >/dev/null
done

iconutil --convert icns --output "$out" "$iconset"
echo "wrote $out"
