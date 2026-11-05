# Tiny Clips Studio project format (v1)

This is the contract between the macOS and Windows implementations of Tiny Clips Studio: the files a project is made of, every field in them, and the math that turns a project into a frame layout. The design it serves is in [plans/video-studio-plan.md](../plans/video-studio-plan.md).

Both platforms implement sections 5 to 8 and the rules of sections 15.2 and 15.3 as pure functions, and both test suites run the shared fixtures in `shared/studio/fixtures/`. If the two implementations disagree, this document decides which one is wrong.

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
- **What no writer writes.** Tiny Clips writes whole numbers without a fraction or an exponent, and timestamps as above. Until a project could be saved as a folder (section 14) none ever went from a Mac to a Windows PC or back, so the two readers were not made to agree on anything else, and they do not:
  - A whole number written with a fraction (`"bytes": 12.0`): the Mac reads the number, and Windows calls the project invalid. A number that is not whole, or a value of another type, makes the project invalid on both.
  - A timestamp with an offset in place of `Z` and no fractional seconds (`2026-10-02T22:41:00+02:00`): Windows reads the time, and the Mac reads the timestamp as missing. Text that is no date at all reads as missing on the Mac and makes the project invalid on Windows. A timestamp that is not a string makes the project invalid on both.

  None of these lets cleanup delete a project it would otherwise keep: an invalid project is never cleaned up, and neither is one whose `lastOpenedAt` reads as missing (section 12).
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
| `keepSources` | bool | false | Pins the project against automatic cleanup. Set by "Keep this project" in the editor, written at once, and not one of the editor's undoable edits |
| `sources` | Sources | | |
| `canvas` | Canvas | | |
| `screen` | ScreenStyle | | |
| `camera` | CameraStyle | | |
| `scenes` | Scene[] | one default scene | See normalization in section 6.1 |
| `zooms` | Zoom[] | `[]` | See section 6.8 |
| `edits` | Edits | | |
| `audio` | Audio | | |
| `overlays` | Overlays | | |
| `captions` | Captions | | See section 15 |
| `exports` | Export[] | `[]` | |

### Sources

| Property | Type | Default | Notes |
|---|---|---|---|
| `screen.file` | string | `"screen.mp4"` | Relative to the project folder, or an absolute path when `external` is true |
| `screen.width`, `screen.height` | int | | Pixels of the picture. Not the coded size: a 1080-line HEVC file is coded as 1088 rows |
| `screen.frameRate` | number | 30 | The rate the recording was made at. It sets the frame step in the editor and the frame rate of an export. See the note below |
| `screen.duration` | number | | Seconds |
| `screen.external` | bool | false | True for flat projects |
| `screen.audioTracks` | string[] or null | null | What each sound track of the screen file holds, in the file's order: `system` (the computer's sound), `microphone`, or `mixed` (both in one track). Null means it is not known. Section 7 says what it is used for |
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
| `background.gradientDirectionDegrees` | number | 45 | 0 to 360 | Direction from primary to secondary in frame pixel space; 0 is left to right and 90 is top to bottom |
| `background.image` | string or null | null | | File name relative to the project folder |

Background drawing: `none` is black. `solid` fills with `primary`. `gradient` uses `gradientDirectionDegrees` from `primary` to `secondary`, matching the screenshot editors. `image` is aspect-filled and falls back to `primary` if the file is missing.

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
| `cutout` | enum | `none` | | What happens to everything in the camera picture that is not a person: `none` keeps it, `blur` blurs it, `remove` takes it away. See the drawing rules in section 6.7 |

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
| `speed` | `{ "start", "end", "rate" }[]` | `[]` | Ranges played faster or slower. `rate` is how many seconds of the recording pass in one second of video: 2 is twice as fast, 0.5 half as fast. Section 7 says which entries count |

### Audio, Overlays, Export

| Property | Type | Default | Notes |
|---|---|---|---|
| `audio.muted` | bool | false | The video has no sound |
| `audio.volume` | number | 1 | How loud the video's sound is, from 0 (silent) to 1 (as recorded). Clamped to that range when used. It is on top of the two below: see section 7 |
| `audio.systemVolume`, `audio.microphoneVolume` | number | 1 | How loud the sound tracks of that kind are in the video, from 0 (silent) to 1 (as recorded). Clamped to that range when used. See section 7 |
| `overlays.clicks.enabled` | bool | true | |
| `overlays.clicks.color` | color | `#0A84FF` | |
| `overlays.clicks.size` | number | 40 | Ring diameter in points of the captured screen |
| `overlays.clicks.strokeWidth` | number | 3 | Points |
| `overlays.clicks.opacity` | number | 0.85 | |
| `overlays.clicks.duration` | number | 0.45 | Seconds |
| `overlays.branding` | bool | false | |
| `overlays.captions.enabled` | bool | false | Draw the captions into the video (section 15.4) |
| `overlays.captions.size` | enum | `medium` | `small`, `medium`, `large` |
| `overlays.captions.position` | enum | `bottom` | `bottom`, `top` |
| `overlays.captions.appearance` | enum | `classicOutline` | `classicOutline`, `contrastPanel`, `boldImpact`, `cleanShadow` |
| `exports[].path` | string | | Absolute path of a rendered video |
| `exports[].exportedAt` | timestamp | | |
| `exports[].bytes` | int or null | null | How large the video file was when it was exported, in bytes. Left out, or null, when that is not known; 0 or less is read the same way. Section 12 says what it is used for |

### Captions

| Property | Type | Default | Notes |
|---|---|---|---|
| `captions.language` | string or null | null | The language of the cues as a BCP 47 tag, such as `en` or `pt-BR`. Left out when it is not known |
| `captions.cues` | Cue[] | `[]` | In any order |
| `cues[].id` | string | `""` | A lowercase UUID that tells a cue apart while it is edited. Nothing else depends on it |
| `cues[].start`, `cues[].end` | number | 0 | Source time |
| `cues[].text` | string | `""` | One or more lines, separated by `\n` |
| `cues[].words` | Word[] | `[]` | When each word is said. Whoever changes `text` empties this, because the words no longer match it |
| `words[].start`, `words[].end` | number | 0 | Source time |
| `words[].text` | string | `""` | One word |

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
- `markers` holds changes of layout made while recording, as `{ "t": 12.3, "layout": "sideBySide" }` with the layout names of a scene. An entry of any other shape is ignored. Section 9.1 says what they become. No recorder writes them yet.
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
- **Person cutout.** With `camera.cutout` set to `blur` or `remove`, the renderer finds the people in each camera frame. How it does that is the platform's own and is not specified, so the edge of a person is not the same pixel for pixel on two platforms. macOS uses the Vision framework. Windows runs a segmentation model with the machine learning API that is part of Windows, and lays the 256 by 256 picture it gets back over the camera frame with a cubic filter.
  - `blur`: everything but the people is blurred with a Gaussian whose standard deviation is 2 percent of the longer side of the camera frame, in camera pixels, before the frame is cropped, mirrored, and scaled into the layer. The layer is otherwise drawn as always.
  - `remove`: everything but the people is transparent, so the layer shows what is under it there. The shape still clips the layer. The layer has no shadow and no border, because those belong to a frame that is no longer there.
  - A renderer that cannot find people draws the layer as with `none`. On Windows that is the case while the app does not have the model's file. When finding them fails for one frame, that frame is drawn as with `none`.
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

The time map converts between source time and output time. It covers the trim, the cuts, and speed.

```
D     = sources.screen.duration
start = clamp(edits.trimStart, 0, D)
end   = clamp(edits.trimEnd if not null else D, start, D)

cuts: clamp each to [start, end], drop those with end <= start,
      sort by start, merge any that overlap or touch
kept: [start, end] minus the cuts, as ordered segments [s_i, e_i) with e_i > s_i

speed: drop an entry whose rate is not a finite number above 0
       rate = clamp(rate, 0.25, 8)
       drop it when end <= start, or when rate is 1
       sort by start, then by end; entries equal in both keep the order of the file
       then in that order: an entry that starts before the end of the last entry kept
       starts at that end instead, and is dropped when that leaves end <= start

pieces: each kept segment, divided at every start and end of a speed entry that lies
        strictly inside it. A part has the rate of the speed entry that contains its
        start, or 1. Neighbouring parts of one segment with the same rate are one piece.
        The pieces in order are [a_k, b_k) with rate r_k and b_k > a_k.

d_k   = (b_k - a_k) / r_k                         the output time piece k takes
c_k   = d_0 + d_1 + ... + d_(k-1), added in that order
outputDuration = c_last + d_last, and 0 with no pieces

sourceToOutput(t):
  t <  a_0                 0
  a_k <= t < b_k           c_k + (t - a_k) / r_k
  b_k <= t < a_(k+1)       c_(k+1)                (inside a cut: the next kept frame)
  t >= b_last              outputDuration
  no pieces                0

outputToSource(u):   u is clamped to [0, outputDuration]
  c_k <= u < c_k + d_k     a_k + (u - c_k) * r_k
  u == outputDuration      b_last
  no pieces                start
```

Without speed entries the pieces are the kept segments, each with rate 1.

Speed entries are not clamped to the trim. Which of two overlapping entries counts is decided by where they are in the recording, so the rate at a source time is the same wherever the trim and the cuts are. An entry counts only where video is kept.

Everything else stays in source time. A zoom, a move between scenes, and a click ring inside a faster piece therefore pass faster in the video, as the picture does.

**Sound.** The video has the recording's sound only in pieces with rate 1. For the output time of a piece with another rate it is silent.

Where the video has sound, the sound tracks of the screen file are added together, each at its gain. A track's gain is two numbers multiplied: the gain of what the track holds, and `audio.volume`. Each is clamped to 0 to 1 before they are multiplied.

`sources.screen.audioTracks` says what each track holds. A `system` track has the gain `audio.systemVolume` and a `microphone` track the gain `audio.microphoneVolume`. Every other track has the gain 1: a `mixed` track, a track named by any other word, and every track when the list is missing or does not have one entry for each sound track in the file.

`audio.volume` is the volume of the whole video: it is in the gain of every track, a `mixed` one too. That is the same as adding the tracks together at the gains of what they hold and multiplying the sum by `audio.volume`. No gain is above 1, so the video is never louder than the recording, and a reader needs no limiter for it.

With `audio.muted` the video has no sound at all, whatever the three volumes are. The video has one sound track, however many the screen file has: a video with two plays without one of them wherever only the first is played.

Both editors have one **Volume** slider for `audio.volume`, from 0% to 100%, under Mute, and it is switched off while Mute is on. The macOS editor also has **System audio** and **Microphone**, for `audio.systemVolume` and `audio.microphoneVolume`, when the recording has those tracks apart. A reader from before `audio.volume` keeps it as a property it does not recognize (section 2) and plays and exports the sound as recorded.

The macOS recorder writes the computer's sound and the microphone as two tracks and lists them, the computer's first. The Windows recorder mixes them into one track while recording and lists nothing, so there `audio.systemVolume` and `audio.microphoneVolume` change nothing and `audio.volume` is the only volume there is. The Windows preview and exporter read the first sound track of the file only, at `audio.volume`, and do not apply the other two volumes yet; a project where that makes a difference cannot be recorded on Windows, but one saved as a folder on a Mac can be opened there (section 14), and until Windows adds the tracks together it plays and exports without its microphone.

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

A recording without clicks gets no suggestions.

## 9. Defaults for a new project

- Canvas `auto`, padding 0.06, background gradient `ocean` (`#2687E8` to `#2EE0BF`).
- Screen `cornerRadius` 0.02, `shadow` 0.5. Camera `circle`, mirrored, `shadow` 0.35.
- One scene at 0: `bubble` at the corner the recording used, size 0.24, when there is a camera; otherwise `screen`.
- `trimStart = max(0, camera.startOffset)` with a camera, otherwise 0. This reproduces the leading trim macOS applies today.
- Click overlay values and branding come from the app's settings at the moment the project is created.
- When the user saves a look as the default, it replaces the first two bullets. A look is the canvas (aspect, padding, background), the screen style, and the camera style. It never carries a crop, because a crop belongs to one recording.

The classic look, matching a recording made without Studio, is background `none`, padding 0, screen `cornerRadius` 0, and screen `shadow` 0.

### 9.1 Scenes from the recording

With a camera, what was changed while recording becomes scenes, so that a new project shows what the recording showed. Without a camera there is the one `screen` scene.

The changes are the entries of `events.cameraCorners`, each of which moves the bubble to a corner, and of `events.markers`, each of which changes the layout. No version of Tiny Clips writes `markers`: choosing a layout while recording was taken out of the plan. A reader applies them all the same, so a file that has them is read as this section says. They are taken in the order of their `t`. At the same `t` a corner comes before a marker, and two of the same kind keep the order of their list. An entry whose `t` is not a number is left out.

Starting with the one scene at 0, each change is made to a copy of the last scene. A corner other than the one the bubble is in sets `bubble.anchor` and sets both bubble offsets to 0, because the offsets are from the corner the bubble was in. A marker sets `layout`. Then the first of these that applies decides what happens:

1. The copy has the same layout as the last scene and its bubble in the same place (anchor and offsets): nothing happens.
2. The change is a corner and the layout of the last scene is not `bubble`, so no bubble is showing: the last scene becomes the copy, keeping its own start and transition. The corner then counts from when a bubble shows again.
3. `t` is less than 0.3 s after the start of the last scene: the last scene becomes the copy, keeping its own start and transition. If it then has the same layout as the scene before it and its bubble in the same place, it is removed.
4. `t` is later than 0.3 s before the end of the recording: nothing happens, because the scene would not last 0.3 s.
5. The copy becomes a new scene that starts at `t` and is entered by moving: its `transition` is `morph` with `duration` 0.35.

0.3 s is the shortest scene the editor makes, and a `morph` of 0.35 s is how the editor enters a scene it has split off. The scenes can be changed or deleted in the editor like any others.

## 10. Flat projects

Opening an existing video that has no project creates a flat project: `sources.screen.external` is true, `screen.file` is the absolute path, and `camera` and `events` are null. The caller reads the video's width, height, duration, and frame rate and passes them in; the store does not open media files. There is at most one flat project per video path. Camera layouts are unavailable, so every scene resolves as `screen`.

Open in Studio checks the export link before creating a flat project, even if the library's cached link has not loaded. Dimensions describe the upright picture; preview and export apply the source's rotation metadata before layout and cropping. Imported edits autosave internally, but Save Project/Save As (portable folders) are disabled with an explanation. Keep the original video. The trimmer asks before discarding its pending edits and prevents competing operations while opening Studio.

## 11. Export links

Each export adds `{ path, exportedAt, bytes }` to `exports`, where `bytes` is the size of the video file at that moment and is left out when it cannot be read. The project store indexes the paths, compared case-insensitively, so the Clips Library can find the project for a video. That link goes by the path alone.

- Exporting to a path the project already lists replaces that entry instead of adding a second one.
- A path belongs to one project. Recording an export removes the same path from every other project, because the file there has been overwritten.
- Renaming or moving an exported video in Tiny Clips updates the `path` of its entry and leaves the rest of it. Tiny Clips does that whether Studio is switched on or off. Deleting the video leaves the entry where it is, whether it was deleted in Tiny Clips or outside it: a video that comes back, from the Recycle Bin or with the drive it is on, is linked again. What a missing video means for its project is in section 12.
- The same move operation updates matching flat projects' external source paths, including videos with no export link, preserving the project's identity and edits.

## 12. Cleanup

- A project is **eligible** for automatic cleanup when at least one of its exported videos is still where it was saved, `keepSources` is false, it is not a flat project, and its `lastOpenedAt` is not `1970-01-01T00:00:00Z`. That is what a missing `lastOpenedAt` reads as (section 2): it says nothing about when the project was opened, and would make it the oldest there is. Opening the project writes the time.
- An exported video is **still where it was saved** when a file, and not a folder, is at the `path` of its entry and, where the entry has `bytes` of 1 or more, the file is that many bytes long. A file of another size, larger or smaller, is another video that has taken the name, or the same one changed since. Either way it is not what the project exported, and counting it would let cleanup delete a project that holds the only copy of its recording. An entry without `bytes`, or with `bytes` of 0 or less, goes by the path alone.
- A project that is open in Studio, or still being recorded, is never deleted. The app passes those ids to every cleanup.
- **Age rule**: an eligible project is deleted when `lastOpenedAt` is more than 30 days old. The number of days is a setting; 0 disables the rule.
- **Size rule**: applied after the age rule. When the eligible projects that remain total more than 10 GB (10 × 1024³ bytes), they are deleted in ascending `lastOpenedAt` order, ties broken by id, until their total is at or under the cap or only the last in that order is left. That one, the project opened most recently, is never deleted by this rule: it is what was just worked on, and where it is over the cap by itself the rule would otherwise delete it the moment its editor closed. Only eligible projects are counted. A draft, a pinned project and a flat project are never removed, so counting them would only use the room up: with more drafts than the cap allows, every exported project would lose its sources the moment it was exported. An eligible project that is open counts and is not the one that goes. The cap is a setting; 0 disables the rule.
- A draft (no exports) is never deleted automatically. Neither is a project whose exported videos are all gone: it holds the only copy of the recording, as a draft does, and is listed with the drafts. A video on a drive that is not connected, or in a folder the app may not read, counts as gone for as long as that lasts.
- A flat project is never automatically deleted, including when its external video is unavailable. A disconnected drive or temporary access failure must not delete saved edits. Restore the video at its saved path to reopen it; projects can be deleted explicitly.
- A folder with no `project.json` is a recording that never finished. It is deleted once it is more than 24 hours old.
- A folder whose `project.json` cannot be read (for example, it was written by a newer version) is left alone. It is listed with the drafts, where its screen recording can be saved as an ordinary video and the folder deleted.
- Cleanup deletes the whole project folder. The exported video is untouched, and opening it in Studio later creates a flat project.
- A folder that cannot be deleted because a file is in use is skipped and tried again next time. One failure does not stop the rest.
- Cleanup runs shortly after app launch, each time an editor window closes, and when asked for in Settings. While Studio is switched off nothing is cleaned up: its storage settings are not shown then, so nothing they would delete is deleted.

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
    "pieces": [ { "start": 2, "end": 5, "rate": 1 }, { "start": 7, "end": 18, "rate": 1 } ],
    "sourceToOutput": [ { "source": 6, "output": 3 } ],
    "outputToSource": [ { "output": 3, "source": 7 } ]
  }
}
```

`segments` are the kept segments of section 7 and `pieces` its pieces. The `speed-*.json` files cover speed.

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

Recording fixtures, in `recording/`:

```json
{
  "description": "what this case covers",
  "duration": 20,
  "first": { },
  "cameraCorners": [ { "t": 0, "corner": "bottomRight" }, { "t": 5, "corner": "topLeft" } ],
  "markers": [ { "t": 9, "layout": "camera" } ],
  "expected": [ { }, { }, { } ]
}
```

`first` is the scene a new project starts with, `duration` the length of the recording, and `expected` the scenes of section 9.1 for those corners and markers. Every scene is written out in full.

Tests compare every number with an absolute tolerance of 1e-6. Strings, booleans, and whether `screen` or `camera` is null are compared exactly.

Fixtures only sit on a decision boundary (a rounding midpoint, or either side of a comparison) when the inputs make the outcome exact in double arithmetic. That keeps them independent of the order in which an implementation multiplies and divides.

Project files, in `folder/`: the `.tinyclips` file of a project saved as a folder (section 14), as an app really wrote it, byte for byte. These are not generated and have no expected values: a test puts a few bytes under the names the file gives, opens the folder, and holds what was read against the file. `saved-on-macos-1.9.0.tinyclips` is from a folder the macOS app saved on 7 October 2026. Only the Windows tests load it so far.

Caption fixtures, in `captions/`: a source duration, edits, `captions`, the `expected` captions of section 15.2, `at` queries with the index of the caption showing at an output time (`null` for none), and the `srt` and `vtt` text of section 15.3. In `captionfiles/`: the `text` of a file, the `expected` cues, and how many blocks are `skipped`. Text there is compared exactly.

## 14. A project saved as a folder

A project lives in the projects root (section 1), which no one is meant to look into. To keep a project elsewhere, or take it to another computer, the editor saves a copy of it as a folder:

```
<any folder>/My Demo/
  My Demo.tinyclips   the project's metadata: required
  screen.mp4          required
  camera.mp4          when the project has a camera
  events.json         when the project has one
  poster.jpg          when the project has one
  <background.image>  when the project has one
```

- **The `.tinyclips` file is `project.json` under another name**: the same JSON, every rule of sections 2 and 3, unknown properties included. It is named after its folder. It is the one file of the folder that says what the others are, and it is what the system opens the app with.
- **It has no `exports`.** The list is written empty. A path of an exported video belongs to the computer it was written on and has its user's name in it.
- **The recordings keep the names the project gives them** (`sources.screen.file`, `sources.camera.file`, `sources.events`), beside the `.tinyclips` file, in no subfolder.
- A flat project (section 10) is not saved as a folder: its video is kept elsewhere.

**A saved project** is a folder with exactly one `.tinyclips` file in it. An entry whose name starts with a dot is not counted: a Mac writes `._My Demo.tinyclips` beside the file on a volume that is not its own, such as the stick a project is carried to a PC on.

**Save and Save As.** The folder is a snapshot as of the moment it is saved. The project stays in the projects root and is edited there; internal autosave never writes to the external folder. First Save asks where to put the folder. Subsequent explicit Save updates that same saved project without asking again. Save As asks for a new name/location and makes the successful result the destination for future Save commands.

The association is stored locally as `saved-project.json` inside the internal project folder, keyed implicitly by the internal project id. It records the exact external `.tinyclips` filename and, on sandboxed macOS, a security-scoped folder bookmark. This filename is reserved without regard to case: an asset with any case variant prevents reading or writing the local association. It survives restarting the app, is removed with the internal project, and is never included in a portable snapshot. No portable schema change is required.

**Updating a linked saved project.** Save stages project files and commits metadata last, with rollback on failure. Only validated project-owned files may be updated: exported videos, unrelated files, and unrelated subfolders are preserved. Only names owned by the previous saved project can be backed up and replaced; late collisions at new asset names fail without replacing the unrelated entry. Links are not followed for writes. A missing or inaccessible target is reported; Save As can select another destination.

**Save As replacement.** Saving to a different existing folder replaces it, with everything in it, only after confirmation and only when all of this holds:

- The folder is a saved project, and its `.tinyclips` file reads as a project (section 2).
- Every other entry in it is a file, not a folder and not a link, and is either something a save writes or something a system leaves in a folder by itself:
  - what a save writes: the names that `.tinyclips` file gives (`sources.screen.file`, `sources.camera.file`, `sources.events` or `events.json` when it names none, `background.image`) and `poster.jpg`;
  - what a system leaves: `.DS_Store`, `Thumbs.db`, `desktop.ini`, and any name that starts with `._`.
- Names are compared without regard to case.

Anything else is left exactly as it is and the Save As replacement is refused. Windows names the entry that does not belong; macOS asks the user to choose another name. So a project file copied into a folder of other things does not make that folder replaceable. Both platforms enforce this rule.

**Opening.** Opening a `.tinyclips` file, or a saved project's folder, makes a new project in the projects root and opens that:

- The file is read as a project by the rules of section 2. One that is invalid, or has a `schemaVersion` above the reader's, is refused, and so is a flat project.
- The screen file must be beside it, and the camera file when `sources.camera` is not null. Without them the file is refused: a `.tinyclips` file by itself is not a project.
- A file name is followed only when it is a plain name (section 1). A screen, camera, or events name with a path separator makes the project invalid, as in section 1; a `background.image` with one is treated as missing. So a `.tinyclips` file cannot make the app copy a file from outside its folder.
- The new project gets an id of its own. Opening the same file twice makes two projects.
- Its `exports` are empty whatever the file says, so it is a draft on this computer (section 12) and cleanup does not remove it until it has been exported here.
- Opening itself only reads the original folder. The new internal draft remembers its exact source `.tinyclips` file as its saved destination, so a later explicit Save updates that original; internal autosave does not.

**Video export destinations.** Every Studio export command, including Export from the close prompt, asks for an MP4 filename and folder. The default is the folder containing the linked `.tinyclips` file, or the configured video-save folder for an unsaved project. Cancelling the picker starts no render and keeps the editor open. An existing output requires native overwrite confirmation; staged rendering preserves the previous file on failure/cancellation. A late filename collision is reported rather than silently renaming or overwriting without consent. Project data and source recordings cannot be export targets. Export bookkeeping records the actual selected final path only after commit.

Overwrite approval captures native file identity and the filesystem's metadata-change version as well as size and modification time. Replacing a file or editing it in place cannot preserve approval merely by restoring its modification time. Windows resolves ancestor junctions/symbolic links for both selected and protected paths before checking source protection, and tracks the resolved output location through commit.

A `.tinyclips` file larger than 16 MB is refused unread; one a writer wrote is a few kilobytes.

**Between a Mac and a PC.** Section 2 lists what the two readers take differently, and none of it is something a writer writes. A folder saved on one is meant to open on the other. What has been tried, as of 7 October 2026:

- A folder the macOS app saved was opened by the Windows store, from its file and from its folder, and saved again as a folder: the only value that differs in the project file is the `id`. Since 8 October the Windows file has one property more, `audio.volume` at 1, which a Mac of 1.9.0 does not write; nothing the Mac wrote is dropped or changed. Its project file is the fixture `folder/saved-on-macos-1.9.0.tinyclips` (section 13). The same project was exported by the Windows exporter and held against the Mac's export; `windows/docs/studio-project-folders.md` has what agreed and what did not.
- The Windows app has the commands for both since the night of 7 October: built and unit tested, and the editor's part run in the real window by the window check. The app itself was not started, so no folder has been saved or opened by a person there, and the folder from the Mac has been opened by the store only. `windows/docs/studio-project-folders.md` has what was run and what was not.
- A folder saved on Windows has not been opened on a Mac.

## 15. Captions

### 15.1 What a project holds

Captions are words on the recording's timeline: `captions.cues` in section 3. Their times are source time, so a trim, a cut, or a speed change never calls for a new transcription. What a video shows is worked out from the cues and the time map whenever it is needed, by section 15.2. A file and a drawn caption both come from there, so they cannot disagree.

`captions` and `overlays.captions` are not part of a look, are undone and redone with the other edits, and go with a project saved as a folder. A reader from before captions keeps both as properties it does not recognize (section 2) and shows no captions.

### 15.2 From cues to captions

```
clean(text): lines end at \r\n, \r, or \n. Every line without the white space around
             it, leaving out blank lines, joined by \n.

words of a cue: drop a word whose start or end is not finite, whose end < start, or
                whose text is only white space. Sort by start, then by end; words equal
                in both keep the order of the file.

cues: drop a cue whose start or end is not finite, whose end <= start, or whose
      clean(text) is empty. Sort by start, then by end; cues equal in both keep the
      order of the file.

parts: for each cue, for each piece k of the time map (section 7) with r_k == 1, in order:
    s = max(cue.start, a_k)
    e = min(cue.end, b_k)
    text = clean(cue.text)
    next piece if e <= s
    if the cue has words:
        kept = its words with a_k <= (word.start + word.end) / 2 < b_k
        next piece if kept is empty
        if kept is not all of its words:
            text = the text of each kept word, without the white space around it,
                   joined by one space
            s = max(a_k, kept[first].start)
            e = min(b_k, kept[last].end)
    next piece if e - s < 0.1
    part: start = c_k + (s - a_k), end = c_k + (e - a_k), text

captions: sort the parts by start, then by end; parts equal in both keep the order above.
    In that order, when the caption before this part ends after this part starts:
        its end = this part's start
        drop it if it now lasts less than 0.1
    Add this part.
```

What follows from that:

- **A piece at another rate has no captions**, faster or slower, because it has no sound (section 7).
- **A cut takes the words said in it.** A word stays or goes by where its middle is, and each side of the cut covers only the words it keeps.
- **A cue without words cannot be divided**, so it is clipped, and each side of a cut shows all of its text. That is what an imported or edited cue is.
- **One caption shows at a time.** A caption ends where the next starts. At output time `u` the caption showing is the one with `start <= u < end`, or none.
- **Mute changes nothing.** A muted video still has its captions.

### 15.3 SRT and WebVTT

**Writing.** Both are UTF-8 with no byte order mark, lines end with `\n`, and the file ends with one. A time is rounded to whole milliseconds with `floor(seconds * 1000 + 0.5)`, and hours take at least two digits.

```
1                                  WEBVTT
00:00:01,000 --> 00:00:02,500
First line                         00:00:01.000 --> 00:00:02.500
Second line                        First line
                                   Second line
2
00:00:03,000 --> 00:00:04,000      00:00:03.000 --> 00:00:04.000
Third line                         Third line
```

An SRT with no captions is empty, and a WebVTT is only its first line. A WebVTT writes `&`, `<`, and `>` in its text as `&amp;`, `&lt;`, and `&gt;`. An SRT writes its text as it is.

**Reading.** A leading byte order mark is ignored, and lines end at `\r\n`, `\r`, or `\n`. Lines of only white space divide the text into blocks. It is a WebVTT when its first block's first line is `WEBVTT`, or starts with that and a space or a tab. Otherwise it is read as an SRT, which has no sign of its own.

- In a WebVTT the first block is the header, and a block whose first line is, or starts with and a space or a tab, `NOTE`, `STYLE`, or `REGION` is not a cue. Neither is counted as skipped.
- The timing is the first line of a block, or the second after a number or a name. It is `[hours:]minutes:seconds` with a `.` or a `,` before one to three digits of fraction, `-->`, and another time. A fraction of fewer than three digits is filled with zeros on the right, so `.5` is 500 ms. Anything after the second time, such as WebVTT cue settings, is ignored.
- The lines after the timing are the text. An SRT loses its tags (`b`, `i`, `u`, `s`, `font`, in any case) and its `{\...}` overrides. A WebVTT loses everything shaped like a tag (`<v Sam>`, `</c>`, `<00:00:03.500>`), and then `&lt;`, `&gt;`, `&nbsp;`, `&lrm;`, `&rlm;`, and last `&amp;` become what they stand for. An SRT has no way to write a literal tag, so `<now>` stays. The result is `clean`ed.
- A block with no timing, with an end at or before its start, or with no text left is skipped and counted, so the app can say so.

Times read this way are taken as source time. Each cue gets a new `id` and no `words`. The cues stay in the order of the file; section 15.2 sorts them when it uses them.

### 15.4 Drawing

With `overlays.captions.enabled`, a frame at output time `u` has the caption showing at `u` drawn over everything else, on the canvas: after the camera and the branding, not moved by a zoom or a scene, and not part of any layer that fades. These rules are not covered by fixtures, because they describe pixels. Both renderers follow them so an export looks alike on either platform, but each lays text out with its own system, so lines may wrap at different words.

`unit = canvas short side / 288`. All sizes below are in units, so captions look the same at 720p and 4K and stay in proportion on a portrait canvas.

| `size` | Text height |
|---|---|
| `small` | 14 |
| `medium` | 18 |
| `large` | 24 |

- **Font.** The platform's system sans-serif, regular weight unless the appearance below says bold.
- **Lines.** The text is centered on the canvas. It keeps its own lines, and a line wider than 90 percent of the canvas wraps between words.
- **Position.** With `bottom`, the bottom of the last line is 12 above the bottom of the canvas. With `top`, the top of the first line is 12 below the top of the canvas. Text too tall for the canvas keeps its first line on it.
- **Paints.** An outline reaches that far outside the letters, under them. A shadow is the letters and their outline in the shadow's color, moved by that much. One panel lies behind all the lines. A paint that is not solid is laid on as one sheet, so it does not darken where letters overlap.

| `appearance` | Text | Outline | Shadow | Panel |
|---|---|---|---|---|
| `classicOutline` | `#FFFFFF` | `#000000`, 1.2 wide | none | none |
| `contrastPanel` | `#FFFFFF` | none | none | `#000000` at 60 percent behind the lines, reaching 4 past them |
| `boldImpact` | `#FFD700`, bold | `#000000`, 2.4 wide | `#000000`, 1.2 down and right | none |
| `cleanShadow` | `#F5F5F5` | `#000000` at 50 percent, 0.6 wide | `#000000` at 50 percent, 1.5 down and right | none |

### 15.5 Importing and editing

**Importing** replaces `captions.cues` with the cues of a file, in the order of section 15.2, and sets `captions.language` to null, as one undoable edit. Other properties of `captions` stay. A file has one of two timings, which the person importing it chooses:

- **Timed to the recording:** its times are source time, as section 15.3 reads them. This is the default.
- **Timed to the edited video:** its times are output time, such as a file made from an exported video or by section 15.3 itself. Each time `u` becomes `outputToSource(u)` (section 7). A cue whose end is then at or before its start is dropped and counted as skipped. What falls in a piece at another rate maps there, and section 15.2 then shows none of it.

Reading back what section 15.3 wrote, timed to the edited video, gives the same captions to within the rounding of a millisecond.

**Editing a cue's text** stores `clean(text)`. When that equals `clean` of what it held, nothing changes. Otherwise its `words` are emptied, because they no longer match. An edit that leaves no text keeps the cue, which then shows nothing and which a check reports as empty.

### 15.6 Checks and safe fixes

Checks look at cues in the order of section 15.2, in source time, against a recording of length `D`. A finding names a cue by its place in that order, from 0. They are limits for reading, not rules of the format: a project with findings is valid.

```
shortest = 0.7 seconds      longest line = 42 characters
most lines = 2              fastest = 20 characters a second

A character is a Unicode code point. count(text) leaves out white space.

tidy(text): lines end at \r\n, \r, or \n. In every line each run of white space becomes
            one space and the white space around it goes. Blank lines go. Joined by \n.

wanted(cue, text): the seconds the cue should last. Its length, or 1 when that is under
                   shortest, or count(text) / fastest when that is more than either.

balance(words, n): n groups of neighbouring words.
    n <= 1: all of them in one group. As many words as n or fewer: each in its own group.
    Otherwise for each group g from 1 to n - 1, from the first word not yet in a group:
        target = length of the remaining words joined by spaces / (n - g + 1)
        for each candidate end, leaving at least n - g words for the groups after it:
            score = |length of the group's words joined by spaces - target| - bonus
            bonus = 6 when its last word ends in . ! ?, 3 when in , ; :, otherwise 0
        take the lowest score, the earliest of equals.
    The last group has what is left.

wrap(text): null when tidy(text) has no words, or a word longer than the longest line.
    The first of balance(words, 1), balance(words, 2) whose lines all fit the longest
    line, joined by \n. Null when neither does.

split(text): null with fewer than two words, or a word longer than the longest line.
    From n = max(2, ceil(length of the words joined by spaces / (longest line * most lines)))
    to min(64, number of words): the first balance(words, n) for which wrap of every group
    is not null gives those wraps as the parts. Null when none does.

timing(start, end, parts): each part lasts its share of end - start, by max(1, count(part)).
    Parts follow each other from start. The last ends at end.
```

| Check, in this order for each cue | Found when | Can be fixed when |
|---|---|---|
| `invalidTiming` | `end <= start`, or either is not finite. No other check looks at this cue, and it is not "the cue before" the next one | Never |
| `overlap` | It starts before the cue before it ends | It starts at least `shortest` after that cue starts |
| `empty` | `tidy(text)` is empty. The checks below do not look at this cue | Always |
| `whitespace` | `tidy(text)` is not `text` | Always |
| `longLine` | A line of `tidy(text)` is longer than the longest line | `wrap` is not null, or `split` is not null and each of its parts would last at least `shortest` |
| `tooManyLines` | `tidy(text)` has more than the most lines | The same |
| `shortDuration` | It lasts less than `shortest` | `start + wanted` is at or before the next cue's start, and at or before `D` |
| `readingSpeed` | `count(tidy(text)) / length` is more than `fastest` | The same |

**The safe fixes** run in this order on all the cues, and each thing one changes counts as one fix:

1. Remove every cue whose `tidy(text)` is empty.
2. Set every cue's text to `tidy(text)`. Its `words` stay: the words are the same.
3. From the second cue on, when a cue overlaps the one before it and starts at least `shortest` after it, end the one before where this one starts.
4. For each cue with a line too long or too many lines: when `wrap` is not null, that is its text, and its `words` stay. Otherwise, when `split` is not null and each part would last at least `shortest`, it becomes those parts at their `timing`. The first keeps the cue's `id`, the others get new ones, and none has `words`.
5. For each cue with a length above 0, when `wanted` is more than its length and `start + wanted` is at or before the next cue's start and at or before `D`, it ends at `start + wanted`.

Fixes are one undoable edit. What they could not fix is what the checks find afterwards.

A summary names the findings by count, in the order of the table, joined by `, `:

| Check | One | More |
|---|---|---|
| `invalidTiming` | `1 invalid time` | `2 invalid times` |
| `overlap` | `1 overlap` | `2 overlaps` |
| `empty` | `1 empty caption` | `2 empty captions` |
| `whitespace` | `1 with extra space` | `2 with extra space` |
| `longLine` | `1 long line` | `2 long lines` |
| `tooManyLines` | `1 with too many lines` | `2 with too many lines` |
| `shortDuration` | `1 too short` | `2 too short` |
| `readingSpeed` | `1 too fast to read` | `2 too fast to read` |

### 15.7 Generating

**Generating** replaces `captions.cues` with what speech recognition heard, and sets `captions.language` to the language it heard, or null if it does not say, as one undoable edit. Other properties of `captions` stay. A cue's times, and those of its `words`, are source time, so generating listens to the whole recording, including what is cut, muted, or trimmed away, and an edit made while it runs is kept. Recognition breaks its cues at word boundaries, at most 84 characters long, and then the safe fixes of section 15.6 are made. A recording in which nothing is heard is an error and leaves the captions as they were, and so does stopping.

The speech recognition is the platform's own, so two platforms need not make the same cues from the same sound.
