# Screenshot Annotation Strokes, Arrows, and Hand-Drawn Style: Research

Status: implemented on macOS and Windows. The hand-drawn style and the Curved arrow preset shipped in early builds and were then removed after testing (see [3.5](#35-decisions-made-after-the-research)).
Scope: macOS (`mac/`) and Windows (`windows/`) screenshot editor annotation tools.

Requested features:

1. Stroke color, thickness, alignment (inside/center/outside), caps, and joins for shapes.
2. Better arrows: tail width, end width, curvature (bend), and head size, with tapered, curved, polished results.
3. A hand-drawn ("sketchy") line style.

The user has already made these decisions (see [Recommendation](#3-recommendation)):

- Bend is set with an on-canvas midpoint handle, and the inspector also shows its value.
- Mac and Windows ship together with the same features, checked by shared geometry fixtures in `shared/`.
- Sketch style applies to rectangles, ellipses, lines, and arrows. The freehand pen gets a smoothed, tapered stroke (perfect-freehand style) instead.
- Arrow UI has a few presets plus an Advanced section with start width, end width, head size, and arc.
- Rounded ends must be offered, or the user must be able to control the ends.

---

## 1. Current code

### 1.1 Tools and style properties today

| | macOS | Windows |
|---|---|---|
| Tools | move, crop, rectangle, circle, arrow, line, pencil, text, number, emoji, blur (`EditTool`, `ScreenshotEditorModels.swift`) | Select, Crop, Rectangle, Ellipse, Arrow, Line, Pen, Text, Counter, Emoji, Redact (`EditTool`, `TinyClips.Core/Editing/ScreenshotRenderState.cs`) |
| Annotation model | `struct ScreenshotAnnotation` in `ScreenshotEditorModels.swift`: `color`, `fillColor`, `lineWidth`, `points` (normalized 0…1), `rect` (normalized), `arrowStyle`, `rotation`, plus text fields | `class ScreenshotAnnotation` (Core) with an immutable `AnnotationSnapshot` record: `Color`, `FillColor`, `Thickness`, `Points` (image px), `Bounds` (image px), `ArrowStyle`, `Rotation`, plus text fields. The App subclass `Annotation` (`EditorModels.cs`) adds only preview caches. |
| Stroke color | Yes. It sets the default for new annotations only, except for text and number badges (`primaryColorBinding`, `ScreenshotEditorWindow.swift`). A selected rectangle, line, or other shape cannot be recolored. | Yes. `SetStrokeColor` updates the selected annotation or the default (`EditorController.cs`). |
| Stroke width | `lineWidth`. The picker offers 1/2/4/6/8/10 and defaults to 4. It sets the default only and does not change the selected shape. | `Thickness`. A slider goes from 1 to 40 and defaults to 6. `SetStrokeThickness` updates the selected annotation. |
| Width units | Relative to the image width. Export uses `lineWidth × max(1, outputWidth/900)` (`exportStrokeWidth`). Preview uses `lineWidth × zoomScale`. | Image pixels. Preview multiplies by the view scale. |
| Fill | Rectangle and circle (`fillColor`). | Rectangle and ellipse (`FillColor`, `FillEnabled`). |
| Arrow style | `ArrowStyle` is `.straight`, `.curvedLeft`, or `.curvedRight`. The quadratic control point is offset `max(20, 0.25·len)` along the normal (`arrowControlPoint`, Models line ~1160). | `ArrowStyle` is `Straight`, `Curved1`, or `Curved2`. The quadratic bow is `0.22·len` (`EditorController.BuildArrow`). |
| Arrowhead | A filled triangle with ±30°. Head length is `max(18·zoom, 4w)` in preview and `max(26, 5w)` in export. | A filled triangle with ±π/7. Head length is `max(12, 3.5·t)`. |
| Caps and joins | None set. SwiftUI and CoreGraphics use their defaults (butt cap, miter join). | Line, arrow shaft, and pen use round caps. Pen also uses a round join. Rectangle and ellipse use Win2D's defaults (flat cap, miter join). |
| Alignment | Centered stroke only. | Centered stroke only. |
| Persistence | None. Annotations exist only in memory with an undo stack (`undoStack`, `recordHistory`). Export bakes them into pixels. | None. Annotations exist only in memory. `AnnotationSnapshot` feeds undo and export. |

### 1.2 Rendering paths

- **macOS preview:** a SwiftUI `Canvas` / `GraphicsContext` in `ScreenshotEditorCanvas.drawAnnotation`.
- **macOS export:** a separate Core Graphics `CGContext` path in `ScreenshotEditorViewModel.drawAnnotationCG`.
  - The two paths duplicate the arrow math with different constants. The head length differs, so the preview and the export do not match exactly today.
- **Windows preview:** retained XAML shapes (`Rectangle`, `Ellipse`, `Line`, `Path` with `QuadraticBezierSegment`, `Polyline`) in `EditorCanvas.xaml.cs`.
- **Windows export:** Win2D (`Microsoft.Graphics.Win2D` 1.4.0) in `EditorController.DrawAnnotationCore`.
  - Both paths call the same `EditorController.BuildArrow` geometry, so arrows match on Windows.
- **SkiaSharp** is referenced by Core, but only for image decoding and encoding (`ScreenshotBackgroundDecoder`, `WebpImageEncoder`). It is not used for annotation drawing.

### 1.3 Platform-neutral code and tests

- Windows Core (`TinyClips.Core/Editing`) holds pure, unit-tested math:
  - `RotatableAnnotationGeometry`, `EmojiAnnotationMath`, `TextBoxStyle`, and the snapshot types.
  - Tests are in `windows/tests/TinyClips.Core.Tests/ScreenshotExportTests.cs`, `EmojiAnnotationGeometryTests.cs`, and others.
- `BuildArrow` is in the App project, not Core, so it has no unit tests.
- On macOS, the equivalent pure helpers are in `ScreenshotEditorModels.swift`. Tests are in `mac/TinyClipsTests/CaptureMathTests.swift`.
- There are no shared fixtures for the screenshot editor today. `shared/studio/fixtures` (layout, timemap, canvas, autozoom) shows the pattern to follow:
  - A stdlib-only Python reference generator (`shared/studio/tools/generate_fixtures.py`, with `--check`) writes deterministic JSON.
  - `mac/TinyClipsTests/StudioFixtureTests.swift` loads the same files.
  - `windows/tests/TinyClips.Core.Tests` loads them as linked `Content` (`TinyClips.Core.Tests.csproj` line 24).

### 1.4 Gaps relevant to this work

- **macOS cannot restyle a selected shape.** Its color and width are fixed once the shape is drawn. Windows can restyle. Parity work has to fix this first.
- **Width units differ.** macOS uses the 900-px reference; Windows uses image pixels. Shared fixtures need one unit.
- **Curve direction labels differ.**
  - macOS `curvedLeft` = `mid − normal·k`, with normal `(−dy, dx)`.
  - Windows `Curved1` = `mid + perp·k`, with the same perpendicular.
  - So mac "Curved Right" matches Windows "Curved1". The labels and the sign convention need to be aligned.
- **Translucent colors double up where shapes overlap.** The color pickers allow opacity.
  - On Windows, the round-capped shaft runs into the head, and the pen draws each segment separately.
  - The overlapping areas therefore render darker with a translucent color. This is inferred from the code and has not been checked visually.
- **The mac pencil looks jagged.** It strokes a polyline with butt caps and miter joins.
- **Existing design convention:** `plans/cross-platform-text-box-styles.md` sets the rules this work follows:
  - Shared data contract with the same names on both platforms.
  - Style is copied by value into each annotation and included in snapshots and undo.
  - Per-editor defaults live in the view model or controller. No `UserDefaults` or settings keys.
  - Explicit "selected annotation" and "new-annotation defaults" contexts.
  - Store metrics in source-image pixels.
  - "The export renderer is authoritative," and the preview must match it.
  - Presets with a "Custom" state.
  - Pure helpers go in Core (Windows) or Models (mac), with deterministic tests.

---

## 2. Research (primary sources)

### 2a. Stroke alignment, caps, and joins

**Native APIs only stroke centered.**

- CoreGraphics: "the line straddles the path, with half of the total width on either side" ([CGContext.setLineWidth](https://developer.apple.com/documentation/coregraphics/cgcontext/setlinewidth(_:))).
- Win2D and Direct2D `DrawGeometry` also stroke along the outline. There is no alignment option on [CanvasStrokeStyle](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasStrokeStyle.htm).

**How Figma defines alignment.**

- `strokeAlign` is `CENTER`, `INSIDE`, or `OUTSIDE`.
- Figma notes that inside and outside strokes are made by doubling the stroke weight and masking it with the fill ([Figma strokeAlign](https://developers.figma.com/docs/plugins/api/properties/nodes-strokealign/)).
- Sketch's definition was not checked against a primary source.

**Techniques**

| Technique | macOS | Windows | Notes |
|---|---|---|---|
| Clip and double the width (Figma's method) | Stroke at `2w`, clipped to the shape for inside, or to the inverse for outside. Uses `GraphicsContext.clip(to:)`, which has an `inverse` option ([docs](https://developer.apple.com/documentation/swiftui/graphicscontext/clip(to:style:options:))). In CG, use an even-odd clip of the bounds plus the shape. | Clip with `CreateLayer(…, clipGeometry, …)` ([docs](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer_5.htm)). | Exact for any closed shape. Inside stays inside even when `w` exceeds the shape. |
| Inset or outset the path by `w/2`, then stroke | This is what SwiftUI `strokeBorder` does: "equivalent to insetting `self` by `lineWidth / 2` and stroking" ([docs](https://developer.apple.com/documentation/swiftui/insettableshape/strokeborder(_:linewidth:antialiased:))). | Insetting a `Rect` is trivial. XAML `Rectangle` and `Ellipse` can use it directly. | Exact for rectangles. Only approximate for ellipses, because an ellipse's offset curve is not an ellipse; the error grows with eccentricity and width. |
| Turn the stroke into a geometry, then combine it with the shape | `CGPath.copy(strokingWithWidth:lineCap:lineJoin:miterLimit:)`: "filling the new path draws the same pixels as stroking the original path" ([docs](https://developer.apple.com/documentation/coregraphics/cgpath/copy(strokingwithwidth:linecap:linejoin:miterlimit:transform:))). Then `intersection` or `subtracting` (macOS 13+; [docs](https://developer.apple.com/documentation/coregraphics/cgpath/subtracting(_:using:)). The app targets macOS 15). | `CanvasGeometry.Stroke` ([docs](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry_Stroke.htm)) plus `CombineWith` ([docs](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry_CombineWith.htm)). This wraps Direct2D [Widen](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview#widen) and [CombineWithGeometry](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview#combining-geometries) (union, intersect, xor, exclude). | Produces one fillable geometry, which is also good for hit testing and bounds. Costs more CPU. |

**Where alignment applies**

- It is meaningful only for closed shapes: rectangle and ellipse. A filled redaction rect has no stroke.
- Open paths (line, arrow, pencil) have no inside or outside. Hide the control for them.
- With inside alignment, the stroke covers the edge of the fill. With outside alignment, the fill stays fully visible.
- Selection bounds and hit testing must grow by `w` for outside alignment, and by `w/2` for center.

**Caps and joins**

| | CoreGraphics / SwiftUI | Win2D | XAML preview `Shape` |
|---|---|---|---|
| Caps | `CGLineCap`: `butt` (the default), `round`, `square` ([docs](https://developer.apple.com/documentation/coregraphics/cglinecap)). There is one `lineCap` per stroke, set in `StrokeStyle` ([docs](https://developer.apple.com/documentation/swiftui/strokestyle)). | `CanvasCapStyle`: `Flat`, `Square`, `Round`, `Triangle` ([docs](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCapStyle.htm)). `StartCap` and `EndCap` are separate, and both default to Flat. | `StrokeStartLineCap` and `StrokeEndLineCap` are separate ([Shape](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.shapes.shape)). |
| Joins | `CGLineJoin`: `miter` (the default), `round`, `bevel`, plus `miterLimit`. | `CanvasLineJoin`: `Miter`, `Bevel`, `Round`, `MiterOrBevel`, plus `MiterLimit` ([docs](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasLineJoin.htm)). | `StrokeLineJoin` and `StrokeMiterLimit`. |

- **Separate start and end caps on macOS.** CoreGraphics has only one `lineCap`, so there are two workarounds:
  - (a) Stroke with butt caps and add each end's cap as geometry: a semicircle for round, or a `w/2` rectangle for square.
  - (b) Build the whole line as a filled outline, which is the recommendation below. Either way, combine everything into one path, or draw it in a transparency layer, before applying alpha.
- **Caps on tapered shapes.** A tapered or variable-width arrow is a *filled polygon*, so native caps do not apply. A rounded tail or tip has to be part of the outline geometry. For example, perfect-freehand's outline adds a round cap that rotates the edge point around the end by π, or four corner points for a flat cap ([getStrokeOutlinePoints.ts](https://github.com/steveruizok/perfect-freehand/blob/main/packages/perfect-freehand/src/getStrokeOutlinePoints.ts)).
- **Caps and alignment.** These never apply to the same shape: caps are for open paths, alignment is for closed ones. Joins and alignment do interact:
  - With clip-and-double, the join shapes only the outer corner. For outside alignment, miter gives sharp corners and round gives rounded ones. The inner corner is always sharp.
- **Avoiding darker overlaps.** Draw each annotation at full opacity inside a layer, then composite the layer once with the annotation's alpha.
  - On macOS, `beginTransparencyLayer` draws into a separate backdrop that is "composited into the context using the global alpha" ([docs](https://developer.apple.com/documentation/coregraphics/cgcontext/begintransparencylayer(auxiliaryinfo:))).
  - On Windows, use `CreateLayer(opacity, …)` ([docs](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer_5.htm)).
  - This fixes the double-stroke overlap from sketch mode and the joints in pen strokes.

### 2b. Arrows

**tldraw** (the source is readable, but the license is *not* open source; see [2d](#2d-licenses-and-ports)):

- An arc arrow has a signed `bend` (absolute distance, multiplied by `scale`).
- The middle handle is `med + perp(u)·(−bend)`. The arrow body is the circle through start, middle, and end (`getArcInfo`, `centerOfCircleFromThreePoints`). It falls back to a straight arrow when the arc is degenerate ([curved-arrow.ts](https://github.com/tldraw/tldraw/blob/main/packages/tldraw/src/lib/shapes/arrow/curved-arrow.ts)).
- Props include `kind`, `bend`, `arrowheadStart`, `arrowheadEnd`, `size`, `scale`, and `dash` ([TLArrowShapeProps](https://tldraw.dev/reference/tlschema/TLArrowShapeProps)).
- Head length is `clamp(bodyLength/5, strokeWidth, strokeWidth·3)`.
- Wings are rotated ±π/6. On arcs, the head's direction comes from where a circle of radius `length` around the tip meets the arc, which gives a chord direction rather than the exact tangent ([arrowheads.ts](https://github.com/tldraw/tldraw/blob/main/packages/tldraw/src/lib/shapes/arrow/arrowheads.ts)).

**Excalidraw** (MIT):

- Linear and arrow elements have `points`, `startArrowhead`, `endArrowhead`, and `elbowed`. Every element has `strokeWidth`, `strokeStyle`, `roughness`, and a `seed`, documented as "used to seed shape generation so that the roughjs shape doesn't differ across renders" ([types.ts](https://github.com/excalidraw/excalidraw/blob/master/packages/element/src/types.ts)).
- Head size depends on the type (`arrow` is 25 px, most others 15). It is capped by a fraction of the last segment's length.
- Orientation comes from the last Bézier op evaluated at `t = 0.3`. The wings use ±20° for `arrow`, widening toward 30° as the head shrinks ([bounds.ts](https://github.com/excalidraw/excalidraw/blob/master/packages/element/src/bounds.ts)).
- Unverified: the exact per-type length multiplier. The fetched source line looked malformed.

**Standard construction for a polished tapered arrow** (the math is standard; this is a synthesis, not taken from one library):

1. **Centerline.** Keep the existing quadratic Bézier. Both apps already use one, and on Windows `BuildArrow` already uses the tangent `B′(1) ∝ (P2 − P1)` ([Bézier derivative](https://en.wikipedia.org/wiki/B%C3%A9zier_curve#Quadratic_B%C3%A9zier_curves)).
   - A midpoint handle `H` on the curve maps exactly to the control point `C = 2H − mid`, because `B(½) = ¼P0 + ½C + ¼P2`.
   - So storing `bend` = (signed offset of H from the chord midpoint) / (chord length) gives a handle that stays on the curve and scales when an endpoint moves.
   - A circular arc, as tldraw uses, is an alternative. It is not needed for parity.
2. **Head.** Set the head length from the stroke width with a minimum, then clamp it to a fraction of the arrow length so short arrows still read correctly. Point the head along the end tangent, and trim the shaft at the head's base using de Casteljau, as `BuildArrow` already does.
3. **Tapered shaft.**
   - Sample the curve at N points. Use arc-length fractions `s`.
   - Width is `w(s) = lerp(startWidth, endWidth, s)`.
   - Offset each point left and right along the normal by `w/2`.
   - Then emit one closed polygon: left edge → head wing → tip → other wing → right edge reversed → tail cap.
   - Fill it once. This is perfect-freehand's layout (left points, end cap, reversed right points, start cap).
4. **Single fill.** One polygon removes the alpha overlap between shaft and head, and makes caps independent on both platforms.

### 2c. Hand-drawn styles

**rough.js** ([repo](https://github.com/rough-stuff/rough), [MIT license](https://github.com/rough-stuff/rough/blob/master/LICENSE)), from [renderer.ts](https://github.com/rough-stuff/rough/blob/master/src/renderer.ts):

- **Lines.** Each segment becomes one cubic Bézier.
  - Both endpoints and both controls are jittered by `roughness × roughnessGain × random(range)`.
  - `roughnessGain` is 1 below 200 px, 0.4 above 500 px, and interpolated in between.
  - The offset is clamped to `length/10` for short lines.
  - The controls sit at a random 20–40% along the segment and are pushed sideways by bowing: `midDisp = bowing × maxRandomnessOffset × Δ/200`.
- **Double stroke.** A second "overlay" pass uses half the offsets, unless `disableMultiStroke` is set. `preserveVertices` pins the endpoints.
- **Ellipses.** Built from about `max(curveStepCount, curveStepCount/√200 × perimeter)` steps. The radii are jittered by `(1 − curveFitting)`. The first pass overlaps past its start; a second pass at a 1.5× offset is added.
- **Roughness 0** gives exact geometry.
- **Defaults** ([generator.ts](https://github.com/rough-stuff/rough/blob/master/src/generator.ts)): `roughness 1`, `bowing 1`, `maxRandomnessOffset 2`, `curveFitting 0.95`, `curveStepCount 9`, `seed 0`.
- **Seeded random number generator** ([math.ts](https://github.com/rough-stuff/rough/blob/master/src/math.ts)): a Park–Miller "minstd" generator, `seed = imul(48271, seed)`, returning `(2³¹−1 & seed) / 2³¹`.
  - **Caveat:** `seed == 0` falls back to `Math.random()`, which is non-deterministic.
  - **Caveat:** `cloneOptionsAlterSeed` uses `seed + 1` for the second curve pass only when the seed is non-zero.
  - A port must reject 0. Swift and C# reproduce `imul` with wrapping 32-bit integer math: `&*` in Swift, `unchecked` in C#.

**perfect-freehand** ([repo](https://github.com/steveruizok/perfect-freehand), [MIT, © Stephen Ruiz Ltd](https://github.com/steveruizok/perfect-freehand/blob/main/LICENSE)):

- `getStroke(points, options)` returns a closed outline polygon to fill.
- Options and defaults: `size 8`, `thinning 0.5`, `smoothing 0.5`, `streamline 0.5`, `simulatePressure true`, `easing`, and `start`/`end` each with `{cap, taper, easing}` ([README](https://github.com/steveruizok/perfect-freehand/blob/main/README.md)).
- Radius comes from pressure, which is simulated from velocity when there is none. Taper multiplies the radius by an eased `runningLength / taperDistance` near each end. Sharp corners get rounded arcs ([getStrokeOutlinePoints.ts](https://github.com/steveruizok/perfect-freehand/blob/main/packages/perfect-freehand/src/getStrokeOutlinePoints.ts)).
- It uses no randomness, so it is deterministic from its input points.

**Which algorithm goes where**

- rough.js: sketchy rectangles, ellipses, lines, and arrow shafts and heads. The user decided these four.
- perfect-freehand: pen strokes, which the user also decided. Its outline approach (not the library itself) is also the model for tapered arrows (2b).

**Deterministic seed.** Store a random non-zero `UInt32` seed on each annotation when it is created. Keep it through moves, resizes, undo, and copy.

- If it is regenerated on each redraw, the wobble shimmers while dragging and the export differs from the preview.
- Excalidraw persists a `seed` for exactly this reason (see `types.ts` above).
- The derived per-pass seeds and the order of random draws must be identical on both platforms.

### 2d. Licenses and ports

| Library | License | Native port | Status |
|---|---|---|---|
| rough.js | MIT | [RoughSwift](https://github.com/onmyway133/RoughSwift): "using Rough.js via JavaScriptCore" ([README](https://github.com/onmyway133/RoughSwift/blob/master/README.md)). It lists iOS 16+ only and is MIT. | Not suitable. It wraps a JavaScript engine, does not target macOS, and does not help Windows. |
| rough.js | — | No C# or .NET port turned up in a web search (unverified: absence is not proven). [roughr](https://docs.rs/crate/roughr/latest) is an MIT Rust port that produces geometry only and could serve as a reference. | A port is required. |
| perfect-freehand | MIT | The README lists Dart, Odin, Python, and Rust ports. No Swift or C# port was found. | A port is required. It is about 300 lines of math. |
| tldraw | [tldraw license](https://github.com/tldraw/tldraw/blob/main/LICENSE.md): production use needs a license key. **Not MIT.** | — | Use it only as a conceptual reference. Do not copy its code. |
| Excalidraw | [MIT](https://github.com/excalidraw/excalidraw/blob/master/LICENSE) | — | It can be used as a reference. |

### 2e. Cross-platform strategy

Options:

- **(A) Port the algorithms once per language, into pure geometry modules, with shared JSON fixtures.**
  - On Windows this module goes in `TinyClips.Core/Editing`. On macOS it goes next to `ScreenshotEditorModels.swift`, or in a new `AnnotationGeometry.swift`.
  - A Python reference generator, mirroring `shared/studio/tools/generate_fixtures.py`, would emit `shared/screenshot/fixtures/{arrow,stroke,rough,freehand}/*.json`.
  - Each fixture holds inputs (points, width, bend, seed, options) and the expected output polygons or Bézier ops.
  - Both test suites would load the files exactly the way Studio fixtures are loaded today.
  - **This matches the user's decision and the existing convention.**
- (B) Embed JavaScript (JavaScriptCore or WebView2). This brings runtime and packaging weight and async rendering, and it breaks the "export is authoritative" rule. Rejected.
- (C) Use one C library through interop. There is no existing library, and it adds build complexity on both platforms. Rejected.

Notes for (A):

- Compare fixture outputs with a tolerance of about 1e-3 px. Apple libm and .NET `Math.Sin`/`Cos` can differ in the last bit, and Windows uses `float` `Vector2` while macOS uses `CGFloat` (double).
- Random-number output must match bit for bit, since it is integer math. Compute floats from it identically.
- Fixtures must use one unit, image pixels. macOS has to move from 900-px-reference widths to image pixels, or convert at the geometry boundary.

---

## 3. Recommendation

### 3.1 Shared data contract (same names on both platforms, copied by value into each annotation and snapshot)

```text
StrokeStyle                       applies to
  color            (existing)     rect, ellipse, line, arrow, pen
  width            image px       rect, ellipse, line, arrow, pen   (default 6; range 1…40)
  alignment        center|inside|outside   rect, ellipse only       (default center = today)
  lineJoin         miter|round|bevel       rect, arrowhead            (default round)
  startCap         butt|round|square       line, arrow tail          (default round)
  endCap           butt|round|square       line                      (default round)

ArrowGeometry                     arrow only
  preset           classic|tapered|custom
  bend             −1…1, H offset / chord length (default 0; mac's 0.25 curve ≈ 0.125, since H = ½ of the control offset)
  startWidthScale  0…2 × width     (classic 1.0, tapered 0.25)
  endWidthScale    0…2 × width     (classic 1.0, tapered 1.0)
  headScale        0.5…3           head length = headScale × max(4·width, 12px), clamped ≤ 0.45·arrow length
  headAngle        fixed ±28° (one shared constant; not user-facing)

Pen                               pen only
  freehand options: size = width, thinning 0.5, smoothing 0.5, streamline 0.5,
  simulatePressure true, start/end taper (fixed small taper), round caps
```

- **Presets:**
  - Classic: straight, uniform width, head scale 1.
  - Tapered: tail scale 0.25, end scale 1, head scale 1.2.
  - Editing any advanced field, or bending the arrow with its handle, shows **Custom**, following the text-box precedent.
- **Mapping the old style:** replace `ArrowStyle` with `bend`. `straight` becomes 0. The two curves become ±0.125 after the left/right sign fix described in 1.4.
- **Defaults** are remembered **per tool across sessions** in `CaptureSettings`: arrow width and preset do not leak into rectangles.
- **Hit testing and bounds** use the stroked outline (Win2D `StrokeContainsPoint`, or the mac stroked path), including the outside-alignment growth and the arrow polygon.

### 3.2 Rendering approach

- **Shared pure geometry modules** go in Core on Windows and Models on macOS. They are tested with fixtures:
  - `ArrowOutline(start, end, bend, widths, head, startCap) → polygon`: the centerline, de Casteljau trim, arc-length sampling, offsets, cap arcs, and head, as one closed polygon.
  - `FreehandOutline(points, options) → polygon`.
- **Platform code only fills or strokes the returned geometry:**
  - Arrows, lines, and pen strokes: fill the polygon. Caps are part of the geometry, so independent start and end caps work the same on macOS.
  - Rectangle and ellipse: stroke natively with `lineJoin`. Get alignment from clip-and-double for exact results on ellipses.
  - Wrap any annotation whose alpha is below 1 in a transparency layer (macOS) or `CreateLayer(opacity)` (Windows).
- **macOS:** make the preview (`GraphicsContext`) and export (`CGContext`) consume the *same* geometry functions. This removes today's duplicated arrow constants. The one `CGPath` per annotation can be wrapped in a SwiftUI `Path`.
- **Windows:**
  - Export stays in Win2D. Build a `CanvasGeometry.CreatePolygon` or `CanvasPathBuilder` from the shared points.
  - For the preview, XAML `Path` with a `PathGeometry` from the same points works for arrows, lines, and pen.
  - Exact ellipse alignment in XAML has no clip-geometry equivalent. Decided: accept the inset/outset approximation in the preview; export stays exact.
- **Bend handle:**
  - The handle is drawn at `H` on the curve.
  - Dragging it sets `bend` from the signed perpendicular distance to the chord.
  - Snap to 0 within a few pixels.
  - Show the value in the inspector's Advanced section as the "Arc" slider. Changes are undoable once per drag.

### 3.3 Implementation order

1. **Parity groundwork.**
   - macOS: let a selected shape's color and width be edited, with undo, like Windows.
   - Unify width units to image pixels.
   - Fix the curve sign and label mismatch.
   - Move `BuildArrow` into Core.
   - Add the `shared/screenshot` fixture generator and the test harness on both platforms.
2. **Caps, joins, and alpha layers** for existing tools. This is low-risk and immediately visible: rounded ends and no darker overlaps.
   - Done for lines, arrow tails, rectangles, and the pencil. Arrowhead corners wait for step 4, when the head becomes part of the arrow's outline.
3. **Stroke alignment** for rectangle and ellipse.
   - Done. Rectangles fill a ring (outer outline shaped by the join, minus a sharp inner rectangle) on every renderer, so all four match exactly. Ellipses clip a doubled stroke everywhere except the Windows XAML preview, which strokes an inset or outset ellipse.
4. **Arrow geometry.**
   - Done. An arrow is one nonzero-filled outline (24-sample tapered shaft trimmed by bisection at the head's base, head with corner fillets, tail cap). `bend` is the handle's offset as a fraction of the chord, positive to the left. The preset is derived from the values rather than stored. `ArrowStyle` is gone on both platforms; no saved data needed migrating.
   - The outline polygon, bend, and on-canvas handle.
   - Presets plus the Advanced section (start width, end width, head size, arc).
   - Migrate `ArrowStyle`.
   - Follow-up: arrows and lines are now a filled body (Fill = the annotation's color, Size = its width) with an optional outline (Stroke color, Stroke width, Stroke position defaulting to Outside), drawn by clipping a doubled stroke to the body. Lines became filled outlines (`shared/screenshot/fixtures/line`), which also replaced the macOS mixed-cap workaround and its fade layer.
5. **Freehand pen** using the perfect-freehand port.
   - Done. Fixed options: thinning 0.3 (0.5 drew a 6 px pen about 3 px wide), smoothing and streamline 0.5, simulated pressure, both ends tapered over 3 widths, `last = true`. Lengths use sqrt instead of hypot and cap loops keep the source's accumulating steps so all three implementations match bit for bit. Rendered through midpoint quadratic curves. Real pen pressure is not used yet (still an open question).
6. **Sketch style** using the rough.js port, with the seed.
   - Built, then removed after testing (see 3.5). Arrows keep the cubic centerline it introduced (the bend's quadratic raised to a cubic).
7. Accessibility pass (VoiceOver and UI Automation for the new controls and the bend handle), then changelogs.

### 3.4 Risks

| Risk | Mitigation |
|---|---|
| Floating-point drift between Swift (double) and C# (float `Vector2`) breaks fixtures | Compare with a tolerance. Use `double` in the C# geometry module, and convert to `Vector2` only at the draw call. |
| Tapered polygon self-intersects at high bend or very short length | Clamp bend and head relative to length. Fill with nonzero winding. Use a minimum sample count and fall back to straight when the arrow is degenerate, as tldraw does. |
| Exact ellipse alignment in the Windows XAML preview does not match export | Use a Win2D preview overlay, or accept a documented approximation. |
| Unit migration on macOS changes the look of existing widths | Annotations are not persisted, so only defaults and presets need remapping. Check the result visually at fit and native zoom. |
| Inspector clutter | Show only the controls that apply to the tool. Advanced arrow fields go in a disclosure. |
| License contamination from tldraw | Implement from the math. Do not translate tldraw code. |

### 3.5 Decisions (made after the research)

1. **Caps:** separate start and end controls in the UI. Both default to round on both platforms, so macOS switches from butt to round.
2. **Joins:** user-facing in the Advanced section for rectangles and arrowheads. Default is round.
3. **Alignment:** new rectangles and ellipses default to centered, as today.
4. **Hand-drawn style:** removed after testing. The look wasn't wanted, and the rough.js port was a lot of code to keep in step on two platforms.
5. **Curved preset:** removed. The on-canvas bend handle sets any curve, so a bent arrow shows **Custom**.
6. **Windows preview:** keep XAML. Ellipse inside/outside alignment is approximated (inset/outset) in the preview only; export stays exact in Win2D. Document the difference.
7. **Defaults:** remembered per tool across sessions, which needs new `CaptureSettings` keys on both platforms.

### 3.6 Still open

1. **Pen pressure:** use real pen or Force Touch pressure where available, or always simulate it?
2. **More arrowheads** (open, bar, dot) and double-headed arrows: now or later?
