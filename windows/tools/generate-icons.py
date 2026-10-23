"""Generate TinyClips icons from the shared 1024px Lilac Cutout master.

Requires Pillow. Run from any directory:
    python windows/tools/generate-icons.py
    python windows/tools/generate-icons.py --macos

The second command also updates the shared TinyClips/TinyClipsMAS asset catalog.
Only app assets are written; menu-bar symbols and marketing artwork are untouched.
Small icons use a flat, higher-contrast version of the same frame/play/cut motif.
"""

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
MASTER = ROOT / "assets" / "TinyClips-AppIcon.png"
ASSETS = ROOT / "windows" / "src" / "TinyClips.App" / "Assets"
MAC_ASSETS = ROOT / "mac" / "TinyClips" / "Assets.xcassets" / "AppIcon.appiconset"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
LILAC = (203, 182, 255, 255)
SMALL_GOLD = (193, 109, 0, 255)


def load_master(path: Path = MASTER) -> Image.Image:
    with Image.open(path) as source:
        if source.mode != "RGBA" or source.size != (1024, 1024):
            raise ValueError(f"{path}: expected a 1024x1024 RGBA master")
        image = source.copy()
    bounds = image.getbbox()
    if bounds is None or image.getchannel("A").getextrema() != (0, 255):
        raise ValueError(f"{path}: expected visible artwork and transparent exterior")
    return image.crop(bounds)


def small_tile(size: int) -> Image.Image:
    scale = 8
    image = Image.new("RGBA", (size * scale, size * scale))
    draw = ImageDraw.Draw(image)
    s = size * scale

    def box(values: tuple[float, float, float, float]) -> tuple[float, ...]:
        return tuple(v * s for v in values)

    draw.rounded_rectangle((0, 0, s - 1, s - 1), radius=s * 0.23, fill=LILAC)
    draw.rounded_rectangle(box((0.145, 0.215, 0.855, 0.775)),
                           radius=s * 0.125, fill=SMALL_GOLD)
    draw.rounded_rectangle(box((0.245, 0.315, 0.755, 0.675)),
                           radius=s * 0.065, fill=LILAC)
    draw.polygon([(s * 0.40, s * 0.38), (s * 0.40, s * 0.62),
                  (s * 0.63, s * 0.50)], fill=SMALL_GOLD)
    # A clean diagonal gap keeps the clipped corner recognizable without shadows.
    draw.line([(s * 0.66, s * 0.195), (s * 0.88, s * 0.415)],
              fill=LILAC, width=max(scale, round(s * 0.045)))
    image = image.resize((size, size), Image.Resampling.LANCZOS)
    # Remove faint Lanczos ringing outside the rounded tile.
    image.putalpha(image.getchannel("A").point(lambda alpha: 0 if alpha < 8 else alpha))
    return image


def square_icon(master: Image.Image, size: int, coverage: float) -> Image.Image:
    artwork_size = max(1, round(size * coverage))
    artwork = (small_tile(artwork_size) if size <= 48 else
               master.resize((artwork_size, artwork_size), Image.Resampling.LANCZOS))
    canvas = Image.new("RGBA", (size, size))
    offset = (size - artwork_size) // 2
    canvas.alpha_composite(artwork, (offset, offset))
    return canvas


def packaged_icon(master: Image.Image, name: str, size: tuple[int, int]) -> Image.Image:
    width, height = size
    if name.startswith("SplashScreen"):
        coverage = 0.50
    elif name.startswith(("LargeTile", "Wide310x150Logo")):
        coverage = 0.65
    elif name.startswith(("SmallTile", "Square150x150Logo")):
        coverage = 0.72
    elif "targetsize-" in name:
        coverage = 0.94
    elif name.startswith(("Square44x44Logo", "StoreLogo", "LockScreenLogo")):
        coverage = 0.84
    elif name == "AppIcon-512.png":
        coverage = 0.90
    else:
        raise ValueError(f"Unrecognized packaged icon: {name}")
    side = min(width, height)
    icon = square_icon(master, side, coverage)
    canvas = Image.new("RGBA", size)
    canvas.alpha_composite(icon, ((width - side) // 2, (height - side) // 2))
    return canvas


def write_ico(master: Image.Image, path: Path) -> None:
    coverage = 0.96 if path.name == "TrayIcon.ico" else 0.90
    frames = [square_icon(master, size, coverage) for size in ICO_SIZES]
    # Supply every frame explicitly; Pillow otherwise downsamples the largest one.
    frames[-1].save(path, format="ICO", sizes=[(s, s) for s in ICO_SIZES],
                    append_images=frames[:-1])


def generate_windows(master: Image.Image, assets: Path = ASSETS) -> None:
    files = sorted(p for p in assets.iterdir() if p.suffix.lower() in (".png", ".ico"))
    if not files:
        raise ValueError(f"No icon assets found in {assets}")
    for path in files:
        if path.suffix.lower() == ".ico":
            if path.name not in ("AppIcon.ico", "TrayIcon.ico"):
                raise ValueError(f"Unrecognized ICO: {path.name}")
            write_ico(master, path)
        else:
            with Image.open(path) as previous:
                size = previous.size
            packaged_icon(master, path.name, size).save(path)
        print(f"wrote {path.name}")


def generate_macos(master: Image.Image, assets: Path = MAC_ASSETS) -> None:
    contents = json.loads((assets / "Contents.json").read_text())
    for entry in contents["images"]:
        size = round(float(entry["size"].split("x")[0]) * float(entry["scale"][:-1]))
        path = assets / entry["filename"]
        square_icon(master, size, 0.82).save(path)
        print(f"wrote {path.name} ({size}x{size})")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--macos", action="store_true", help="Also regenerate macOS app icons")
    args = parser.parse_args()
    master = load_master()
    generate_windows(master)
    if args.macos:
        generate_macos(master)


if __name__ == "__main__":
    main()
