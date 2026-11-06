# Editor Themes Plan

Tracks [#569](https://github.com/jamesmontemagno/tiny-clips/issues/569).

## Goal

Let people save a named look for screenshot annotations and the export canvas, then switch looks from the editor in one step, on macOS and Windows. The reporter's example is one theme per brand.

## Confirmed Decisions

- Switching happens in the editor. Settings holds the manager.
- Name the feature **Editor Themes**. Windows already uses "Theme" for light and dark.
- **None** is the default and keeps today's behavior.
- Shape gradients and shadows ship in the first version.
- Switching themes restyles existing annotations and the canvas as one undo step.
- Redactions and emoji are never restyled, so a theme can never weaken a redaction.
- Themes are free. Nothing in the editor is Pro-gated today.
- Agent Control covers every capability in the same change.

## Behavior

- The selected theme carries over to new editor windows.
- Switching also sets the defaults for new annotations.
- Manual edits don't change the saved theme. The picker shows **Modified** and offers **Update** and **Save as New**.
- A theme applies its background on open only if the theme opts in, and that counts as an edit.
- Captures that skip the editor stay unthemed.

## What a Theme Contains

| Group | Settings |
| --- | --- |
| Shapes | Stroke color and width, solid or gradient fill, shadow |
| Arrows, lines, pen | Color, width, arrow style |
| Text | Color, font, size, bold, italic, underline, box style |
| Numbers | Badge color, text color, size |
| Canvas | Solid or gradient background, padding, corners, shadow, frame, alignment |

Themes leave out redaction, emoji, image backgrounds, and strikethrough, which only Windows has.

## Shared Shape Effects

Rectangles and ellipses get these as regular editor features, with or without a theme. Arrows, lines, text, and number badges can follow later.

```text
ShapeEffects
  isGradientFill
  gradientEndColor
  gradientDirectionDegrees   0 is left to right, 90 is top to bottom
  hasShadow
  shadowColor                includes opacity
  shadowBlur                 0 means no shadow
```

Rules for both platforms:

- A gradient runs from the shape's fill color to `gradientEndColor`, uses the canvas background's gradient math, and rotates with the shape.
- A gradient draws only when the shape has a fill.
- `shadowBlur` follows CSS: the Gaussian standard deviation is half the blur.
- The shadow falls straight down by `0.35 × shadowBlur`, and stays pointing down when the shape rotates.
- The shadow is cast by the fill and stroke drawn together, so a shape with no fill casts only its outline.
- Shadows never change selection or hit testing.
- Defaults last for one editor window. Edits to a selected shape are one undo step, including a slider drag.
- Export is authoritative, and the preview must match it.

### Units

Mac editor sizes are reference units: strokes scale by `max(1, width / 900)`. Windows editor sizes are image pixels. Shadow blur follows each platform's stroke rule, and agent values are always image pixels. Themes must store the Mac unit and convert on Windows for each image.

### Agent fields

`editor.annotate` accepts these on `rectangle` and `ellipse`:

| Field | Meaning |
| --- | --- |
| `fillEndColor` | Gradient end color. Needs `fillColor`, which is the start color. |
| `fillDirection` | Gradient direction, 0 to 360 degrees. Needs `fillEndColor`. Default 45. |
| `shadowBlur` | Blur in image pixels, 0 to 500. 0 means no shadow. |
| `shadowColor` | Shadow color with opacity. Needs `shadowBlur`. Default `#59000000`. |

## Delivery

| PR | App work | Agent Control |
| --- | --- | --- |
| 1 | Shape effects: inspector controls and all four renderers | New fields on `editor.annotate` |
| 2 | Theme format, storage, shared test fixtures | Commands to list, get, save, set active, and delete (delete needs approval) |
| 3 | Editor picker, switching, one-step undo, save from editor | `themeId` on `editor.open` and `editor.annotate`; explicit values win |
| 4 | Settings page, import and export | Import and export if agents need them |
| Later | Rounded shape corners, effects on arrows and text, image backgrounds | New style fields |

## Risks

- **Windows shadow preview:** the canvas preview's `ThemeShadow` has no blur or color settings, so shape shadows use a composition `DropShadow` for the preview and a Win2D shadow for export. A blur radius of three standard deviations came close to the export on a Windows runner at 94% zoom. For target standard deviations of 10 and 20, the preview measured 9.5 and 19.6, and the export 10.1 and 19.9. The preview's shadow edge sat about 1.5 px closer to a shape with a 2 px stroke, partly because XAML draws strokes inside a shape's bounds.
- **Canvas shadow differences:** Mac draws 25% black with no offset, and Windows exports about 47% black shifted down. A theme's canvas shadow will look different on each platform until this is fixed separately, because a fix changes existing output.
- **Undo:** Mac history leaves out the canvas. Windows only undoes style edits and the last added annotation, and has no redo. PR 3 has to make a theme switch one undo step on both.
- **Save immediately:** the raw capture is already saved when the editor opens, so a theme background that applies on open must not make an untouched editor prompt unexpectedly.
