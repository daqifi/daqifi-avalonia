#!/usr/bin/env python3
"""Build side-by-side parity montages from captured PNGs.

Usage:  python3 montage.py <out-root>

<out-root> must contain wpf/ and avalonia/ subdirs (from the two capture
harnesses). Writes montage/*.png. Requires Pillow (pip install pillow).
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFont

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "out")
WPF = os.path.join(ROOT, "wpf")
AVA = os.path.join(ROOT, "avalonia")
OUT = os.path.join(ROOT, "montage")
os.makedirs(OUT, exist_ok=True)

BG = (24, 26, 30)
LABELBG = (12, 13, 15)
FG = (235, 238, 242)
GAP = 24
LABEL_H = 46


def font(size):
    for p in ["/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
              "/mnt/c/Windows/Fonts/segoeui.ttf",
              "/mnt/c/Windows/Fonts/arialbd.ttf",
              "C:/Windows/Fonts/segoeui.ttf"]:
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()


F = font(24)


def labeled(img, text, target_h):
    scale = target_h / img.height
    w = int(img.width * scale)
    img = img.resize((w, target_h), Image.LANCZOS)
    canvas = Image.new("RGB", (w, target_h + LABEL_H), LABELBG)
    d = ImageDraw.Draw(canvas)
    d.text((12, 11), text, fill=FG, font=F)
    canvas.paste(img, (0, LABEL_H))
    return canvas


def montage(pairs, out_name, target_h):
    """pairs: list of (path, label)"""
    tiles = []
    for path, label in pairs:
        if not os.path.exists(path):
            continue
        tiles.append(labeled(Image.open(path).convert("RGB"), label, target_h))
    if not tiles:
        return
    total_w = sum(t.width for t in tiles) + GAP * (len(tiles) + 1)
    h = max(t.height for t in tiles) + GAP * 2
    canvas = Image.new("RGB", (total_w, h), BG)
    x = GAP
    for t in tiles:
        canvas.paste(t, (x, GAP))
        x += t.width + GAP
    canvas.save(os.path.join(OUT, out_name))
    print("wrote", out_name, canvas.size)


DESKTOP = ["1-livegraph", "2-loggeddata", "3-channels", "4-devices", "5-profiles",
           "6-settings-drawer", "7-notifications-flyout", "8-livegraph-settings-flyout",
           "9-summary-flyout"]
for name in DESKTOP:
    montage(
        [(os.path.join(WPF, f"wpf-{name}.png"), "ORIGINAL  ·  WPF (daqifi-desktop)"),
         (os.path.join(AVA, f"desktop-{name}.png"), "PORT  ·  Avalonia Desktop")],
        f"desktop-{name}.png", 760)

MOBILE = ["1-stream", "2-channels", "3-storage", "4-profiles"]
for name in MOBILE:
    montage(
        [(os.path.join(AVA, f"mobile-landscape-{name}.png"), "Avalonia Android  ·  LANDSCAPE"),
         (os.path.join(AVA, f"mobile-portrait-{name}.png"), "Avalonia Android  ·  PORTRAIT")],
        f"mobile-{name}.png", 620)

print("done ->", OUT)
