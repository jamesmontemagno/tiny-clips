"""Regenerates every TinyClips app icon (macOS + Windows) from the shared master
artwork at shared/branding/app-icon-1024.png (full-bleed rounded square with
transparent corners).

Run from the repo root:  python3 scripts/generate-app-icons.py
"""
import glob
import json
import os

from PIL import Image

MASTER = os.path.join("shared", "branding", "app-icon-1024.png")
WIN_ASSETS = os.path.join("windows", "src", "TinyClips.App", "Assets")
MAC_ICONSET = os.path.join("mac", "TinyClips", "Assets.xcassets", "AppIcon.appiconset")
DOCS_ICON = os.path.join("docs", "icon.png")

# macOS icon grid: the rounded square occupies 824/1024 of the canvas.
MAC_CONTENT_FRAC = 824 / 1024
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

_master = Image.open(MASTER).convert("RGBA")
_cache: dict = {}


def square(size: int) -> Image.Image:
    if size not in _cache:
        _cache[size] = _master.resize((size, size), Image.LANCZOS)
    return _cache[size]


def padded(size: int, frac: float) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    inner = max(1, round(size * frac))
    off = (size - inner) // 2
    canvas.alpha_composite(square(inner), (off, off))
    return canvas


def centered(w: int, h: int) -> Image.Image:
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    s = min(w, h)
    canvas.alpha_composite(square(s), ((w - s) // 2, (h - s) // 2))
    return canvas


def write_windows() -> None:
    for path in sorted(glob.glob(os.path.join(WIN_ASSETS, "*"))):
        name = os.path.basename(path)
        lower = name.lower()
        if lower.endswith(".ico"):
            frames = [square(s) for s in ICO_SIZES]
            frames[-1].save(path, format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=frames[:-1])
        elif lower.endswith(".png"):
            with Image.open(path) as im:
                w, h = im.size
            (square(w) if w == h else centered(w, h)).save(path)
        else:
            continue
        print(f"wrote {path}")


def write_mac() -> None:
    with open(os.path.join(MAC_ICONSET, "Contents.json")) as f:
        images = json.load(f)["images"]
    for entry in images:
        base = int(entry["size"].split("x")[0])
        px = base * int(entry["scale"].rstrip("x"))
        path = os.path.join(MAC_ICONSET, entry["filename"])
        padded(px, MAC_CONTENT_FRAC).save(path)
        print(f"wrote {path}")


def main() -> None:
    write_mac()
    write_windows()
    padded(1024, MAC_CONTENT_FRAC).save(DOCS_ICON)
    print(f"wrote {DOCS_ICON}")


if __name__ == "__main__":
    main()
