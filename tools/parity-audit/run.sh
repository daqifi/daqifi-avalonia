#!/usr/bin/env bash
# Parity-audit capture pipeline. Renders the original WPF app and the Avalonia
# port to PNGs and builds side-by-side comparison montages.
#
# Runs from WSL but drives the WINDOWS dotnet (win-x64 Skia native + fonts are
# solid there, and WPF requires Windows). Requires:
#   - Windows .NET 10 SDK at "/mnt/c/Program Files/dotnet/dotnet.exe"
#   - the sibling repo GitHub/daqifi-desktop next to this one (for the WPF leg)
#   - python3 + Pillow in WSL (for the montages)
#
# Usage:   ./run.sh [out-dir]
# Default out-dir: ./out (gitignored). Outputs: <out>/wpf, <out>/avalonia, <out>/montage.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/out}"
mkdir -p "$OUT"
OUT_WIN="$(wslpath -w "$OUT")"
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"

echo "== Avalonia port capture =="
"$DOTNET" run --project "$(wslpath -w "$HERE/AvaloniaCapture/AvaloniaCapture.csproj")" -c Release -- "$OUT_WIN\\avalonia" \
  | grep -E '\[OK\]|\[FAIL\]|\[SKIP\]|done' || true

echo "== Original WPF capture =="
"$DOTNET" run --project "$(wslpath -w "$HERE/WpfCapture/WpfCapture.csproj")" -c Release -- "$OUT_WIN\\wpf" \
  | grep -E '\[OK\]|\[FAIL\]|\[SKIP\]|done' || true

echo "== Montages =="
python3 "$HERE/montage.py" "$OUT"

echo "Done. Montages: $OUT/montage"
