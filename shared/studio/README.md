# Tiny Clips Studio shared fixtures

`shared/studio/fixtures/` contains JSON golden files for the Tiny Clips Studio pure layout and time-map contracts in [`docs/studio-project-format.md`](../../docs/studio-project-format.md). The same files are loaded by `mac/TinyClipsTests` and `windows/tests/TinyClips.Core.Tests` so the Swift and C# implementations stay in step.

## Formats

- `fixtures/layout/*.json`: a description, a complete `project` (except fixtures whose description says optional defaults were deliberately omitted), the expected `naturalCanvas`, and resolved layout `cases` for section 6. A fixture may carry an `events` member, an `events.json`, which the layout is then resolved with; the `zoom-*.json` files cover section 6.8.
- `fixtures/timemap/*.json`: a source duration, edits, expected kept segments, output duration, and source/output query pairs for section 7.
- `fixtures/canvas/*.json`: export-size cases for section 5, each with a natural canvas size, long-side limit, and expected export size.
- `fixtures/autozoom/*.json`: a `project`, its `events`, and the zoom suggestions section 8 gives for them.

## Regenerating

Run from the repository root:

```powershell
python -B shared/studio/tools/generate_fixtures.py
```

To verify the checked-in files are current and that there are no stray fixtures:

```powershell
python -B shared/studio/tools/generate_fixtures.py --check
```

The generator is a stdlib-only reference implementation of the spec math. Its output is deterministic: two-space JSON, stable key order, full double-precision values, LF line endings, and a trailing newline.

Fixtures should change only when the Studio project-format spec changes. When they do, regenerate the files rather than editing expected values by hand.
