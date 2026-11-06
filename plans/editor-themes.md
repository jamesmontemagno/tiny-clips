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
- A theme applies its background on open only if the theme opts in. An untouched editor still doesn't prompt on close.
- Captures that skip the editor stay unthemed.

## What a Theme Contains

Each annotation type has its own section with its own settings, so changing one never changes another.

| Section | Settings |
| --- | --- |
| Rectangle | Stroke color and width, solid or gradient fill, shadow |
| Ellipse | Stroke color and width, solid or gradient fill, shadow |
| Arrow | Color, width, style |
| Line | Color, width |
| Pen | Color, width |
| Text | Color, font, size, bold, italic, underline, box style |
| Number | Badge color, text color, size |
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

## Theme Design

The format, units, ranges, and storage are in [`shared/editor-themes/README.md`](../shared/editor-themes/README.md).

### Switching

- **None** stops theming and changes nothing: annotations, canvas, and current defaults stay as they are. It is not an undo step.
- Choosing a theme restyles every annotation it covers, applies its canvas, and sets the defaults for new annotations as one undo step. It always applies the canvas, whatever `canvas.applyOnOpen` says.
- An image background stays when a theme applies, because themes leave out images. The theme's padding, corners, shadow, frame, and alignment still apply.
- Restyling never moves an annotation. A number keeps its center. Text keeps its anchor, which is the center on Mac and the top left on Windows.
- The picker choice is saved as the active theme for new windows. Open windows don't follow changes made elsewhere.
- Opening an editor with an active theme sets its defaults, and applies its canvas only if `canvas.applyOnOpen` is set. That rebaselines the document, so an untouched editor doesn't prompt on close. Save still exports the background.

### Modified, Update, and Save as New

The look to save is computed from the document, not from hooks on each control. For each annotation type:

1. Use the newest annotation of that type that differs from the theme.
2. Otherwise use the defaults for new annotations, when they differ.
3. Otherwise keep the theme's values.

The canvas is compared with its state when the theme became active, because a theme that doesn't apply on open must not read as Modified. **Modified** means this result differs from the saved theme, after rounding sizes to two decimals. **Update** saves it over the theme. **Save as New** saves it under a new name and makes it active. With None selected, the look starts from the default theme. After either one, older annotations keep their looks, and those looks stop counting as edits.

Tools share one color and one width in the inspector, and rectangles and ellipses share one fill and one set of effects. While a theme is active, each tool keeps its own, stored when the tool is left and loaded when it is chosen. With None, tools share them as they do today.

On Windows, a theme's pixel sizes use the image width when the theme was applied, so a later crop doesn't read as Modified.

### Undo

- **Mac:** history entries gain an optional part holding the canvas, theme choice, and defaults. Only theme switches record it, so other undo steps still leave the canvas alone.
- **Windows:** a style undo entry can hold many annotations plus the same optional part. Like every style undo entry, it is dropped by the next structural edit, such as adding or moving an annotation.

### Agents

Agent values use the theme JSON. `editor.themes.get` is export and `editor.themes.save` is import, so agents don't need file paths. `editor.themes.delete` needs approval through the `delete` scope. Unknown ids return `not_found`, and invalid themes return `invalid_params`.

## Delivery

| PR | App work | Agent Control | Status |
| --- | --- | --- | --- |
| 1 | Shape effects: inspector controls and all four renderers | New fields on `editor.annotate` | #580 |
| 2 | Theme format, storage, shared fixtures, editor picker, switching, one-step undo, Settings page, import and export | `editor.themes.*`; `themeId` on `editor.open` and `editor.annotate` | This PR |
| Later | Rounded shape corners, effects on arrows and text, image backgrounds | New style fields | |

## Risks

- **Windows shadow preview:** the canvas preview's `ThemeShadow` has no blur or color settings, so shape shadows use a composition `DropShadow` for the preview and a Win2D shadow for export. A blur radius of three standard deviations came close to the export on a Windows runner at 94% zoom. For target standard deviations of 10 and 20, the preview measured 9.5 and 19.6, and the export 10.1 and 19.9. The preview's shadow edge sat about 1.5 px closer to a shape with a 2 px stroke, partly because XAML draws strokes inside a shape's bounds.
- **Canvas shadow differences:** Mac draws 25% black with no offset, and Windows exports about 47% black shifted down. A theme's canvas shadow will look different on each platform until this is fixed separately, because a fix changes existing output.
- **Undo:** Windows only undoes style edits and the last added annotation, and has no redo. Undoing a theme switch there stops being possible after the next structural edit.
