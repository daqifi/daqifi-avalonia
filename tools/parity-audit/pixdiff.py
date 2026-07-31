#!/usr/bin/env python3
"""Pixel-diff two capture sets and report per-screen divergence.

Usage: pixdiff.py <baseline-dir> <candidate-dir> [diff-out-dir]

Reports, per screen: dimensions, % of pixels differing at all, mean absolute
channel delta, and the worst single-pixel delta. Writes an amplified diff image
per changed screen when an output dir is given.
"""
import functools
import os
import sys

from PIL import Image, ImageChops

if len(sys.argv) < 3:
    sys.exit(f"usage: {os.path.basename(sys.argv[0])} "
             "<baseline-dir> <candidate-dir> [diff-out-dir]")

base_dir, cand_dir = sys.argv[1], sys.argv[2]
diff_dir = sys.argv[3] if len(sys.argv) > 3 else None

# Check both inputs before doing anything: a typo'd path would otherwise surface as
# a bare OSError traceback, or — worse — an empty baseline listing would report
# "0 screens compared" as if that were a clean result.
for label, path in (("baseline", base_dir), ("candidate", cand_dir)):
    if not os.path.isdir(path):
        sys.exit(f"error: {label} directory does not exist: {path}")

if diff_dir:
    os.makedirs(diff_dir, exist_ok=True)

names = sorted(n for n in os.listdir(base_dir) if n.endswith(".png"))
missing = [n for n in names if not os.path.exists(os.path.join(cand_dir, n))]
extra = sorted(
    n for n in os.listdir(cand_dir)
    if n.endswith(".png") and n not in names)

rows = []
for name in names:
    if name in missing:
        continue
    # RGBA, not RGB: Pillow's RGBA->RGB drops alpha without compositing, so an
    # alpha-only change would compare as identical. AvaloniaCapture's PNGs carry no
    # alpha today, making this a no-op there (alpha reads 255 everywhere and folds in
    # as a constant-zero channel) — but "the current producer happens not to emit it"
    # is not a property a regression gate should quietly depend on.
    a = Image.open(os.path.join(base_dir, name)).convert("RGBA")
    b = Image.open(os.path.join(cand_dir, name)).convert("RGBA")
    if a.size != b.size:
        rows.append((name, f"{a.size[0]}x{a.size[1]}", "SIZE MISMATCH",
                     f"{b.size[0]}x{b.size[1]}", "", ""))
        continue

    diff = ImageChops.difference(a, b)
    total = a.size[0] * a.size[1]
    # Per-pixel MAX channel delta, folding all channels (incl. alpha) with lighter().
    #
    # NOT diff.convert("L"): that is a luminance-weighted mix
    # (0.299R + 0.587G + 0.114B), so it systematically UNDER-REPORTS. A pure-blue
    # change of 255 would score 0.114*255 = 29, and a pixel differing by 1 in blue
    # alone rounds to 0 and vanishes from the changed-pixel count entirely. For a
    # parity gate that must answer "did anything move", channel-blind is the wrong
    # question: a colour shift matters regardless of how much it moves perceived
    # brightness.
    flat = functools.reduce(ImageChops.lighter, diff.split())
    # bbox from the FOLDED single channel, never from the RGBA diff: getbbox() treats
    # alpha=0 as transparent, and an alpha-identical pair has an all-zero alpha channel
    # in its diff — so diff.getbbox() reports None (i.e. "identical") for images that
    # differ substantially in colour. Folding first asks the question we actually mean:
    # is any channel non-zero anywhere.
    bbox = flat.getbbox()
    hist = flat.histogram()
    changed = total - hist[0]
    worst = max((i for i, c in enumerate(hist) if c), default=0)
    mean = sum(i * c for i, c in enumerate(hist)) / total
    rows.append((name, f"{a.size[0]}x{a.size[1]}",
                 f"{100.0 * changed / total:.3f}%", f"{mean:.3f}", str(worst),
                 "identical" if bbox is None else ""))

    if diff_dir and bbox is not None:
        # Amplify so sub-perceptual deltas are visible in the artifact.
        amplified = flat.point(lambda v: min(255, v * 12))
        amplified.save(os.path.join(diff_dir, name))

width = max(len(r[0]) for r in rows) if rows else 20
print(f"{'screen'.ljust(width)}  {'size':>9}  {'px diff':>8}  "
      f"{'mean':>7}  {'worst':>5}  note")
for r in rows:
    print(f"{r[0].ljust(width)}  {r[1]:>9}  {r[2]:>8}  {r[3]:>7}  {r[4]:>5}  {r[5]}")

if missing:
    print(f"\nMISSING from candidate ({len(missing)}): {', '.join(missing)}")
if extra:
    print(f"\nEXTRA in candidate ({len(extra)}): {', '.join(extra)}")
print(f"\n{len(rows)} screens compared, {len(missing)} missing, {len(extra)} extra")
