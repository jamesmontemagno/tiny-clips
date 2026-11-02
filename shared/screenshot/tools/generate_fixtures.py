#!/usr/bin/env python3
"""Generate Tiny Clips screenshot editor shared golden fixtures.

This is a stdlib-only reference implementation of the annotation geometry the
macOS and Windows editors share (see docs/screenshot-annotation-strokes-research.md).
It intentionally does not import platform code.
"""

from __future__ import annotations

import argparse
import json
import math
import shutil
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
FIXTURES = ROOT / "shared" / "screenshot" / "fixtures"
ARROW_DIR = FIXTURES / "arrow"
FIXTURE_DIRS = [ARROW_DIR]

# Arrow geometry. Points are y-down; widths are in source-image pixels.
MINIMUM_HEAD_LENGTH = 12
HEAD_LENGTH_PER_WIDTH = 3.5
HEAD_SPREAD = math.pi / 7
CURVE_BOW = 0.22
VALID_ARROW_STYLES = {"straight", "curvedLeft", "curvedRight"}


def point(x, y):
    return {"x": x, "y": y}


def lerp(a, b, t):
    return point(a["x"] + (b["x"] - a["x"]) * t, a["y"] + (b["y"] - a["y"]) * t)


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def head_length(width):
    return max(MINIMUM_HEAD_LENGTH, width * HEAD_LENGTH_PER_WIDTH)


def wings(tip, angle, length):
    return (
        point(tip["x"] - length * math.cos(angle - HEAD_SPREAD), tip["y"] - length * math.sin(angle - HEAD_SPREAD)),
        point(tip["x"] - length * math.cos(angle + HEAD_SPREAD), tip["y"] - length * math.sin(angle + HEAD_SPREAD)),
    )


def shape(curved, shaft_start, shaft_control, shaft_end, tip, head1, head2):
    return {
        "curved": curved,
        "shaftStart": shaft_start,
        "shaftControl": shaft_control,
        "shaftEnd": shaft_end,
        "tip": tip,
        "head1": head1,
        "head2": head2,
    }


def build_arrow(start, end, width, style, scale):
    if style not in VALID_ARROW_STYLES:
        raise ValueError(f"unknown arrow style {style!r}")
    dx = end["x"] - start["x"]
    dy = end["y"] - start["y"]
    length = math.sqrt(dx * dx + dy * dy)
    head = head_length(width) * scale

    if length < 0.001:
        return shape(False, start, start, start, end, end, end)

    if style == "straight":
        trim = min(head * 0.9, length * 0.98)
        shaft_end = point(end["x"] - dx / length * trim, end["y"] - dy / length * trim)
        head1, head2 = wings(end, math.atan2(dy, dx), head)
        return shape(False, start, shaft_end, shaft_end, end, head1, head2)

    # (-dy, dx) points to the right of travel in a y-down space, so a left curve bows against it.
    bow = length * CURVE_BOW * (-1 if style == "curvedLeft" else 1)
    control = point(
        (start["x"] + end["x"]) / 2 - dy / length * bow,
        (start["y"] + end["y"]) / 2 + dx / length * bow,
    )
    head1, head2 = wings(end, math.atan2(end["y"] - control["y"], end["x"] - control["x"]), head)
    trim = min(head * 0.9, length * 0.6)
    t = clamp(1.0 - trim / length, 0.02, 0.98)
    a = lerp(start, control, t)
    b = lerp(control, end, t)
    return shape(True, start, a, lerp(a, b, t), end, head1, head2)


def arrow_case(start, end, width, style, scale=1):
    start = point(*start)
    end = point(*end)
    return {
        "start": start,
        "end": end,
        "width": width,
        "style": style,
        "scale": scale,
        "expected": build_arrow(start, end, width, style, scale),
    }


def generate_arrows():
    fixtures = {}
    for style in sorted(VALID_ARROW_STYLES):
        fixtures[f"{style}.json"] = {
            "description": f"{style} arrows: thin (minimum head), thick, diagonal, upward, short (trim clamp), and a preview scale.",
            "cases": [
                arrow_case((100, 100), (400, 100), 2, style),
                arrow_case((100, 100), (400, 100), 10, style),
                arrow_case((50, 300), (350, 60), 6, style),
                arrow_case((200, 400), (200, 80), 4, style),
                arrow_case((10, 10), (22, 14), 8, style),
                arrow_case((100, 100), (400, 100), 6, style, 0.5),
            ],
        }
    fixtures["degenerate.json"] = {
        "description": "A zero-length arrow has no direction yet, so every part collapses onto its endpoints and it reports straight.",
        "cases": [arrow_case((120, 80), (120, 80), 6, style) for style in sorted(VALID_ARROW_STYLES)],
    }
    return fixtures


def generated_files():
    files = {}
    for filename, fixture in generate_arrows().items():
        files[Path("arrow") / filename] = fixture
    return files


def json_text(value):
    return json.dumps(value, indent=2, ensure_ascii=False) + "\n"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json_text(value), encoding="utf-8", newline="\n")


def check_files(files):
    expected = {FIXTURES / rel for rel in files}
    actual = set()
    for directory in FIXTURE_DIRS:
        if directory.exists():
            actual.update(path for path in directory.rglob("*.json"))
    stray = sorted(actual - expected)
    missing = sorted(expected - actual)
    changed = []
    for rel, value in files.items():
        path = FIXTURES / rel
        if path.exists() and path.read_text(encoding="utf-8") != json_text(value):
            changed.append(path)
    if stray or missing or changed:
        for path in stray:
            print(f"stray fixture: {path.relative_to(ROOT)}", file=sys.stderr)
        for path in missing:
            print(f"missing fixture: {path.relative_to(ROOT)}", file=sys.stderr)
        for path in changed:
            print(f"outdated fixture: {path.relative_to(ROOT)}", file=sys.stderr)
        return 1
    return 0


def write_files(files):
    for directory in FIXTURE_DIRS:
        if directory.exists():
            shutil.rmtree(directory)
    for rel, fixture in files.items():
        write_json(FIXTURES / rel, fixture)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="exit non-zero if generated fixtures differ from disk")
    args = parser.parse_args()
    files = generated_files()
    if args.check:
        return check_files(files)
    write_files(files)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
