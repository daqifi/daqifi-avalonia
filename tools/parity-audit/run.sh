#!/usr/bin/env bash
# Parity-audit capture pipeline. Renders the original WPF app and the Avalonia
# port to PNGs and builds side-by-side comparison montages.
#
# TWO HOST SHAPES, because the two legs do not have the same requirements:
#
#   WSL — both legs, i.e. an actual parity comparison. Drives the WINDOWS dotnet,
#     because WPF requires Windows at all. Requires:
#       - Windows .NET 10 SDK at "/mnt/c/Program Files/dotnet/dotnet.exe"
#       - the sibling repo GitHub/daqifi-desktop next to this one (WPF leg)
#       - python3 + Pillow (montages)
#
#   macOS / Linux — the Avalonia leg only. AvaloniaCapture is RID-less (see its
#     csproj) so it builds and runs on the host runtime; this is what gives the
#     visual gate macOS coverage at all (#89). The WPF leg is SKIPPED with a
#     notice, and so are the montages, which exist to put the two side by side.
#     Captures still land in <out>/avalonia and are what the visual gate consumes.
#
# The host SDK is used as-is. If `dotnet --version` cannot satisfy global.json's
# pinned SDK, point DOTNET at one that can (e.g. DOTNET=~/.dotnet/dotnet ./run.sh).
#
# Usage:   ./run.sh [out-dir]
#          ./run.sh --determinism [out-dir]          # 5 runs by default
#          DETERMINISM_RUNS=10 ./run.sh --determinism [out-dir]
#          ./run.sh --check-baseline [out-dir]       # capture once, diff vs the manifest
#          ./run.sh --check-captured <capture-dir>   # diff an EXISTING capture, no capture
#
# --check-baseline captures once and verifies every screen against the committed
# baselines/<os>-<arch>.sha256, failing on a changed screen, a missing one, or one the
# baseline does not list. Run --determinism first on a host you have not captured from:
# a baseline check on a non-deterministic host reports differences that are not regressions.
#
# --check-captured runs that same verification against a directory that has ALREADY been
# captured, so the two checks can be chained over ONE capture set instead of two. CI runs
# --determinism and then points this at determinism/r1 (#188), which makes the verdict
# "these runs agreed with each other AND with the committed baseline" a statement about
# the same bytes, rather than about a further capture nothing has vouched for. It also
# costs nothing: the capture is the expensive part and it has already happened.
#
# --determinism captures the Avalonia side N times into <out>/determinism/r1..rN and
# requires every PNG to be byte-identical to the first run's. Run it before you trust
# any comparison number, and on any host nobody has captured from before: a harness
# that races its own animations produces differences indistinguishable from a real
# regression, and this project has already been burned by one (a 65.6% "regression"
# that was an unfinished fade-in). AvaloniaCapture's settle loop is the fix — and
# running this mode on macOS is what found two ways it could still lose that race.
#
# Default out-dir: ./out (gitignored). Outputs: <out>/wpf, <out>/avalonia, <out>/montage.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"

MODE="capture"
case "${1:-}" in
  --determinism)    MODE="determinism"; shift ;;
  --check-baseline) MODE="baseline";    shift ;;
  --check-captured) MODE="captured";    shift ;;
  --*) echo "unknown option '$1' (expected --determinism, --check-baseline" \
            "or --check-captured)" >&2; exit 1 ;;
esac

if [ "$MODE" = "captured" ]; then
  # This mode READS a directory that already exists and writes nothing at all, so its
  # argument is required and is taken as given. `mkdir -p` here would happily create a
  # mistyped path, and the run would then fail as eighteen missing screens rather than
  # as the typo it is — a diagnosis pointing at the UI for a mistake in an argument.
  OUT="${1:-}"
  if [ -z "$OUT" ]; then
    echo "!! --check-captured needs the capture directory to verify, e.g." >&2
    echo "     ./run.sh --check-captured out/determinism/r1" >&2
    exit 1
  fi
  if [ ! -d "$OUT" ]; then
    echo "!! --check-captured: '$OUT' is not a directory, so there is nothing to verify" >&2
    exit 1
  fi
  OUT="$(cd "$OUT" && pwd)"
else
  OUT="${1:-$HERE/out}"
  mkdir -p "$OUT"
  # Canonicalize to an absolute real path and refuse a root/empty target BEFORE the
  # cleanup rm -rf below — so a stray `./run.sh /` (or an empty arg) can never delete
  # outside the intended output dir.
  OUT="$(cd "$OUT" && pwd)"
  case "$OUT" in
    ""|"/") echo "Refusing to use unsafe output dir '$OUT'" >&2; exit 1 ;;
  esac
fi
# Start from clean output dirs so stale PNGs from a prior run can't be mistaken for this run's
# captures (a failed/partial capture would otherwise be masked by old images).
#
# Scoped to the dirs THIS mode writes, and deferred until the mode has finished its preflight.
# Neither is cosmetic. An unconditional wipe up here meant `--check-baseline` on a host with no
# committed manifest deleted the determinism evidence you had just been told to produce, then
# exited without replacing it — the cleanup destroyed the very thing the failure message asked
# you to go and get. Per-mode scoping also stops the three modes clobbering each other's output
# when they share an out-dir, which is the normal way to use them.
clean_dirs() { rm -rf "$@"; }

# Under WSL the harnesses run on the WINDOWS runtime through interop, so every path
# handed to them has to be a Windows path — a Linux-style argument would resolve
# against a Windows root and the files would land somewhere else entirely (#74).
# On a native host the path is already the right kind.
if grep -qi microsoft /proc/version 2>/dev/null; then
  HOST_KIND="wsl"
  DOTNET="${DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
  to_native() { wslpath -w "$1"; }
else
  HOST_KIND="native"
  DOTNET="${DOTNET:-dotnet}"
  to_native() { printf '%s' "$1"; }
fi

AVALONIA_CSPROJ="$HERE/AvaloniaCapture/AvaloniaCapture.csproj"

# Run a capture harness, showing only its status lines, but FAIL the pipeline if
# `dotnet run` itself failed (build error / crash). Piping straight into `grep`
# would hide the dotnet exit code — even a `grep ... || true` swallows it — so the
# script would march on to montage with missing/partial captures.
#
# [INFO] is in the filter because those lines are the harness's evidence, not chatter:
# the resolved output directory (the #74 trap, which the README says is printed up front
# and which this grep used to swallow), the visual-tree read-backs that say a dialog's
# binding actually resolved, and the notice that an indeterminate progress animation was
# frozen for a capture. All three are things a reader of a green run needs to see.
#
# No -r here, ever. An explicit-RID restore rewrites AvaloniaCapture's
# packages.lock.json to that single RID and the next CI restore fails NU1004
# (Directory.Build.props documents this at length).
run_capture() {
  local label="$1" csproj="$2" dir="$3" log="$OUT/.capture-$1.log"
  mkdir -p "$dir"
  echo "== $label =="
  if ! "$DOTNET" run --project "$(to_native "$csproj")" -c Release \
        -- "$(to_native "$dir")" >"$log" 2>&1; then
    grep -E '\[OK\]|\[FAIL\]|\[SKIP\]|\[INFO\]|done' "$log" || true
    echo "!! $label failed (see $log)" >&2
    return 1
  fi
  grep -E '\[OK\]|\[FAIL\]|\[SKIP\]|\[INFO\]|done' "$log" || true
}

# Byte-compare two capture sets. Byte identity, not a pixel diff: it needs no
# Pillow, and it is the stronger claim — two encodings that differ at all are two
# encodings the gate would have to explain.
determinism_check() {
  local ref="$1" other="$2" label="$3" rc=0 count=0 name path
  # An unmatched *.png must expand to nothing rather than to the literal pattern —
  # otherwise an empty capture dir would "compare" a filename that does not exist and
  # report it as a missing screen instead of as an empty run.
  shopt -s nullglob
  for path in "$ref"/*.png; do
    name="$(basename "$path")"
    count=$((count + 1))
    if [ ! -f "$other/$name" ]; then
      echo "!! $name: run 1 produced it, run $label did not" >&2
      rc=1
    elif ! cmp -s "$path" "$other/$name"; then
      echo "!! $name: run $label DIFFERS from run 1 — same binary, same commit." \
           "The capture is not deterministic on this host; any comparison against it" \
           "would report differences that are not regressions." >&2
      rc=1
    fi
  done
  for path in "$other"/*.png; do
    name="$(basename "$path")"
    if [ ! -f "$ref/$name" ]; then
      echo "!! $name: run $label produced it, run 1 did not" >&2
      rc=1
    fi
  done
  SCREEN_COUNT="$count"
  return "$rc"
}

# Which committed baseline belongs to this host. NOT uname alone: under WSL the capture
# executes on the WINDOWS runtime through interop (run.sh drives the Windows dotnet there),
# so `uname -s` says Linux while the pixels are Windows pixels. Getting that backwards would
# compare a Windows capture against a Linux baseline and report the whole set as regressed.
platform_id() {
  if [ "$HOST_KIND" = "wsl" ]; then printf 'windows-x64'; return 0; fi
  local os arch
  case "$(uname -s)" in
    Darwin) os="macos" ;;
    Linux)  os="linux" ;;
    *)      return 1 ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch="arm64" ;;
    x86_64|amd64)  arch="x64" ;;
    *)             return 1 ;;
  esac
  printf '%s-%s' "$os" "$arch"
}

# `shasum` is present on macOS and most Linux; `sha256sum` is the coreutils one. Both read
# the same "<hash>  <name>" format, so the committed manifest works with either.
sha256_verify() {   # $1 = manifest path; cwd must be the capture dir
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 -c "$1"
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c "$1"
  else
    echo "!! neither shasum nor sha256sum is on PATH; cannot verify the baseline" >&2
    return 1
  fi
}

# Which committed manifest this host is measured against, or a non-zero exit and the
# reason there is none. Sets BASELINE_ID and BASELINE_MANIFEST.
#
# Kept separate from the comparison itself so both baseline modes run it as a PREFLIGHT,
# before anything is captured or deleted: --check-baseline on a host with no committed
# manifest used to wipe the capture dir first and exit second, destroying the determinism
# evidence its own failure message then told you to go and produce (#179's trap).
resolve_baseline() {
  if ! BASELINE_ID="$(platform_id)"; then
    echo "!! no baseline naming for $(uname -s)/$(uname -m); baselines are per OS+arch" >&2
    return 1
  fi
  BASELINE_MANIFEST="$HERE/baselines/$BASELINE_ID.sha256"
  if [ ! -f "$BASELINE_MANIFEST" ]; then
    echo "!! no committed baseline for '$BASELINE_ID' ($BASELINE_MANIFEST)." >&2
    echo "   Record one only after './run.sh --determinism' passes on this host:" >&2
    echo "     cd <capture-dir> && shasum -a 256 *.png > $BASELINE_MANIFEST" >&2
    echo "   and state the host in tools/parity-audit/README.md, as the macOS entry does." >&2
    return 1
  fi
}

# Verify one capture directory against that manifest, in both directions. $1 = the
# directory holding the PNGs.
baseline_check() {
  local dir="$1" rc=0 manifest_names name path
  local -a pngs

  # A comparison of nothing must never read as success. `shasum -c` on an empty
  # directory does fail — every listed name is missing — but it fails as eighteen
  # unrelated-looking lines rather than as the one fact that matters, and a caller
  # pointing this at the wrong directory deserves to be told that instead.
  shopt -s nullglob
  pngs=("$dir"/*.png)
  if [ "${#pngs[@]}" -eq 0 ]; then
    echo "!! $dir holds no PNGs at all, so there is nothing to verify against" \
         "$BASELINE_MANIFEST" >&2
    return 1
  fi

  echo "== baseline ($BASELINE_ID) =="
  ( cd "$dir" && sha256_verify "$BASELINE_MANIFEST" ) || rc=1

  # `shasum -c` only checks the names the manifest lists, so a screen the harness gained
  # since the baseline was recorded would pass unnoticed. Both directions, always.
  #
  # Matched as whole strings, not with grep: a screen name contains dots, and as a regex
  # "desktop-1-livegraph.png" also matches "desktop-1-livegraphXpng" — a near-miss name
  # would be silently accepted by the check that exists to catch exactly that. Field 2 is
  # the filename in both shasum and sha256sum output, and screen names carry no spaces.
  manifest_names="$(awk '{ print $2 }' "$BASELINE_MANIFEST")"
  for path in "${pngs[@]}"; do
    name="$(basename "$path")"
    case $'\n'"$manifest_names"$'\n' in
      *$'\n'"$name"$'\n'*) ;;
      *)
        echo "!! $name: captured but absent from the baseline — the screen set has grown" >&2
        rc=1
        ;;
    esac
  done

  if [ "$rc" -ne 0 ]; then
    echo "!! baseline check FAILED against $BASELINE_MANIFEST." >&2
    echo "   If the UI changed on purpose, re-record it (see README 'Baselines') and say so" >&2
    echo "   in the same commit. If it did not, this is a visual regression — or, on a host" >&2
    echo "   that has not produced these bytes before, a difference between the hosts." >&2
    return 1
  fi
  echo "baseline OK: every screen in $dir matches $BASELINE_MANIFEST"
}

if [ "$MODE" = "captured" ]; then
  # Verify captures somebody else already produced. The caller owns the question of
  # whether they are trustworthy — in CI that is the --determinism run immediately
  # before this one, which is the whole point of splitting the mode out (#188).
  resolve_baseline
  baseline_check "$OUT"
  exit 0
fi

if [ "$MODE" = "baseline" ]; then
  # Compare a fresh capture against the committed manifest for this host. The manifest is a
  # record of what the UI looked like at a known commit; this is the step that turns it from
  # documentation into something with an exit code.
  #
  # Run --determinism FIRST on any host you have not captured from. A baseline check on a
  # host whose captures are not deterministic reports differences that are not regressions,
  # which is the failure this whole tool exists to avoid.
  resolve_baseline

  clean_dirs "$OUT/avalonia"
  run_capture "avalonia" "$AVALONIA_CSPROJ" "$OUT/avalonia"

  baseline_check "$OUT/avalonia"
  exit 0
fi

if [ "$MODE" = "determinism" ]; then
  # How many times to capture. More than two, by default, and the reason is arithmetic
  # rather than caution: the two real defects this mode found on macOS were each a ~50/50
  # per-run coin flip (a one-pixel flyout edge, and a pane caught mid-fade), and two runs
  # miss a 50/50 flip half the time. Five brings that to ~6%, ten to ~0.2%, at roughly
  # 15 s a run. portomatic's own --determinism captures twice; this is the same check with
  # the sample size the failures here actually call for.
  runs="${DETERMINISM_RUNS:-5}"
  case "$runs" in
    ''|*[!0-9]*) echo "DETERMINISM_RUNS must be a whole number (got '$runs')" >&2; exit 1 ;;
  esac
  if [ "$runs" -lt 2 ]; then
    echo "DETERMINISM_RUNS must be at least 2 — one run compares against nothing" >&2
    exit 1
  fi

  clean_dirs "$OUT/determinism"
  echo "== determinism ($runs runs of the Avalonia leg, byte-compared) =="
  i=1
  while [ "$i" -le "$runs" ]; do
    run_capture "determinism-$i" "$AVALONIA_CSPROJ" "$OUT/determinism/r$i"
    i=$((i + 1))
  done

  # A comparison of nothing must never read as success — the same failure pixdiff.py
  # guards against. Checked ONCE, here, rather than inside the pairwise comparison: an
  # empty reference set is one fact about the run, not N-1 separate findings.
  shopt -s nullglob
  ref_pngs=("$OUT/determinism/r1"/*.png)
  if [ "${#ref_pngs[@]}" -eq 0 ]; then
    echo "!! determinism: run 1 produced no PNGs at all, so there is nothing to compare" >&2
    exit 1
  fi

  SCREEN_COUNT=0
  rc=0
  i=2
  while [ "$i" -le "$runs" ]; do
    determinism_check "$OUT/determinism/r1" "$OUT/determinism/r$i" "$i" || rc=1
    i=$((i + 1))
  done
  if [ "$rc" -ne 0 ]; then
    echo "!! determinism FAILED. Do not record a baseline or trust a comparison from this" \
         "host until it passes." >&2
    exit 1
  fi
  echo "determinism OK: $SCREEN_COUNT/$SCREEN_COUNT screens byte-identical across $runs runs"
  echo "Done. Capture sets: $OUT/determinism/r1..r$runs"
  exit 0
fi

clean_dirs "$OUT/avalonia" "$OUT/wpf" "$OUT/montage"
run_capture "avalonia" "$AVALONIA_CSPROJ" "$OUT/avalonia"

if [ "$HOST_KIND" = "wsl" ]; then
  run_capture "wpf" "$HERE/WpfCapture/WpfCapture.csproj" "$OUT/wpf"

  echo "== Montages =="
  python3 "$HERE/montage.py" "$OUT"
  echo "Done. Montages: $OUT/montage"
else
  # Say what did NOT happen, in the same breath as what did. The whole value of this
  # tool is a WPF-vs-Avalonia comparison, and on this host only one side exists — a
  # run that printed "Done" and nothing else would read as a completed comparison.
  echo "== wpf =="
  echo "   SKIPPED: this is a $(uname -s) host, not WSL. The WPF leg boots the original"
  echo "   daqifi-desktop app, which requires Windows, and this script only knows how to"
  echo "   drive it through WSL. Run it from WSL to get the WPF side."
  echo "== Montages =="
  echo "   SKIPPED: montages pair WPF against Avalonia and there is no WPF side here."
  echo "   For a montage of the mobile orientations alone: python3 $HERE/montage.py $OUT"
  echo "Done. Avalonia captures: $OUT/avalonia"
  echo "NOTE: this is the Avalonia side ONLY. It is a macOS/Linux baseline and a"
  echo "      candidate set for regression comparison, NOT a parity comparison."
fi
