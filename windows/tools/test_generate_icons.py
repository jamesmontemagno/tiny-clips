"""Run with: python -m unittest discover -s windows/tools -p 'test_*.py'."""

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageChops

SPEC = importlib.util.spec_from_file_location(
    "generate_icons", Path(__file__).with_name("generate-icons.py"))
assert SPEC is not None and SPEC.loader is not None
icons = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(icons)


class IconTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.master = icons.load_master()

    def assert_transparent_exterior(self, image: Image.Image) -> None:
        self.assertEqual(image.mode, "RGBA")
        self.assertEqual(image.getchannel("A").getextrema(), (0, 255))
        for point in ((0, 0), (image.width - 1, 0),
                      (0, image.height - 1), (image.width - 1, image.height - 1)):
            self.assertEqual(image.getpixel(point)[3], 0)

    def test_all_packaged_pngs_match_generator(self) -> None:
        paths = list(icons.ASSETS.glob("*.png"))
        self.assertEqual(len(paths), 53)
        for path in paths:
            with self.subTest(asset=path.name), Image.open(path) as image:
                self.assert_transparent_exterior(image)
                expected = icons.packaged_icon(self.master, path.name, image.size)
                self.assertEqual(image.tobytes(), expected.tobytes())

    def test_ico_contains_optically_rendered_frames(self) -> None:
        for name, coverage in (("AppIcon.ico", 0.90), ("TrayIcon.ico", 0.96)):
            with Image.open(icons.ASSETS / name) as image:
                self.assertEqual(image.ico.sizes(), {(s, s) for s in icons.ICO_SIZES})
                for size in icons.ICO_SIZES:
                    with self.subTest(asset=name, size=size):
                        frame = image.ico.getimage((size, size))
                        self.assert_transparent_exterior(frame)
                        expected = icons.square_icon(self.master, size, coverage)
                        self.assertEqual(frame.tobytes(), expected.tobytes())

    def test_shared_mac_catalog_dimensions_and_pixels(self) -> None:
        catalog = json.loads((icons.MAC_ASSETS / "Contents.json").read_text())
        self.assertEqual(len(catalog["images"]), 10)
        for entry in catalog["images"]:
            size = round(float(entry["size"].split("x")[0]) * float(entry["scale"][:-1]))
            with self.subTest(asset=entry["filename"]):
                with Image.open(icons.MAC_ASSETS / entry["filename"]) as image:
                    self.assertEqual(image.size, (size, size))
                    self.assert_transparent_exterior(image)
                    expected = icons.square_icon(self.master, size, 0.82)
                    self.assertEqual(image.tobytes(), expected.tobytes())

    def test_tiny_frames_are_not_downsampled_master(self) -> None:
        for size in (16, 20, 24, 32, 40, 48):
            with self.subTest(size=size):
                image = icons.square_icon(self.master, size, 0.96)
                pixels = (image.getpixel((x, y)) for y in range(size) for x in range(size))
                gold_pixels = sum(
                    1 for r, g, b, a in pixels
                    if r > 140 and 60 < g < 160 and b < 60 and a > 240)
                self.assertGreaterEqual(gold_pixels, size)
                scaled = self.master.resize((size, size), Image.Resampling.LANCZOS)
                self.assertIsNotNone(ImageChops.difference(image, scaled).getbbox())

    def test_wide_and_large_surfaces_have_centered_padding(self) -> None:
        for name, size in (("SplashScreen.scale-100.png", (620, 300)),
                           ("Wide310x150Logo.scale-100.png", (310, 150)),
                           ("LargeTile.scale-100.png", (310, 310))):
            with self.subTest(asset=name):
                image = icons.packaged_icon(self.master, name, size)
                bounds = image.getbbox()
                self.assertIsNotNone(bounds)
                left, top, right, bottom = bounds
                self.assertLessEqual(abs(left - (image.width - right)), 2)
                self.assertLessEqual(abs(top - (image.height - bottom)), 2)
                self.assertGreater(left, image.width * 0.15)
                self.assertGreater(top, image.height * 0.15)

    def test_repeated_ico_exports_are_identical(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "TrayIcon.ico"
            icons.write_ico(self.master, path)
            first = hashlib.sha256(path.read_bytes()).digest()
            icons.write_ico(self.master, path)
            self.assertEqual(hashlib.sha256(path.read_bytes()).digest(), first)

    def test_unknown_assets_and_opaque_master_fail_explicitly(self) -> None:
        with self.assertRaisesRegex(ValueError, "Unrecognized packaged icon"):
            icons.packaged_icon(self.master, "unrelated.png", (32, 32))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "opaque.png"
            Image.new("RGBA", (1024, 1024), "red").save(path)
            with self.assertRaisesRegex(ValueError, "transparent exterior"):
                icons.load_master(path)


if __name__ == "__main__":
    unittest.main()
