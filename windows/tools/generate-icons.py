"""Regenerates every Windows app/tray icon asset using the macOS app's glyph
(four corner focus-brackets + a solid center dot) on a blue gradient background.

Run from the repo root:  python windows/tools/generate-icons.py
Requires Pillow and NumPy.
"""
import os
import glob
import numpy as np
from PIL import Image, ImageDraw

ASSETS = os.path.join("windows", "src", "TinyClips.App", "Assets")

# Blue gradient (kept blue per request) — matches the existing Windows palette.
BLUE_TOP = (62, 156, 254)   # #3E9CFE
BLUE_BOT = (22, 110, 241)   # #166EF1

# Glyph proportions, derived by measuring the macOS 512px icon.
SPAN_FRAC = 0.56     # glyph bounding box as a fraction of the canvas
STROKE_FRAC = 0.073  # bracket stroke width as a fraction of the glyph span
ARM_FRAC = 0.40      # bracket arm length as a fraction of the glyph span
DOT_R_FRAC = 0.145   # center-dot radius as a fraction of the glyph span
CORNER_FRAC = 0.225  # background rounded-square corner radius (fraction of size)

WHITE = (255, 255, 255, 255)


def _gradient(s: int) -> Image.Image:
    ax = np.linspace(0.0, 1.0, s)
    x, y = np.meshgrid(ax, ax)
    t = (x + y) / 2.0  # diagonal top-left -> bottom-right
    r = BLUE_TOP[0] + (BLUE_BOT[0] - BLUE_TOP[0]) * t
    g = BLUE_TOP[1] + (BLUE_BOT[1] - BLUE_TOP[1]) * t
    b = BLUE_TOP[2] + (BLUE_BOT[2] - BLUE_TOP[2]) * t
    a = np.full((s, s), 255.0)
    arr = np.clip(np.dstack([r, g, b, a]), 0, 255).astype(np.uint8)
    return Image.fromarray(arr, "RGBA")


def _box(x0, y0, x1, y1):
    return [min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)]


def square_icon(size: int) -> Image.Image:
    ss = 4 if size <= 256 else 2
    s = size * ss
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Rounded-square blue background (full bleed).
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=s * CORNER_FRAC, fill=255)
    img.paste(_gradient(s), (0, 0), mask)

    d = ImageDraw.Draw(img)
    span = s * SPAN_FRAC
    stroke = span * STROKE_FRAC
    arm = span * ARM_FRAC
    hs = stroke / 2.0
    cc = s / 2.0
    gx0 = cc - span / 2.0
    gy0 = cc - span / 2.0
    gx1 = cc + span / 2.0
    gy1 = cc + span / 2.0

    # Four L-shaped corner brackets with rounded caps and corners.
    for cx, cy, sx, sy in ((gx0, gy0, 1, 1), (gx1, gy0, -1, 1), (gx0, gy1, 1, -1), (gx1, gy1, -1, -1)):
        d.rounded_rectangle(_box(cx, cy - hs, cx + sx * arm, cy + hs), radius=hs, fill=WHITE)
        d.rounded_rectangle(_box(cx - hs, cy, cx + hs, cy + sy * arm), radius=hs, fill=WHITE)
        d.ellipse([cx - hs, cy - hs, cx + hs, cy + hs], fill=WHITE)

    # Solid center dot.
    rdot = span * DOT_R_FRAC
    d.ellipse([cc - rdot, cc - rdot, cc + rdot, cc + rdot], fill=WHITE)

    return img.resize((size, size), Image.LANCZOS)


def wide_icon(w: int, h: int) -> Image.Image:
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    icon = square_icon(h)
    canvas.alpha_composite(icon, ((w - h) // 2, 0))
    return canvas


_cache: dict = {}


def cached_square(size: int) -> Image.Image:
    if size not in _cache:
        _cache[size] = square_icon(size)
    return _cache[size]


def main() -> None:
    files = [
        p for p in glob.glob(os.path.join(ASSETS, "*"))
        if os.path.isfile(p)
        and os.path.splitext(p)[1].lower() in (".png", ".ico")
        and "backup" not in os.path.basename(p).lower()
    ]
    for path in sorted(files):
        name = os.path.basename(path)
        lower = name.lower()
        with Image.open(path) as im:
            w, h = im.size

        if lower.endswith(".ico"):
            master = cached_square(256)
            sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
            master.save(path, format="ICO", sizes=sizes)
        elif w == h:
            cached_square(w).save(path)
        else:
            wide_icon(w, h).save(path)
        print(f"wrote {name} ({w}x{h})")


if __name__ == "__main__":
    main()
