# Tiny Clips Studio project format (v1)

This is the contract between the macOS and Windows implementations of Tiny Clips Studio: the files a project is made of, every field in them, and the math that turns a project into a frame layout. The design it serves is in [plans/video-studio-plan.md](../plans/video-studio-plan.md).

Both platforms implement sections 5 to 8 as pure functions, and both test suites run the shared fixtures in `shared/studio/fixtures/`. If the two implementations disagree, this document decides which one is wrong.

## 1. Files

```
<projects root>/<project id>/
  project.json   required
  events.json    optional: cursor, click, and camera-corner data
  screen.mp4     the screen source (absent for flat projects, see section 10)
  camera.mp4     optional
  poster.jpg     optional thumbnail
```

- Projects root on Windows: the app's local data folder, then `TinyClips\Projects`. For the installed (packaged) app that is `%LOCALAPPDATA%\Packages\<package family>\LocalState\TinyClips\Projects`; for an unpackaged run it is `%LOCALAPPDATA%\TinyClips\Projects`. On macOS: `Application Support/TinyClips/Projects`.
- The project id is a lowercase UUID in hyphenated form, such as `3f0013cf-ba10-4453-af91-792b7882dae6`.
- **The folder name is the id.** A store ignores any folder whose name is not such a UUID, and never builds a path from an id it has not validated. When `project.json` holds a different `id`, the folder name wins.
- `sources.screen.file` (unless `external` is true), `sources.camera.file`, and `sources.events` are file names inside the project folder. A value containing a path separator makes the project invalid. A `background.image` containing one is treated as missing.

## 2. Conventions

- UTF-8 JSON with camelCase property names. Writers emit indented JSON.
- **Unknown properties survive a save.** Every object type keeps the properties it does not recognize and writes them back unchanged.
- **Missing properties take their default.** A reader never fails because an optional property is absent. A missing timestamp reads as `1970-01-01T00:00:00Z`.
- **Required properties** have no default: `id`, `sources.screen.width`, `sources.screen.height`, `sources.screen.duration`, and, when `sources.camera` is present, its `width`, `height`, and `duration`. A project missing one of these is invalid and is not opened. So is one where a `width` or `height` is not an integer of at least 1, or a `duration` is negative.
- **`null` counts as missing** unless the property's type below says "or null". So a `null` optional property takes its default, and a `null` required property makes the project invalid. A `null` element inside an array is dropped.
- A reader refuses to open a project whose `schemaVersion` is greater than the version it supports.
- Enumerations are camelCase strings. An unrecognized value reads as the field's default.
- Times are seconds as finite doubles. Unless stated otherwise they are **source time**: seconds on the pause-adjusted recording timeline, where 0 is the first screen frame.
- Timestamps are ISO 8601 UTC with a `Z` suffix, written to whole seconds (`2026-10-02T22:41:00Z`). Readers accept fractional seconds.
- Colors are `#RRGGBB` in uppercase. Readers also accept lowercase and `#RRGGBBAA`.
- A `Rect` is `{ "x", "y", "width", "height" }`. A normalized rect uses 0 to 1 with the origin at the top-left of the frame it describes, x to the right, y down.
- "Canvas short side" means `min(W, H)` of the canvas being rendered.

## 3. project.json

Defaults apply when a property is missing. Clamps are applied when the value is used, not when it is read, so an out-of-range value round-trips unchanged.

### Root

| Property | Type | Default | Notes |
|---|---|---|---|
| `schemaVersion` | int | 1 | |
| `id` | string | | Lowercase UUID |
| `name` | string | `""` | Display name |
| `createdAt`, `modifiedAt`, `lastOpenedAt` | timestamp | | |
| `app` | object | | `{ "platform": "windows" or "macos", "version": "1.9.0" }`, the app that created the project |
| `keepSources` | bool | false | Pins the project against automatic cleanup |
| `sources` | Sources | | |
| `canvas` | Canvas | | |
| `screen` | ScreenStyle | | |
| `camera` | CameraStyle | | |
| `scenes` | Scene[] | one default scene | See normalization in section 6.1 |
| `zooms` | Zoom[] | `[]` | See section 6.8 |
| `edits` | Edits | | |
| `audio` | Audio | | |
| `overlays` | Overlays | | |
| `exports` | Export[] | `[]` | |

### Sources

| Property | Type | Default | Notes |
|---|---|---|---|
| `screen.file` | string | `"screen.mp4"` | Relative to the project folder, or an absolute path when `external` is true |
| `screen.width`, `screen.height` | int | | Pixels of the picture. Not the coded size: a 1080-line HEVC file is coded as 1088 rows |
| `screen.frameRate` | number | 30 | The rate the recording was made at. It sets the frame step in the editor and the frame rate of an export. See the note below |
| `screen.duration` | number | | Seconds |
| `screen.external` | bool | false | True for flat projects |
| `camera` | object or null | null | Null when there is no camera track |
| `camera.file` | string | `"camera.mp4"` | |
| `camera.width`, `camera.height` | int | | |
| `camera.duration` | number | | |
| `camera.startOffset` | number | 0 | Source time of the camera's first frame. May be negative |
| `events` | string or null | null | `"events.json"` when present |

A recording's frames are not always evenly spaced. The macOS recorder writes a frame only when something on screen changed, and either recorder can drop frames under load. The frame rate a media library reads from the file is then an average, below the rate the recording was made at and on macOS often far below it. `screen.frameRate` is therefore the configured rate, written by the recorder. A reader shows each source frame from the instant it starts until the next one starts.

### Canvas

| Property | Type | Default | Clamp | Notes |
|---|---|---|---|---|
| `aspect` | enum | `auto` | | `auto`, `square`, `landscape4x3`, `landscape16x9`, `portrait3x4`, `portrait9x16` |
| `padding` | number | 0.06 | 0 to 0.4 | Fraction of the canvas short side |
| `background.style` | enum | `gradient` | | `none`, `solid`, `gradient`, `image` |
| `background.preset` | string or null | `"ocean"` | | Informational; the colors below are what is drawn |
| `background.primary` | color | `#2687E8` | | |
| `background.secondary` | color or null | `#2EE0BF` | | |
| `background.image` | string or null | null | | File name relative to the project folder |

Background drawing: `none` is black. `solid` fills with `primary`. `gradient` is linear from the top-left corner (`primary`) to the bottom-right corner (`secondary`), matching the screenshot editors. `image` is aspect-filled and falls back to `primary` if the file is missing.

### ScreenStyle

| Property | Type | Default | Clamp | Notes |
|---|---|---|---|---|
| `cornerRadius` | number | 0.02 | 0 to 0.2 | Fraction of the canvas short side |
| `shadow` | number | 0.5 | 0 to 1 | Intensity |
| `crop` | Rect or null | null | | Normalized, in the screen frame. See valid crops below |

### CameraStyle

| Property | Type | Default | Clamp | Notes |
|---|---|---|---|---|
| `shape` | enum | `circle` | | `circle`, `roundedRectangle`, `squircle`, `rectangle`. Applies to the bubble layout |
| `cornerRadius` | number | 0.12 | 0 to 0.5 | Fraction of the bubble's short side, used by `roundedRectangle` |
| `mirror` | bool | true | | Flip horizontally |
| `borderWidth` | number | 0 | 0 to 0.02 | Fraction of the canvas short side |
| `borderColor` | color | `#FFFFFF` | | |
| `shadow` | number | 0.35 | 0 to 1 | Intensity |
| `crop` | Rect or null | null | | Normalized, in the camera frame |
| `cutout` | enum | `none` | | Reserved for person cutout: `blur`, `remove` |

A crop is **valid** when `x >= 0`, `y >= 0`, `width >= 0.05`, `height >= 0.05`, `x + width <= 1`, and `y + height <= 1` (allow 1e-9 of slack on the upper bounds). An invalid crop is treated as null.

### Scene

| Property | Type | Default | Clamp | Notes |
|---|---|---|---|---|
| `start` | number | 0 | | Source time |
| `layout` | enum | `bubble` | | `screen`, `bubble`, `sideBySide`, `camera`. Without a camera source every layout resolves as `screen` (section 6.1) |
| `bubble.anchor` | enum | `bottomRight` | | `topLeft`, `topRight`, `bottomLeft`, `bottomRight` |
| `bubble.size` | number | 0.24 | 0.08 to 0.6 | Fraction of the canvas short side |
| `bubble.offsetX`, `bubble.offsetY` | number | 0 | | Fractions of canvas width and height, added to the anchored position |
| `split.cameraSide` | enum | `trailing` | | `leading` is left or top, `trailing` is right or bottom |
| `split.cameraFraction` | number | 0.3 | 0.15 to 0.6 | |
| `transition.kind` | enum | `cut` | | `cut`, `morph`. How this scene is entered (section 6.9). The first scene's is ignored |
| `transition.duration` | number | 0.35 | 0 to 2 | Seconds a `morph` takes |

### Zoom

| Property | Type | Default | Clamp | Notes |
|---|---|---|---|---|
| `start`, `end` | number | 0 | | Source time. See normalization in section 6.8 |
| `scale` | number | 2 | 1 to 5 | How much the screen is magnified inside its card |
| `focus.mode` | enum | `point` | | `point` looks at one place. `cursor` follows the pointer |
| `focus.x`, `focus.y` | number | 0.5 | 0 to 1 | Normalized in the screen frame, like a click. Where a `point` zoom looks, and where a `cursor` zoom looks when the recording has no cursor samples |
| `easeIn`, `easeOut` | number | 0.5 | 0 to 3 | Seconds spent moving in at the start and back out at the end |
| `origin` | enum | `manual` | | `manual` or `auto`. `auto` marks a suggestion (section 8) that has not been edited since it was made |

### Edits

| Property | Type | Default | Notes |
|---|---|---|---|
| `trimStart` | number | 0 | |
| `trimEnd` | number or null | null | Null means the end of the screen source |
| `cuts` | `{ "start", "end" }[]` | `[]` | Ranges removed from the output |
| `speed` | `{ "start", "end", "rate" }[]` | `[]` | Reserved. Evaluated in a later revision |

### Audio, Overlays, Export

| Property | Type | Default | Notes |
|---|---|---|---|
| `audio.muted` | bool | false | |
| `audio.systemVolume`, `audio.microphoneVolume` | number | 1 | Reserved |
| `overlays.clicks.enabled` | bool | true | |
| `overlays.clicks.color` | color | `#0A84FF` | |
| `overlays.clicks.size` | number | 40 | Ring diameter in points of the captured screen |
| `overlays.clicks.strokeWidth` | number | 3 | Points |
| `overlays.clicks.opacity` | number | 0.85 | |
| `overlays.clicks.duration` | number | 0.45 | Seconds |
| `overlays.branding` | bool | false | |
| `exports[].path` | string | | Absolute path of a rendered video |
| `exports[].exportedAt` | timestamp | | |

## 4. events.json

```json
{
  "schemaVersion": 1,
  "capture": { "width": 3440, "height": 1440, "scale": 1.0, "kind": "display" },
  "clicks": [ { "t": 1.234, "x": 0.41, "y": 0.77, "button": "left" } ],
  "cursor": [ { "t": 0.0, "x": 0.5, "y": 0.5 } ],
  "cameraCorners": [ { "t": 0.0, "corner": "bottomRight" } ],
  "markers": []
}
```

- `capture.width` and `capture.height` are the captured rectangle in pixels. `scale` is pixels per point. `kind` is `display`, `region`, or `window`.
- Points are normalized to the captured rectangle at record time. Clicks outside it are not recorded. Cursor samples may fall outside 0 to 1 when the pointer leaves the rectangle.
- Clicks and cursor samples from before the first screen frame, or from while the recording was paused, are not recorded, because no moment in the video corresponds to them.
- `clicks[].button` is `left`, `right`, `middle`, or `other`.
- `cursor` holds at most 60 samples per second, sorted by `t`, with consecutive duplicates removed. Samples are steps (section 6.7).
- `cameraCorners` uses the bubble anchor names.
- `markers` is reserved for live layout switches.
- Section 2 applies here too: unknown properties survive, missing or `null` properties take their default (`scale` 1, `kind` `display`, empty lists), and a `schemaVersion` above 1 is refused.
- A project with no `events.json` file has no events. Reading it gives the defaults rather than an error.

## 5. Canvas size

The **natural canvas size** is the output size before any export scaling.

```
sw = screen.width  * (crop.width  if the screen crop is valid, else 1)
sh = screen.height * (crop.height if the screen crop is valid, else 1)

target aspect a:
  auto            sw / sh
  square          1
  landscape4x3    4 / 3
  landscape16x9   16 / 9
  portrait3x4     3 / 4
  portrait9x16    9 / 16

if aspect is auto:        W = sw,      H = sh
else if sw / sh >= a:     W = sw,      H = sw / a
else:                     W = sh * a,  H = sh

W = even(W), H = even(H)
even(v) = max(2, 2 * round(v / 2))    round = half away from zero
```

Rounding must be half away from zero on both platforms. In C# that is `Math.Round(x, MidpointRounding.AwayFromZero)`; Swift's `rounded()` already behaves this way.

**Export size** for a limit `L` on the long side (0 means no limit):

```
s = 1 if L <= 0, else min(1, L / max(W, H))
exportW = even(W * s), exportH = even(H * s)
```

The layout resolver accepts any `(W, H)` and is scale invariant: doubling the canvas doubles every output length.

## 6. Layout resolution

Inputs: a project, its events, a source time `t`, and a canvas size `(W, H)`. Output: a resolved frame (section 6.6). All arithmetic is in doubles with no rounding. Of the events only the cursor samples are used, and only by a zoom that follows the pointer (section 6.8); a project without events resolves as one with none.

`W` and `H` are positive doubles. They need not be integers, and they need not match the natural canvas aspect: a preview resolves at whatever size its view happens to be.

### 6.1 Scene selection

Normalize scenes before use: replace any negative `start` with 0, sort by `start` ascending (stable), keep only the last scene among those sharing a `start`, and treat the first scene's `start` as 0. An empty list becomes one default scene.

The active scene is the last one whose `start <= t`. For `t < 0` it is the first. Sections 6.2 to 6.5 lay out the active scene. Just after a scene starts, the layers may still be on their way from the scene before (section 6.9).

The **effective layout** is the scene's layout, except that a project with no camera source always resolves as `screen`.

### 6.2 Shared quantities

```
m   = min(W, H)
p   = clamp(canvas.padding, 0, 0.4) * m
A   = Rect(p, p, W - 2p, H - 2p)                      content area
as  = sw / sh                                         screen content aspect (section 5)
cw  = camera.width  * (crop.width  if the camera crop is valid, else 1)
ch  = camera.height * (crop.height if the camera crop is valid, else 1)
ac  = cw / ch                                         camera content aspect
Rs  = clamp(screen.cornerRadius, 0, 0.2) * m          card radius in pixels

fit(a, R):                                            largest rect of aspect a centered in R
  if R.width / R.height > a:  h = R.height, w = h * a
  else:                       w = R.width,  h = w / a
  return Rect(R.x + (R.width - w) / 2, R.y + (R.height - h) / 2, w, h)
```

### 6.3 Layouts

**`screen`**: `screenRect = fit(as, A)`. No camera.

**`bubble`**: `screenRect = fit(as, A)`, and the camera is placed against a canvas corner.

```
d = clamp(bubble.size, 0.08, 0.6) * m
if shape is circle or squircle:   bw = d, bh = d
else:                             bh = d, bw = d * clamp(ac, 0.5, 2)
if bw > 0.9 * W:                  bh = bh * (0.9 * W / bw), bw = 0.9 * W

g = 0.03 * m
x = g            for a Left anchor      x = W - g - bw   for a Right anchor
y = g            for a Top anchor       y = H - g - bh   for a Bottom anchor
x = x + bubble.offsetX * W
y = y + bubble.offsetY * H
x = clamp(x, 0, W - bw)
y = clamp(y, 0, H - bh)
cameraRect = Rect(x, y, bw, bh)
```

**`sideBySide`**: the two cards form one group centered in the content area. The canvas is split horizontally when `W >= H` and stacked vertically otherwise.

```
gap = 0.02 * m
f   = clamp(split.cameraFraction, 0.15, 0.6)

horizontal (W >= H):
  camW = f * (A.width - gap)
  s    = fit(as, Rect(0, 0, A.width - gap - camW, A.height))     only s.width and s.height are used
  x0   = A.x + (A.width - (s.width + gap + camW)) / 2
  y0   = A.y + (A.height - s.height) / 2
  trailing:  screenRect = Rect(x0, y0, s.width, s.height)
             cameraRect = Rect(x0 + s.width + gap, y0, camW, s.height)
  leading:   cameraRect = Rect(x0, y0, camW, s.height)
             screenRect = Rect(x0 + camW + gap, y0, s.width, s.height)

vertical (W < H):
  camH = f * (A.height - gap)
  s    = fit(as, Rect(0, 0, A.width, A.height - gap - camH))
  x0   = A.x + (A.width - s.width) / 2
  y0   = A.y + (A.height - (s.height + gap + camH)) / 2
  trailing:  screenRect = Rect(x0, y0, s.width, s.height)
             cameraRect = Rect(x0, y0 + s.height + gap, s.width, camH)
  leading:   cameraRect = Rect(x0, y0, s.width, camH)
             screenRect = Rect(x0, y0 + camH + gap, s.width, s.height)
```

**`camera`**: `cameraRect = A`. No screen.

### 6.4 Source rectangles

- Screen: the zoom window at `t` (section 6.8). When no zoom is active that is the valid screen crop, or `Rect(0, 0, 1, 1)`.
- Camera: the camera content is aspect-filled into `cameraRect`, centered.

```
c  = the valid camera crop, or Rect(0, 0, 1, 1)
ad = cameraRect.width / cameraRect.height
if ac > ad:   k = ad / ac,  source = Rect(c.x + c.width * (1 - k) / 2, c.y, c.width * k, c.height)
else:         k = ac / ad,  source = Rect(c.x, c.y + c.height * (1 - k) / 2, c.width, c.height * k)
```

### 6.5 Styling values

```
screen cornerRadius = min(Rs, min(screenRect.width, screenRect.height) / 2)

camera in the bubble layout, by camera.shape:
  circle             shape circle,            cornerRadius = min(bw, bh) / 2
  squircle           shape squircle,          cornerRadius = min(bw, bh) / 2
  roundedRectangle   shape roundedRectangle,  cornerRadius = clamp(camera.cornerRadius, 0, 0.5) * min(bw, bh)
  rectangle          shape rectangle,         cornerRadius = 0

camera in the sideBySide and camera layouts:
  cornerRadius = min(Rs, min(cameraRect.width, cameraRect.height) / 2)
  shape        = roundedRectangle if cornerRadius > 0, else rectangle

shadow for intensity s = clamp(value, 0, 1):
  blur = s * 0.04 * m,  offsetY = s * 0.012 * m,  opacity = s * 0.5

camera borderWidth = clamp(camera.borderWidth, 0, 0.02) * m
```

A squircle is drawn as the superellipse `|x/a|^5 + |y/b|^5 = 1` inscribed in the bubble rect.

Camera timing: `tc = t - camera.startOffset`. The camera is `visible` when `0 <= tc <= camera.duration`, and `sourceTime = clamp(tc, 0, camera.duration)`. Visibility never changes the geometry.

Draw order: background, screen shadow, screen, click rings (clipped to the screen card), camera shadow, camera, camera border, branding.

### 6.6 Resolved frame

```json
{
  "sceneIndex": 0,
  "layout": "bubble",
  "screen": {
    "rect": { "x": 0, "y": 0, "width": 0, "height": 0 },
    "source": { "x": 0, "y": 0, "width": 1, "height": 1 },
    "cornerRadius": 0,
    "shadow": { "blur": 0, "offsetY": 0, "opacity": 0 },
    "opacity": 1
  },
  "camera": {
    "rect": { "x": 0, "y": 0, "width": 0, "height": 0 },
    "source": { "x": 0, "y": 0, "width": 1, "height": 1 },
    "shape": "circle",
    "cornerRadius": 0,
    "mirror": true,
    "borderWidth": 0,
    "shadow": { "blur": 0, "offsetY": 0, "opacity": 0 },
    "sourceTime": 0,
    "visible": true,
    "opacity": 1
  }
}
```

`screen` is null in the `camera` layout. `camera` is null when the effective layout is `screen`. `sceneIndex` indexes the normalized scene list, and `layout` is the effective layout. `opacity` is 1, and a layer the layout does not have is null, except while a scene is being entered with a morph (section 6.9). A renderer draws the layers the frame has and does not go by `layout`.

### 6.7 Drawing rules

These are not covered by fixtures, because they describe pixels rather than geometry. Both renderers follow them so an export looks the same on either platform.

- **Color.** Colors are sRGB. Gradients are interpolated, and layers are blended, on the encoded (gamma) values, as the screenshot editors do. Screen pixels are not color-converted. The frame is opaque: a background color with alpha is that color over black.
- **Shadow.** The layer's shape, moved down by `offsetY`, is blurred with a Gaussian whose standard deviation is `blur` pixels, then drawn in black at `opacity` under the layer. A renderer may limit the blur; Direct2D stops at 250 pixels, which no canvas up to 3840 pixels reaches.
- **Layer edges.** A renderer may move a layer's edges to the nearest whole pixel before drawing.
- **Camera border.** A stroke of `borderWidth` pixels (at least 1 when the width is above 0) drawn inside the camera shape, in `camera.borderColor`.
- **Click rings.** A click at time `tc` is drawn while `0 <= t - tc <= overlays.clicks.duration`, with `p = (t - tc) / duration`:

  ```
  k        = capture.scale * (sources.screen.width / capture.width) * (screenRect.width / (source.width * sources.screen.width))
  diameter = overlays.clicks.size * k
  radius   = diameter / 2 + diameter * 0.58 * p        the center line of the stroke
  stroke   = overlays.clicks.strokeWidth * k
  alpha    = (1 - p) * clamp(overlays.clicks.opacity, 0, 1)
  center   = screenRect origin + ((click - source origin) / source size) * screenRect size
  ```

  `k` is canvas pixels per point of the captured screen. `source` is the resolved screen source rect. When `capture.width` is missing or 0 the ratio `sources.screen.width / capture.width` is 1. A click whose center falls outside the visible source rect is not drawn.
- **Opacity.** A layer whose `opacity` is below 1 is put together by itself first, with everything that belongs to it: its shadow, its picture, its border, and for the screen its click rings. The whole is then laid on the frame that much see-through. So a card that fades does not show its own shadow through itself, and its border and its click rings fade with it. A layer whose `opacity` is 0 is not drawn.
- **Cursor samples** are steps, not line segments: at time `t` the pointer is at the latest sample at or before `t`. That is why a resting pointer needs no repeated samples.

### 6.8 Zoom

A zoom magnifies the screen inside its card. For as long as it lasts, the screen's source rectangle is a smaller one, the zoom window. The card, the camera, and the canvas do not move, and the natural canvas size does not change. The `camera` layout shows no screen, so a zoom does nothing there.

```
base = the valid screen crop, or Rect(0, 0, 1, 1)
```

**Normalization.** Before use, in this order:

1. Replace a negative `start` with 0.
2. Drop every zoom with `end <= start`.
3. Sort by `start` ascending (stable), and keep only the last zoom among those sharing a `start`.
4. Where a zoom starts before the one before it ends, the earlier one ends there: `earlier.end = later.start`.

Normalized zooms do not overlap. A zoom is **chained** to the one before it when its `start` equals that zoom's `end` exactly. An editor that wants two zooms chained stores the same number in both.

The **active zoom** at `t` is the one with `start <= t < end`. With no active zoom the window is `base`.

**The window a zoom holds** at time `t`:

```
s        = clamp(scale, 1, 5)
w        = base.width  / s
h        = base.height / s
(fx, fy) = the focus at t (below)
x        = max(base.x, min(fx - w / 2, base.x + base.width  - w))
y        = max(base.y, min(fy - h / 2, base.y + base.height - h))
held(zoom, t) = Rect(x, y, w, h)
```

The window is centered on the focus and pushed back inside `base` where it would stick out. The lower bound is applied last, so it wins if rounding puts the upper bound below it.

**Focus.** For `point`, `(clamp(focus.x, 0, 1), clamp(focus.y, 0, 1))`. For `cursor`, the pointer's mean position over the second centered on `t`:

```
samples = events.cursor sorted by t (stable), each x and y clamped to 0..1
none:     use the point focus

a = t - 0.5,  b = t + 0.5
sample i lasts from from_i to until_i:
  from_i  = samples[i].t,      or minus infinity for the first sample
  until_i = samples[i + 1].t,  or plus infinity for the last sample
fx = sum over i of samples[i].x * max(0, min(b, until_i) - max(a, from_i))
fy = the same with y
```

The second is one long, so the sum is the mean. Before the first sample the pointer counts as being at the first sample, and after the last one at the last.

**Moving in and out.**

```
in   = clamp(easeIn, 0, 3)
out  = clamp(easeOut, 0, 3), or 0 when the next zoom is chained to this one
d    = end - start
if in + out > d:   f = d / (in + out),  in = in * f,  out = out * f

from(t)       = held(previous zoom, t) when this zoom is chained to it, otherwise base
ease(u)       = u * u * (3 - 2 * u)
lerp(A, B, k) = A + (B - A) * k        for x, y, width, and height separately

if t < start + in:        window = lerp(from(t), held(zoom, t), ease((t - start) / in))
else if t > end - out:    window = lerp(base,    held(zoom, t), ease((end - t) / out))
else:                     window = held(zoom, t)
```

A zoom with `easeIn` 0 cuts in, and one with `easeOut` 0 cuts out. Between two chained zooms the window moves straight from the first place to the second and does not open out in between.

Nothing else changes for a zoom. The click rings follow by themselves, because their size and position are already computed from the resolved source rectangle (section 6.7).

### 6.9 Scene transitions

A scene whose `transition.kind` is `morph` is entered by moving the layers from where the scene before had them to where this scene has them. A scene whose kind is `cut` is entered at once, and so is the first scene whatever it says.

```
i = the index of the active scene (section 6.1), with i >= 1 and kind morph
d = min(clamp(transition.duration, 0, 2), start of scene i + 1 - start of scene i)
    the last scene has no scene after it and no such limit

the layers are moving while d > 0 and start_i <= t < start_i + d
k = ease((t - start_i) / d)                       ease as in section 6.8

F = the frame sections 6.2 to 6.5 give for scene i - 1 at time t      where the layers come from
G = the frame they give for scene i at time t                         where they are going
```

F and G are worked out for the same `t`, so they have the same zoom window and the same camera timing. A move is never longer than its scene, so the scene before has always finished its own move by the time the next one begins: F is a scene at rest.

A layer that F and G both have (the screen, or the camera):

```
rect           lerp(F.rect, G.rect, k)            x, y, width, and height separately
cornerRadius   rF + (rG - rF) * k                 rF and rG are the corner radii in F and G
opacity        1

screen source  the zoom window, as in F and G
camera source  section 6.4 again, for the rect the camera now has
camera shape   the shape F and G share, or roundedRectangle when they differ

when the camera's shapes differ, a squircle's radius counts as
               0.22 * min(rect.width, rect.height)        of its own rect, in F or in G
```

Shadows, the camera's border, its mirroring, and its timing do not depend on the scene, so they are the same in F and G and stay as they are. Working the camera's source out again for each rect keeps its picture cropped to its card and never stretched.

A camera that changes shape moves as a rounded rectangle, and its outline does not jump at either end. A circle is a rounded rectangle whose radius is half its side, which is the radius it resolves to. A squircle resolves to that radius too, but is drawn without one, and reaches further into the corners of its box than a circle: the rounded rectangle that reaches as far has a radius of 0.22 of the side, so that is what a squircle counts as when it turns into another shape or another shape turns into it. Between two squircles the shape stays a squircle and the radius is not drawn.

A layer that only F has stays exactly as it is in F, with `opacity` `1 - k`. A layer that only G has is exactly as it is in G, with `opacity` `k`.

`sceneIndex` and `layout` are those of scene `i`, also while the layers move. So while the `camera` layout is being entered the frame still has a screen, fading out.

## 7. Time map

The time map converts between source time and output time. v1 covers trim and cuts; speed is reserved.

```
D     = sources.screen.duration
start = clamp(edits.trimStart, 0, D)
end   = clamp(edits.trimEnd if not null else D, start, D)

cuts: clamp each to [start, end], drop those with end <= start,
      sort by start, merge any that overlap or touch
kept: [start, end] minus the cuts, as ordered segments [s_i, e_i) with e_i > s_i
c_i   = sum of (e_j - s_j) for j < i
outputDuration = sum of all (e_i - s_i)

sourceToOutput(t):
  t <  s_0                 0
  s_i <= t < e_i           c_i + (t - s_i)
  e_i <= t < s_(i+1)       c_(i+1)                (inside a cut: the next kept frame)
  t >= e_last              outputDuration
  no kept segments         0

outputToSource(u):   u is clamped to [0, outputDuration]
  c_i <= u < c_i + (e_i - s_i)     s_i + (u - c_i)
  u == outputDuration              e_last
  no kept segments                 start
```

## 8. Zoom suggestions

Suggestions are zooms worked out from the clicks of a recording. They are a pure function of the project and its events, and they are only ever a proposal: the editor adds them as zooms with `origin` `auto`, and the user keeps, changes, or deletes them.

```
scale = 2    lead = 0.6    hold = 1.5    join = 4    inset = 0.15    shortest = 0.3

base   = the valid screen crop, or Rect(0, 0, 1, 1)
D      = sources.screen.duration
clicks = events.clicks sorted by t (stable), keeping those with 0 <= t <= D
         whose point is inside base, edges included

groups = []
for each click c:
    g = the last group, if there is one
    if g exists and c.t - g.last <= join:
        w     = the window a point zoom of this scale at g.focus holds (section 6.8)
        inner = Rect(w.x + inset * w.width, w.y + inset * w.height,
                     (1 - 2 * inset) * w.width, (1 - 2 * inset) * w.height)
        if c is inside inner, edges included:
            g.last = c.t                                      the same place: stay
        else:
            g.end = max(c.t - lead, (g.last + c.t) / 2)       another place, soon after: move there
            add the group { start: g.end, focus: (c.x, c.y), last: c.t }
    else:
        add the group { start: max(0, c.t - lead), focus: (c.x, c.y), last: c.t }

a group that was not given an end:   end = min(D, last + hold)
```

Each group with `end - start >= shortest` becomes one suggestion, in order:

```json
{ "start": 2.4, "end": 4.5, "scale": 2, "focus": { "mode": "point", "x": 0.2, "y": 0.3 }, "easeIn": 0.5, "easeOut": 0.5, "origin": "auto" }
```

A group that follows another within `join` starts exactly where that one ends, so the two zooms are chained and the picture moves from one place to the next. Groups further apart never touch, because `join` is longer than `lead + hold`.

**Manual zooms win.** Take the project's zooms whose `origin` is not `auto` and normalize them by themselves (section 6.8). A suggestion that overlaps one of them (`suggestion.start < manual.end` and `manual.start < suggestion.end`) is dropped.

**Applying suggestions** replaces the project's `auto` zooms with the new list and leaves every other zoom as it is stored. A zoom stops being `auto` the first time the user changes it.

A window recording carries no clicks today, so it gets no suggestions.

## 9. Defaults for a new project

- Canvas `auto`, padding 0.06, background gradient `ocean` (`#2687E8` to `#2EE0BF`).
- Screen `cornerRadius` 0.02, `shadow` 0.5. Camera `circle`, mirrored, `shadow` 0.35.
- One scene at 0: `bubble` at the corner the recording used, size 0.24, when there is a camera; otherwise `screen`.
- `trimStart = max(0, camera.startOffset)` with a camera, otherwise 0. This reproduces the leading trim macOS applies today.
- Click overlay values and branding come from the app's settings at the moment the project is created.
- When the user saves a look as the default, it replaces the first two bullets. A look is the canvas (aspect, padding, background), the screen style, and the camera style. It never carries a crop, because a crop belongs to one recording.

The classic look, matching a recording made without Studio, is background `none`, padding 0, screen `cornerRadius` 0, and screen `shadow` 0.

## 10. Flat projects

Opening an existing video that has no project creates a flat project: `sources.screen.external` is true, `screen.file` is the absolute path, and `camera` and `events` are null. The caller reads the video's width, height, duration, and frame rate and passes them in; the store does not open media files. There is at most one flat project per video path. Camera layouts are unavailable, so every scene resolves as `screen`.

## 11. Export links

Each export adds `{ path, exportedAt }` to `exports`. The project store indexes those paths, compared case-insensitively, so the Clips Library can find the project for a video.

- Exporting to a path the project already lists replaces that entry instead of adding a second one.
- A path belongs to one project. Recording an export removes the same path from every other project, because the file there has been overwritten.
- Renaming or moving an exported video updates its entry; deleting the video removes it.

## 12. Cleanup

- A project is **eligible** for automatic cleanup when it has at least one export, `keepSources` is false, and it is not a flat project.
- A project that is open in Studio, or still being recorded, is never deleted. The app passes those ids to every cleanup.
- **Age rule**: an eligible project is deleted when `lastOpenedAt` is more than 30 days old. The number of days is a setting; 0 disables the rule.
- **Size rule**: applied after the age rule. When the projects that remain total more than 10 GB (10 × 1024³ bytes), eligible ones are deleted in ascending `lastOpenedAt` order, ties broken by id, until the total is at or under the cap or none remain. The cap is a setting; 0 disables the rule.
- A draft (no exports) is never deleted automatically.
- A flat project is deleted when its external video no longer exists.
- A folder with no `project.json` is a recording that never finished. It is deleted once it is more than 24 hours old.
- A folder whose `project.json` cannot be read (for example, it was written by a newer version) is left alone.
- Cleanup deletes the whole project folder. The exported video is untouched, and opening it in Studio later creates a flat project.
- A folder that cannot be deleted because a file is in use is skipped and tried again next time. One failure does not stop the rest.
- Cleanup runs at app launch and after each export.

## 13. Fixtures

`shared/studio/fixtures/` holds JSON files loaded by both test suites.

Layout fixtures, in `layout/`:

```json
{
  "description": "what this case covers",
  "project": { },
  "naturalCanvas": { "width": 1920, "height": 1080 },
  "cases": [
    { "time": 0, "canvas": { "width": 1920, "height": 1080 }, "expected": { } }
  ]
}
```

`project` is a complete `project.json`, `naturalCanvas` is the expected result of section 5, and each `expected` is a resolved frame from section 6.6. The `zoom-*.json` files cover section 6.8 and the `scene-morph-*.json` files section 6.9. A fixture may also have an `events` member holding an `events.json`; the layout is then resolved with those events. Without the member there are no events.

Zoom suggestion fixtures, in `autozoom/`:

```json
{
  "description": "what this case covers",
  "project": { },
  "events": { },
  "expected": { "zooms": [ ] }
}
```

`expected.zooms` is the list of suggestions from section 8 for that project and those events, after the ones that overlap a manual zoom have been dropped. It does not include the project's other zooms.

Time map fixtures, in `timemap/`:

```json
{
  "description": "what this case covers",
  "sourceDuration": 20,
  "edits": { "trimStart": 2, "trimEnd": 18, "cuts": [ { "start": 5, "end": 7 } ] },
  "expected": {
    "outputDuration": 14,
    "segments": [ { "start": 2, "end": 5 }, { "start": 7, "end": 18 } ],
    "sourceToOutput": [ { "source": 6, "output": 3 } ],
    "outputToSource": [ { "output": 3, "source": 7 } ]
  }
}
```

Canvas fixtures, in `canvas/`:

```json
{
  "description": "what this case covers",
  "cases": [
    { "natural": { "width": 3440, "height": 1440 }, "limit": 1920, "expected": { "width": 1920, "height": 804 } }
  ]
}
```

Each case is the export size of section 5 for a natural canvas size and a long-side limit.

Tests compare every number with an absolute tolerance of 1e-6. Strings, booleans, and whether `screen` or `camera` is null are compared exactly.

Fixtures only sit on a decision boundary (a rounding midpoint, or either side of a comparison) when the inputs make the outcome exact in double arithmetic. That keeps them independent of the order in which an implementation multiplies and divides.
