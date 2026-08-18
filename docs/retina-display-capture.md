# Retina Display & HiDPI Capture

This document explains how TinyClips handles Retina (HiDPI) displays across screenshot, video, and GIF capture — the problems we encountered and the solutions we landed on.

## Background: Points vs Pixels on macOS

macOS uses a **point-based coordinate system** for window layout. On a standard (1×) display, 1 point = 1 pixel. On a Retina (2×) display, 1 point = 2×2 pixels = 4 physical pixels. The ratio is exposed as `NSScreen.backingScaleFactor`.

| Concept | Example (2× Retina) |
|---------|---------------------|
| Point dimensions | 150 × 200 pt |
| Pixel dimensions | 300 × 400 px |
| Scale factor | 2.0 |

All AppKit/SwiftUI layout, `NSScreen.frame`, `NSWindow.frame`, and `SCWindow.frame` are in **point coordinates**. Pixel coordinates only matter when configuring capture output buffers and writing image/video files.

## ScreenCaptureKit Coordinate Spaces

`SCStreamConfiguration` has four key properties:

| Property | Unit | Purpose |
|----------|------|---------|
| `sourceRect` | **Points** | Region of the display to capture (in display-local coordinates, Y-down) |
| `width` | **Pixels** | Width of the output image/video buffer |
| `height` | **Pixels** | Height of the output image/video buffer |
| `scalesToFit` | Boolean | How `sourceRect` maps to the output buffer |

### `scalesToFit` behavior

- **`false`** — The framework captures at the display's native pixel density. If `sourceRect` is 150×200 pt on a 2× display, and `width`/`height` are 300×400 px, you get a 1:1 native-resolution capture. The output buffer is filled with the exact pixels that exist on screen. If the output dimensions don't match the native pixel count, the capture is **clipped or padded** rather than resampled.

- **`true`** — The framework captures `sourceRect` and **resamples** it to fill the output buffer dimensions. This guarantees the entire output buffer is filled with content (no blank space), but the capture may be upscaled or downscaled.

### Critical difference: `SCScreenshotManager` vs `SCStream`

| API | Best `scalesToFit` | Why |
|-----|-------------------|-----|
| `SCScreenshotManager.captureImage()` | **`false`** | Native pixel capture. With `true`, we observed 1× rasterization upscaled to 2× — causing blur. |
| `SCStream` (video/GIF) | **`true`** | Streaming requires the buffer to be completely filled every frame. With `false`, window/screen captures can produce blank margins when the sourceRect doesn't perfectly align with the output buffer grid. |

This difference is the core of our solution.

## CaptureRegion: The Shared Model

All capture modes start with a `CaptureRegion` struct:

```swift
struct CaptureRegion: Sendable {
    let sourceRect: CGRect    // Always in POINTS (display-local, Y-down)
    let displayID: CGDirectDisplayID
    let scaleFactor: CGFloat  // Screen's backingScaleFactor

    var pixelWidth: Int {
        max(1, Int((sourceRect.width * scaleFactor).rounded()))
    }

    var pixelHeight: Int {
        max(1, Int((sourceRect.height * scaleFactor).rounded()))
    }
}
```

**Key invariants:**
- `sourceRect` is always in point coordinates — never multiply by scale factor.
- `sourceRect` is always **snapped to whole device pixels** via `pixelAligned()` before capture (see below).
- `pixelWidth` / `pixelHeight` are derived computed properties using consistent `.rounded()` rounding.
- `scaleFactor` comes from the `NSScreen` where the region was selected.

### Pixel alignment

Region selections come from raw mouse coordinates, so `sourceRect` is almost always fractional in point space. On a 2× display a `minX` of `412.3 pt` lands at pixel `824.6` — a 0.6-pixel crop offset. ScreenCaptureKit resolves that offset by resampling the entire frame, which softens the whole capture. A fractional *size* causes the same problem from the other direction: `100.25 pt → 201 px` implies an effective scale of 2.005 rather than 2.0.

Window screenshots never hit this because their `sourceRect` origin is hardcoded to `(0, 0)`, which is why they looked sharp while region captures did not.

`CaptureCoordinateMath.pixelAlignedRect(_:scaleFactor:)` converts the rect into pixel space, rounds all four edges to integers, and converts back to points. `CaptureRegion.pixelAligned()` wraps it and is idempotent, a no-op on 1× displays with integral rects, and clamps to a minimum of one device pixel. It is applied at every region creation site (`RegionSelector`, `CaptureRegion.fullScreen`, `CaptureManager.captureRegion(for:)`), defensively inside `ScreenshotCapture.captureImage(region:)`, and to the window-capture `sourceRect`.

The resulting invariant is that `sourceRect.origin × scaleFactor` and `sourceRect.size × scaleFactor` are always exact integers, so the point→pixel mapping is a pure integral scale with no interpolation.

### How regions are created

| Source | Origin | Y direction |
|--------|--------|-------------|
| Region selector drag | View → window → screen → display-local | Converted to Y-down |
| Full screen | `(0, 0, screen.frame.width, screen.frame.height)` | Already display-local |
| Window capture | `SCWindow.frame` converted to display-local | Y-flipped from AppKit |

## Screenshot Capture

```
User selects 150×200 pt region on 2× display
→ sourceRect = (x, y, 150, 200) in points
→ pixelWidth = 300, pixelHeight = 400
→ SCStreamConfiguration: sourceRect=(x,y,150,200), width=300, height=400, scalesToFit=false
→ SCScreenshotManager.captureImage() → CGImage(300×400)
→ Saved as PNG/JPEG at 72 DPI (standard)
```

### Why `scalesToFit = false` for screenshots

With `scalesToFit = true`, we observed `SCScreenshotManager` rasterizing the region at 1× resolution and then upscaling to fill the output buffer. The resulting image was blurry — every logical pixel was doubled rather than capturing the native backing-store pixels. Setting `false` tells the framework to pull pixels directly from the display's backing store at native resolution.

### Window screenshots

Window screenshots use `SCContentFilter(desktopIndependentWindow:)` which captures the window's own backing store. The scale factor is determined by finding which screen most overlaps the window:

```swift
let sourceRect = CaptureCoordinateMath.pixelAlignedRect(
    CGRect(origin: .zero, size: window.frame.size),
    scaleFactor: scaleFactor
)
config.sourceRect = sourceRect                                          // points
config.width  = Int((sourceRect.width * scaleFactor).rounded())         // pixels
config.height = Int((sourceRect.height * scaleFactor).rounded())        // pixels
config.scalesToFit = false
```

### No custom DPI metadata

We intentionally save screenshots at standard 72 DPI (the CGImage default). Writing `72 × scaleFactor` DPI was considered but removed because:
- It made `NSImage.size` report point dimensions instead of pixel dimensions, confusing the editor's display logic.
- macOS Preview and other viewers already handle Retina screenshots correctly.
- The pixel data itself is the full native resolution — DPI metadata only affects how viewers interpret the "intended display size."

## Video Capture

```
User selects 150×200 pt region on 2× display
→ sourceRect = (x, y, 150, 200) in points
→ pixelWidth = 300, pixelHeight = 400
→ SCStreamConfiguration: sourceRect=(x,y,150,200), width=300, height=400, scalesToFit=true
→ SCStream delivers CMSampleBuffers at 300×400
→ AVAssetWriter configured with width=300, height=400
→ Saved as .mp4 (H.264)
```

### Why `scalesToFit = true` for video

`SCStream` delivers frames continuously. With `scalesToFit = false`, we encountered blank margins/padding in the output when capturing full screens or windows. This happens because the sourceRect-to-buffer mapping is strict: any sub-pixel misalignment or rounding difference leaves unfilled regions in the buffer. `scalesToFit = true` ensures the entire source region is resampled to exactly fill the output buffer, eliminating blank space.

For video, the resampling is acceptable because:
- The output is already at full pixel resolution (300×400 for a 150×200 pt region on 2×).
- Any resampling is a near-identity transform (source and destination are nearly the same size).
- Video compression (H.264) introduces its own artifacts that dwarf any sub-pixel resampling.

### AVAssetWriter dimensions

The `AVAssetWriter` video input is configured with `region.pixelWidth` and `region.pixelHeight` — the same computed properties used by `SCStreamConfiguration`. This ensures the SCStream output buffer dimensions exactly match what the writer expects.

## GIF Capture

```
User selects 150×200 pt region on 2× display
→ sourceRect = (x, y, 150, 200) in points
→ pixelWidth = 300, pixelHeight = 400
→ SCStreamConfiguration: sourceRect=(x,y,150,200), width=300, height=400, scalesToFit=true
→ SCStream delivers CGImage frames at 300×400
→ Frames optionally downscaled to maxWidth (default 640px)
→ Saved as .gif via ImageIO
```

GIF uses `scalesToFit = true` for the same reasons as video — the SCStream needs to fill the buffer completely. The `maxWidth` setting independently controls final GIF dimensions for file size management.

## Editor Display: Preventing Upscale Blur

The screenshot editor displays the captured image in a resizable window. Without capping, a 300×400 pixel image on a 2× display would be scaled to fill a ~700×500 pt editor window — a 2.3× upscale that visibly blurs the image.

The `displaySize(in:)` method caps the display to native pixel mapping:

```swift
let screenScale = NSScreen.main?.backingScaleFactor ?? 2.0
let maxWidth = min(containerSize.width * 0.95, imagePixelSize.width / screenScale)
let maxHeight = min(containerSize.height * 0.95, imagePixelSize.height / screenScale)
```

On a 2× display, a 300×400 px image displays at most 150×200 **points** — exactly 1:1 pixel mapping where each image pixel maps to exactly one physical display pixel. The image appears sharp at its native size rather than being stretched to fill the window.

## Region Selector Labels

During region selection, the dimension label adapts to the display:

- **Retina (scale ≠ 1×):** `150 × 200 pt · 300 × 400 px` — shows both point size (what you're dragging on screen) and pixel size (what you'll get in the file).
- **Standard (scale = 1×):** `150 × 200` — no distinction needed since points = pixels.

## Summary Table

| Aspect | Screenshot | Video | GIF |
|--------|-----------|-------|-----|
| `sourceRect` | Points | Points | Points |
| `width`/`height` | Pixels via `pixelWidth`/`pixelHeight` | Pixels via `pixelWidth`/`pixelHeight` | Pixels via `pixelWidth`/`pixelHeight` |
| `scalesToFit` | `false` | `true` | `true` |
| API | `SCScreenshotManager` | `SCStream` | `SCStream` |
| Output resolution | Native pixel (e.g. 300×400) | Native pixel (e.g. 300×400) | Native pixel, optionally capped by `maxWidth` |
| DPI metadata | 72 (default) | N/A (video) | N/A (GIF) |
| Editor display | Capped at 1:1 pixel mapping | AVPlayer (native) | AVPlayer (native) |

## Pitfalls We Avoided

1. **Putting pixel coordinates in `sourceRect`** — Causes the capture region to be offset and oversized. ScreenCaptureKit expects points.

2. **Using `scalesToFit = true` for screenshots** — `SCScreenshotManager` rasterizes at 1× then upscales, producing blurry captures on Retina.

3. **Using `scalesToFit = false` for video/GIF streams** — `SCStream` leaves blank margins when the sourceRect doesn't perfectly tile into the pixel grid.

4. **Inconsistent pixel rounding** — Using `Int()` truncation in some places and `.rounded()` in others could produce 1px differences between SCStream config and AVAssetWriter config. Now all pixel dimensions use `.rounded()` via `CaptureRegion.pixelWidth`/`pixelHeight`.

5. **Writing custom DPI to screenshots** — Setting 144 DPI on a 2× capture makes `NSImage.size` return point dimensions, which confused the editor's display sizing and didn't help external viewers.

6. **Upscaling small captures in the editor** — Scaling a 300×400 px image to fill a 700×500 pt window (1400×1000 backing pixels) causes visible blur. Capping at `pixelSize / scaleFactor` ensures 1:1 pixel display.
