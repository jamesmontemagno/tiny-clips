#!/usr/bin/env python3
"""Generate Tiny Clips Studio shared golden fixtures.

This is a stdlib-only reference implementation of docs/studio-project-format.md
sections 5 to 8, including scene transitions (section 6.9) and speed in the time map (section 7).
It intentionally does not import platform code.
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
AUTOZOOM_DIR = FIXTURES / "autozoom"
FIXTURE_DIRS = [CANVAS_DIR, LAYOUT_DIR, TIMEMAP_DIR, AUTOZOOM_DIR]
STAMP = "2026-10-02T22:41:00Z"
EPS = 1e-9
CLAIMS = {}

VALID_ASPECTS = {"auto", "square", "landscape4x3", "landscape16x9", "portrait3x4", "portrait9x16"}
VALID_LAYOUTS = {"screen", "bubble", "sideBySide", "camera"}
VALID_SHAPES = {"circle", "roundedRectangle", "squircle", "rectangle"}
VALID_ANCHORS = {"topLeft", "topRight", "bottomLeft", "bottomRight"}
VALID_CAMERA_SIDES = {"leading", "trailing"}
VALID_FOCUS_MODES = {"point", "cursor"}
VALID_ZOOM_ORIGINS = {"manual", "auto"}
VALID_TRANSITIONS = {"cut", "morph"}

# Section 8: zoom suggestions.
SUGGEST_SCALE = 2
SUGGEST_LEAD = 0.6
SUGGEST_HOLD = 1.5
SUGGEST_JOIN = 4
SUGGEST_INSET = 0.15
SUGGEST_SHORTEST = 0.3
SUGGEST_EASE = 0.5

# Section 7: speed.
SPEED_SLOWEST = 0.25
SPEED_FASTEST = 8


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
DEFAULT_ZOOM_FOCUS = {"mode": "point", "x": 0.5, "y": 0.5}
DEFAULT_ZOOM = {
    "start": 0,
    "end": 0,
    "scale": 2,
    "focus": DEFAULT_ZOOM_FOCUS,
    "easeIn": 0.5,
    "easeOut": 0.5,
    "origin": "manual",
}
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
    scene["transition"]["kind"] = enum(scene["transition"].get("kind"), VALID_TRANSITIONS, "cut")
    return scene


def normalize_edits(value):
    return deep_merge(DEFAULT_EDITS, value)


def normalize_zoom(value):
    z = deep_merge(DEFAULT_ZOOM, value)
    z["focus"]["mode"] = enum(z["focus"].get("mode"), VALID_FOCUS_MODES, "point")
    z["origin"] = enum(z.get("origin"), VALID_ZOOM_ORIGINS, "manual")
    return z


def zoom(start, end, **overrides):
    z = copy.deepcopy(DEFAULT_ZOOM)
    z["start"] = start
    z["end"] = end
    for key, value in overrides.items():
        if isinstance(value, dict) and isinstance(z.get(key), dict):
            z[key] = deep_merge(z[key], value)
        else:
            z[key] = value
    return z


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
    zooms=None,
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
        "zooms": zooms if zooms is not None else [],
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


def screen_base_rect(p):
    style = get_screen_style(p)
    return style.get("crop") if valid_crop(style.get("crop"), "screen crop") else rect(0, 0, 1, 1)


def normalize_zooms(raw):
    prepared = []
    for item in raw or []:
        if item is None:
            continue
        z = normalize_zoom(item)
        z["start"] = max(0, z["start"])
        if le(z["end"], z["start"], "zoom normalization: end <= start"):
            continue
        prepared.append(z)
    prepared.sort(key=lambda z: z["start"])
    kept = []
    for z in prepared:
        if kept and z["start"] == kept[-1]["start"]:
            BOUNDARIES.exact.add("zoom normalization: exact duplicate start")
            kept[-1] = z
        else:
            kept.append(z)
    for index in range(len(kept) - 1):
        if lt(kept[index + 1]["start"], kept[index]["end"], "zoom normalization: next starts before previous ends"):
            kept[index]["end"] = kept[index + 1]["start"]
    return kept


def cursor_samples(events):
    raw = [s for s in ((events or {}).get("cursor") or []) if s is not None]
    raw.sort(key=lambda s: s.get("t", 0))
    return [(s.get("t", 0), clamp(s.get("x", 0), 0, 1), clamp(s.get("y", 0), 0, 1)) for s in raw]


def cursor_focus(samples, t):
    a = t - 0.5
    b = t + 0.5
    fx = 0.0
    fy = 0.0
    count = len(samples)
    for index, (sample_t, x, y) in enumerate(samples):
        start = -math.inf if index == 0 else sample_t
        until = math.inf if index == count - 1 else samples[index + 1][0]
        length = max(0.0, min(b, until) - max(a, start))
        fx += x * length
        fy += y * length
    return fx, fy


def zoom_focus(z, samples, t):
    focus = z["focus"]
    if focus["mode"] == "cursor" and samples:
        return cursor_focus(samples, t)
    return clamp(focus["x"], 0, 1), clamp(focus["y"], 0, 1)


def held_window(base, scale, fx, fy):
    s = clamp(scale, 1, 5)
    w = base["width"] / s
    h = base["height"] / s
    x = max(base["x"], min(fx - w / 2, base["x"] + base["width"] - w))
    y = max(base["y"], min(fy - h / 2, base["y"] + base["height"] - h))
    return rect(x, y, w, h)


def lerp_rect(a, b, k):
    return rect(*(a[key] + (b[key] - a[key]) * k for key in ("x", "y", "width", "height")))


def ease(u):
    return u * u * (3 - 2 * u)


def zoom_window(p, events, t):
    base = screen_base_rect(p)
    zooms = normalize_zooms(p.get("zooms"))
    active = None
    for index, z in enumerate(zooms):
        if ge(t, z["start"], "zoom selection: t >= start") and lt(t, z["end"], "zoom selection: t < end"):
            active = index
            break
    if active is None:
        return base

    z = zooms[active]
    samples = cursor_samples(events)
    chained_to_previous = active > 0 and zooms[active - 1]["end"] == z["start"]
    next_is_chained = active + 1 < len(zooms) and zooms[active + 1]["start"] == z["end"]
    if chained_to_previous or next_is_chained:
        BOUNDARIES.exact.add("zoom chaining: start equals previous end")
    ease_in = clamp(z["easeIn"], 0, 3)
    ease_out = 0 if next_is_chained else clamp(z["easeOut"], 0, 3)
    duration = z["end"] - z["start"]
    if gt(ease_in + ease_out, duration, "zoom easing: in + out > duration"):
        factor = duration / (ease_in + ease_out)
        ease_in *= factor
        ease_out *= factor

    held = held_window(base, z["scale"], *zoom_focus(z, samples, t))
    if t < z["start"] + ease_in:
        if chained_to_previous:
            previous = zooms[active - 1]
            origin = held_window(base, previous["scale"], *zoom_focus(previous, samples, t))
        else:
            origin = base
        return lerp_rect(origin, held, ease((t - z["start"]) / ease_in))
    if t > z["end"] - ease_out:
        return lerp_rect(base, held, ease((z["end"] - t) / ease_out))
    return held


def suggest_zooms(p, events):
    base = screen_base_rect(p)
    duration = p["sources"]["screen"]["duration"]
    raw = [c for c in ((events or {}).get("clicks") or []) if c is not None]
    raw.sort(key=lambda c: c.get("t", 0))
    clicks = []
    for c in raw:
        t, x, y = c.get("t", 0), c.get("x", 0), c.get("y", 0)
        if not (ge(t, 0, "suggest: click t >= 0") and le(t, duration, "suggest: click t <= duration")):
            continue
        inside_base = (
            ge(x, base["x"], "suggest: click x >= base left")
            and le(x, base["x"] + base["width"], "suggest: click x <= base right")
            and ge(y, base["y"], "suggest: click y >= base top")
            and le(y, base["y"] + base["height"], "suggest: click y <= base bottom")
        )
        if inside_base:
            clicks.append((t, x, y))

    groups = []
    for t, x, y in clicks:
        g = groups[-1] if groups else None
        if g is not None and le(t - g["last"], SUGGEST_JOIN, "suggest: click within join of the group"):
            w = held_window(base, SUGGEST_SCALE, g["x"], g["y"])
            inner = rect(
                w["x"] + SUGGEST_INSET * w["width"],
                w["y"] + SUGGEST_INSET * w["height"],
                (1 - 2 * SUGGEST_INSET) * w["width"],
                (1 - 2 * SUGGEST_INSET) * w["height"],
            )
            same_place = (
                ge(x, inner["x"], "suggest: click x >= inner left")
                and le(x, inner["x"] + inner["width"], "suggest: click x <= inner right")
                and ge(y, inner["y"], "suggest: click y >= inner top")
                and le(y, inner["y"] + inner["height"], "suggest: click y <= inner bottom")
            )
            if same_place:
                g["last"] = t
            else:
                g["end"] = max(t - SUGGEST_LEAD, (g["last"] + t) / 2)
                groups.append({"start": g["end"], "x": x, "y": y, "last": t, "end": None})
        else:
            groups.append({"start": max(0, t - SUGGEST_LEAD), "x": x, "y": y, "last": t, "end": None})

    suggestions = []
    for g in groups:
        end = g["end"] if g["end"] is not None else min(duration, g["last"] + SUGGEST_HOLD)
        if ge(end - g["start"], SUGGEST_SHORTEST, "suggest: group is long enough"):
            suggestions.append(
                {
                    "start": g["start"],
                    "end": end,
                    "scale": SUGGEST_SCALE,
                    "focus": {"mode": "point", "x": g["x"], "y": g["y"]},
                    "easeIn": SUGGEST_EASE,
                    "easeOut": SUGGEST_EASE,
                    "origin": "auto",
                }
            )

    manual = normalize_zooms(
        [
            z
            for z in (p.get("zooms") or [])
            if z is not None and enum(z.get("origin"), VALID_ZOOM_ORIGINS, "manual") != "auto"
        ]
    )
    return [
        s
        for s in suggestions
        if not any(
            lt(s["start"], m["end"], "suggest: suggestion starts before manual ends")
            and lt(m["start"], s["end"], "suggest: manual starts before suggestion ends")
            for m in manual
        )
    ]


def scene_frame(p, sc, t, canvas_size, events=None):
    """Sections 6.2 to 6.5: the layout of one scene at rest, at time t."""
    w, h = canvas_size["width"], canvas_size["height"]
    m = min(w, h)
    canvas = get_canvas(p)
    screen_style = get_screen_style(p)
    has_camera = get_sources_camera(p) is not None
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
        screen_out = {
            "rect": screen_rect,
            "source": zoom_window(p, events, t),
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

    if screen_out is not None:
        screen_out["opacity"] = 1
    if camera_out is not None:
        camera_out["opacity"] = 1
    return layout, screen_out, camera_out


def transition_length(scenes, index):
    """Section 6.9: how long the layers take to move into scene `index`. 0 for a cut and for the first scene."""
    if index < 1:
        return 0
    transition = scenes[index]["transition"]
    if transition["kind"] != "morph":
        return 0
    d = clamp(transition["duration"], 0, 2)
    if index + 1 < len(scenes):
        d = min(d, scenes[index + 1]["start"] - scenes[index]["start"])
    return d


def lerp(a, b, k):
    return a + (b - a) * k


def morph_radius(camera):
    """Section 6.9: the corner radius a camera has as one end of a move between two shapes."""
    if camera["shape"] == "squircle":
        return 0.22 * min(camera["rect"]["width"], camera["rect"]["height"])
    return camera["cornerRadius"]


def move_layer(origin, target, k):
    """Section 6.9: one layer on its way from the scene before (origin) to this scene (target)."""
    if origin is None and target is None:
        return None
    if origin is None:
        return {**target, "opacity": k}
    if target is None:
        return {**origin, "opacity": 1 - k}
    moved = dict(target)
    moved["rect"] = lerp_rect(origin["rect"], target["rect"], k)
    moved["cornerRadius"] = lerp(origin["cornerRadius"], target["cornerRadius"], k)
    moved["opacity"] = 1
    return moved


def resolve_layout(p, t, canvas_size, events=None):
    scenes = normalize_scenes(p)
    scene_index, sc = active_scene(p, t)
    layout, screen_out, camera_out = scene_frame(p, sc, t, canvas_size, events)

    d = transition_length(scenes, scene_index)
    if d > 0 and t >= 0 and lt(t, sc["start"] + d, "scene transition: t < start + duration"):
        if t == sc["start"]:
            BOUNDARIES.exact.add("scene transition: exact start")
        k = ease((t - sc["start"]) / d)
        _, origin_screen, origin_camera = scene_frame(p, scenes[scene_index - 1], t, canvas_size, events)
        both_cameras = origin_camera is not None and camera_out is not None
        screen_out = move_layer(origin_screen, screen_out, k)
        moved_camera = move_layer(origin_camera, camera_out, k)
        if both_cameras:
            moved_camera["source"] = camera_source_rect(p, moved_camera["rect"], "moving camera source")
            if origin_camera["shape"] != camera_out["shape"]:
                moved_camera["shape"] = "roundedRectangle"
                moved_camera["cornerRadius"] = lerp(morph_radius(origin_camera), morph_radius(camera_out), k)
        camera_out = moved_camera

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
    pieces = time_pieces(kept, normalize_speed(edits))
    output_duration = 0
    for piece in pieces:
        output_duration = output_duration + at_rate(piece["end"] - piece["start"], piece["rate"])
    return start, [{"start": s, "end": e} for s, e in kept], pieces, output_duration


def at_rate(source_seconds, rate):
    """How long a stretch of the source lasts in the output. Dividing by 1 changes no number, and
    is left out so that a whole number stays written as one."""
    return source_seconds if rate == 1 else source_seconds / rate


def from_rate(output_seconds, rate):
    """How much of the source a stretch of output covers."""
    return output_seconds if rate == 1 else output_seconds * rate


def normalize_speed(edits):
    """The speed entries that count, in order and clear of each other, as [start, end, rate].

    The trim and the cuts play no part here, so the rate at a source time is the same wherever they are.
    """
    entries = []
    for entry in edits.get("speed", []):
        rate = entry.get("rate", 1)
        if isinstance(rate, bool) or not isinstance(rate, (int, float)) or not math.isfinite(rate) or rate <= 0:
            continue
        if rate < SPEED_SLOWEST or rate > SPEED_FASTEST:
            BOUNDARIES.exact.add("time map: a rate outside 0.25 to 8 is clamped")
        rate = clamp(rate, SPEED_SLOWEST, SPEED_FASTEST)
        s = entry.get("start", 0)
        e = entry.get("end", 0)
        if e > s and rate != 1:
            entries.append([s, e, rate])
    # sorted() is stable, so the file's order decides between entries with the same start and end.
    entries = sorted(entries, key=lambda item: (item[0], item[1]))
    result = []
    for s, e, rate in entries:
        if result and s < result[-1][1]:
            BOUNDARIES.exact.add("time map: a speed entry that overlaps the one before starts where that ends")
            s = result[-1][1]
        if e > s:
            result.append([s, e, rate])
    return result


def time_pieces(kept, speed):
    """The kept segments divided where the rate changes inside them."""
    pieces = []
    for s, e in kept:
        points = {s, e}
        for a, b, _ in speed:
            for point in (a, b):
                if s < point < e:
                    points.add(point)
        points = sorted(points)
        first = len(pieces)
        for a, b in zip(points, points[1:]):
            rate = 1
            for x, y, r in speed:
                if x <= a < y:
                    rate = r
                    break
            if len(pieces) > first and pieces[-1]["rate"] == rate:
                BOUNDARIES.exact.add("time map: neighbouring parts with the same rate are one piece")
                pieces[-1]["end"] = b
            else:
                pieces.append({"start": a, "end": b, "rate": rate})
    return pieces


def source_to_output(t, pieces, output_duration):
    if not pieces:
        return 0
    cumulative = 0
    for i, piece in enumerate(pieces):
        s, e, rate = piece["start"], piece["end"], piece["rate"]
        if t == s:
            BOUNDARIES.exact.add("time map: source query at segment start")
        if t == e:
            BOUNDARIES.exact.add("time map: source query at segment end")
        if t < s:
            return cumulative
        if s <= t < e:
            return cumulative + at_rate(t - s, rate)
        cumulative = cumulative + at_rate(e - s, rate)
        if i + 1 < len(pieces) and e <= t < pieces[i + 1]["start"]:
            return cumulative
    return output_duration


def output_to_source(u, start, pieces, output_duration):
    if not pieces:
        return start
    u = clamp(u, 0, output_duration)
    if u == output_duration:
        BOUNDARIES.exact.add("time map: output query at outputDuration")
    cumulative = 0
    for piece in pieces:
        s, e, rate = piece["start"], piece["end"], piece["rate"]
        length = at_rate(e - s, rate)
        if u == cumulative:
            BOUNDARIES.exact.add("time map: output query at segment start")
        if cumulative <= u < cumulative + length:
            return s + from_rate(u - cumulative, rate)
        cumulative = cumulative + length
    return pieces[-1]["end"]


def layout_fixture(description, p, cases, events=None):
    natural = natural_canvas(p)
    fixture = {"description": description, "project": p}
    if events is not None:
        fixture["events"] = events
    fixture["naturalCanvas"] = natural
    fixture["cases"] = [
        {"time": t, "canvas": canvas, "expected": resolve_layout(p, t, canvas, events)} for t, canvas in cases
    ]
    return fixture


def autozoom_fixture(description, p, events):
    return {
        "description": description,
        "project": p,
        "events": events,
        "expected": {"zooms": suggest_zooms(p, events)},
    }


def events_file(*, clicks=None, cursor=None, kind="display", capture_size=(1920, 1080)):
    return {
        "schemaVersion": 1,
        "capture": {"width": capture_size[0], "height": capture_size[1], "scale": 1.0, "kind": kind},
        "clicks": clicks or [],
        "cursor": cursor or [],
        "cameraCorners": [],
        "markers": [],
    }


def click(t, x, y, button="left"):
    return {"t": t, "x": x, "y": y, "button": button}


def cursor_sample(t, x, y):
    return {"t": t, "x": x, "y": y}


def add_fixture(fixtures, filename, fixture, *, folder="layout", null=None, omitted=None, unknown=None):
    fixtures[filename] = fixture
    if null or omitted or unknown:
        CLAIMS[Path(folder) / filename] = {
            "null": null or [],
            "omitted": omitted or [],
            "unknown": unknown or {},
        }


def timemap_fixture(description, duration, edits, source_queries, output_queries):
    start, segments, pieces, output_duration = time_map(duration, edits)
    return {
        "description": description,
        "sourceDuration": duration,
        "edits": edits,
        "expected": {
            "outputDuration": output_duration,
            "segments": segments,
            "pieces": pieces,
            "sourceToOutput": [
                {"source": t, "output": source_to_output(t, pieces, output_duration)} for t in source_queries
            ],
            "outputToSource": [
                {"output": u, "source": output_to_source(u, start, pieces, output_duration)}
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


def assert_rect_close(actual, expected, label):
    for key, value in zip(("x", "y", "width", "height"), expected):
        assert_close(actual[key], value, f"{label}.{key}")


def zoom_source(fixture, case_index):
    return fixture["cases"][case_index]["expected"]["screen"]["source"]


def assert_suggestions(fixture, expected, label):
    zooms = fixture["expected"]["zooms"]
    assert_equal(len(zooms), len(expected), f"{label}: suggestion count")
    for index, (start, end, x, y) in enumerate(expected):
        assert_close(zooms[index]["start"], start, f"{label}[{index}].start")
        assert_close(zooms[index]["end"], end, f"{label}[{index}].end")
        assert_close(zooms[index]["focus"]["x"], x, f"{label}[{index}].focus.x")
        assert_close(zooms[index]["focus"]["y"], y, f"{label}[{index}].focus.y")
        assert_equal(zooms[index]["origin"], "auto", f"{label}[{index}].origin")


def generate_zoom_layouts():
    fixtures = {}
    full = size(1920, 1080)

    def screen_project(number, name, zooms, **kwargs):
        return project(
            f"00000000-0000-4000-8000-{number:012d}",
            name=name,
            has_camera=False,
            scenes=[scene(layout="screen")],
            zooms=zooms,
            **kwargs,
        )

    p = screen_project(48, "Zoom on the center", [zoom(2, 8)])
    f = layout_fixture(
        "One zoom on the center at scale 2: before it, the ease in, the held window, the ease out, the instant it ends, and after it.",
        p,
        [(t, full) for t in (0, 2, 2.25, 2.5, 5, 7.5, 7.75, 8, 12)],
    )
    assert_rect_close(zoom_source(f, 0), (0, 0, 1, 1), "zoom center before")
    assert_rect_close(zoom_source(f, 1), (0, 0, 1, 1), "zoom center at start")
    assert_rect_close(zoom_source(f, 2), (0.125, 0.125, 0.75, 0.75), "zoom center half way in")
    assert_rect_close(zoom_source(f, 3), (0.25, 0.25, 0.5, 0.5), "zoom center held from start + in")
    assert_rect_close(zoom_source(f, 5), (0.25, 0.25, 0.5, 0.5), "zoom center held until end - out")
    assert_rect_close(zoom_source(f, 6), (0.125, 0.125, 0.75, 0.75), "zoom center half way out")
    assert_rect_close(zoom_source(f, 7), (0, 0, 1, 1), "zoom center at end")
    assert_equal(f["cases"][4]["expected"]["screen"]["rect"], f["cases"][0]["expected"]["screen"]["rect"], "zoom leaves the card alone")
    fixtures["zoom-point-center.json"] = f

    p = screen_project(
        49,
        "Zoom pushed back inside",
        [
            zoom(1, 4, scale=3, focus={"x": 0.05, "y": 0.9}, easeIn=0, easeOut=0),
            zoom(6, 9, focus={"x": -0.2, "y": 1.4}, easeIn=0, easeOut=0),
            zoom(11, 14, scale=4, focus={"x": 1, "y": 0}, easeIn=0, easeOut=0),
        ],
    )
    f = layout_fixture(
        "Windows that would stick out are pushed back inside the frame, a focus outside 0 to 1 is clamped, and a zoom without eases cuts in and out.",
        p,
        [(t, full) for t in (1, 2, 4, 7, 12, 13.5)],
    )
    assert_rect_close(zoom_source(f, 0), (0, 2 / 3, 1 / 3, 1 / 3), "zoom cut in at start")
    assert_rect_close(zoom_source(f, 2), (0, 0, 1, 1), "zoom cut out at end")
    assert_rect_close(zoom_source(f, 3), (0, 0.5, 0.5, 0.5), "zoom focus clamped")
    assert_rect_close(zoom_source(f, 4), (0.75, 0, 0.25, 0.25), "zoom corner focus")
    fixtures["zoom-point-pushed-back-inside.json"] = f

    p = screen_project(
        50,
        "Zoom inside a crop",
        [zoom(1, 5, focus={"x": 0.4, "y": 0.45}), zoom(7, 11, focus={"x": 0.95, "y": 0.05})],
        screen_size=(2000, 1000),
        screen_style={"crop": rect(0.1, 0.2, 0.6, 0.5)},
    )
    f = layout_fixture(
        "A zoom works inside the screen crop: scale 2 shows half of the cropped view, a focus outside the crop is pushed to its edge, and the natural canvas is the crop's.",
        p,
        [(t, size(1200, 500)) for t in (0, 1.25, 3, 6, 9)],
    )
    assert_equal(f["naturalCanvas"], size(1200, 500), "zoom crop natural canvas")
    assert_rect_close(zoom_source(f, 0), (0.1, 0.2, 0.6, 0.5), "zoom crop base")
    assert_rect_close(zoom_source(f, 2), (0.25, 0.325, 0.3, 0.25), "zoom crop held")
    assert_rect_close(zoom_source(f, 1), (0.175, 0.2625, 0.45, 0.375), "zoom crop half way in")
    assert_rect_close(zoom_source(f, 4), (0.4, 0.2, 0.3, 0.25), "zoom crop focus outside")
    fixtures["zoom-inside-screen-crop.json"] = f

    p = screen_project(
        51,
        "Zoom clamps",
        [
            zoom(1, 4, scale=0.5),
            zoom(5, 9, scale=9, easeIn=-1),
            zoom(10, 18, easeIn=10, easeOut=0.25),
        ],
    )
    f = layout_fixture(
        "Scale clamps to 1 through 5 and the eases to 0 through 3 when they are used: a scale under 1 shows no zoom, a negative ease in is a cut, and an ease in of 10 takes 3 seconds.",
        p,
        [(t, full) for t in (2, 5, 7, 11.5, 13, 17.75, 17.875)],
    )
    assert_rect_close(zoom_source(f, 0), (0, 0, 1, 1), "zoom scale under 1")
    assert_rect_close(zoom_source(f, 1), (0.4, 0.4, 0.2, 0.2), "zoom scale over 5 cut in")
    assert_rect_close(zoom_source(f, 3), (0.125, 0.125, 0.75, 0.75), "zoom ease in clamped to 3")
    assert_rect_close(zoom_source(f, 4), (0.25, 0.25, 0.5, 0.5), "zoom held after 3 seconds")
    assert_rect_close(zoom_source(f, 5), (0.25, 0.25, 0.5, 0.5), "zoom held until end - out")
    assert_rect_close(zoom_source(f, 6), (0.125, 0.125, 0.75, 0.75), "zoom short ease out")
    assert_equal(p["zooms"][1]["scale"], 9, "zoom scale is stored unclamped")
    fixtures["zoom-scale-and-ease-clamps.json"] = f

    p = screen_project(
        52,
        "Zoom shorter than its eases",
        [zoom(2, 2.75), zoom(5, 6, easeIn=3, easeOut=1), zoom(8, 9)],
    )
    f = layout_fixture(
        "Eases that add up to more than the zoom are shortened in proportion, and eases that add up to exactly the zoom are left alone.",
        p,
        [(t, full) for t in (2.1875, 2.375, 2.5625, 5.375, 5.75, 5.875, 8.25, 8.5, 8.75)],
    )
    for index in (0, 2, 3, 5, 6, 8):
        assert_rect_close(zoom_source(f, index), (0.125, 0.125, 0.75, 0.75), f"zoom short ease case {index}")
    for index in (1, 4, 7):
        assert_rect_close(zoom_source(f, index), (0.25, 0.25, 0.5, 0.5), f"zoom short held case {index}")
    fixtures["zoom-eases-longer-than-zoom.json"] = f

    p = screen_project(
        53,
        "Zoom normalization",
        [
            zoom(12, 16, scale=3),
            zoom(4, 9, focus={"x": 0.25, "y": 0.25}),
            None,
            zoom(7, 6),
            zoom(-2, 3, scale=4, easeIn=0, easeOut=0),
            zoom(8, 13, focus={"x": 0.75, "y": 0.75}),
            zoom(12, 15, scale=5),
        ],
    )
    f = layout_fixture(
        "Zooms out of order, one that ends before it starts, a negative start, a null entry, two sharing a start, and overlaps: the later zoom takes over where it starts, which chains it to the one before.",
        p,
        [(t, full) for t in (1, 3.5, 6, 7.9, 8, 8.25, 10, 12, 12.25, 14, 14.75, 15.5)],
    )
    assert_rect_close(zoom_source(f, 0), (0.375, 0.375, 0.25, 0.25), "zoom negative start becomes 0")
    assert_rect_close(zoom_source(f, 1), (0, 0, 1, 1), "zoom gap")
    assert_rect_close(zoom_source(f, 3), (0, 0, 0.5, 0.5), "zoom cut short has no ease out")
    assert_rect_close(zoom_source(f, 4), (0, 0, 0.5, 0.5), "zoom chained starts from the one before")
    assert_rect_close(zoom_source(f, 5), (0.25, 0.25, 0.5, 0.5), "zoom chained half way")
    assert_rect_close(zoom_source(f, 6), (0.5, 0.5, 0.5, 0.5), "zoom second held")
    assert_rect_close(zoom_source(f, 8), (0.45, 0.45, 0.35, 0.35), "zoom last of a shared start wins")
    assert_rect_close(zoom_source(f, 10), (0.2, 0.2, 0.6, 0.6), "zoom last eases out")
    add_fixture(fixtures, "zoom-normalization.json", f, null=["project.zooms[2]"])

    p = screen_project(
        54,
        "Chained zooms",
        [
            zoom(2, 5, focus={"x": 0.25, "y": 0.25}),
            zoom(5, 9, scale=3, focus={"x": 0.75, "y": 0.7}),
            zoom(9.5, 12),
        ],
    )
    f = layout_fixture(
        "A zoom that starts exactly where the one before ends is chained to it: the first has no ease out and the window moves straight to the second. A zoom after a gap starts from the full view.",
        p,
        [(t, full) for t in (4.75, 5, 5.25, 5.5, 7, 8.75, 9.25, 9.75)],
    )
    held_second = (0.75 - 1 / 6, 0.7 - 1 / 6, 1 / 3, 1 / 3)
    assert_rect_close(zoom_source(f, 0), (0, 0, 0.5, 0.5), "zoom chain first stays held")
    assert_rect_close(zoom_source(f, 1), (0, 0, 0.5, 0.5), "zoom chain second starts on the first")
    assert_rect_close(zoom_source(f, 2), tuple((a + b) / 2 for a, b in zip((0, 0, 0.5, 0.5), held_second)), "zoom chain half way")
    assert_rect_close(zoom_source(f, 3), held_second, "zoom chain second held")
    assert_rect_close(zoom_source(f, 5), tuple((a + b) / 2 for a, b in zip((0, 0, 1, 1), held_second)), "zoom chain second eases out")
    assert_rect_close(zoom_source(f, 6), (0, 0, 1, 1), "zoom chain gap")
    assert_rect_close(zoom_source(f, 7), (0.125, 0.125, 0.75, 0.75), "zoom after a gap eases in from the full view")
    fixtures["zoom-chained.json"] = f

    follow = events_file(
        cursor=[
            cursor_sample(2.0, 0.2, 0.2),
            cursor_sample(3.0, 0.8, 0.2),
            cursor_sample(6.0, 0.8, 0.9),
            cursor_sample(9.0, 1.6, -0.3),
        ]
    )
    p = screen_project(55, "Zoom that follows the pointer", [zoom(1, 12, focus={"mode": "cursor"})])
    f = layout_fixture(
        "A zoom in cursor mode centers on the pointer's mean position over the second around each time: before the first sample, across a move, at rest, and with a sample outside the frame.",
        p,
        [(t, full) for t in (1.25, 1.75, 3, 3.25, 4, 6.25, 10, 11.5)],
        events=follow,
    )
    assert_rect_close(zoom_source(f, 0), (0, 0, 0.75, 0.75), "zoom cursor easing in")
    assert_rect_close(zoom_source(f, 1), (0, 0, 0.5, 0.5), "zoom cursor before the first sample")
    assert_rect_close(zoom_source(f, 2), (0.25, 0, 0.5, 0.5), "zoom cursor half way through a move")
    assert_rect_close(zoom_source(f, 3), (0.4, 0, 0.5, 0.5), "zoom cursor three quarters through a move")
    assert_rect_close(zoom_source(f, 4), (0.5, 0, 0.5, 0.5), "zoom cursor at rest")
    assert_rect_close(zoom_source(f, 5), (0.5, 0.475, 0.5, 0.5), "zoom cursor vertical move")
    assert_rect_close(zoom_source(f, 6), (0.5, 0, 0.5, 0.5), "zoom cursor sample outside the frame")
    assert_rect_close(zoom_source(f, 7), (0.5, 0, 0.5, 0.5), "zoom cursor after the last sample")
    fixtures["zoom-follows-cursor.json"] = f

    shuffled = events_file(
        cursor=[
            cursor_sample(6.0, 0.8, 0.9),
            cursor_sample(2.0, 0.2, 0.2),
            None,
            cursor_sample(9.0, 1.6, -0.3),
            cursor_sample(3.0, 0.8, 0.2),
        ]
    )
    p = screen_project(56, "Zoom with unsorted cursor samples", [zoom(1, 12, focus={"mode": "cursor"})])
    g = layout_fixture(
        "Cursor samples out of order, with a null entry among them, give the same windows as the sorted list.",
        p,
        [(t, full) for t in (1.75, 3.25, 6.25, 10)],
        events=shuffled,
    )
    for index, same in enumerate((1, 3, 5, 6)):
        assert_equal(zoom_source(g, index), zoom_source(f, same), f"zoom unsorted cursor case {index}")
    add_fixture(fixtures, "zoom-cursor-samples-unsorted.json", g, null=["events.cursor[2]"])

    p = screen_project(57, "Cursor zoom without samples", [zoom(1, 6, focus={"mode": "cursor", "x": 0.3, "y": 0.6}, easeIn=0, easeOut=0)])
    f = layout_fixture(
        "A zoom in cursor mode on a recording with no cursor samples looks at its own focus point.",
        p,
        [(3, full)],
        events=events_file(kind="window"),
    )
    assert_rect_close(zoom_source(f, 0), (0.05, 0.35, 0.5, 0.5), "zoom cursor without samples")
    fixtures["zoom-cursor-without-samples.json"] = f

    p = screen_project(58, "Cursor zoom without events", [zoom(1, 6, focus={"mode": "cursor", "x": 0.3, "y": 0.6}, easeIn=0, easeOut=0)])
    p["sources"]["events"] = None
    f = layout_fixture(
        "A zoom in cursor mode on a project with no events file looks at its own focus point.",
        p,
        [(3, full)],
    )
    assert_rect_close(zoom_source(f, 0), (0.05, 0.35, 0.5, 0.5), "zoom cursor without events")
    add_fixture(fixtures, "zoom-cursor-without-events.json", f, null=["project.sources.events"], omitted=["events"])

    p = project(
        "00000000-0000-4000-8000-000000000059",
        name="Zoom in every layout",
        scenes=[
            scene(start=0, layout="bubble"),
            scene(start=5, layout="sideBySide"),
            scene(start=10, layout="camera"),
            scene(start=15, layout="screen"),
        ],
        zooms=[zoom(1, 19, easeIn=0, easeOut=0)],
    )
    f = layout_fixture(
        "A zoom changes only the screen's source rectangle: the screen card and the camera are where they are without it, and the camera layout has no screen to zoom.",
        p,
        [(t, full) for t in (0.5, 2, 7, 12, 17)],
    )
    assert_rect_close(zoom_source(f, 1), (0.25, 0.25, 0.5, 0.5), "zoom in bubble layout")
    assert_rect_close(zoom_source(f, 2), (0.25, 0.25, 0.5, 0.5), "zoom in side by side layout")
    assert_equal(f["cases"][3]["expected"]["screen"], None, "zoom in camera layout has no screen")
    assert_rect_close(zoom_source(f, 4), (0.25, 0.25, 0.5, 0.5), "zoom in screen layout")
    assert_equal(f["cases"][1]["expected"]["screen"]["rect"], f["cases"][0]["expected"]["screen"]["rect"], "zoom keeps the bubble layout's card")
    assert_equal(f["cases"][1]["expected"]["camera"]["rect"], f["cases"][0]["expected"]["camera"]["rect"], "zoom keeps the camera")
    assert_equal(f["cases"][1]["expected"]["camera"]["source"], f["cases"][0]["expected"]["camera"]["source"], "zoom keeps the camera source")
    fixtures["zoom-in-every-layout.json"] = f

    p = screen_project(
        60,
        "Zoom defaults",
        [
            {"start": 1, "end": 5},
            {"start": 6, "end": 9, "scale": None, "focus": None, "easeIn": None, "origin": "robot"},
            {"start": 10, "end": 13, "focus": {"mode": "magnet", "x": 0.9}, "futureField": 7},
            {"start": 14},
        ],
    )
    f = layout_fixture(
        "Zooms with members missing or null take the defaults (scale 2, the center, half-second eases), an unknown focus mode reads as point, an unknown member is kept, and a zoom without an end is dropped.",
        p,
        [(t, full) for t in (1.25, 3, 6.25, 7.5, 11.5, 14.5)],
    )
    assert_rect_close(zoom_source(f, 0), (0.125, 0.125, 0.75, 0.75), "zoom default ease in")
    assert_rect_close(zoom_source(f, 1), (0.25, 0.25, 0.5, 0.5), "zoom default scale and focus")
    assert_rect_close(zoom_source(f, 2), (0.125, 0.125, 0.75, 0.75), "zoom null ease in is the default")
    assert_rect_close(zoom_source(f, 3), (0.25, 0.25, 0.5, 0.5), "zoom null scale and focus are the defaults")
    assert_rect_close(zoom_source(f, 4), (0.5, 0.25, 0.5, 0.5), "zoom unknown focus mode is point")
    assert_rect_close(zoom_source(f, 5), (0, 0, 1, 1), "zoom without an end is dropped")
    add_fixture(
        fixtures,
        "zoom-defaults-and-tolerant-reading.json",
        f,
        null=["project.zooms[1].scale", "project.zooms[1].focus", "project.zooms[1].easeIn"],
        omitted=[
            "project.zooms[0].scale",
            "project.zooms[0].focus",
            "project.zooms[0].easeIn",
            "project.zooms[0].origin",
            "project.zooms[2].focus.y",
            "project.zooms[3].end",
        ],
        unknown={"project.zooms[2].futureField": 7},
    )

    return fixtures


def generate_scene_layouts():
    """Section 6.9: scenes entered with a morph. Canvas 1920x1080 unless a case says otherwise."""
    fixtures = {}
    full = size(1920, 1080)

    # Where the layers are at rest on that canvas with the default look, worked out by hand.
    screen_card = (115.2, 64.8, 1689.6, 950.4)
    bubble_bottom_right = (1628.4, 788.4, 259.2, 259.2)
    bubble_top_left = (32.4, 32.4, 259.2, 259.2)
    split_screen = (64.8, 191.7675, 1238.16, 696.465)
    split_camera = (1324.56, 191.7675, 530.64, 696.465)
    camera_card = (64.8, 64.8, 1790.4, 950.4)

    def scene_project(number, name, scenes, **kwargs):
        return project(f"00000000-0000-4000-8000-{number:012d}", name=name, scenes=scenes, **kwargs)

    def morph(duration=1):
        return {"kind": "morph", "duration": duration}

    def expected(fixture, index):
        return fixture["cases"][index]["expected"]

    def halfway(a, b):
        return tuple((x + y) / 2 for x, y in zip(a, b))

    def at_rest(frame):
        # A frame without the one value that follows the clock while a scene is at rest.
        still = copy.deepcopy(frame)
        if still["camera"] is not None:
            still["camera"].pop("sourceTime")
        return still

    p = scene_project(
        101,
        "Bubble to side by side",
        [scene(start=0, layout="bubble"), scene(start=4, layout="sideBySide", transition=morph())],
    )
    f = layout_fixture(
        "A round bubble becomes a side-by-side card over one second: just before, the instant the scene starts, a quarter, half and three quarters of the way, the instant it ends, and after.",
        p,
        [(t, full) for t in (3.9, 4, 4.25, 4.5, 4.75, 5, 6)],
    )
    assert_equal((expected(f, 0)["sceneIndex"], expected(f, 0)["layout"], expected(f, 0)["camera"]["shape"]), (0, "bubble", "circle"), "morph before")
    assert_equal((expected(f, 1)["sceneIndex"], expected(f, 1)["layout"]), (1, "sideBySide"), "morph at start names the new scene")
    assert_rect_close(expected(f, 1)["screen"]["rect"], screen_card, "morph at start screen")
    assert_rect_close(expected(f, 1)["camera"]["rect"], bubble_bottom_right, "morph at start camera")
    assert_equal(expected(f, 1)["camera"]["shape"], "roundedRectangle", "morph at start camera shape")
    assert_close(expected(f, 1)["camera"]["cornerRadius"], 129.6, "morph at start camera radius")
    assert_close(expected(f, 2)["camera"]["cornerRadius"], 112.725, "morph a quarter of the way camera radius")
    assert_rect_close(expected(f, 3)["screen"]["rect"], (90, 128.28375, 1463.88, 823.4325), "morph halfway screen")
    assert_rect_close(expected(f, 3)["camera"]["rect"], (1476.48, 490.08375, 394.92, 477.8325), "morph halfway camera")
    assert_close(expected(f, 3)["camera"]["cornerRadius"], 75.6, "morph halfway camera radius")
    assert_close(expected(f, 3)["screen"]["cornerRadius"], 21.6, "morph halfway screen radius")
    assert_close(expected(f, 3)["camera"]["source"]["width"], (394.92 / 477.8325) / (16 / 9), "morph halfway camera source fills its card")
    assert_equal((expected(f, 3)["screen"]["opacity"], expected(f, 3)["camera"]["opacity"]), (1, 1), "morph halfway opacity")
    assert_rect_close(expected(f, 5)["camera"]["rect"], split_camera, "morph at end camera")
    assert_rect_close(expected(f, 5)["screen"]["rect"], split_screen, "morph at end screen")
    assert_close(expected(f, 5)["camera"]["cornerRadius"], 21.6, "morph at end camera radius")
    assert_equal(at_rest(expected(f, 5)), at_rest(expected(f, 6)), "morph at end is the scene at rest")
    fixtures["scene-morph-bubble-to-side-by-side.json"] = f

    p = scene_project(
        102,
        "Bubble to camera",
        [scene(start=0, layout="bubble"), scene(start=4, layout="camera", transition=morph())],
    )
    f = layout_fixture(
        "Entering the camera layout: the bubble grows into the card while the screen, which the new scene does not have, fades out where it was.",
        p,
        [(t, full) for t in (4, 4.25, 4.5, 5)],
    )
    assert_equal(expected(f, 0)["layout"], "camera", "bubble to camera layout")
    assert_close(expected(f, 0)["screen"]["opacity"], 1, "bubble to camera screen at start")
    assert_close(expected(f, 1)["screen"]["opacity"], 0.84375, "bubble to camera screen a quarter of the way")
    assert_close(expected(f, 2)["screen"]["opacity"], 0.5, "bubble to camera screen halfway")
    assert_rect_close(expected(f, 2)["screen"]["rect"], screen_card, "bubble to camera screen stays")
    assert_rect_close(expected(f, 2)["camera"]["rect"], (846.6, 426.6, 1024.8, 604.8), "bubble to camera camera halfway")
    assert_close(expected(f, 2)["camera"]["cornerRadius"], 75.6, "bubble to camera radius halfway")
    assert_equal(expected(f, 2)["camera"]["opacity"], 1, "bubble to camera camera opacity")
    assert_equal(expected(f, 3)["screen"], None, "bubble to camera screen gone at the end")
    assert_rect_close(expected(f, 3)["camera"]["rect"], camera_card, "bubble to camera at rest")
    fixtures["scene-morph-bubble-to-camera.json"] = f

    p = scene_project(
        103,
        "Screen to bubble",
        [scene(start=0, layout="screen"), scene(start=2, layout="bubble", transition=morph(0.5))],
    )
    f = layout_fixture(
        "A camera the scene before did not have fades in where the new scene puts it, and the screen, which both have in the same place, stays.",
        p,
        [(t, full) for t in (1.5, 2, 2.125, 2.25, 2.5)],
    )
    assert_equal(expected(f, 0)["camera"], None, "screen to bubble before")
    assert_close(expected(f, 1)["camera"]["opacity"], 0, "screen to bubble camera at start")
    assert_close(expected(f, 2)["camera"]["opacity"], 0.15625, "screen to bubble camera a quarter of the way")
    assert_close(expected(f, 3)["camera"]["opacity"], 0.5, "screen to bubble camera halfway")
    assert_rect_close(expected(f, 3)["camera"]["rect"], bubble_bottom_right, "screen to bubble camera place")
    assert_equal(expected(f, 3)["camera"]["shape"], "circle", "screen to bubble camera shape")
    assert_equal(expected(f, 3)["screen"]["opacity"], 1, "screen to bubble screen opacity")
    assert_rect_close(expected(f, 3)["screen"]["rect"], screen_card, "screen to bubble screen stays")
    assert_equal(expected(f, 4)["camera"]["opacity"], 1, "screen to bubble at rest")
    fixtures["scene-morph-screen-to-bubble.json"] = f

    p = scene_project(
        104,
        "Camera to screen",
        [scene(start=0, layout="camera"), scene(start=3, layout="screen", transition=morph())],
    )
    f = layout_fixture(
        "Two scenes with no layer in common: the camera card fades out while the screen fades in.",
        p,
        [(t, full) for t in (3, 3.25, 3.5, 4)],
    )
    assert_close(expected(f, 1)["screen"]["opacity"], 0.15625, "camera to screen screen a quarter of the way")
    assert_close(expected(f, 1)["camera"]["opacity"], 0.84375, "camera to screen camera a quarter of the way")
    assert_close(expected(f, 2)["screen"]["opacity"], 0.5, "camera to screen screen halfway")
    assert_close(expected(f, 2)["camera"]["opacity"], 0.5, "camera to screen camera halfway")
    assert_rect_close(expected(f, 2)["camera"]["rect"], camera_card, "camera to screen camera stays")
    assert_rect_close(expected(f, 2)["screen"]["rect"], screen_card, "camera to screen screen place")
    assert_equal(expected(f, 2)["layout"], "screen", "camera to screen layout")
    assert_equal(expected(f, 3)["camera"], None, "camera to screen camera gone at the end")
    fixtures["scene-morph-camera-to-screen.json"] = f

    p = scene_project(
        105,
        "Bubble moves to another corner",
        [
            scene(start=0, layout="bubble", bubble={"anchor": "topLeft"}),
            scene(start=2, layout="bubble", bubble={"anchor": "bottomRight"}, transition=morph()),
        ],
    )
    f = layout_fixture(
        "A round bubble moves from the top left corner to the bottom right in a straight line and stays a circle.",
        p,
        [(t, full) for t in (2, 2.5, 3)],
    )
    assert_rect_close(expected(f, 0)["camera"]["rect"], bubble_top_left, "bubble moves at start")
    assert_rect_close(expected(f, 1)["camera"]["rect"], (830.4, 410.4, 259.2, 259.2), "bubble moves halfway")
    assert_equal(expected(f, 1)["camera"]["shape"], "circle", "bubble moves shape")
    assert_close(expected(f, 1)["camera"]["cornerRadius"], 129.6, "bubble moves radius")
    assert_equal(expected(f, 1)["camera"]["source"], expected(f, 0)["camera"]["source"], "bubble moves source")
    assert_rect_close(expected(f, 2)["camera"]["rect"], bubble_bottom_right, "bubble moves at rest")
    fixtures["scene-morph-bubble-moves-corner.json"] = f

    p = scene_project(
        106,
        "Squircle bubble grows",
        [
            scene(start=0, layout="bubble", bubble={"anchor": "topLeft", "size": 0.24}),
            scene(start=2, layout="bubble", bubble={"anchor": "bottomRight", "size": 0.4}, transition=morph()),
        ],
        camera_style={"shape": "squircle"},
    )
    f = layout_fixture(
        "A squircle bubble moves and grows from 0.24 to 0.4 of the short side, and stays a squircle.",
        p,
        [(t, full) for t in (2.5, 3)],
    )
    assert_rect_close(expected(f, 0)["camera"]["rect"], (744, 324, 345.6, 345.6), "squircle grows halfway")
    assert_equal(expected(f, 0)["camera"]["shape"], "squircle", "squircle grows shape")
    assert_close(expected(f, 0)["camera"]["cornerRadius"], 172.8, "squircle grows radius")
    assert_rect_close(expected(f, 1)["camera"]["rect"], (1455.6, 615.6, 432, 432), "squircle grows at rest")
    fixtures["scene-morph-squircle-bubble-grows.json"] = f

    p = scene_project(
        107,
        "Rectangle bubble to side by side",
        [scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide", transition=morph())],
        camera_style={"shape": "rectangle", "borderWidth": 0.01},
    )
    f = layout_fixture(
        "A rectangle bubble becomes a card with round corners: the two shapes differ, so it moves as a rounded rectangle whose radius grows from nothing. The border keeps its width.",
        p,
        [(t, full) for t in (1, 2, 2.5, 3)],
    )
    assert_rect_close(expected(f, 0)["camera"]["rect"], (1426.8, 788.4, 460.8, 259.2), "rectangle bubble at rest")
    assert_equal((expected(f, 0)["camera"]["shape"], expected(f, 0)["camera"]["cornerRadius"]), ("rectangle", 0), "rectangle bubble shape")
    assert_equal((expected(f, 1)["camera"]["shape"], expected(f, 1)["camera"]["cornerRadius"]), ("roundedRectangle", 0), "rectangle bubble as it starts to move")
    assert_close(expected(f, 2)["camera"]["cornerRadius"], 10.8, "rectangle bubble radius halfway")
    assert_close(expected(f, 2)["camera"]["borderWidth"], 10.8, "rectangle bubble border halfway")
    assert_equal(expected(f, 3)["camera"]["shape"], "roundedRectangle", "rectangle bubble as a card")
    fixtures["scene-morph-rectangle-bubble-to-card.json"] = f

    p = scene_project(
        108,
        "Rounded bubble to side by side",
        [scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide", transition=morph())],
        camera_style={"shape": "roundedRectangle", "cornerRadius": 0.25},
    )
    f = layout_fixture(
        "A rounded rectangle bubble becomes a card: both are rounded rectangles, and the radius goes from a quarter of the bubble's short side to the card's.",
        p,
        [(t, full) for t in (2, 2.5, 3)],
    )
    assert_close(expected(f, 0)["camera"]["cornerRadius"], 64.8, "rounded bubble radius at start")
    assert_close(expected(f, 1)["camera"]["cornerRadius"], 43.2, "rounded bubble radius halfway")
    assert_equal(expected(f, 1)["camera"]["shape"], "roundedRectangle", "rounded bubble shape")
    fixtures["scene-morph-rounded-bubble-to-card.json"] = f

    p = scene_project(
        109,
        "Rectangle bubble to square cards",
        [scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide", transition=morph())],
        camera_style={"shape": "rectangle"},
        screen_style={"cornerRadius": 0},
    )
    f = layout_fixture(
        "With a card radius of 0 the side-by-side camera is a rectangle too, so a rectangle bubble stays a rectangle all the way.",
        p,
        [(t, full) for t in (2, 2.5, 3)],
    )
    assert_equal([expected(f, index)["camera"]["shape"] for index in range(3)], ["rectangle"] * 3, "rectangle all the way")
    assert_equal([expected(f, index)["camera"]["cornerRadius"] for index in range(3)], [0, 0, 0], "no radius all the way")
    fixtures["scene-morph-rectangle-stays-rectangle.json"] = f

    p = scene_project(
        110,
        "Moves limited by their scenes",
        [
            scene(start=0, layout="bubble"),
            scene(start=2, layout="sideBySide", transition=morph(2)),
            scene(start=2.5, layout="camera", transition=morph(5)),
        ],
    )
    f = layout_fixture(
        "A move is never longer than its scene: one that asks for 2 seconds in a scene half a second long takes half a second, so the next move starts from a scene at rest. The last scene's move is limited only by the 2 second clamp.",
        p,
        [(t, full) for t in (2.25, 2.5, 3.5, 4.5, 5)],
    )
    assert_rect_close(expected(f, 0)["camera"]["rect"], halfway(bubble_bottom_right, split_camera), "short scene halfway")
    assert_rect_close(expected(f, 1)["camera"]["rect"], split_camera, "next move starts from the scene at rest: camera")
    assert_rect_close(expected(f, 1)["screen"]["rect"], split_screen, "next move starts from the scene at rest: screen")
    assert_close(expected(f, 1)["screen"]["opacity"], 1, "next move starts with the screen still there")
    assert_equal(expected(f, 1)["sceneIndex"], 2, "next move scene index")
    assert_rect_close(expected(f, 2)["camera"]["rect"], (694.68, 128.28375, 1160.52, 823.4325), "last move halfway after 1 of 2 seconds")
    assert_close(expected(f, 2)["screen"]["opacity"], 0.5, "last move screen halfway")
    assert_equal((expected(f, 2)["camera"]["shape"], expected(f, 2)["camera"]["cornerRadius"]), ("roundedRectangle", 21.6), "card to card keeps its shape and radius")
    assert_equal(expected(f, 3)["screen"], None, "last move over after 2 seconds")
    assert_equal(at_rest(expected(f, 3)), at_rest(expected(f, 4)), "last move at rest")
    fixtures["scene-morph-limited-by-its-scene.json"] = f

    p = scene_project(
        111,
        "Cuts and ignored transitions",
        [
            scene(start=0, layout="bubble", transition=morph()),
            scene(start=2, layout="sideBySide"),
            scene(start=4, layout="bubble", transition=morph(0)),
            scene(start=6, layout="camera", transition={"kind": "dissolve", "duration": 1}),
            scene(start=8, layout="bubble", transition=morph(-1)),
            scene(start=10, layout="sideBySide", transition=morph()),
        ],
    )
    p["scenes"][5]["transition"]["duration"] = None
    f = layout_fixture(
        "Scenes that are entered at once: the first scene whatever it says, a cut, a morph of no length, a kind this version does not know, and a negative length. A null length is the default 0.35 seconds.",
        p,
        [(t, full) for t in (0, 0.5, 2, 4, 6, 8, 10, 10.175, 10.5)],
    )
    assert_equal(at_rest(expected(f, 0)), at_rest(expected(f, 1)), "first scene is entered at once")
    assert_equal(expected(f, 1)["camera"]["shape"], "circle", "first scene at rest")
    assert_rect_close(expected(f, 2)["camera"]["rect"], split_camera, "a cut")
    assert_rect_close(expected(f, 3)["camera"]["rect"], bubble_bottom_right, "a morph of no length")
    assert_equal(expected(f, 3)["camera"]["shape"], "circle", "a morph of no length leaves the shape")
    assert_equal(expected(f, 4)["screen"], None, "an unknown kind is a cut")
    assert_rect_close(expected(f, 5)["camera"]["rect"], bubble_bottom_right, "a negative length")
    assert_equal(expected(f, 5)["screen"]["opacity"], 1, "a negative length leaves the screen opaque")
    assert_rect_close(expected(f, 6)["camera"]["rect"], bubble_bottom_right, "a null length starts moving")
    assert_equal(expected(f, 6)["camera"]["shape"], "roundedRectangle", "a null length is a morph")
    assert_rect_close(expected(f, 7)["camera"]["rect"], halfway(bubble_bottom_right, split_camera), "a null length halfway after 0.175 seconds")
    assert_rect_close(expected(f, 8)["camera"]["rect"], split_camera, "a null length at rest")
    add_fixture(
        fixtures,
        "scene-morph-entered-at-once.json",
        f,
        null=["project.scenes[5].transition.duration"],
    )

    p = scene_project(
        112,
        "A move during a zoom",
        [scene(start=0, layout="bubble"), scene(start=4, layout="sideBySide", transition=morph())],
        zooms=[zoom(3, 7, focus={"x": 0.3, "y": 0.4})],
    )
    f = layout_fixture(
        "The screen's card moves while a zoom is held: the card is on its way, and what it shows is the zoom window, as in both scenes.",
        p,
        [(t, full) for t in (4.5, 5)],
    )
    assert_rect_close(expected(f, 0)["screen"]["rect"], halfway(screen_card, split_screen), "move during zoom card")
    assert_rect_close(expected(f, 0)["screen"]["source"], (0.05, 0.15, 0.5, 0.5), "move during zoom window")
    assert_rect_close(expected(f, 1)["screen"]["source"], (0.05, 0.15, 0.5, 0.5), "after the move, still zoomed")
    fixtures["scene-morph-during-a-zoom.json"] = f

    p = scene_project(
        113,
        "No camera",
        [scene(start=0, layout="bubble"), scene(start=2, layout="camera", transition=morph())],
        has_camera=False,
    )
    f = layout_fixture(
        "Without a camera source every scene is the screen layout, so a morph between two of them moves nothing.",
        p,
        [(t, full) for t in (1, 2, 2.5, 3)],
    )
    for index in range(4):
        assert_rect_close(expected(f, index)["screen"]["rect"], screen_card, f"no camera case {index}")
        assert_equal((expected(f, index)["layout"], expected(f, index)["camera"], expected(f, index)["screen"]["opacity"]), ("screen", None, 1), f"no camera case {index} frame")
    assert_equal([expected(f, index)["sceneIndex"] for index in range(4)], [0, 1, 1, 1], "no camera scene index")
    fixtures["scene-morph-without-a-camera.json"] = f

    p = scene_project(
        114,
        "Portrait canvas",
        [scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide", transition=morph())],
        canvas={"aspect": "portrait9x16"},
    )
    tall = size(1080, 1920)
    f = layout_fixture(
        "On a portrait canvas the side-by-side cards are stacked. Halfway through the move each layer is halfway between its two places.",
        p,
        [(t, tall) for t in (1, 2.5, 3)],
    )
    for layer in ("screen", "camera"):
        before = tuple(expected(f, 0)[layer]["rect"][key] for key in ("x", "y", "width", "height"))
        after = tuple(expected(f, 2)[layer]["rect"][key] for key in ("x", "y", "width", "height"))
        assert_rect_close(expected(f, 1)[layer]["rect"], halfway(before, after), f"portrait {layer} halfway")
    assert_close(expected(f, 2)["camera"]["rect"]["x"], expected(f, 2)["screen"]["rect"]["x"], "portrait cards are stacked")
    fixtures["scene-morph-portrait-stacked.json"] = f

    p = scene_project(
        115,
        "Cropped camera",
        [scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide", transition=morph())],
        camera_style={"crop": rect(0.1, 0.1, 0.8, 0.6)},
    )
    f = layout_fixture(
        "A cropped camera on the move: its source is worked out again for the card it has at each instant, inside the crop and with the card's shape.",
        p,
        [(t, full) for t in (2, 2.25, 2.5, 2.75, 3)],
    )
    for index in range(5):
        camera = expected(f, index)["camera"]
        source = camera["source"]
        assert_close(
            (source["width"] * 1280) / (source["height"] * 720),
            camera["rect"]["width"] / camera["rect"]["height"],
            f"cropped camera case {index}: the source has the card's shape",
        )
        if source["x"] < 0.1 - EPS or source["y"] < 0.1 - EPS or source["x"] + source["width"] > 0.9 + EPS or source["y"] + source["height"] > 0.7 + EPS:
            raise AssertionError(f"cropped camera case {index}: the source leaves the crop")
    fixtures["scene-morph-cropped-camera.json"] = f

    p = scene_project(
        116,
        "Scenes out of order",
        [
            scene(start=4, layout="sideBySide", transition=morph(1)),
            scene(start=0, layout="bubble"),
            scene(start=4, layout="camera", transition=morph(2)),
            scene(start=-3, layout="screen"),
        ],
    )
    f = layout_fixture(
        "Transitions are worked out on the normalized scenes: the last scene stored for a start wins, with its own transition, and it comes from the scene that survives before it.",
        p,
        [(t, full) for t in (1, 5, 6)],
    )
    assert_equal((expected(f, 0)["sceneIndex"], expected(f, 0)["layout"], expected(f, 0)["camera"]), (0, "screen", None), "out of order first scene")
    assert_close(expected(f, 1)["screen"]["opacity"], 0.5, "out of order screen halfway through 2 seconds")
    assert_close(expected(f, 1)["camera"]["opacity"], 0.5, "out of order camera halfway through 2 seconds")
    assert_rect_close(expected(f, 1)["camera"]["rect"], camera_card, "out of order camera place")
    assert_equal((expected(f, 2)["sceneIndex"], expected(f, 2)["layout"], expected(f, 2)["screen"]), (1, "camera", None), "out of order at rest")
    fixtures["scene-morph-scenes-out-of-order.json"] = f

    p = scene_project(
        117,
        "Other canvas sizes",
        [scene(start=0, layout="bubble"), scene(start=4, layout="sideBySide", transition=morph())],
    )
    f = layout_fixture(
        "A move resolved at other sizes than the natural canvas: twice as large, half as large, and a preview of another shape.",
        p,
        [(4.5, size(3840, 2160)), (4.5, size(960, 540)), (4.5, size(800, 600))],
    )
    assert_rect_close(expected(f, 0)["screen"]["rect"], (180, 256.5675, 2927.76, 1646.865), "twice as large screen")
    assert_rect_close(expected(f, 1)["camera"]["rect"], (738.24, 245.041875, 197.46, 238.91625), "half as large camera")
    assert_close(expected(f, 0)["camera"]["cornerRadius"], 151.2, "twice as large radius")
    fixtures["scene-morph-other-canvas-sizes.json"] = f

    p = scene_project(
        118,
        "Camera starts later",
        [scene(start=0, layout="bubble"), scene(start=2, layout="sideBySide", transition=morph())],
        camera_start=5,
    )
    f = layout_fixture(
        "A camera that is not showing yet still has its place worked out, and that place moves with the scene.",
        p,
        [(t, full) for t in (2.5, 6)],
    )
    assert_equal((expected(f, 0)["camera"]["visible"], expected(f, 0)["camera"]["sourceTime"]), (False, 0), "camera not showing yet")
    assert_rect_close(expected(f, 0)["camera"]["rect"], halfway(bubble_bottom_right, split_camera), "camera not showing yet, halfway")
    assert_equal(expected(f, 1)["camera"]["visible"], True, "camera showing later")
    fixtures["scene-morph-before-the-camera-starts.json"] = f

    p = scene_project(
        119,
        "Cards change sides",
        [
            scene(start=0, layout="sideBySide", split={"cameraSide": "trailing"}),
            scene(start=2, layout="sideBySide", split={"cameraSide": "leading"}, transition=morph()),
        ],
    )
    f = layout_fixture(
        "The camera goes from the right of the screen to its left: the two cards cross, each in a straight line.",
        p,
        [(t, full) for t in (2, 2.5, 3)],
    )
    assert_rect_close(expected(f, 1)["screen"]["rect"], (340.92, 191.7675, 1238.16, 696.465), "cards cross: screen halfway")
    assert_rect_close(expected(f, 1)["camera"]["rect"], (694.68, 191.7675, 530.64, 696.465), "cards cross: camera halfway")
    assert_rect_close(expected(f, 2)["screen"]["rect"], (617.04, 191.7675, 1238.16, 696.465), "cards cross: screen at rest")
    assert_rect_close(expected(f, 2)["camera"]["rect"], (64.8, 191.7675, 530.64, 696.465), "cards cross: camera at rest")
    assert_equal(expected(f, 1)["camera"]["shape"], "roundedRectangle", "cards cross: shape")
    fixtures["scene-morph-cards-change-sides.json"] = f

    p = scene_project(
        120,
        "Side by side to a round bubble",
        [scene(start=0, layout="sideBySide"), scene(start=2, layout="bubble", transition=morph())],
    )
    f = layout_fixture(
        "A card becomes a round bubble: the two shapes differ, so it moves as a rounded rectangle whose radius grows to half its side, and is a circle from the instant the move ends.",
        p,
        [(t, full) for t in (1, 2, 2.5, 3)],
    )
    assert_equal((expected(f, 0)["camera"]["shape"], expected(f, 0)["camera"]["cornerRadius"]), ("roundedRectangle", 21.6), "card at rest")
    assert_rect_close(expected(f, 1)["camera"]["rect"], split_camera, "card to round bubble at start")
    assert_equal(expected(f, 1)["camera"]["shape"], "roundedRectangle", "card to round bubble shape at start")
    assert_rect_close(expected(f, 2)["camera"]["rect"], halfway(split_camera, bubble_bottom_right), "card to round bubble halfway")
    assert_equal(expected(f, 2)["camera"]["shape"], "roundedRectangle", "card to round bubble shape halfway")
    assert_close(expected(f, 2)["camera"]["cornerRadius"], 75.6, "card to round bubble radius halfway")
    assert_rect_close(expected(f, 2)["screen"]["rect"], halfway(split_screen, screen_card), "card to round bubble screen halfway")
    assert_equal((expected(f, 3)["camera"]["shape"], expected(f, 3)["camera"]["cornerRadius"]), ("circle", 129.6), "round bubble at rest")
    fixtures["scene-morph-card-to-round-bubble.json"] = f

    p = scene_project(
        121,
        "Camera to a squircle bubble",
        [scene(start=0, layout="camera"), scene(start=2, layout="bubble", transition=morph())],
        camera_style={"shape": "squircle"},
    )
    f = layout_fixture(
        "Leaving the camera layout: the card shrinks into the bubble as a rounded rectangle whose radius goes to 0.22 of the bubble's side, which is what a squircle looks like, and is a squircle once it is there. The screen fades in behind it where the new scene has it.",
        p,
        [(t, full) for t in (2, 2.25, 2.5, 2.75, 3)],
    )
    assert_equal(expected(f, 0)["layout"], "bubble", "camera to squircle layout")
    assert_rect_close(expected(f, 0)["camera"]["rect"], camera_card, "camera to squircle at start")
    assert_equal((expected(f, 0)["camera"]["shape"], expected(f, 0)["camera"]["cornerRadius"]), ("roundedRectangle", 21.6), "camera to squircle shape at start")
    assert_close(expected(f, 0)["screen"]["opacity"], 0, "camera to squircle screen at start")
    assert_close(expected(f, 1)["screen"]["opacity"], 0.15625, "camera to squircle screen a quarter of the way")
    assert_rect_close(expected(f, 2)["camera"]["rect"], (846.6, 426.6, 1024.8, 604.8), "camera to squircle halfway")
    assert_equal(expected(f, 2)["camera"]["shape"], "roundedRectangle", "camera to squircle shape halfway")
    assert_close(expected(f, 2)["camera"]["cornerRadius"], 39.312, "camera to squircle radius halfway: between 21.6 and 0.22 * 259.2")
    assert_close(expected(f, 3)["camera"]["cornerRadius"], 51.489, "camera to squircle radius three quarters of the way")
    assert_close(expected(f, 2)["screen"]["opacity"], 0.5, "camera to squircle screen halfway")
    assert_rect_close(expected(f, 2)["screen"]["rect"], screen_card, "camera to squircle screen place")
    assert_equal((expected(f, 4)["camera"]["shape"], expected(f, 4)["camera"]["cornerRadius"]), ("squircle", 129.6), "squircle bubble at rest")
    assert_equal(expected(f, 4)["screen"]["opacity"], 1, "camera to squircle screen at rest")
    fixtures["scene-morph-camera-to-squircle-bubble.json"] = f

    p = scene_project(
        122,
        "Squircle bubble to side by side",
        [
            scene(start=0, layout="bubble", bubble={"size": 0.36}),
            scene(start=2, layout="sideBySide", transition=morph()),
        ],
        camera_style={"shape": "squircle"},
    )
    f = layout_fixture(
        "A squircle bubble becomes a card. From the instant it starts to move it is a rounded rectangle, and its radius starts at 0.22 of its side, not at the half a squircle has on paper, so its outline does not jump.",
        p,
        [(t, full) for t in (1, 2, 2.5, 3)],
    )
    assert_equal((expected(f, 0)["camera"]["shape"], expected(f, 0)["camera"]["cornerRadius"]), ("squircle", 194.4), "squircle at rest")
    assert_rect_close(expected(f, 1)["camera"]["rect"], (1498.8, 658.8, 388.8, 388.8), "squircle to card at start")
    assert_equal(expected(f, 1)["camera"]["shape"], "roundedRectangle", "squircle to card shape at start")
    assert_close(expected(f, 1)["camera"]["cornerRadius"], 85.536, "squircle to card radius at start: 0.22 * 388.8")
    assert_close(expected(f, 2)["camera"]["cornerRadius"], 53.568, "squircle to card radius halfway: between 85.536 and 21.6")
    assert_equal((expected(f, 3)["camera"]["shape"], expected(f, 3)["camera"]["cornerRadius"]), ("roundedRectangle", 21.6), "squircle to card at rest")
    fixtures["scene-morph-squircle-bubble-to-card.json"] = f

    return fixtures


def generate_autozooms():
    fixtures = {}

    def screen_project(number, name, **kwargs):
        return project(
            f"00000000-0000-4000-8000-{number:012d}",
            name=name,
            has_camera=False,
            scenes=[scene(layout="screen")],
            **kwargs,
        )

    f = autozoom_fixture(
        "One click gives one zoom on it, from 0.6 seconds before the click until 1.5 seconds after.",
        screen_project(70, "One click"),
        events_file(clicks=[click(3.0, 0.2, 0.3)]),
    )
    assert_suggestions(f, [(2.4, 4.5, 0.2, 0.3)], "one click")
    assert_equal(f["expected"]["zooms"][0]["scale"], 2, "suggested scale")
    assert_equal(f["expected"]["zooms"][0]["focus"]["mode"], "point", "suggested focus mode")
    fixtures["single-click.json"] = f

    f = autozoom_fixture(
        "Clicks that follow each other within 4 seconds and land inside the middle of the first click's window stay in one zoom, which ends 1.5 seconds after the last of them.",
        screen_project(71, "Clicks in one place"),
        events_file(clicks=[click(3.0, 0.61, 0.72), click(3.2, 0.61, 0.72), click(5.5, 0.55, 0.8), click(9.0, 0.7, 0.6)]),
    )
    assert_suggestions(f, [(2.4, 10.5, 0.61, 0.72)], "clicks in one place")
    fixtures["clicks-in-one-place.json"] = f

    f = autozoom_fixture(
        "A click somewhere else within 4 seconds ends the zoom and starts the next one at the same instant, so the two are chained: 0.6 seconds before the click, or half way between the two clicks when that is later.",
        screen_project(72, "Clicks that move"),
        events_file(clicks=[click(3.0, 0.2, 0.3), click(5.0, 0.8, 0.7), click(5.4, 0.85, 0.75), click(6.0, 0.1, 0.1)]),
    )
    assert_suggestions(f, [(2.4, 4.4, 0.2, 0.3), (4.4, 5.7, 0.8, 0.7), (5.7, 7.5, 0.1, 0.1)], "clicks that move")
    assert_equal(f["expected"]["zooms"][0]["end"], f["expected"]["zooms"][1]["start"], "moved zooms are chained")
    assert_equal(f["expected"]["zooms"][1]["end"], f["expected"]["zooms"][2]["start"], "moved zooms are chained again")
    fixtures["clicks-that-move.json"] = f

    f = autozoom_fixture(
        "Clicks more than 4 seconds apart get separate zooms even in the same place, and a click exactly 4 seconds after the one before still belongs with it.",
        screen_project(73, "Clicks far apart"),
        events_file(clicks=[click(2.0, 0.5, 0.5), click(8.0, 0.5, 0.5), click(12.0, 0.5, 0.5)]),
    )
    assert_suggestions(f, [(1.4, 3.5, 0.5, 0.5), (7.4, 13.5, 0.5, 0.5)], "clicks far apart")
    fixtures["clicks-far-apart.json"] = f

    f = autozoom_fixture(
        "A zoom cannot start before the recording or end after it, a click at the very end still counts, and clicks outside the recording's time are ignored.",
        screen_project(74, "Clicks at the start and the end", screen_duration=10),
        events_file(
            clicks=[
                click(-0.5, 0.5, 0.5),
                click(0.2, 0.3, 0.3),
                click(9.5, 0.7, 0.7),
                click(10, 0.7, 0.7),
                click(10.5, 0.7, 0.7),
            ]
        ),
    )
    assert_suggestions(f, [(0, 1.7, 0.3, 0.3), (8.9, 10, 0.7, 0.7)], "clicks at the start and the end")
    fixtures["clicks-at-the-start-and-end.json"] = f

    f = autozoom_fixture(
        "Three quick clicks in three places: the zoom for the middle one would last a tenth of a second, which is under the shortest allowed, so it is left out and its neighbours are not chained.",
        screen_project(75, "Quick clicks in different places"),
        events_file(clicks=[click(5.0, 0.1, 0.1), click(5.1, 0.9, 0.9), click(5.2, 0.1, 0.9)]),
    )
    assert_suggestions(f, [(4.4, 5.05, 0.1, 0.1), (5.15, 6.7, 0.1, 0.9)], "quick clicks")
    fixtures["quick-clicks-in-different-places.json"] = f

    f = autozoom_fixture(
        "With a screen crop, clicks outside it are ignored, a click on its edge counts, and the window that decides whether a click is in the same place is the cropped view's.",
        screen_project(76, "Clicks with a crop", screen_style={"crop": rect(0.25, 0.25, 0.5, 0.5)}),
        events_file(
            clicks=[
                click(2.0, 0.1, 0.1),
                click(3.0, 0.3, 0.3),
                click(4.0, 0.45, 0.45),
                click(5.0, 0.7, 0.7),
                click(12.0, 0.75, 0.5),
            ]
        ),
    )
    assert_suggestions(f, [(2.4, 4.5, 0.3, 0.3), (4.5, 6.5, 0.7, 0.7), (11.4, 13.5, 0.75, 0.5)], "clicks with a crop")
    fixtures["clicks-with-a-crop.json"] = f

    manual_project = screen_project(
        77,
        "Manual zooms win",
        zooms=[
            zoom(2, 4),
            zoom(7, 9, origin="auto"),
            {"start": 12.5, "end": 14},
            zoom(19.5, 20),
        ],
    )
    f = autozoom_fixture(
        "A suggestion that overlaps a zoom the user made is dropped. One that overlaps only an earlier suggestion is kept, because applying replaces those, and one that ends exactly where a manual zoom starts does not overlap it. A zoom without an origin is manual.",
        manual_project,
        events_file(clicks=[click(3.0, 0.5, 0.5), click(8.0, 0.5, 0.5), click(13.0, 0.2, 0.2), click(18.0, 0.5, 0.5)]),
    )
    assert_suggestions(f, [(7.4, 9.5, 0.5, 0.5), (17.4, 19.5, 0.5, 0.5)], "manual zooms win")
    add_fixture(fixtures, "manual-zooms-win.json", f, folder="autozoom", omitted=["project.zooms[2].origin"])

    f = autozoom_fixture(
        "A recording without clicks, which is what a window recording is, gets no suggestions.",
        screen_project(78, "No clicks"),
        events_file(kind="window", cursor=[cursor_sample(1.0, 0.5, 0.5)]),
    )
    assert_suggestions(f, [], "no clicks")
    fixtures["no-clicks.json"] = f

    f = autozoom_fixture(
        "Clicks are taken in time order whatever order they are stored in, every mouse button counts, and a null entry is skipped.",
        screen_project(79, "Unsorted clicks"),
        events_file(clicks=[click(9.0, 0.5, 0.5, "right"), None, click(2.0, 0.5, 0.5), click(2.5, 0.52, 0.5, "middle")]),
    )
    assert_suggestions(f, [(1.4, 4.0, 0.5, 0.5), (8.4, 10.5, 0.5, 0.5)], "unsorted clicks")
    add_fixture(fixtures, "unsorted-clicks.json", f, folder="autozoom", null=["events.clicks[1]"])

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
        "speed-one-range.json": timemap_fixture("One range at twice the speed in the middle of the trim takes half its time.", 20, {"trimStart": 2, "trimEnd": 18, "cuts": [], "speed": [speed(6, 10, 2)]}, [0, 2, 6, 8, 10, 14, 18, 20], [-1, 0, 4, 5, 5.5, 6, 10, 14, 15]),
        "speed-slow.json": timemap_fixture("A range at half speed takes twice its time, so the video is longer than the recording.", 10, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(4, 6, 0.5)]}, [0, 4, 4.5, 5, 6, 10], [0, 4, 5, 6, 8, 9, 12, 13]),
        "speed-three-halves.json": timemap_fixture("A rate that is not a power of two.", 12, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(3, 9, 1.5)]}, [0, 3, 4.5, 6, 9, 12], [0, 3, 4, 5, 7, 10]),
        "speed-with-cut-inside.json": timemap_fixture("A cut inside a speed range: what is kept on both sides of it has the rate.", 20, {"trimStart": 0, "trimEnd": None, "cuts": [{"start": 6, "end": 8}], "speed": [speed(4, 12, 4)]}, [0, 4, 5, 6, 7, 8, 10, 12, 20], [0, 4, 4.25, 4.5, 5, 5.5, 13.5]),
        "speed-inside-a-cut.json": timemap_fixture("A speed range inside a cut changes nothing, and one that reaches out of a cut counts where video is kept.", 20, {"trimStart": 0, "trimEnd": None, "cuts": [{"start": 6, "end": 12}], "speed": [speed(7, 9, 2), speed(10, 14, 2)]}, [0, 6, 8, 12, 13, 14, 20], [0, 6, 6.5, 7, 13]),
        "speed-unsorted-overlapping.json": timemap_fixture("Speed entries out of order that overlap: the later one starts where the one before it ends, and one that lies inside another is dropped.", 20, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(8, 12, 2), speed(4, 9, 4), speed(5, 7, 8)]}, [0, 4, 6, 8, 9, 10, 12, 20], [0, 4, 4.5, 5.25, 6, 6.75, 14.75]),
        "speed-touching.json": timemap_fixture("Speed ranges that touch: two with the same rate are one piece, and one with another rate is its own.", 20, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(4, 6, 2), speed(6, 8, 2), speed(8, 10, 4)]}, [0, 4, 6, 7, 8, 9, 10, 20], [0, 4, 5, 6, 6.25, 6.5, 16.5]),
        "speed-same-range-twice.json": timemap_fixture("Two entries for the same stretch: the first in the file counts.", 20, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(4, 8, 4), speed(4, 8, 2)]}, [0, 4, 6, 8, 20], [0, 4, 4.5, 5, 17]),
        "speed-rates.json": timemap_fixture("A rate above 8 counts as 8 and one below 0.25 as 0.25. A rate of 1, of 0, below 0, or left out makes no range.", 40, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(2, 4, 16), speed(6, 8, 0.125), speed(10, 12, 1), speed(14, 16, 0), speed(18, 20, -2), {"start": 22, "end": 24}]}, [0, 2, 3, 4, 6, 7, 8, 11, 15, 19, 23, 40], [0, 2, 2.125, 2.25, 4.25, 8.25, 12.25, 44.25]),
        "speed-rate-one-overlaps.json": timemap_fixture("An entry with a rate of 1 is no range at all, so it takes nothing from an entry it overlaps.", 20, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(4, 8, 1), speed(6, 10, 2), speed(12, 16, 0), speed(14, 18, 4)]}, [0, 6, 8, 10, 14, 16, 18, 20], [0, 6, 7, 8, 12, 13, 15]),
        "speed-and-trim.json": timemap_fixture("Speed entries outside the trim change nothing, and those that straddle its ends count for the part inside it.", 20, {"trimStart": 4, "trimEnd": 16, "cuts": [], "speed": [speed(0, 2, 2), speed(2, 6, 2), speed(14, 18, 4), speed(17, 19, 2)]}, [0, 4, 5, 6, 14, 15, 16, 20], [-1, 0, 0.5, 1, 9, 9.25, 9.5, 10]),
        "speed-overlapping-before-the-trim.json": timemap_fixture("Which of two overlapping speed entries counts does not depend on the trim. Both start before it: the one that starts first in the recording counts, not the one that ends first inside the trim.", 20, {"trimStart": 2, "trimEnd": 10, "cuts": [], "speed": [speed(1, 5, 2), speed(0, 8, 4)]}, [0, 2, 3, 5, 6, 8, 9, 10, 20], [-1, 0, 0.5, 1, 1.5, 2.5, 3.5, 4]),
        "speed-overlapping-past-the-trim.json": timemap_fixture("Which of two overlapping speed entries counts does not depend on the trim. Both start together and end after it: the one that ends first in the recording counts, not the first in the file.", 20, {"trimStart": 0, "trimEnd": 10, "cuts": [], "speed": [speed(4, 14, 4), speed(4, 12, 2)]}, [0, 4, 6, 8, 10, 12, 20], [-1, 0, 4, 5, 6, 7, 8]),
        "speed-everything.json": timemap_fixture("The whole recording at eight times the speed.", 16, {"trimStart": 0, "trimEnd": None, "cuts": [], "speed": [speed(0, 16, 8)]}, [-1, 0, 4, 8, 16, 17], [-1, 0, 0.5, 1, 2, 3]),
    }


def speed(start, end, rate):
    return {"start": start, "end": end, "rate": rate}


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
    for filename, fixture in generate_zoom_layouts().items():
        files[Path("layout") / filename] = fixture
    for filename, fixture in generate_scene_layouts().items():
        files[Path("layout") / filename] = fixture
    for filename, fixture in generate_timemaps().items():
        files[Path("timemap") / filename] = fixture
    for filename, fixture in generate_autozooms().items():
        files[Path("autozoom") / filename] = fixture
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
