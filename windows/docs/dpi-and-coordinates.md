# DPI & Coordinates on Windows

Tiny Clips for Windows captures pixels, but Windows describes most UI geometry in
**device-independent pixels (DIPs)**. Getting crisp captures on high-DPI and mixed-DPI
multi-monitor setups requires being deliberate about which coordinate space each value is in.
This is the Windows analogue of the macOS *points vs. pixels* / retina capture document.

## The two coordinate spaces

| Space | Unit | Where it shows up |
| --- | --- | --- |
| **Physical / pixels** | Raw device pixels | `Windows.Graphics.Capture` frames, `HMONITOR` bounds from `GetMonitorInfo`, the BGRA buffers we encode |
| **Logical / DIPs** | 1 DIP = 1/96 inch | WinUI layout, `AppWindow`/`DisplayArea` positions, pointer events, `RasterizationScale` |

The conversion factor is the monitor's **scale factor** (`RasterizationScale`, e.g. `1.0`,
`1.25`, `1.5`, `2.0`). On a 150% display, a 100‑DIP overlay is 150 physical pixels wide.

> **Rule of thumb:** anything that touches the capture buffer is in **pixels**; anything that
> positions a WinUI window or reads a pointer is in **DIPs**. Never mix them without scaling.

## Per-monitor DPI awareness

The app runs **Per-Monitor-V2** DPI aware (the WinUI 3 default). Consequences:

- Each monitor can have a different scale factor; a window's effective scale changes when it
  moves between monitors (`AppWindow`/`XamlRoot` `RasterizationScale` updates).
- `DisplayArea.GetFrom...` returns **physical-pixel** `WorkArea`/`OuterBounds` rectangles. We use
  these to place full-screen overlays (region select, screen/window pickers) so they line up
  exactly with the capture, then size their *content* in DIPs via the window's rasterization scale.
- Graphics.Capture always delivers **physical** frames at the monitor's true resolution,
  independent of scale factor — so screenshots are pixel-perfect regardless of display scaling.

## Region capture

Graphics.Capture targets a whole monitor (or window); a *region* is the monitor frame cropped
to a sub-rectangle. The pipeline:

1. The region-select overlay covers the monitor using its **physical** `OuterBounds`.
2. The user's drag (in DIPs, from pointer events) is converted to **physical pixels** using the
   overlay's `RasterizationScale`, yielding a monitor-relative `PixelRect`.
3. `ContinuousCaptureSession` / `ScreenCaptureService` crop the BGRA frame to that `PixelRect`
   honouring the frame's `RowPitch` (stride ≥ width × 4; never assume they're equal).

Because the crop is computed in pixels against a pixel frame, no rounding drift accumulates.

Video/GIF sessions first intersect the requested rectangle with the initial WGC item in physical
pixels, then crop the resulting width/height down to even encoder dimensions. Negative origins
reduce the intersection rather than moving the rectangle; empty or sub-2-pixel intersections
fail explicitly instead of inventing a 2x2 image. Both recording paths use `CaptureOutputGeometry`.
The video performance report records `Requested`, `Clipped`, and `Encoded` rectangles and
adjustment flags. A window target ignores a monitor-relative region and uses its own WGC size.
The headless recording benchmark establishes Per-Monitor-V2 awareness before querying monitors;
its requested dimensions are pixels, never scaled a second time.

## Window capture

`CreateForWindow` captures a window's client area at physical resolution. We do **not** apply a
region crop to window targets — the window item is already scoped to the content. The window's
own DPI may differ from the monitor it's on; the captured frame reflects the window's pixels.

## Practical checklist

- Convert pointer/DIP values to pixels with the *correct* monitor's scale, not the primary's.
- Read monitor bounds from `DisplayArea` (pixels) for overlay placement; don't hand-roll from DIPs.
- Respect `RowPitch` when cropping or copying frame buffers.
- Re-query scale on `XamlRoot.Changed` if an overlay can move between monitors mid-gesture.
- Keep saved images at native pixel size; only the optional *scale* setting downsamples on save.

## Known limitations

- Capturing a region that **spans two monitors with different scale factors** is not supported;
  the region is constrained to the monitor under the start of the drag (matching the macOS app).
- DPI changes *during* an active recording are not re-negotiated; the session keeps the
  resolution it started with.
