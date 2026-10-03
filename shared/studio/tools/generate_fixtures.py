#!/usr/bin/env python3
"""Generate Tiny Clips Studio shared golden fixtures.

This is a stdlib-only reference implementation of docs/studio-project-format.md
sections 5, 6, and 7. It intentionally does not import platform code.
"""

from __future__ import annotations

import argparse
import copy
import json
import math
import shutil
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
FIXTURES = ROOT / "shared" / "studio" / "fixtures"
CANVAS_DIR = FIXTURES / "canvas"
LAYOUT_DIR = FIXTURES / "layout"
TIMEMAP_DIR = FIXTURES / "timemap"
STAMP = "2026-10-02T22:41:00Z"
EPS = 1e-9
CLAIMS = {}

VALID_ASPECTS = {"auto", "square", "landscape4x3", "landscape16x9", "portrait3x4", "portrait9x16"}
VALID_LAYOUTS = {"screen", "bubble", "sideBySide", "camera"}
VALID_SHAPES = {"circle", "roundedRectangle", "squircle", "rectangle"}
VALID_ANCHORS = {"topLeft", "topRight", "bottomLeft", "bottomRight"}
VALID_CAMERA_SIDES = {"leading", "trailing"}


class BoundaryLog:
    def __init__(self):
        self.exact = set()

    def compare(self, left, op, right, label):
        diff = left - right
        if diff == 0:
            self.exact.add(label)
        elif abs(diff) < EPS:
            raise AssertionError(f"near decision boundary in {label}: {left!r} {op} {right!r}")
        if op == ">":
            return left > right
        if op == ">=":
            return left >= right
        if op == "<":
            return left < right
        if op == "<=":
            return left <= right
        raise ValueError(op)


BOUNDARIES = BoundaryLog()


def gt(left, right, label):
    return BOUNDARIES.compare(left, ">", right, label)


def ge(left, right, label):
    return BOUNDARIES.compare(left, ">=", right, label)


def lt(left, right, label):
    return BOUNDARIES.compare(left, "<", right, label)


def le(left, right, label):
    return BOUNDARIES.compare(left, "<=", right, label)


def clamp(v, lo, hi):
    if v < lo:
        return lo
    if v > hi:
        return hi
    return v


def round_half_away_from_zero(v):
    if v >= 0:
        return math.floor(v + 0.5)
    return math.ceil(v - 0.5)


def even(v, label="even"):
    half = v / 2
    if half == math.floor(half) + 0.5:
        BOUNDARIES.exact.add(f"{label}: exact rounding midpoint {v}")
    return max(2, int(2 * round_half_away_from_zero(half)))


def rect(x, y, width, height):
    return {"x": x, "y": y, "width": width, "height": height}


def size(width, height):
    return {"width": width, "height": height}


def shadow(intensity, m):
    s = clamp(intensity, 0, 1)
    return {"blur": s * 0.04 * m, "offsetY": s * 0.012 * m, "opacity": s * 0.5}


DEFAULT_BACKGROUND = {
    "style": "gradient",
    "preset": "ocean",
    "primary": "#2687E8",
    "secondary": "#2EE0BF",
    "image": None,
}
DEFAULT_CANVAS = {"aspect": "auto", "padding": 0.06, "background": DEFAULT_BACKGROUND}
DEFAULT_SCREEN = {"cornerRadius": 0.02, "shadow": 0.5, "crop": None}
DEFAULT_CAMERA = {
    "shape": "circle",
    "cornerRadius": 0.12,
    "mirror": True,
    "borderWidth": 0,
    "borderColor": "#FFFFFF",
    "shadow": 0.35,
    "crop": None,
    "cutout": "none",
}
DEFAULT_BUBBLE = {"anchor": "bottomRight", "size": 0.24, "offsetX": 0, "offsetY": 0}
DEFAULT_SPLIT = {"cameraSide": "trailing", "cameraFraction": 0.3}
DEFAULT_TRANSITION = {"kind": "cut", "duration": 0.35}
DEFAULT_SCENE = {
    "start": 0,
    "layout": "bubble",
    "bubble": DEFAULT_BUBBLE,
    "split": DEFAULT_SPLIT,
    "transition": DEFAULT_TRANSITION,
}
DEFAULT_EDITS = {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": []}
DEFAULT_AUDIO = {"muted": False, "systemVolume": 1, "microphoneVolume": 1}
DEFAULT_OVERLAYS = {
    "clicks": {
        "enabled": True,
        "color": "#0A84FF",
        "size": 40,
        "strokeWidth": 3,
        "opacity": 0.85,
        "duration": 0.45,
    },
    "branding": False,
}


ASPECTS = {
    "auto": None,
    "square": 1,
    "landscape4x3": 4 / 3,
    "landscape16x9": 16 / 9,
    "portrait3x4": 3 / 4,
    "portrait9x16": 9 / 16,
}


def enum(value, valid, default):
    return value if isinstance(value, str) and value in valid else default


def deep_merge(defaults, value):
    if value is None:
        return copy.deepcopy(defaults)
    if not isinstance(defaults, dict) or not isinstance(value, dict):
        return copy.deepcopy(value)
    merged = copy.deepcopy(defaults)
    for key, item in value.items():
        if key in merged:
            merged[key] = deep_merge(merged[key], item)
        else:
            merged[key] = copy.deepcopy(item)
    return merged


def normalize_canvas(value):
    canvas = deep_merge(DEFAULT_CANVAS, value)
    canvas["aspect"] = enum(canvas.get("aspect"), VALID_ASPECTS, "auto")
    return canvas


def normalize_screen(value):
    return deep_merge(DEFAULT_SCREEN, value)


def normalize_camera_style(value):
    camera = deep_merge(DEFAULT_CAMERA, value)
    camera["shape"] = enum(camera.get("shape"), VALID_SHAPES, "circle")
    return camera


def normalize_bubble(value):
    bubble = deep_merge(DEFAULT_BUBBLE, value)
    bubble["anchor"] = enum(bubble.get("anchor"), VALID_ANCHORS, "bottomRight")
    return bubble


def normalize_split(value):
    split = deep_merge(DEFAULT_SPLIT, value)
    split["cameraSide"] = enum(split.get("cameraSide"), VALID_CAMERA_SIDES, "trailing")
    return split


def normalize_scene(value):
    scene = deep_merge(DEFAULT_SCENE, value)
    scene["layout"] = enum(scene.get("layout"), VALID_LAYOUTS, "bubble")
    scene["bubble"] = normalize_bubble(scene.get("bubble"))
    scene["split"] = normalize_split(scene.get("split"))
    return scene


def normalize_edits(value):
    return deep_merge(DEFAULT_EDITS, value)


def scene(**overrides):
    base = copy.deepcopy(DEFAULT_SCENE)
    for key, value in overrides.items():
        if isinstance(value, dict) and isinstance(base.get(key), dict):
            base[key] = deep_merge(base[key], value)
        else:
            base[key] = value
    return base


def project(
    pid,
    *,
    name,
    screen_size=(1920, 1080),
    screen_duration=20,
    camera_size=(1280, 720),
    camera_duration=20,
    camera_start=0,
    has_camera=True,
    canvas=None,
    screen_style=None,
    camera_style=None,
    scenes=None,
    edits=None,
    app_platform="windows",
    omit=None,
):
    omit = set(omit or [])
    p = {
        "schemaVersion": 1,
        "id": pid,
        "name": name,
        "createdAt": STAMP,
        "modifiedAt": STAMP,
        "lastOpenedAt": STAMP,
        "app": {"platform": app_platform, "version": "1.9.0"},
        "keepSources": False,
        "sources": {
            "screen": {
                "file": "screen.mp4",
                "width": screen_size[0],
                "height": screen_size[1],
                "frameRate": 30,
                "duration": screen_duration,
                "external": False,
            },
            "camera": None,
            "events": "events.json",
        },
        "canvas": deep_merge(DEFAULT_CANVAS, canvas or {}),
        "screen": deep_merge(DEFAULT_SCREEN, screen_style or {}),
        "camera": deep_merge(DEFAULT_CAMERA, camera_style or {}),
        "scenes": scenes if scenes is not None else [scene(layout="bubble" if has_camera else "screen")],
        "zooms": [],
        "edits": deep_merge(DEFAULT_EDITS, edits or {}),
        "audio": copy.deepcopy(DEFAULT_AUDIO),
        "overlays": copy.deepcopy(DEFAULT_OVERLAYS),
        "exports": [],
    }
    if has_camera:
        p["sources"]["camera"] = {
            "file": "camera.mp4",
            "width": camera_size[0],
            "height": camera_size[1],
            "duration": camera_duration,
            "startOffset": camera_start,
        }
    for key in omit:
        if "." in key:
            target = p
            parts = key.split(".")
            for part in parts[:-1]:
                target = target[part]
            target.pop(parts[-1], None)
        else:
            p.pop(key, None)
    return p


def minimal_project(pid, *, screen_size=(1920, 1080), camera_size=(1280, 720), has_camera=True):
    sources = {"screen": {"width": screen_size[0], "height": screen_size[1], "duration": 20}}
    if has_camera:
        sources["camera"] = {"width": camera_size[0], "height": camera_size[1], "duration": 20}
    return {"id": pid, "sources": sources}


def get_sources_camera(p):
    return p.get("sources", {}).get("camera")


def get_canvas(p):
    return normalize_canvas(p.get("canvas"))


def get_screen_style(p):
    return normalize_screen(p.get("screen"))


def get_camera_style(p):
    return normalize_camera_style(p.get("camera"))


def get_edits(p):
    return normalize_edits(p.get("edits"))


def crop_upper_valid(sum_value, label):
    upper = 1 + EPS
    diff = sum_value - upper
    if diff == 0:
        BOUNDARIES.exact.add(f"{label}: exact crop slack boundary")
    elif abs(diff) < EPS:
        raise AssertionError(f"near crop slack boundary in {label}: {sum_value!r}")
    return sum_value <= upper


def valid_crop(crop, label="crop"):
    if crop is None:
        return False
    x = crop.get("x", 0)
    y = crop.get("y", 0)
    width = crop.get("width", 0)
    height = crop.get("height", 0)
    return (
        ge(x, 0, f"{label}.x>=0")
        and ge(y, 0, f"{label}.y>=0")
        and ge(width, 0.05, f"{label}.width>=0.05")
        and ge(height, 0.05, f"{label}.height>=0.05")
        and crop_upper_valid(x + width, f"{label}.x+width<=1+slack")
        and crop_upper_valid(y + height, f"{label}.y+height<=1+slack")
    )


def fit(aspect, r, label="fit"):
    rw = r["width"]
    rh = r["height"]
    if gt(rw / rh, aspect, f"{label}: rect aspect > content aspect"):
        h = rh
        w = h * aspect
    else:
        w = rw
        h = w / aspect
    return rect(r["x"] + (rw - w) / 2, r["y"] + (rh - h) / 2, w, h)


def screen_content_size(p):
    screen = p["sources"]["screen"]
    style = get_screen_style(p)
    crop = style.get("crop") if valid_crop(style.get("crop"), "screen crop") else None
    return (
        screen["width"] * (crop["width"] if crop else 1),
        screen["height"] * (crop["height"] if crop else 1),
    )


def natural_canvas(p):
    sw, sh = screen_content_size(p)
    aspect_name = get_canvas(p).get("aspect", "auto")
    a = ASPECTS[aspect_name]
    if a is None:
        w, h = sw, sh
    elif ge(sw / sh, a, f"natural {aspect_name}: source aspect >= target aspect"):
        w, h = sw, sw / a
    else:
        w, h = sh * a, sh
    return size(even(w, f"natural width {p.get('id', '')}"), even(h, f"natural height {p.get('id', '')}"))


def export_size_for_natural(natural, long_side_limit, label):
    w, h = natural["width"], natural["height"]
    scale = 1 if long_side_limit <= 0 else min(1, long_side_limit / max(w, h))
    return size(even(w * scale, f"{label} export width"), even(h * scale, f"{label} export height"))


def normalize_scenes(p):
    raw = p.get("scenes")
    if not raw:
        raw = [copy.deepcopy(DEFAULT_SCENE)]
    prepared = []
    for order, item in enumerate(raw):
        s = normalize_scene(item)
        start = s.get("start", 0)
        s["start"] = max(0, 0 if start is None else start)
        prepared.append((order, s))
    prepared.sort(key=lambda pair: pair[1]["start"])
    kept = []
    for _, s in prepared:
        if kept and s["start"] == kept[-1]["start"]:
            BOUNDARIES.exact.add("scene normalization: exact duplicate start")
            kept[-1] = s
        else:
            kept.append(s)
    kept[0]["start"] = 0
    return kept


def active_scene(p, t):
    scenes = normalize_scenes(p)
    index = 0
    if t >= 0:
        for i, s in enumerate(scenes):
            if s["start"] == t:
                BOUNDARIES.exact.add("scene selection: exact scene start")
            if s["start"] <= t:
                index = i
            else:
                break
    return index, scenes[index]


def camera_content_size_and_aspect(p):
    camera = get_sources_camera(p)
    style = get_camera_style(p)
    crop = style.get("crop") if valid_crop(style.get("crop"), "camera crop") else None
    cw = camera["width"] * (crop["width"] if crop else 1)
    ch = camera["height"] * (crop["height"] if crop else 1)
    return cw, ch, cw / ch


def camera_source_rect(p, camera_rect, label="camera source"):
    style = get_camera_style(p)
    c = style.get("crop") if valid_crop(style.get("crop"), "camera crop") else rect(0, 0, 1, 1)
    _, _, ac = camera_content_size_and_aspect(p)
    ad = camera_rect["width"] / camera_rect["height"]
    if gt(ac, ad, f"{label}: camera aspect > destination aspect"):
        k = ad / ac
        return rect(c["x"] + c["width"] * (1 - k) / 2, c["y"], c["width"] * k, c["height"])
    k = ac / ad
    return rect(c["x"], c["y"] + c["height"] * (1 - k) / 2, c["width"], c["height"] * k)


def resolve_layout(p, t, canvas_size):
    w, h = canvas_size["width"], canvas_size["height"]
    m = min(w, h)
    canvas = get_canvas(p)
    screen_style = get_screen_style(p)
    has_camera = get_sources_camera(p) is not None
    scene_index, sc = active_scene(p, t)
    layout = sc.get("layout", "bubble") if has_camera else "screen"
    padding = clamp(canvas.get("padding", 0.06), 0, 0.4) * m
    content = rect(padding, padding, w - 2 * padding, h - 2 * padding)
    sw, sh = screen_content_size(p)
    screen_aspect = sw / sh
    rs = clamp(screen_style.get("cornerRadius", 0.02), 0, 0.2) * m
    screen_rect = None
    camera_rect = None

    if layout == "screen":
        screen_rect = fit(screen_aspect, content, "screen layout fit")
    elif layout == "bubble":
        screen_rect = fit(screen_aspect, content, "bubble screen fit")
        camera_style = get_camera_style(p)
        _, _, camera_aspect = camera_content_size_and_aspect(p)
        bubble = normalize_bubble(sc.get("bubble"))
        d = clamp(bubble.get("size", 0.24), 0.08, 0.6) * m
        shape = enum(camera_style.get("shape"), VALID_SHAPES, "circle")
        if shape in ("circle", "squircle"):
            bw = d
            bh = d
        else:
            bh = d
            bw = d * clamp(camera_aspect, 0.5, 2)
        if gt(bw, 0.9 * w, "bubble width > 0.9 * canvas width"):
            scale = 0.9 * w / bw
            bh *= scale
            bw = 0.9 * w
        gap = 0.03 * m
        anchor = bubble.get("anchor", "bottomRight")
        x = gap if anchor.endswith("Left") else w - gap - bw
        y = gap if anchor.startswith("top") else h - gap - bh
        x += bubble.get("offsetX", 0) * w
        y += bubble.get("offsetY", 0) * h
        camera_rect = rect(clamp(x, 0, w - bw), clamp(y, 0, h - bh), bw, bh)
    elif layout == "sideBySide":
        split = normalize_split(sc.get("split"))
        gap = 0.02 * m
        f = clamp(split.get("cameraFraction", 0.3), 0.15, 0.6)
        if ge(w, h, "sideBySide canvas width >= height"):
            cam_w = f * (content["width"] - gap)
            s = fit(screen_aspect, rect(0, 0, content["width"] - gap - cam_w, content["height"]), "side horizontal screen fit")
            x0 = content["x"] + (content["width"] - (s["width"] + gap + cam_w)) / 2
            y0 = content["y"] + (content["height"] - s["height"]) / 2
            if split.get("cameraSide", "trailing") == "trailing":
                screen_rect = rect(x0, y0, s["width"], s["height"])
                camera_rect = rect(x0 + s["width"] + gap, y0, cam_w, s["height"])
            else:
                camera_rect = rect(x0, y0, cam_w, s["height"])
                screen_rect = rect(x0 + cam_w + gap, y0, s["width"], s["height"])
        else:
            cam_h = f * (content["height"] - gap)
            s = fit(screen_aspect, rect(0, 0, content["width"], content["height"] - gap - cam_h), "side vertical screen fit")
            x0 = content["x"] + (content["width"] - s["width"]) / 2
            y0 = content["y"] + (content["height"] - (s["height"] + gap + cam_h)) / 2
            if split.get("cameraSide", "trailing") == "trailing":
                screen_rect = rect(x0, y0, s["width"], s["height"])
                camera_rect = rect(x0, y0 + s["height"] + gap, s["width"], cam_h)
            else:
                camera_rect = rect(x0, y0, s["width"], cam_h)
                screen_rect = rect(x0, y0 + cam_h + gap, s["width"], s["height"])
    elif layout == "camera":
        camera_rect = content

    screen_out = None
    if screen_rect is not None:
        screen_crop = screen_style.get("crop") if valid_crop(screen_style.get("crop"), "screen crop") else rect(0, 0, 1, 1)
        screen_out = {
            "rect": screen_rect,
            "source": screen_crop,
            "cornerRadius": min(rs, min(screen_rect["width"], screen_rect["height"]) / 2),
            "shadow": shadow(screen_style.get("shadow", 0.5), m),
        }

    camera_out = None
    if camera_rect is not None:
        camera_style = get_camera_style(p)
        if layout == "bubble":
            shape = enum(camera_style.get("shape"), VALID_SHAPES, "circle")
            if shape in ("circle", "squircle"):
                corner_radius = min(camera_rect["width"], camera_rect["height"]) / 2
            elif shape == "roundedRectangle":
                corner_radius = clamp(camera_style.get("cornerRadius", 0.12), 0, 0.5) * min(
                    camera_rect["width"], camera_rect["height"]
                )
            else:
                corner_radius = 0
        else:
            corner_radius = min(rs, min(camera_rect["width"], camera_rect["height"]) / 2)
            shape = "roundedRectangle" if corner_radius > 0 else "rectangle"
        source = get_sources_camera(p)
        start_offset = source.get("startOffset", 0)
        tc = t - (0 if start_offset is None else start_offset)
        if tc == 0:
            BOUNDARIES.exact.add("camera timing: exact start")
        if tc == source["duration"]:
            BOUNDARIES.exact.add("camera timing: exact end")
        camera_out = {
            "rect": camera_rect,
            "source": camera_source_rect(p, camera_rect),
            "shape": shape,
            "cornerRadius": corner_radius,
            "mirror": camera_style.get("mirror", True),
            "borderWidth": clamp(camera_style.get("borderWidth", 0), 0, 0.02) * m,
            "shadow": shadow(camera_style.get("shadow", 0.35), m),
            "sourceTime": clamp(tc, 0, source["duration"]),
            "visible": 0 <= tc <= source["duration"],
        }

    return {"sceneIndex": scene_index, "layout": layout, "screen": screen_out, "camera": camera_out}


def time_map(duration, edits):
    edits = normalize_edits(edits)
    start = clamp(edits.get("trimStart", 0), 0, duration)
    trim_end = duration if edits.get("trimEnd") is None else edits.get("trimEnd")
    end = clamp(trim_end, start, duration)
    cuts = []
    for cut in edits.get("cuts", []):
        s = clamp(cut["start"], start, end)
        e = clamp(cut["end"], start, end)
        if e > s:
            cuts.append([s, e])
    cuts.sort(key=lambda item: item[0])
    merged = []
    for s, e in cuts:
        if merged and s <= merged[-1][1]:
            if s == merged[-1][1]:
                BOUNDARIES.exact.add("time map: touching cuts merge")
            merged[-1][1] = max(merged[-1][1], e)
        else:
            merged.append([s, e])
    kept = []
    cursor = start
    for s, e in merged:
        if s > cursor:
            kept.append([cursor, s])
        cursor = max(cursor, e)
    if cursor < end:
        kept.append([cursor, end])
    output_duration = sum(e - s for s, e in kept)
    return start, [{"start": s, "end": e} for s, e in kept], output_duration


def source_to_output(t, segments, output_duration):
    if not segments:
        return 0
    cumulative = 0
    for i, segment in enumerate(segments):
        s, e = segment["start"], segment["end"]
        if t == s:
            BOUNDARIES.exact.add("time map: source query at segment start")
        if t == e:
            BOUNDARIES.exact.add("time map: source query at segment end")
        if t < s:
            return cumulative
        if s <= t < e:
            return cumulative + (t - s)
        cumulative += e - s
        if i + 1 < len(segments) and e <= t < segments[i + 1]["start"]:
            return cumulative
    return output_duration


def output_to_source(u, start, segments, output_duration):
    if not segments:
        return start
    u = clamp(u, 0, output_duration)
    if u == output_duration:
        BOUNDARIES.exact.add("time map: output query at outputDuration")
    cumulative = 0
    for segment in segments:
        s, e = segment["start"], segment["end"]
        length = e - s
        if u == cumulative:
            BOUNDARIES.exact.add("time map: output query at segment start")
        if cumulative <= u < cumulative + length:
            return s + (u - cumulative)
        cumulative += length
    return segments[-1]["end"]


def layout_fixture(description, p, cases):
    natural = natural_canvas(p)
    return {
        "description": description,
        "project": p,
        "naturalCanvas": natural,
        "cases": [{"time": t, "canvas": canvas, "expected": resolve_layout(p, t, canvas)} for t, canvas in cases],
    }


def add_fixture(fixtures, filename, fixture, *, null=None, omitted=None, unknown=None):
    fixtures[filename] = fixture
    if null or omitted or unknown:
        CLAIMS[Path("layout") / filename] = {
            "null": null or [],
            "omitted": omitted or [],
            "unknown": unknown or {},
        }


def timemap_fixture(description, duration, edits, source_queries, output_queries):
    start, segments, output_duration = time_map(duration, edits)
    return {
        "description": description,
        "sourceDuration": duration,
        "edits": edits,
        "expected": {
            "outputDuration": output_duration,
            "segments": segments,
            "sourceToOutput": [
                {"source": t, "output": source_to_output(t, segments, output_duration)} for t in source_queries
            ],
            "outputToSource": [
                {"output": u, "source": output_to_source(u, start, segments, output_duration)}
                for u in output_queries
            ],
        },
    }


def canvas_fixture(description, cases):
    return {
        "description": description,
        "cases": [
            {"natural": natural, "limit": limit, "expected": export_size_for_natural(natural, limit, description)}
            for natural, limit in cases
        ],
    }


def assert_equal(actual, expected, label):
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")


def assert_close(actual, expected, label):
    if abs(actual - expected) > 1e-9:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")


def generate_layouts():
    fixtures = {}

    p = project("00000000-0000-4000-8000-000000000001", name="Default 1080p screen", has_camera=False, scenes=[scene(layout="screen")])
    f = layout_fixture("Default look screen layout on a 1920x1080 source with explicit project properties.", p, [(0, size(1920, 1080))])
    assert_close(f["cases"][0]["expected"]["screen"]["rect"]["width"], 1689.6, "default screen fit width")
    fixtures["screen-default-1080p.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000002",
        name="Classic screen look",
        has_camera=False,
        canvas={"padding": 0, "background": {"style": "none", "preset": None, "primary": "#000000", "secondary": None}},
        screen_style={"cornerRadius": 0, "shadow": 0},
        scenes=[scene(layout="screen")],
    )
    f = layout_fixture("Classic flattened-recording look: no background, padding, radius, or shadow.", p, [(0, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["screen"]["cornerRadius"], 0, "classic radius")
    fixtures["screen-classic-look.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000003",
        name="Bubble anchors and shapes",
        scenes=[
            scene(start=0, layout="bubble", bubble={"anchor": "topLeft", "size": 0.24}),
            scene(start=2, layout="bubble", bubble={"anchor": "topRight", "size": 0.24}),
            scene(start=4, layout="bubble", bubble={"anchor": "bottomLeft", "size": 0.24}),
            scene(start=6, layout="bubble", bubble={"anchor": "bottomRight", "size": 0.24}),
        ],
    )
    f = layout_fixture(
        "Circle bubble with a 16:9 camera at all four anchors.",
        p,
        [(0, size(1920, 1080)), (2, size(1920, 1080)), (4, size(1920, 1080)), (6, size(1920, 1080))],
    )
    assert_equal([case["expected"]["camera"]["shape"] for case in f["cases"]], ["circle"] * 4, "circle anchors")
    fixtures["bubble-16x9-anchors-circle.json"] = f

    for idx, shape_name in enumerate(["roundedRectangle", "squircle", "rectangle"], start=4):
        p = project(
            f"00000000-0000-4000-8000-0000000000{idx:02d}",
            name=f"Bubble {shape_name}",
            camera_style={"shape": shape_name, "cornerRadius": 0.25, "borderWidth": 0.01},
            scenes=[scene(layout="bubble", bubble={"anchor": "bottomRight", "size": 0.24})],
        )
        f = layout_fixture(f"Bubble layout using the {shape_name} camera shape with a 16:9 camera.", p, [(0, size(1920, 1080))])
        assert_equal(f["cases"][0]["expected"]["camera"]["shape"], shape_name, f"{shape_name} branch")
        fixtures[f"bubble-16x9-shape-{shape_name}.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000007",
        name="Bubble camera aspects",
        camera_size=(1024, 768),
        camera_style={"shape": "roundedRectangle"},
        scenes=[scene(start=0, layout="bubble", bubble={"anchor": "bottomRight", "size": 0.25})],
    )
    f = layout_fixture("Rounded-rectangle bubble with a 4:3 camera, exercising aspect-based bubble width and source fill.", p, [(0, size(1920, 1080))])
    assert_close(f["cases"][0]["expected"]["camera"]["rect"]["width"], 360, "4:3 rounded width")
    fixtures["bubble-4x3-rounded-camera.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000008",
        name="Bubble portrait camera",
        camera_size=(600, 1600),
        camera_style={"shape": "roundedRectangle"},
        scenes=[scene(layout="bubble", bubble={"anchor": "bottomRight", "size": 0.3})],
    )
    f = layout_fixture("Rounded-rectangle bubble with a portrait camera, exercising the 0.5 minimum camera aspect clamp.", p, [(0, size(1920, 1080))])
    assert_close(f["cases"][0]["expected"]["camera"]["rect"]["width"], 162, "portrait 0.5 aspect clamp")
    fixtures["bubble-portrait-camera.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000009",
        name="Bubble clamp and size limits",
        camera_style={"shape": "roundedRectangle"},
        scenes=[
            scene(start=0, layout="bubble", bubble={"anchor": "bottomRight", "size": 0.01, "offsetX": 1, "offsetY": 1}),
            scene(start=3, layout="bubble", bubble={"anchor": "topLeft", "size": 1.2, "offsetX": -1, "offsetY": -1}),
        ],
    )
    f = layout_fixture("Bubble offsets clamp to canvas, size clamps below 0.08 and above 0.6.", p, [(0, size(1920, 1080)), (3, size(1920, 1080))])
    assert_close(f["cases"][0]["expected"]["camera"]["rect"]["height"], 86.4, "bubble min size clamp")
    assert_close(f["cases"][1]["expected"]["camera"]["rect"]["height"], 648, "bubble max size clamp")
    fixtures["bubble-clamps-and-size-limits.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000010",
        name="Bubble width limit",
        camera_size=(3840, 720),
        camera_style={"shape": "roundedRectangle"},
        scenes=[scene(layout="bubble", bubble={"anchor": "bottomRight", "size": 0.6})],
    )
    f = layout_fixture(
        "Wide rounded-rectangle camera has one case clearly under and one case clearly over the 0.9 * canvas width limit.",
        p,
        [(0, size(800, 480)), (0, size(600, 480))],
    )
    assert_close(f["cases"][0]["expected"]["camera"]["rect"]["width"], 576, "bubble width under limit")
    assert_close(f["cases"][1]["expected"]["camera"]["rect"]["width"], 540, "bubble width limit branch")
    assert_close(f["cases"][1]["expected"]["camera"]["rect"]["height"], 270, "bubble width limit scaled height")
    fixtures["bubble-width-limit.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000011",
        name="Bubble 4x3 circle mirror false",
        camera_size=(1024, 768),
        camera_style={"shape": "circle", "mirror": False},
        scenes=[scene(layout="bubble", bubble={"size": 0.25})],
    )
    f = layout_fixture("Circle bubble with a 4:3 camera and mirror false.", p, [(0, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["camera"]["mirror"], False, "mirror false")
    fixtures["bubble-4x3-circle-mirror-false.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000012",
        name="Bubble 4x3 rectangle",
        camera_size=(1024, 768),
        camera_style={"shape": "rectangle"},
        scenes=[scene(layout="bubble", bubble={"size": 0.25})],
    )
    f = layout_fixture("Rectangle bubble with a 4:3 camera.", p, [(0, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["camera"]["shape"], "rectangle", "4:3 rectangle")
    fixtures["bubble-4x3-rectangle.json"] = f

    for name, screen_size, aspect in [
        ("1080p", (1920, 1080), "auto"),
        ("ultrawide", (3440, 1440), "auto"),
        ("portrait-in-landscape", (1080, 1920), "landscape16x9"),
    ]:
        p = project(
            f"00000000-0000-4000-8000-0000000001{len(fixtures) % 10}",
            name=f"Side by side {name}",
            screen_size=screen_size,
            canvas={"aspect": aspect},
            scenes=[
                scene(start=0, layout="sideBySide", split={"cameraSide": "trailing", "cameraFraction": 0.3}),
                scene(start=2, layout="sideBySide", split={"cameraSide": "leading", "cameraFraction": 0.3}),
            ],
        )
        n = natural_canvas(p)
        f = layout_fixture(f"Horizontal side-by-side trailing and leading on {name} screen content.", p, [(0, n), (2, n)])
        assert_equal([case["expected"]["layout"] for case in f["cases"]], ["sideBySide", "sideBySide"], f"side {name}")
        fixtures[f"side-by-side-horizontal-{name}.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000014",
        name="Side by side vertical",
        canvas={"aspect": "portrait9x16"},
        scenes=[
            scene(start=0, layout="sideBySide", split={"cameraSide": "trailing", "cameraFraction": 0.3}),
            scene(start=2, layout="sideBySide", split={"cameraSide": "leading", "cameraFraction": 0.3}),
        ],
    )
    f = layout_fixture("Vertical side-by-side trailing and leading on a portrait9x16 canvas.", p, [(0, natural_canvas(p)), (2, natural_canvas(p))])
    assert_equal(f["cases"][0]["expected"]["screen"]["rect"]["y"] < f["cases"][0]["expected"]["camera"]["rect"]["y"], True, "vertical trailing")
    fixtures["side-by-side-vertical-portrait.json"] = f

    p = project(
        "00000000-0000-4000-8000-000000000015",
        name="Side by side square fractions",
        scenes=[
            scene(start=0, layout="sideBySide", split={"cameraSide": "trailing", "cameraFraction": 0.15}),
            scene(start=1, layout="sideBySide", split={"cameraSide": "trailing", "cameraFraction": 0.45}),
            scene(start=2, layout="sideBySide", split={"cameraSide": "trailing", "cameraFraction": 0.6}),
        ],
    )
    f = layout_fixture("Square canvas takes horizontal sideBySide branch with cameraFraction 0.15, 0.45, and 0.6.", p, [(0, size(1000, 1000)), (1, size(1000, 1000)), (2, size(1000, 1000))])
    assert_equal(all(case["expected"]["screen"]["rect"]["x"] < case["expected"]["camera"]["rect"]["x"] for case in f["cases"]), True, "square horizontal branch")
    fixtures["side-by-side-square-fractions.json"] = f

    for aspect in ["square", "landscape4x3", "landscape16x9", "portrait3x4", "portrait9x16"]:
        p = project(f"00000000-0000-4000-8000-0000000002{len(fixtures) % 10}", name=f"Aspect wider {aspect}", screen_size=(3000, 1000), has_camera=False, canvas={"aspect": aspect}, scenes=[scene(layout="screen")])
        fixtures[f"canvas-aspect-wider-{aspect}.json"] = layout_fixture(f"Natural canvas for {aspect} with source wider than the target aspect.", p, [(0, natural_canvas(p))])
    for aspect in ["square", "landscape4x3", "landscape16x9", "portrait3x4", "portrait9x16"]:
        p = project(f"00000000-0000-4000-8000-0000000003{len(fixtures) % 10}", name=f"Aspect narrower {aspect}", screen_size=(1000, 3000), has_camera=False, canvas={"aspect": aspect}, scenes=[scene(layout="screen")])
        fixtures[f"canvas-aspect-narrower-{aspect}.json"] = layout_fixture(f"Natural canvas for {aspect} with source narrower than the target aspect.", p, [(0, natural_canvas(p))])

    p = project("00000000-0000-4000-8000-000000000026", name="Valid crop with fractional midpoint", screen_size=(1001, 1001), camera_size=(1001, 751), canvas={"aspect": "landscape16x9"}, screen_style={"crop": rect(0.25, 0.25, 0.5, 0.5)}, camera_style={"crop": rect(0.2, 0.1, 0.6, 0.8), "shape": "roundedRectangle"}, scenes=[scene(layout="bubble", bubble={"size": 0.3})])
    f = layout_fixture("Valid screen and camera crops affect natural size, screen source, camera aspect, and camera source.", p, [(0, natural_canvas(p))])
    assert_equal(f["cases"][0]["expected"]["screen"]["source"], rect(0.25, 0.25, 0.5, 0.5), "valid crop source")
    fixtures["valid-crops-fractional-midpoints.json"] = f

    for pid, dims in [("027", (1001, 1081)), ("028", (1441, 1001))]:
        p = project(f"00000000-0000-4000-8000-000000000{pid}", name=f"Half away natural {dims[0]}x{dims[1]}", screen_size=dims, has_camera=False, scenes=[scene(layout="screen")])
        f = layout_fixture("Aspect auto with odd integer natural dimensions exercises exact half-away-from-zero even() rounding.", p, [(0, natural_canvas(p))])
        assert_equal(f["naturalCanvas"], size(even(dims[0], "assert odd width"), even(dims[1], "assert odd height")), "odd auto natural")
        fixtures[f"natural-even-half-away-auto-{dims[0]}x{dims[1]}.json"] = f

    p = project("00000000-0000-4000-8000-000000000029", name="No camera fallback with omitted defaults", has_camera=False, scenes=[scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide"), scene(start=4, layout="camera")], omit=["canvas", "screen"])
    f = layout_fixture("A project with no camera resolves bubble, sideBySide, and camera scene requests as screen with camera null; deliberately omits optional canvas and screen properties to exercise defaults.", p, [(0, size(1920, 1080)), (2, size(1920, 1080)), (4, size(1920, 1080))])
    assert_equal([case["expected"]["layout"] for case in f["cases"]], ["screen", "screen", "screen"], "no camera fallback")
    add_fixture(fixtures, "no-camera-layouts-fallback-to-screen.json", f, omitted=["project.canvas", "project.screen"])

    for offset, label in [(1.5, "positive"), (-1.5, "negative")]:
        p = project(f"00000000-0000-4000-8000-00000000003{0 if offset > 0 else 1}", name=f"Camera timing {label}", camera_duration=5, camera_start=offset, scenes=[scene(layout="camera" if offset > 0 else "bubble")])
        times = [offset - 0.1, offset, offset + 2.5, offset + 5, offset + 5.1]
        f = layout_fixture(f"Camera visibility and sourceTime with a {label} startOffset before, at, inside, at end, and after range.", p, [(t, size(1920, 1080)) for t in times])
        assert_equal([case["expected"]["camera"]["visible"] for case in f["cases"]], [False, True, True, True, False], f"camera timing {label}")
        fixtures[f"camera-timing-{label}-start-offset.json"] = f

    p = project("00000000-0000-4000-8000-000000000032", name="Scene normalization", scenes=[scene(start=5, layout="camera"), scene(start=-2, layout="screen"), scene(start=3, layout="bubble", bubble={"anchor": "topLeft"}), scene(start=3, layout="sideBySide", split={"cameraSide": "leading"}), scene(start=10, layout="bubble", bubble={"anchor": "bottomLeft"})])
    f = layout_fixture("Unsorted scenes, duplicate starts, negative start normalization, t < 0, and exact boundaries.", p, [(-1, size(1920, 1080)), (0, size(1920, 1080)), (3, size(1920, 1080)), (5, size(1920, 1080)), (10, size(1920, 1080))])
    assert_equal([case["expected"]["sceneIndex"] for case in f["cases"]], [0, 0, 1, 2, 3], "scene normalization indexes")
    fixtures["scene-normalization-unsorted-duplicates.json"] = f

    p = project("00000000-0000-4000-8000-000000000033", name="Styling clamps", canvas={"padding": 0.9}, screen_style={"cornerRadius": 9, "shadow": 2, "crop": rect(0.96, 0, 0.05, 1)}, camera_style={"cornerRadius": 9, "borderWidth": 1, "shadow": -1, "shape": "roundedRectangle", "crop": rect(-0.1, 0, 0.5, 0.5)}, scenes=[scene(layout="sideBySide", split={"cameraFraction": 1.5})])
    f = layout_fixture("Out-of-range padding, radii, shadows, border width, cameraFraction, and invalid screen/camera crops clamp or are ignored at use.", p, [(0, size(1920, 1080))])
    assert_close(f["cases"][0]["expected"]["camera"]["borderWidth"], 21.6, "border clamp")
    fixtures["style-and-field-clamps.json"] = f

    p = project("00000000-0000-4000-8000-000000000034", name="Scale invariance and arbitrary canvas", scenes=[scene(layout="bubble", bubble={"anchor": "bottomRight", "size": 0.24})])
    n = natural_canvas(p)
    f = layout_fixture("Same project resolved at natural, half, double, square, and portrait canvases.", p, [(0, n), (0, size(n["width"] / 2, n["height"] / 2)), (0, size(n["width"] * 2, n["height"] * 2)), (0, size(1000, 1000)), (0, size(720, 1280))])
    assert_close(f["cases"][1]["expected"]["camera"]["rect"]["width"] * 2, f["cases"][0]["expected"]["camera"]["rect"]["width"], "scale half")
    fixtures["scale-invariance-multiple-canvases.json"] = f

    p = minimal_project("00000000-0000-4000-8000-000000000035")
    f = layout_fixture("Minimal project with only required screen and camera properties; every optional value defaults.", p, [(0, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["layout"], "bubble", "minimal default bubble")
    add_fixture(
        fixtures,
        "minimal-project-all-defaults.json",
        f,
        omitted=[
            "project.schemaVersion",
            "project.canvas",
            "project.screen",
            "project.camera",
            "project.scenes",
            "project.zooms",
            "project.edits",
            "project.audio",
            "project.overlays",
            "project.exports",
        ],
    )

    p = minimal_project("00000000-0000-4000-8000-000000000036", has_camera=False)
    f = layout_fixture("Minimal project with sources.camera omitted entirely resolves default no-camera screen layout.", p, [(0, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["camera"], None, "minimal no camera")
    add_fixture(fixtures, "minimal-project-no-camera.json", f, omitted=["project.sources.camera"])

    p = project("00000000-0000-4000-8000-000000000037", name="Empty scenes", scenes=[])
    f = layout_fixture("An empty scenes array normalizes to one default bubble scene at bottomRight with size 0.24.", p, [(0, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["layout"], "bubble", "empty scenes default layout")
    fixtures["scenes-empty.json"] = f

    p = project("00000000-0000-4000-8000-000000000038", name="Tolerant enums", canvas={"aspect": "cinemascope"}, camera_style={"shape": "hexagon"}, scenes=[scene(start=0, layout="nonsense", bubble={"anchor": "middle"}), scene(start=2, layout="sideBySide", split={"cameraSide": "center"}), scene(start=4, layout="bubble", bubble={"anchor": "topLeft"})])
    f = layout_fixture("Unrecognized enum strings read as defaults for canvas.aspect, camera.shape, scene.layout, bubble.anchor, and split.cameraSide.", p, [(0, size(1920, 1080)), (2, size(1920, 1080)), (4, size(1920, 1080))])
    assert_equal(f["naturalCanvas"], size(1920, 1080), "unknown aspect default auto")
    assert_equal(f["cases"][0]["expected"]["layout"], "bubble", "unknown layout default")
    assert_equal(f["cases"][0]["expected"]["camera"]["shape"], "circle", "unknown shape default")
    assert_equal(f["cases"][1]["expected"]["camera"]["rect"]["x"] > f["cases"][1]["expected"]["screen"]["rect"]["x"], True, "unknown cameraSide trailing")
    add_fixture(
        fixtures,
        "tolerant-enums.json",
        f,
        unknown={
            "project.canvas.aspect": "cinemascope",
            "project.camera.shape": "hexagon",
            "project.scenes[0].layout": "nonsense",
            "project.scenes[0].bubble.anchor": "middle",
            "project.scenes[1].split.cameraSide": "center",
        },
    )

    p = project("00000000-0000-4000-8000-000000000039", name="Null as missing", scenes=[{"start": None, "layout": None, "bubble": None}])
    p["camera"] = None
    p["canvas"]["aspect"] = None
    p["canvas"]["padding"] = None
    p["canvas"]["background"] = None
    p["screen"]["cornerRadius"] = None
    p["sources"]["camera"]["startOffset"] = None
    p["zooms"] = None
    p["edits"] = None
    p["audio"] = None
    p["overlays"] = None
    p["exports"] = None
    f = layout_fixture("Explicit null for root camera, nested numbers, nested objects, enums, array values, sources.camera.startOffset, and safe root arrays/objects counts as missing and defaults.", p, [(0, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["camera"]["shape"], "circle", "null camera defaults")
    assert_close(f["cases"][0]["expected"]["screen"]["cornerRadius"], 21.6, "null corner radius default")
    add_fixture(
        fixtures,
        "null-as-missing.json",
        f,
        null=[
            "project.camera",
            "project.canvas.aspect",
            "project.canvas.padding",
            "project.canvas.background",
            "project.screen.cornerRadius",
            "project.sources.camera.startOffset",
            "project.scenes[0].start",
            "project.scenes[0].layout",
            "project.scenes[0].bubble",
            "project.zooms",
            "project.edits",
            "project.audio",
            "project.overlays",
            "project.exports",
        ],
    )

    p = project("00000000-0000-4000-8000-000000000047", name="Null camera members", camera_style={"shape": "rectangle"}, scenes=[scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide")])
    p["camera"]["shape"] = None
    p["camera"]["mirror"] = None
    p["scenes"][1]["split"] = None
    f = layout_fixture("Explicit null camera bool/enum members and a null scene split object count as missing and default.", p, [(0, size(1920, 1080)), (2, size(1920, 1080))])
    assert_equal(f["cases"][0]["expected"]["camera"]["shape"], "circle", "null camera shape default")
    assert_equal(f["cases"][0]["expected"]["camera"]["mirror"], True, "null mirror default")
    assert_equal(f["cases"][1]["expected"]["camera"]["rect"]["x"] > f["cases"][1]["expected"]["screen"]["rect"]["x"], True, "null split defaults trailing")
    add_fixture(
        fixtures,
        "null-camera-members-and-split.json",
        f,
        null=["project.camera.shape", "project.camera.mirror", "project.scenes[1].split"],
    )

    crop_cases = [
        ("crop-boundary-min-size.json", "Screen and camera crops at exactly the 0.05 minimum size are valid.", rect(0.1, 0.1, 0.05, 0.05), rect(0.2, 0.2, 0.05, 0.05), True),
        ("crop-boundary-edge-slack.json", "Screen and camera crops at x=0.95,width=0.05 and y=0.95,height=0.05 are valid at the exact edge.", rect(0.95, 0.95, 0.05, 0.05), rect(0.95, 0.95, 0.05, 0.05), True),
        ("crop-invalid-under-min.json", "Screen and camera crops just under the 0.05 minimum size are invalid and ignored.", rect(0.1, 0.1, 0.049, 0.05), rect(0.2, 0.2, 0.05, 0.049), False),
        ("crop-invalid-past-slack.json", "Screen and camera crops past the 1e-9 upper-bound slack are invalid and ignored.", rect(0.95001, 0, 0.05, 1), rect(0, 0.95001, 1, 0.05), False),
        ("crop-invalid-negative-origin.json", "Screen and camera crops with negative origins are invalid and ignored.", rect(-0.001, 0, 0.5, 0.5), rect(0, -0.001, 0.5, 0.5), False),
    ]
    for i, (filename, description, screen_crop, camera_crop, should_be_valid) in enumerate(crop_cases, start=40):
        p = project(f"00000000-0000-4000-8000-0000000000{i}", name=filename, screen_style={"crop": screen_crop}, camera_style={"crop": camera_crop, "shape": "roundedRectangle"}, scenes=[scene(layout="bubble")])
        f = layout_fixture(description, p, [(0, size(1920, 1080))])
        expected_screen_source = screen_crop if should_be_valid else rect(0, 0, 1, 1)
        assert_equal(f["cases"][0]["expected"]["screen"]["source"], expected_screen_source, filename)
        fixtures[filename] = f

    p = project("00000000-0000-4000-8000-000000000045", name="Non-integer canvases", scenes=[scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide")])
    f = layout_fixture("Resolver accepts non-integer canvas widths and heights.", p, [(0, size(1000.5, 562.75)), (2, size(999.25, 1000.5))])
    assert_equal(isinstance(f["cases"][0]["canvas"]["width"], float), True, "non-integer canvas")
    fixtures["non-integer-canvas-sizes.json"] = f

    p = project("00000000-0000-4000-8000-000000000046", name="Camera layout source branches", camera_size=(1600, 900), scenes=[scene(layout="camera")])
    f = layout_fixture("Camera layout covers both camera source fill branches with destination wider and narrower than the camera.", p, [(0, size(2000, 900)), (0, size(720, 1280))])
    assert_equal(f["cases"][0]["expected"]["camera"]["source"]["x"], 0, "camera source vertical crop branch")
    assert_equal(f["cases"][1]["expected"]["camera"]["source"]["y"], 0, "camera source horizontal crop branch")
    fixtures["camera-layout-source-fill-branches.json"] = f

    return fixtures


def generate_timemaps():
    return {
        "no-edits.json": timemap_fixture("No trim or cuts maps the full source directly.", 20, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": []}, [-1, 0, 7.5, 20, 25], [-1, 0, 7.5, 20, 25]),
        "trim-only.json": timemap_fixture("Trim start and end with boundary, before-start, and after-end queries.", 20, {"trimStart": 2, "trimEnd": 18, "cuts": [], "speed": []}, [-1, 0, 2, 10, 18, 20], [-1, 0, 8, 16, 20]),
        "one-cut.json": timemap_fixture("One cut removes the middle of the trim range.", 20, {"trimStart": 2, "trimEnd": 18, "cuts": [{"start": 5, "end": 7}], "speed": []}, [2, 4.999, 5, 6, 7, 18], [0, 3, 3.5, 14, 15]),
        "unsorted-overlapping-touching-cuts.json": timemap_fixture("Unsorted cuts that overlap or touch merge before mapping.", 20, {"trimStart": 1, "trimEnd": 19, "cuts": [{"start": 9, "end": 12}, {"start": 4, "end": 6}, {"start": 6, "end": 8}, {"start": 11, "end": 14}], "speed": []}, [0, 1, 3, 4, 7, 8, 10, 14, 19, 21], [-1, 0, 2, 3, 5, 8, 10, 11, 12]),
        "cuts-outside-and-straddling-trim.json": timemap_fixture("Cuts outside the trim range and straddling both trim ends are clamped or dropped.", 20, {"trimStart": 3, "trimEnd": 15, "cuts": [{"start": -5, "end": 1}, {"start": 1, "end": 4}, {"start": 8, "end": 10}, {"start": 14, "end": 25}, {"start": 16, "end": 17}], "speed": []}, [2, 3, 3.5, 4, 9, 14.5, 15, 16], [-1, 0, 1, 4, 7, 8, 9]),
        "trim-end-null.json": timemap_fixture("Null trimEnd uses the screen source duration.", 12, {"trimStart": 3, "trimEnd": None, "cuts": [{"start": 5, "end": 6}], "speed": []}, [2, 3, 5.5, 6, 12, 13], [-1, 0, 2, 3, 8, 9, 10]),
        "trim-start-beyond-duration.json": timemap_fixture("trimStart beyond duration clamps to duration and produces no kept segments.", 10, {"trimStart": 15, "trimEnd": None, "cuts": [], "speed": []}, [-1, 0, 10, 15], [-1, 0, 1]),
        "trim-end-before-start.json": timemap_fixture("trimEnd before trimStart clamps up to start and produces no kept segments.", 20, {"trimStart": 12, "trimEnd": 5, "cuts": [], "speed": []}, [0, 12, 20], [-1, 0, 5]),
        "everything-cut.json": timemap_fixture("Cuts remove the entire trimmed range.", 20, {"trimStart": 2, "trimEnd": 18, "cuts": [{"start": 0, "end": 20}], "speed": []}, [-1, 2, 10, 18, 21], [-1, 0, 1]),
    }


def generate_canvases():
    fixtures = {
        "limits-no-limit-and-no-upscale.json": canvas_fixture(
            "Limit 0 and negative limits do not scale, and a limit above the long side never upscales.",
            [(size(1920, 1080), 0), (size(1920, 1080), -1), (size(1920, 1080), 4000)],
        ),
        "natural-orientations.json": canvas_fixture(
            "Landscape, portrait, and square natural canvas export sizes, including a portrait case that rounds down.",
            [(size(1920, 1080), 1280), (size(1080, 1920), 1280), (size(1000, 1000), 512), (size(1080, 1920), 1000)],
        ),
        "ultrawide-3440-limit-1920.json": canvas_fixture(
            "3440x1440 natural canvas at long-side limit 1920 exports to 1920x804.",
            [(size(3440, 1440), 1920)],
        ),
        "exact-ties-power-of-two-scale.json": canvas_fixture(
            "Exact power-of-two scales hit even() midpoint ties plus a no-tie landscape half-scale case.",
            [(size(2004, 1002), 1002), (size(4008, 2004), 1002), (size(3840, 2160), 1920)],
        ),
        "tiny-limit-minimum-even-size.json": canvas_fixture(
            "Tiny long-side limits floor both export dimensions at the minimum even size of 2.",
            [(size(1000, 500), 1)],
        ),
    }
    assert_equal(fixtures["ultrawide-3440-limit-1920.json"]["cases"][0]["expected"], size(1920, 804), "ultrawide export")
    assert_equal(fixtures["natural-orientations.json"]["cases"][3]["expected"], size(562, 1000), "portrait rounds down export")
    assert_equal(fixtures["exact-ties-power-of-two-scale.json"]["cases"][0]["expected"], size(1002, 502), "s=0.5 tie")
    assert_equal(fixtures["exact-ties-power-of-two-scale.json"]["cases"][1]["expected"], size(1002, 502), "s=0.25 tie")
    assert_equal(fixtures["exact-ties-power-of-two-scale.json"]["cases"][2]["expected"], size(1920, 1080), "s=0.5 no tie")
    assert_equal(fixtures["tiny-limit-minimum-even-size.json"]["cases"][0]["expected"], size(2, 2), "tiny limit")
    return fixtures


def parse_path(path):
    parts = []
    for piece in path.split("."):
        while "[" in piece:
            before, rest = piece.split("[", 1)
            if before:
                parts.append(before)
            index, piece = rest.split("]", 1)
            parts.append(int(index))
            if piece.startswith("."):
                piece = piece[1:]
        if piece:
            parts.append(piece)
    return parts


def path_state(root, path):
    current = root
    for part in parse_path(path):
        if isinstance(part, int):
            if not isinstance(current, list) or part >= len(current):
                return False, None
            current = current[part]
        else:
            if not isinstance(current, dict) or part not in current:
                return False, None
            current = current[part]
    return True, current


def verify_claims(files):
    for rel, claims in CLAIMS.items():
        fixture = files[rel]
        serialized = json.loads(json_text(fixture))
        for path in claims["null"]:
            present, value = path_state(serialized, path)
            if not present or value is not None:
                raise AssertionError(f"{rel}: claimed null path {path} is {value!r} (present={present})")
        for path in claims["omitted"]:
            present, value = path_state(serialized, path)
            if present:
                raise AssertionError(f"{rel}: claimed omitted path {path} is present with {value!r}")
        for path, expected in claims["unknown"].items():
            present, value = path_state(serialized, path)
            if not present or value != expected:
                raise AssertionError(f"{rel}: claimed unknown path {path} expected {expected!r}, got {value!r}")


def generated_files():
    CLAIMS.clear()
    files = {}
    for filename, fixture in generate_canvases().items():
        files[Path("canvas") / filename] = fixture
    for filename, fixture in generate_layouts().items():
        files[Path("layout") / filename] = fixture
    for filename, fixture in generate_timemaps().items():
        files[Path("timemap") / filename] = fixture
    verify_claims(files)
    return files


def json_text(value):
    return json.dumps(value, indent=2, ensure_ascii=False) + "\n"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json_text(value), encoding="utf-8", newline="\n")


def check_files(files):
    expected = {FIXTURES / rel for rel in files}
    actual = set()
    for directory in [CANVAS_DIR, LAYOUT_DIR, TIMEMAP_DIR]:
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
    for directory in [CANVAS_DIR, LAYOUT_DIR, TIMEMAP_DIR]:
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
