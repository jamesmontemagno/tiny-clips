# Tiny Clips screenshot editor shared fixtures

`shared/screenshot/fixtures/` contains JSON golden files for the annotation geometry the screenshot editors share. The same files are loaded by `mac/TinyClipsTests` and `windows/tests/TinyClips.Core.Tests` so the Swift and C# implementations draw the same shapes. The design notes are in [`docs/screenshot-annotation-strokes-research.md`](../../docs/screenshot-annotation-strokes-research.md).

## Formats

- `fixtures/arrow/*.json`: a description and `cases`, each with a `start` and `end` point (y-down), a stroke `width` in source-image pixels, a `style` (`straight`, `curvedLeft`, or `curvedRight`), a `scale` (the caller's units per image pixel), and the `expected` arrow: whether it is `curved`, the quadratic shaft (`shaftStart`, `shaftControl`, `shaftEnd`), and the head triangle (`tip`, `head1`, `head2`). `curvedLeft` bows to the left of the direction of travel; on Windows it is `ArrowStyle.Curved1`.

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
