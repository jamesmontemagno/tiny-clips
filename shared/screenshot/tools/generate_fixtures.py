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
ALIGNMENT_DIR = FIXTURES / "alignment"
LINE_DIR = FIXTURES / "line"
PEN_DIR = FIXTURES / "pen"
FIXTURE_DIRS = [ARROW_DIR, ALIGNMENT_DIR, LINE_DIR, PEN_DIR]

# Arrow geometry. Points are y-down; widths are in source-image pixels and `scale` is the
# caller's units per image pixel. An arrow is one filled outline: a tapered shaft along a
# quadratic Bezier, the head, and the tail cap. `bend` is how far the curve's midpoint sits from
# the chord's midpoint, as a fraction of the chord length; positive bows left of travel.
MINIMUM_HEAD_LENGTH = 12
HEAD_LENGTH_PER_WIDTH = 3.5
HEAD_SPREAD = math.pi / 7
MAXIMUM_HEAD_FRACTION = 0.45
SHAFT_END_FRACTION = 0.8
SHAFT_SAMPLES = 24
CAP_SEGMENTS = 8
CORNER_SEGMENTS = 6
TRIM_ITERATIONS = 40
VALID_CAPS = {"butt", "round", "square"}
ARROW_PRESETS = {
    "classic": {"bend": 0, "startWidthScale": 1, "endWidthScale": 1, "headScale": 1},
    "tapered": {"bend": 0, "startWidthScale": 0.25, "endWidthScale": 1, "headScale": 1.2},
    "curved": {"bend": 0.2, "startWidthScale": 1, "endWidthScale": 1, "headScale": 1},
}


def point(x, y):
    return {"x": x, "y": y}


def add(a, b):
    return point(a["x"] + b["x"], a["y"] + b["y"])


def sub(a, b):
    return point(a["x"] - b["x"], a["y"] - b["y"])


def mul(a, k):
    return point(a["x"] * k, a["y"] * k)


def length(a):
    return math.sqrt(a["x"] * a["x"] + a["y"] * a["y"])


def normalize(a, fallback):
    n = length(a)
    return mul(a, 1 / n) if n > 1e-9 else fallback


def lerp(a, b, t):
    return point(a["x"] + (b["x"] - a["x"]) * t, a["y"] + (b["y"] - a["y"]) * t)


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def head_length(width):
    return max(MINIMUM_HEAD_LENGTH, width * HEAD_LENGTH_PER_WIDTH)


def right_normal(direction):
    """The unit normal to the right of travel in a y-down space."""
    return point(-direction["y"], direction["x"])


def quad(p0, c, p2, t):
    u = 1 - t
    return point(
        u * u * p0["x"] + 2 * u * t * c["x"] + t * t * p2["x"],
        u * u * p0["y"] + 2 * u * t * c["y"] + t * t * p2["y"],
    )


def quad_tangent(p0, c, p2, t):
    return point(
        2 * (1 - t) * (c["x"] - p0["x"]) + 2 * t * (p2["x"] - c["x"]),
        2 * (1 - t) * (c["y"] - p0["y"]) + 2 * t * (p2["y"] - c["y"]),
    )


def arrow_handle(start, end, bend):
    """The point on the curve at t = 1/2, where the bend handle sits."""
    chord = sub(end, start)
    mid = lerp(start, end, 0.5)
    n = right_normal(normalize(chord, point(1, 0)))
    return sub(mid, mul(n, bend * length(chord)))


def arrow_bend(start, end, handle):
    """The bend that puts the curve's midpoint nearest `handle`, clamped to -1...1."""
    chord = sub(end, start)
    l = length(chord)
    if l < 0.001:
        return 0
    n = right_normal(mul(chord, 1 / l))
    offset = sub(handle, lerp(start, end, 0.5))
    return clamp(-(offset["x"] * n["x"] + offset["y"] * n["y"]) / l, -1, 1)


def corner(prev, vertex, nxt, size, join):
    if join == "miter" or size <= 0:
        return [vertex]
    to_prev = sub(prev, vertex)
    to_next = sub(nxt, vertex)
    k = min(size, 0.45 * length(to_prev), 0.45 * length(to_next))
    a = add(vertex, mul(normalize(to_prev, point(0, 0)), k))
    b = add(vertex, mul(normalize(to_next, point(0, 0)), k))
    if join == "bevel":
        return [a, b]
    return [quad(a, vertex, b, j / CORNER_SEGMENTS) for j in range(CORNER_SEGMENTS + 1)]


def arrow_outline(start, end, width, bend, start_scale, end_scale, head_scale, start_cap, join, scale):
    if start_cap not in VALID_CAPS:
        raise ValueError(f"unknown cap {start_cap!r}")
    if join not in VALID_JOINS:
        raise ValueError(f"unknown join {join!r}")
    chord = sub(end, start)
    l = length(chord)
    handle = arrow_handle(start, end, bend)
    control = sub(mul(handle, 2), lerp(start, end, 0.5))
    if l < 0.001:
        return {"control": control, "handle": handle, "points": []}
    u = mul(chord, 1 / l)

    head = min(head_scale * head_length(width) * scale, MAXIMUM_HEAD_FRACTION * l)
    tip_direction = normalize(sub(end, control), u)
    angle = math.atan2(tip_direction["y"], tip_direction["x"])
    wing_right = point(end["x"] - head * math.cos(angle - HEAD_SPREAD), end["y"] - head * math.sin(angle - HEAD_SPREAD))
    wing_left = point(end["x"] - head * math.cos(angle + HEAD_SPREAD), end["y"] - head * math.sin(angle + HEAD_SPREAD))

    # The shaft stops where the curve is as far from the tip as the head's base.
    base = head * math.cos(HEAD_SPREAD)
    lo, hi = 0.0, 1.0
    for _ in range(TRIM_ITERATIONS):
        m = (lo + hi) / 2
        if length(sub(quad(start, control, end, m), end)) > base:
            lo = m
        else:
            hi = m
    trim = (lo + hi) / 2

    start_half = width * start_scale / 2 * scale
    end_half = min(width * end_scale / 2 * scale, SHAFT_END_FRACTION * head * math.sin(HEAD_SPREAD))
    left, right = [], []
    for i in range(SHAFT_SAMPLES + 1):
        f = i / SHAFT_SAMPLES
        t = trim * f
        p = quad(start, control, end, t)
        n = right_normal(normalize(quad_tangent(start, control, end, t), u))
        half = start_half + (end_half - start_half) * f
        left.append(sub(p, mul(n, half)))
        right.append(add(p, mul(n, half)))

    size = min(width / 2 * scale, 0.2 * head)
    points = list(left)
    points += corner(left[-1], wing_left, end, size, join)
    points += corner(wing_left, end, wing_right, size, join)
    points += corner(end, wing_right, right[-1], size, join)
    points += list(reversed(right))

    tail_tangent = normalize(quad_tangent(start, control, end, 0), u)
    tail_normal = right_normal(tail_tangent)
    if start_cap == "round":
        for j in range(1, CAP_SEGMENTS):
            theta = math.pi * j / CAP_SEGMENTS
            points.append(add(start, add(
                mul(tail_normal, start_half * math.cos(theta)),
                mul(tail_tangent, -start_half * math.sin(theta)),
            )))
    elif start_cap == "square":
        back = mul(tail_tangent, -start_half)
        points.append(add(right[0], back))
        points.append(add(left[0], back))
    return {"control": control, "handle": handle, "points": points}


# Stroke alignment for closed shapes. A rectangle's stroke is drawn as a filled ring: the outer
# outline (corners shaped by the join) minus the inner rectangle (always sharp). An ellipse's
# outer edge is not an ellipse, so renderers that can clip use a doubled stroke clipped to the
# shape; `centerline` is the ellipse to stroke where they cannot (an approximation).
VALID_ALIGNMENTS = {"center", "inside", "outside"}
VALID_JOINS = {"miter", "round", "bevel"}


def rect(x, y, width, height):
    return {"x": x, "y": y, "width": width, "height": height}


def inset(r, d):
    """Insets by d (outsets when negative), never below an empty rectangle at the center."""
    dx = min(d, r["width"] / 2)
    dy = min(d, r["height"] / 2)
    return rect(r["x"] + dx, r["y"] + dy, r["width"] - 2 * dx, r["height"] - 2 * dy)


def rect_stroke_ring(bounds, width, alignment, join):
    if alignment not in VALID_ALIGNMENTS:
        raise ValueError(f"unknown alignment {alignment!r}")
    if join not in VALID_JOINS:
        raise ValueError(f"unknown join {join!r}")
    outer_offset = {"center": width / 2, "inside": 0, "outside": width}[alignment]
    inner_inset = {"center": width / 2, "inside": width, "outside": 0}[alignment]
    return {
        "outer": inset(bounds, -outer_offset),
        "outerJoin": join if outer_offset > 0 else "miter",
        "cornerSize": outer_offset if outer_offset > 0 and join != "miter" else 0,
        "inner": inset(bounds, inner_inset),
    }


def ellipse_centerline(bounds, width, alignment):
    if alignment not in VALID_ALIGNMENTS:
        raise ValueError(f"unknown alignment {alignment!r}")
    return inset(bounds, {"center": 0, "inside": width / 2, "outside": -width / 2}[alignment])


def generate_alignments():
    bounds_cases = [rect(40, 30, 200, 120), rect(10, 10, 6, 40)]
    rect_cases = []
    for bounds in bounds_cases:
        for alignment in sorted(VALID_ALIGNMENTS):
            for join in sorted(VALID_JOINS):
                rect_cases.append({
                    "bounds": bounds,
                    "width": 8,
                    "alignment": alignment,
                    "join": join,
                    "expected": rect_stroke_ring(bounds, 8, alignment, join),
                })
    ellipse_cases = []
    for bounds in bounds_cases:
        for alignment in sorted(VALID_ALIGNMENTS):
            ellipse_cases.append({
                "bounds": bounds,
                "width": 8,
                "alignment": alignment,
                "expected": ellipse_centerline(bounds, 8, alignment),
            })
    return {
        "rectangle.json": {
            "description": "Rectangle stroke rings for each alignment and join, including a rectangle narrower than an inside stroke.",
            "cases": rect_cases,
        },
        "ellipse.json": {
            "description": "The ellipse to stroke for each alignment where a renderer cannot clip; inside never shrinks below empty.",
            "cases": ellipse_cases,
        },
    }


def line_outline(start, end, width, start_cap, end_cap, scale):
    """A straight line's filled outline: its two edges joined by each end's cap."""
    for cap in (start_cap, end_cap):
        if cap not in VALID_CAPS:
            raise ValueError(f"unknown cap {cap!r}")
    chord = sub(end, start)
    if length(chord) < 0.001:
        return []
    u = normalize(chord, point(1, 0))
    n = right_normal(u)
    half = width / 2 * scale
    points = [sub(start, mul(n, half)), sub(end, mul(n, half))]
    points += cap_points(end, u, n, half, end_cap)
    points += [add(end, mul(n, half)), add(start, mul(n, half))]
    points += cap_points(start, mul(u, -1), mul(n, -1), half, start_cap)
    return points


def cap_points(center, outward, normal, half, cap):
    """Points between a line's left edge and right edge around one end, going away along
    `outward`. `normal` points from the edge the outline arrives on to the one it leaves by."""
    if cap == "round":
        return [
            add(center, add(
                mul(normal, -half * math.cos(math.pi * j / CAP_SEGMENTS)),
                mul(outward, half * math.sin(math.pi * j / CAP_SEGMENTS)),
            ))
            for j in range(1, CAP_SEGMENTS)
        ]
    if cap == "square":
        out = mul(outward, half)
        return [add(sub(center, mul(normal, half)), out), add(add(center, mul(normal, half)), out)]
    return []


# Freehand pen: a port of perfect-freehand's getStroke (MIT, (c) 2021 Stephen Ruiz Ltd,
# github.com/steveruizok/perfect-freehand at 176e00f) with fixed options: size = the stroke width,
# thinning 0.3 (so a stroke reads close to its width), smoothing 0.5, streamline 0.5, simulated pressure, linear easing, both ends
# tapered over PEN_TAPER_PER_WIDTH widths, and the stroke treated as complete. Lengths use
# sqrt rather than hypot so every platform rounds the same way; the cap loops keep the
# source's accumulating steps for the same reason.
PEN_THINNING = 0.3
PEN_SMOOTHING = 0.5
PEN_STREAMLINE = 0.5
PEN_TAPER_PER_WIDTH = 3
RATE_OF_PRESSURE_CHANGE = 0.275
FIXED_PI = math.pi + 0.0001
START_CAP_SEGMENTS = 13
END_CAP_SEGMENTS = 29
CORNER_CAP_SEGMENTS = 13
END_NOISE_THRESHOLD = 3
MIN_STREAMLINE_T = 0.15
STREAMLINE_T_RANGE = 0.85
MIN_RADIUS = 0.01
DEFAULT_FIRST_PRESSURE = 0.25
DEFAULT_PRESSURE = 0.5


def v_len(a):
    return math.sqrt(a["x"] * a["x"] + a["y"] * a["y"])


def v_uni(a):
    n = v_len(a)
    return point(a["x"] / n, a["y"] / n)


def v_per(a):
    return point(a["y"], -a["x"])


def v_dpr(a, b):
    return a["x"] * b["x"] + a["y"] * b["y"]


def v_dist2(a, b):
    dx = a["x"] - b["x"]
    dy = a["y"] - b["y"]
    return dx * dx + dy * dy


def v_lrp(a, b, t):
    return point(a["x"] + (b["x"] - a["x"]) * t, a["y"] + (b["y"] - a["y"]) * t)


def v_rot_around(a, c, r):
    s_, c_ = math.sin(r), math.cos(r)
    px = a["x"] - c["x"]
    py = a["y"] - c["y"]
    return point(px * c_ - py * s_ + c["x"], px * s_ + py * c_ + c["y"])


def simulate_pressure(prev, distance, size):
    sp = min(1, distance / size)
    rp = min(1, 1 - sp)
    return min(1, prev + (rp - prev) * (sp * RATE_OF_PRESSURE_CHANGE))


def stroke_radius(size, pressure):
    return size * (0.5 - PEN_THINNING * (0.5 - pressure))


def pen_stroke_points(pts, size):
    """getStrokePoints with last = true. `pts` are input points without pressure."""
    if not pts:
        return []
    t = MIN_STREAMLINE_T + (1 - PEN_STREAMLINE) * STREAMLINE_T_RANGE
    if len(pts) == 2:
        last = pts[1]
        pts = [pts[0]] + [v_lrp(pts[0], last, i / 4) for i in range(1, 5)]
    if len(pts) == 1:
        pts = [pts[0], add(pts[0], point(1, 1))]
    stroke = [{"point": pts[0], "pressure": DEFAULT_FIRST_PRESSURE, "vector": point(1, 1), "distance": 0, "runningLength": 0}]
    reached_minimum = False
    running = 0
    prev = stroke[0]
    last_index = len(pts) - 1
    for i in range(1, len(pts)):
        p = pts[i] if i == last_index else v_lrp(prev["point"], pts[i], t)
        if p["x"] == prev["point"]["x"] and p["y"] == prev["point"]["y"]:
            continue
        distance = v_len(sub(p, prev["point"]))
        running += distance
        if i < last_index and not reached_minimum:
            if running < size:
                continue
            reached_minimum = True
        prev = {
            "point": p,
            "pressure": DEFAULT_PRESSURE,
            "vector": v_uni(sub(prev["point"], p)),
            "distance": distance,
            "runningLength": running,
        }
        stroke.append(prev)
    stroke[0]["vector"] = stroke[1]["vector"] if len(stroke) > 1 else point(0, 0)
    return stroke


def pen_outline(input_points, width):
    """getStrokeOutlinePoints for the fixed options above."""
    size = width
    points = pen_stroke_points(input_points, size)
    if not points or size <= 0:
        return []
    total = points[-1]["runningLength"]
    taper_start = size * PEN_TAPER_PER_WIDTH
    taper_end = size * PEN_TAPER_PER_WIDTH
    min_distance = (size * PEN_SMOOTHING) ** 2
    left, right = [], []

    prev_pressure = points[0]["pressure"]
    for curr in points[:10]:
        prev_pressure = (prev_pressure + simulate_pressure(prev_pressure, curr["distance"], size)) / 2

    radius = stroke_radius(size, points[-1]["pressure"])
    first_radius = None
    prev_vector = points[0]["vector"]
    prev_left = points[0]["point"]
    prev_right = prev_left
    temp_left = prev_left
    temp_right = prev_right
    prev_sharp = False

    for i, sp in enumerate(points):
        p, vector, distance, running = sp["point"], sp["vector"], sp["distance"], sp["runningLength"]
        is_last = i == len(points) - 1
        if not is_last and total - running < END_NOISE_THRESHOLD:
            continue
        pressure = simulate_pressure(prev_pressure, distance, size)
        radius = stroke_radius(size, pressure)
        if first_radius is None:
            first_radius = radius
        start_strength = (lambda x: x * (2 - x))(running / taper_start) if running < taper_start else 1
        end_strength = (lambda x: (x - 1) * (x - 1) * (x - 1) + 1)((total - running) / taper_end) if total - running < taper_end else 1
        radius = max(MIN_RADIUS, radius * min(start_strength, end_strength))

        next_vector = (points[i + 1] if not is_last else sp)["vector"]
        next_dpr = v_dpr(vector, next_vector) if not is_last else 1.0
        prev_dpr = v_dpr(vector, prev_vector)
        is_sharp = prev_dpr < 0 and not prev_sharp
        is_next_sharp = next_dpr < 0
        if is_sharp or is_next_sharp:
            offset = mul(v_per(prev_vector), radius)
            step = 1 / CORNER_CAP_SEGMENTS
            t = 0
            while t <= 1:
                temp_left = v_rot_around(sub(p, offset), p, FIXED_PI * t)
                left.append(temp_left)
                temp_right = v_rot_around(add(p, offset), p, FIXED_PI * -t)
                right.append(temp_right)
                t += step
            prev_left = temp_left
            prev_right = temp_right
            if is_next_sharp:
                prev_sharp = True
            continue
        prev_sharp = False
        if is_last:
            offset = mul(v_per(vector), radius)
            left.append(sub(p, offset))
            right.append(add(p, offset))
            continue
        offset = mul(v_per(v_lrp(next_vector, vector, next_dpr)), radius)
        temp_left = sub(p, offset)
        if i <= 1 or v_dist2(prev_left, temp_left) > min_distance:
            left.append(temp_left)
            prev_left = temp_left
        temp_right = add(p, offset)
        if i <= 1 or v_dist2(prev_right, temp_right) > min_distance:
            right.append(temp_right)
            prev_right = temp_right
        prev_pressure = pressure
        prev_vector = vector

    first = points[0]["point"]
    last = points[-1]["point"] if len(points) > 1 else add(points[0]["point"], point(1, 1))
    if len(points) == 1:
        # A dot: the stroke is complete, so even a tapered one draws a circle.
        center = first
        start = add(center, mul(v_uni(v_per(sub(center, add(center, point(1, 1))))), -(first_radius or radius)))
        dot = []
        step = 1 / START_CAP_SEGMENTS
        t = step
        while t <= 1:
            dot.append(v_rot_around(start, center, FIXED_PI * 2 * t))
            t += step
        return dot
    # Both ends taper, so there is no start cap and the end is the last point.
    return left + [last] + list(reversed(right))


def pen_case(description, points, width):
    pts = [point(x, y) for x, y in points]
    return {"description": description, "points": pts, "width": width, "expected": pen_outline(pts, width)}


def generate_pens():
    wave = [(20 + i * 6, 100 + 30 * math.sin(i / 5)) for i in range(60)]
    zigzag = [(20 + i * 4, 60) for i in range(30)] + [(136 - i * 4, 60 + i * 3) for i in range(1, 30)]
    slow = [(50 + i * 0.5, 50 + i * 0.25) for i in range(80)]
    return {
        "pen.json": {
            "description": "Freehand outlines: a smooth wave, a sharp reversal, slow dense input, a two-point dash, a single dot, and a thick stroke.",
            "cases": [
                pen_case("wave", wave, 6),
                pen_case("sharp reversal", zigzag, 8),
                pen_case("slow dense input", slow, 4),
                pen_case("two points", [(10, 10), (90, 40)], 6),
                pen_case("single point", [(40, 40)], 10),
                pen_case("thick wave", wave, 24),
            ],
        },
    }


def line_case(start, end, width, start_cap, end_cap, scale=1):
    start = point(*start)
    end = point(*end)
    return {
        "start": start,
        "end": end,
        "width": width,
        "startCap": start_cap,
        "endCap": end_cap,
        "scale": scale,
        "expected": line_outline(start, end, width, start_cap, end_cap, scale),
    }


def generate_lines():
    return {
        "line.json": {
            "description": "Straight line outlines for each pair of end caps, a diagonal, a preview scale, and a zero-length line (no points).",
            "cases": [
                line_case((100, 100), (400, 100), 8, start_cap, end_cap)
                for start_cap in sorted(VALID_CAPS)
                for end_cap in sorted(VALID_CAPS)
            ] + [
                line_case((50, 300), (350, 60), 6, "round", "square"),
                line_case((100, 100), (400, 100), 6, "round", "round", scale=0.5),
                line_case((120, 80), (120, 80), 6, "round", "round"),
            ],
        },
    }


def arrow_case(start, end, width, preset=None, scale=1, start_cap="round", join="round", **overrides):
    values = dict(ARROW_PRESETS[preset or "classic"])
    values.update(overrides)
    start = point(*start)
    end = point(*end)
    return {
        "start": start,
        "end": end,
        "width": width,
        "bend": values["bend"],
        "startWidthScale": values["startWidthScale"],
        "endWidthScale": values["endWidthScale"],
        "headScale": values["headScale"],
        "startCap": start_cap,
        "join": join,
        "scale": scale,
        "expected": arrow_outline(
            start, end, width, values["bend"], values["startWidthScale"], values["endWidthScale"],
            values["headScale"], start_cap, join, scale,
        ),
    }


def generate_arrows():
    fixtures = {}
    for preset in sorted(ARROW_PRESETS):
        fixtures[f"{preset}.json"] = {
            "description": f"The {preset} preset: thin (minimum head), thick, diagonal, upward, short (head clamp), and a preview scale.",
            "preset": ARROW_PRESETS[preset],
            "cases": [
                arrow_case((100, 100), (400, 100), 2, preset),
                arrow_case((100, 100), (400, 100), 10, preset),
                arrow_case((50, 300), (350, 60), 6, preset),
                arrow_case((200, 400), (200, 80), 4, preset),
                arrow_case((10, 10), (22, 14), 8, preset),
                arrow_case((100, 100), (400, 100), 6, preset, scale=0.5),
            ],
        }
    fixtures["options.json"] = {
        "description": "Tail caps, head corners, a right bend, a full bend, a wide tail, a small head that clamps the shaft, and a zero-length arrow.",
        "cases": [
            arrow_case((100, 100), (400, 100), 8, start_cap="butt"),
            arrow_case((100, 100), (400, 100), 8, start_cap="square"),
            arrow_case((100, 100), (400, 100), 8, join="miter"),
            arrow_case((100, 100), (400, 100), 8, join="bevel"),
            arrow_case((100, 100), (400, 220), 6, bend=-0.3),
            arrow_case((100, 100), (400, 100), 6, bend=1),
            arrow_case((100, 100), (400, 100), 6, startWidthScale=2, endWidthScale=0.5),
            arrow_case((100, 100), (400, 100), 20, endWidthScale=2, headScale=0.5),
            arrow_case((120, 80), (120, 80), 6),
        ],
    }
    fixtures["handle.json"] = {
        "description": "The bend a dragged handle sets: on the curve, off to the side (projected), past the clamp, and on a zero-length arrow.",
        "cases": [
            {"start": point(100, 100), "end": point(400, 100), "handle": point(250, 40), "expected": arrow_bend(point(100, 100), point(400, 100), point(250, 40))},
            {"start": point(100, 100), "end": point(400, 100), "handle": point(310, 130), "expected": arrow_bend(point(100, 100), point(400, 100), point(310, 130))},
            {"start": point(50, 300), "end": point(350, 60), "handle": point(300, 300), "expected": arrow_bend(point(50, 300), point(350, 60), point(300, 300))},
            {"start": point(100, 100), "end": point(110, 100), "handle": point(105, 400), "expected": arrow_bend(point(100, 100), point(110, 100), point(105, 400))},
            {"start": point(120, 80), "end": point(120, 80), "handle": point(130, 90), "expected": arrow_bend(point(120, 80), point(120, 80), point(130, 90))},
        ],
    }
    return fixtures


def generated_files():
    files = {}
    for filename, fixture in generate_arrows().items():
        files[Path("arrow") / filename] = fixture
    for filename, fixture in generate_pens().items():
        files[Path("pen") / filename] = fixture
    for filename, fixture in generate_lines().items():
        files[Path("line") / filename] = fixture
    for filename, fixture in generate_alignments().items():
        files[Path("alignment") / filename] = fixture
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
