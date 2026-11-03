# Tiny Clips screenshot editor shared fixtures

`shared/screenshot/fixtures/` contains JSON golden files for the annotation geometry the screenshot editors share. The same files are loaded by `mac/TinyClipsTests` and `windows/tests/TinyClips.Core.Tests` so the Swift and C# implementations draw the same shapes. The design notes are in [`docs/screenshot-annotation-strokes-research.md`](../../docs/screenshot-annotation-strokes-research.md).

## Formats

- `fixtures/arrow/{classic,tapered,curved}.json`: one preset each (its values in `preset`, which must match that preset by name) and `cases`, each with a `start` and `end` point (y-down), a stroke `width` in source-image pixels, the shape (`bend`, `startWidthScale`, `endWidthScale`, `headScale`), a tail `startCap`, a head corner `join`, a `scale` (the caller's units per image pixel), and the `expected` arrow: the curve's `control` point, the bend `handle`, and the outline `points` in order, to fill as one polygon with the nonzero rule. `bend` is the curve midpoint's offset from the chord midpoint as a fraction of the chord length; positive bows left of travel.
- `fixtures/arrow/options.json`: the same format without a preset, covering caps, corners, bends, widths, a clamped head, and a zero-length arrow (no points).
- `fixtures/line/line.json`: a `start`, an `end`, a `width`, a `startCap` and `endCap`, and a `scale`, with the `expected` outline points of the straight line, to fill as one polygon. Lines are filled outlines like arrows so each end can take its own cap and the body can carry an outline.
- `fixtures/pen/pen.json`: freehand `points` (image pixels) and a stroke `width`, with the `expected` outline points to fill as one polygon. The algorithm is a port of perfect-freehand (MIT) with fixed options; see the generator for them. Renderers draw quadratic curves through each pair of outline points' midpoints so the edge stays smooth.
- `fixtures/sketch/*.json`: the hand-drawn style (a port of rough.js, MIT). `random.json` holds the first values of the seeded generator, which must match bit for bit. `rectangle.json` and `ellipse.json` give `bounds`, a `width`, a `roughness`, and a `seed`, with the `expected` stroke as `move` and `bcurveTo` ops (rough.js's names) to stroke with round caps and joins. `line.json` and `arrow.json` use the line and arrow formats plus `roughness` and `seed`; their outlines follow the wobble. Shapes are computed in units of half the stroke width, so the wobble scales with it.
- `fixtures/arrow/handle.json`: a `start`, an `end`, and a dragged `handle` point, with the `expected` bend, clamped to -1...1.
- `fixtures/alignment/rectangle.json`: rectangle `bounds`, a stroke `width`, an `alignment` (`center`, `inside`, or `outside`), and a `join` (`miter`, `round`, or `bevel`), with the `expected` stroke ring: the `outer` rectangle, its `outerJoin` and `cornerSize` (the corner radius or bevel cut), and the always-sharp `inner` rectangle. Renderers fill the outer outline minus the inner rectangle.
- `fixtures/alignment/ellipse.json`: ellipse `bounds`, a `width`, and an `alignment`, with the `expected` ellipse to stroke at the plain width. Only the Windows live preview uses this approximation; renderers that can clip draw a doubled stroke clipped to the shape, so macOS does not load this file.

## Regenerating

Run from the repository root:

```bash
python3 -B shared/screenshot/tools/generate_fixtures.py
```

To verify the checked-in files are current and that there are no stray fixtures:

```bash
python3 -B shared/screenshot/tools/generate_fixtures.py --check
```

The generator is a stdlib-only reference implementation. Its output is deterministic: two-space JSON, stable key order, full double-precision values, LF line endings, and a trailing newline. When the geometry changes, change the generator and both platforms together, then regenerate the files rather than editing expected values by hand.
