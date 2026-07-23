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
# Canonicalize to an absolute real path and refuse a root/empty target BEFORE the
# cleanup rm -rf below — so a stray `./run.sh /` (or an empty arg) can never delete
# outside the intended output dir.
OUT="$(cd "$OUT" && pwd)"
case "$OUT" in
  ""|"/") echo "Refusing to use unsafe output dir '$OUT'" >&2; exit 1 ;;
esac
# Start from clean output dirs so stale PNGs from a prior run can't be mistaken for
# this run's captures (a failed/partial capture would otherwise be masked by old images).
rm -rf "$OUT/avalonia" "$OUT/wpf" "$OUT/montage"
OUT_WIN="$(wslpath -w "$OUT")"
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"

# Run a capture harness, showing only its status lines, but FAIL the pipeline if
# `dotnet run` itself failed (build error / crash). Piping straight into `grep`
# would hide the dotnet exit code — even a `grep ... || true` swallows it — so the
# script would march on to montage with missing/partial captures.
run_capture() {
  local label="$1" csproj="$2" arg="$3" log="$OUT/.capture-$1.log"
  echo "== $label =="
  if ! "$DOTNET" run --project "$(wslpath -w "$csproj")" -c Release -- "$arg" >"$log" 2>&1; then
    grep -E '\[OK\]|\[FAIL\]|\[SKIP\]|done' "$log" || true
    echo "!! $label failed (see $log)" >&2
    return 1
  fi
  grep -E '\[OK\]|\[FAIL\]|\[SKIP\]|done' "$log" || true
}

run_capture "avalonia" "$HERE/AvaloniaCapture/AvaloniaCapture.csproj" "$OUT_WIN\\avalonia"
run_capture "wpf"      "$HERE/WpfCapture/WpfCapture.csproj"           "$OUT_WIN\\wpf"

echo "== Montages =="
python3 "$HERE/montage.py" "$OUT"

echo "Done. Montages: $OUT/montage"
