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

## Screenshot-editor output

The editor uses `ScreenshotExportSize` for both its accessible output-resolution label and
Save/Copy. It truncates the logical export frame to integer pixels, then applies midpoint-to-even
scale rounding with a one-pixel minimum. Padding, aspect-ratio frames, and image alignment are
part of that frame; monitor rasterization scale is not. Crop pre-baking always uses native image
dimensions and excludes export backgrounds, padding, corners, and shadows.

The UI thread captures immutable annotation/style data and a lease on the immutable source
image. Workers record list-order Win2D composition at 96 DPI and replay it directly into one
final-size render target. At 100% there is no subsequent resample; other scales do not create an
intermediate full-size bitmap. Each export has one explicit target pixel readback instead of the
previous flatten-readback/upload/resample-readback sequence. Effects can still use internal GPU
surfaces. The source upload is lazy, worker-owned, and reused until the image or shared device
changes.

Rendering/readback, redaction processing, PNG/JPEG encoding, JPEG alpha conversion, and WebP
encoding run off the UI thread. Clipboard output remains a PNG bitmap data package with the
existing flush contract; publication and XAML preview creation happen on the UI thread only.
Redaction requests are deduplicated/canceled and validated again after the XAML pixel copy.
Document replacement and closure cancel pending work, while source leases prevent premature
disposal. Cancellation is cooperative: in-flight native work drains before its lease is released.
Edits and undo invalidate pending preview/clipboard results. Saving a snapshot can
finish while editing continues, but it clears dirty state only if its revision is still current.
Output clicks/shortcuts during another output operation are coalesced, not queued; save staging
preserves the prior destination on cancellation or encoding failure.

Deterministic Core tests cover frame/scale dimensions, deep annotation snapshots, styling/order
retention, revision/dirty behavior, late-worker ownership, and staged-save cancellation/failure.
They do not establish native visual fidelity or UI latency. Before release, use disposable
synthetic images on **native x64 and native ARM64** to check PNG/JPEG/WebP output, transparent
pixels, rotated/text/emoji/redaction ordering, backgrounds/frames/corners/shadows, crop, rapid
style/geometry changes, undo, repeated output, Reset, and closure. Inspect clipboard PNG output
only with consent. Compare UI-thread traces before/after separately from deterministic tests;
do not infer responsiveness or a speedup from the reduced explicit operation count.

## Known limitations

- Capturing a region that **spans two monitors with different scale factors** is not supported;
  the region is constrained to the monitor under the start of the drag (matching the macOS app).
- DPI changes *during* an active recording are not re-negotiated; the session keeps the
  resolution it started with.
