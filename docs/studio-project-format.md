# Tiny Clips Studio project format (v1)

This is the contract between the macOS and Windows implementations of Tiny Clips Studio: the files a project is made of, every field in them, and the math that turns a project into a frame layout. The design it serves is in [plans/video-studio-plan.md](../plans/video-studio-plan.md).

Both platforms implement sections 5 to 7 as pure functions, and both test suites run the shared fixtures in `shared/studio/fixtures/`. If the two implementations disagree, this document decides which one is wrong.

## 1. Files

```
<projects root>/<project id>/
  project.json   required
  events.json    optional: cursor, click, and camera-corner data
  screen.mp4     the screen source (absent for flat projects, see section 9)
  camera.mp4     optional
  poster.jpg     optional thumbnail
```

- Projects root on Windows: `%LOCALAPPDATA%\TinyClips\Projects`. On macOS: `Application Support/TinyClips/Projects`.
- The project id is a lowercase UUID without braces. It is also the folder name.

## 2. Conventions

- UTF-8 JSON with camelCase property names. Writers emit indented JSON.
- **Unknown properties survive a save.** Every object type keeps the properties it does not recognize and writes them back unchanged.
- **Missing properties take their default.** A reader never fails because an optional property is absent. A missing timestamp reads as `1970-01-01T00:00:00Z`.
- **Required properties** have no default: `id`, `sources.screen.width`, `sources.screen.height`, `sources.screen.duration`, and, when `sources.camera` is present, its `width`, `height`, and `duration`. A project missing one of these is invalid and is not opened.
- **`null` counts as missing** unless the property's type below says "or null". So a `null` optional property takes its default, and a `null` required property makes the project invalid.
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
| `zooms` | Zoom[] | `[]` | Stored and round-tripped in v1. Evaluation is defined in a later revision |
| `edits` | Edits | | |
| `audio` | Audio | | |
| `overlays` | Overlays | | |
| `exports` | Export[] | `[]` | |

### Sources

| Property | Type | Default | Notes |
|---|---|---|---|
| `screen.file` | string | `"screen.mp4"` | Relative to the project folder, or an absolute path when `external` is true |
| `screen.width`, `screen.height` | int | | Pixels of the encoded video |
| `screen.frameRate` | number | 30 | |
| `screen.duration` | number | | Seconds |
| `screen.external` | bool | false | True for flat projects |
| `camera` | object or null | null | Null when there is no camera track |
| `camera.file` | string | `"camera.mp4"` | |
| `camera.width`, `camera.height` | int | | |
| `camera.duration` | number | | |
| `camera.startOffset` | number | 0 | Source time of the camera's first frame. May be negative |
| `events` | string or null | null | `"events.json"` when present |

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
| `transition.kind` | enum | `cut` | | `cut`, `morph`. How this scene is entered. Evaluated in a later revision |
| `transition.duration` | number | 0.35 | 0 to 2 | Seconds |

### Zoom (reserved)

`{ "start", "end", "scale", "focus": { "mode": "point" or "cursor", "x", "y" }, "easeIn", "easeOut", "origin": "manual" or "auto" }`

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
- `clicks[].button` is `left`, `right`, `middle`, or `other`.
- `cursor` holds at most 60 samples per second, sorted by `t`, with consecutive duplicates removed.
- `cameraCorners` uses the bubble anchor names.
- `markers` is reserved for live layout switches.

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

Inputs: a project, a source time `t`, and a canvas size `(W, H)`. Output: a resolved frame (section 6.6). All arithmetic is in doubles with no rounding.

`W` and `H` are positive doubles. They need not be integers, and they need not match the natural canvas aspect: a preview resolves at whatever size its view happens to be.

### 6.1 Scene selection

Normalize scenes before use: replace any negative `start` with 0, sort by `start` ascending (stable), keep only the last scene among those sharing a `start`, and treat the first scene's `start` as 0. An empty list becomes one default scene.

The active scene is the last one whose `start <= t`. For `t < 0` it is the first.

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

- Screen: the valid screen crop, or `Rect(0, 0, 1, 1)`.
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
    "shadow": { "blur": 0, "offsetY": 0, "opacity": 0 }
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
    "visible": true
  }
}
```

`screen` is null in the `camera` layout. `camera` is null when the effective layout is `screen`. `sceneIndex` indexes the normalized scene list, and `layout` is the effective layout.

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

## 8. Defaults for a new project

- Canvas `auto`, padding 0.06, background gradient `ocean` (`#2687E8` to `#2EE0BF`).
- Screen `cornerRadius` 0.02, `shadow` 0.5. Camera `circle`, mirrored, `shadow` 0.35.
- One scene at 0: `bubble` at the corner the recording used, size 0.24, when there is a camera; otherwise `screen`.
- `trimStart = max(0, camera.startOffset)` with a camera, otherwise 0. This reproduces the leading trim macOS applies today.
- Click overlay values and branding come from the app's settings at the moment the project is created.
- When the user saves a look as the default, its canvas, background, and layer styling replace the first three bullets.

The classic look, matching a recording made without Studio, is background `none`, padding 0, screen `cornerRadius` 0, and screen `shadow` 0.

## 9. Flat projects

Opening an existing video that has no project creates a flat project: `sources.screen.external` is true, `screen.file` is the absolute path, and `camera` and `events` are null. There is at most one flat project per video path. Camera layouts are unavailable, so every scene resolves as `screen`.

## 10. Export links

Each export appends `{ path, exportedAt }` to `exports`. The project store indexes those paths, compared case-insensitively, so the Clips Library can find the project for a video. Renaming or moving an exported video updates its entry; deleting the video removes it.

## 11. Cleanup

- A project is **eligible** for automatic cleanup when it has at least one export, `keepSources` is false, and it is not a flat project.
- **Age rule**: an eligible project is deleted when `lastOpenedAt` is more than 30 days old. The number of days is a setting; 0 disables the rule.
- **Size rule**: when the total size of the projects root exceeds 10 GB (10 × 1024³ bytes), eligible projects are deleted in ascending `lastOpenedAt` order until the total is under the cap or none remain. The cap is a setting; 0 disables the rule.
- A draft (no exports) is never deleted automatically.
- A flat project is deleted when its external video no longer exists.
- Cleanup deletes the whole project folder. The exported video is untouched, and opening it in Studio later creates a flat project.
- Cleanup runs at app launch and after each export.

## 12. Fixtures

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

`project` is a complete `project.json`, `naturalCanvas` is the expected result of section 5, and each `expected` is a resolved frame from section 6.6.

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
