# Windows Scrolling (Panorama) Capture Plan

Tracks [#272](https://github.com/jamesmontemagno/tiny-clips/issues/272). Goal: parity with the macOS v1.6.0 scrolling capture — select a region, scroll normally, get one tall stitched screenshot that flows into the existing screenshot editor / save pipeline.

## Reference implementation (mac)

| mac file | Role | Windows counterpart |
| --- | --- | --- |
| `mac/TinyClips/Capture/ScrollingPanoramaCapture.swift` | `PanoramaFrame`, `PanoramaAccumulator` (stitching), `ScrollingPanoramaCapture` (SCStream loop) | `TinyClips.Core/Capture/Panorama*.cs` + `ScrollingCaptureSession.cs` |
| `mac/TinyClips/Views/ScrollingCapturePanel.swift` | Floating Done/Cancel panel with frame count + status | `TinyClips.App/Views/ScrollingCaptureWindow.xaml(.cs)` |
| `mac/TinyClips/Views/CapturePickerPanel.swift` (`.scrolling`, key `P`) | Picker entry point, screenshot mode only | `CapturePickerWindow` new `Scrolling` mode + button + `P` key |
| `mac/TinyClips/CaptureManager.swift` `startScrollingCapture` / `finishScrollingCapture` | Orchestration | `App.xaml.cs` `BeginCaptureAsync` switch + new helpers |
| `mac/TinyClipsTests/CaptureMathTests.swift` `testPanorama*` | Algorithm tests | `TinyClips.Core.Tests/PanoramaStitcherTests.cs` |

The mac algorithm is deliberately simple and well-tested; the plan is a **faithful port** rather than the heavier NCC/SAD two-pass described in the issue. The row-luma SAD search + pixel verification already handles the issue's acceptance criteria (seamless output, periodic content, sticky footer suppression) and is what the mac tests lock in. NCC can be a later refinement if real-world captures show drift.

## Architecture

```
TinyClips.Core/Capture/
  PanoramaCaptureLimits.cs     record: MaxFrames, MaxOutputHeight, MaxMemoryBytes, NoMovementTimeout
  PanoramaCaptureException.cs  + PanoramaCaptureError enum (Cancelled, NoMovement, OutputTooLarge, MemoryLimit, NoFrames, AlignmentFailed)
  PanoramaCaptureLimitReason.cs enum + Message extension
  PanoramaFrame.cs             BGRA8 pixels + precomputed float[] RowLuma (from CapturedFrame)
  PanoramaAccumulator.cs       incremental stitcher (pure, testable, no WinRT)
  PanoramaStitcher.cs          convenience: stitch(IEnumerable<PanoramaFrame>) -> PanoramaResult
  IScrollingCaptureService.cs  StartAsync(target, region, ct) / StopAsync() -> CapturedFrame / Cancel(); events
  ScrollingCaptureService.cs   WGC loop using a new ContinuousCaptureSession-style reader

TinyClips.App/
  Views/CapturePickerWindow.xaml(.cs)   "Scroll" button (screenshot only) + P key + CapturePickerMode.Scrolling
  Views/ScrollingCaptureWindow.xaml(.cs) floating panel: mode label, pulsing dot + frame count, status, Done, Cancel
  App.xaml.cs                            BeginCaptureAsync: case Scrolling -> StartScrollingCaptureAsync(...)
```

### Core: PanoramaFrame

- Constructed from `CapturedFrame` (tightly packed BGRA8). Note mac pixels are RGBA; Windows is **BGRA** — luma weights must be applied as `B=29, G=150, R=77` (integer Rec.601 ×256) on indices `[0],[1],[2]`.
- `RowLuma`: mean luma per row, sampling every `max(1, width/160)` column, matching mac.
- `ByteCount` for memory accounting.

### Core: PanoramaAccumulator (direct port)

- `Append(PanoramaFrame) -> PanoramaAppendOutcome { Accepted, Skipped, LimitReached(reason) }`
- `Finish() -> PanoramaResult { CapturedFrame Image, int FrameCount, int OutputHeight, bool ReachedLimit }`
- `EstimateVerticalShift(prev, cur) -> Alignment? { Shift, Score, FixedBottomHeight }`
  - Ignore top/bottom `height/20` bands; shift range `[2, height - height/10]`; min samples `max(8, height/8)`.
  - SAD over `RowLuma` for every shift (plain loops / `Span<float>`; optionally `System.Numerics.Vector<float>` — no vDSP).
  - Acceptance band `max(best*1.6, best+0.5)`; iterate local minima ascending; verify with `PixelAlignmentScore <= 12`; give up after 6 verifications. **Smallest credible shift wins** (periodic content).
  - `StationaryBottomBand` up to `min(shift/2, height/4)` rows with per-row mean luma diff `<= 2`.
- `AreMeaningfullyDifferent(a, b)`: 80×80 sample grid, mean |Δluma| > 2.5 → different (duplicate-frame rejection).
- Output buffer: a growing `byte[]` via `ArrayBufferWriter<byte>`/`MemoryStream`-style doubling, committed rows + held footer band exactly as mac. Memory check `outputBytes*2 + frameBytes*2 <= MaxMemoryBytes`.
- Defaults: `MaxFrames=600`, `MaxMemoryBytes=1_200_000_000` (issue says ~1.2 GB; mac uses 1.5 GB), `NoMovementTimeout=8s`, `MaxOutputHeight` — see open question 1.

### Core: ScrollingCaptureService (WGC loop)

- Reuse the `ContinuousCaptureSession` pattern but **without the pump timer**: WGC only fires `FrameArrived` on change, which is exactly what we want. Add an internal `WgcFrameReader` (or a `ContinuousCaptureSession` ctor flag `emitOnArrivalOnly`) that crops to the region and raises raw frames as they arrive, throttled to ≤ 12 fps (mac uses `minimumFrameInterval = 1/12`) by dropping frames closer than 83 ms to the last processed one.
- Cursor excluded (`TryConfigureSession(session, includeCursor: false)`).
- Processing happens on a dedicated single-consumer `Channel<CapturedFrame>` (bounded, `DropOldest`) so stitching never blocks WGC delivery; accumulator state lives on that consumer only (no locks on hot path).
- Per frame: build `PanoramaFrame`; if `!AreMeaningfullyDifferent(previous, frame)` → skip; if idle > `NoMovementTimeout` **after at least one accepted frame** → raise `Failed(NoMovement)`? Mac raises failure; for Windows prefer raising `LimitReached`-style auto-stop only if frames ≥ 2, otherwise keep waiting (user may be reading the panel). Decide in open question 3.
- Events (marshalled by the App to the UI thread): `Progress(int acceptedFrames)`, `LimitReached(PanoramaCaptureLimitReason)`, `Failed(Exception)`.
- `StopAsync()` → stops WGC, drains channel, `accumulator.Finish()` → `CapturedFrame`. `Cancel()` → stops and discards. Both idempotent.
- Registered in `ServiceCollectionExtensions` as transient `IScrollingCaptureService`.

### App: Capture picker

- `CapturePickerMode.Scrolling`; new `ScrollButton` between Window and Recognize Text with glyph `\uE74B` (Down) or `\uE8A1` (Page) — pick in design pass; `AutomationProperties.Name="Scrolling capture"`, tooltip "Scrolling capture (P)"; key `P`.
- Visible only when `captureType == Screenshot` (same as `RecognizeTextButton` should be — verify and align both in `Configure`).
- Countdown is **not** applied to scrolling (mac passes `countdownEnabled: false`); the picker result ignores the timer for this mode.

### App: Flow in `App.xaml.cs`

```
case CapturePickerMode.Scrolling (CaptureType.Screenshot):
  selection = ResolveTargetAsync(CapturePickerMode.Region, earlyBackdrop)   // reuse region overlay
  → StartScrollingCaptureAsync(selection, settings, wasPickerInitiated)
```

`StartScrollingCaptureAsync`:
1. Guard `_scrollingCapture is null`; mark `_captureFlowCts` busy so hotkeys don't start another capture; `IsAnyRecordingActive()` should treat scrolling as active (tray menu state).
2. Show `RegionIndicatorWindow` for the region if `settings.ShowRegionIndicator` (mac parity) — it's already excluded from capture.
3. Show `ScrollingCaptureWindow` near the bottom-center of the selected monitor's work area (remember last dragged position per session like mac `scrollingCapturePanelPosition`). Announce "Scrolling capture started. Scroll the page, then press Enter to finish." via `AutomationNotificationAnnouncer`.
4. `await service.StartAsync(selection.Target, selection.Region, ct)`.
5. Wire events → `panel.UpdateFrameCount`, `panel.ShowStatus(reason.Message)` + auto-stop, failure → teardown + `ShowErrorToast` (unless Cancelled).
6. Done (button / Enter / global hotkey?) → `panel.MarkFinishing()`; `frame = await service.StopAsync()`; teardown panel + indicator; then **reuse `CaptureScreenshotAndPresentAsync`'s tail**: brand if enabled, `SaveScreenshotFrameAsync`, open editor from memory (or from file if scale < 100), else reveal + toast; `ReopenPickerAfterCaptureIfNeeded(Screenshot, wasPickerInitiated)`. Extract that tail into `PresentCapturedScreenshotAsync(CapturedFrame, settings, wasPickerInitiated)` so both paths share it.
7. Cancel (button / Esc) → `service.Cancel()`, teardown, reopen picker if configured.
8. Record analytics as `CaptureType.Screenshot` (same as mac).

### App: `ScrollingCaptureWindow`

- Model on `RecordingIndicatorWindow`: `OverlappedPresenter.CreateForContextMenu()`, always-on-top, `ExcludeFromCapture`, `FloatingWindowDragger`, acrylic/Mica-alt backdrop, rounded corners, not in switchers.
- Layout (single row, mirrors mac): `FontIcon` + "Scrolling" label · separator · pulsing red dot + "N frames" (monospace digits) · status `TextBlock` (260 px, orange when limit message) · separator · accent **Done** button (✓, Enter) · subtle **Cancel** icon button (✕, Esc).
- Disable both buttons + set status "Saving…" while finishing.
- Accessibility: `AutomationProperties.Name` on all buttons, `LiveSetting="Polite"` on status, frame-count `AutomationProperties.Name="Captured frames"`; Enter/Esc handled in `KeyDown` on root; window takes focus on show so keys work without clicking (the page keeps receiving wheel scroll since wheel goes to the window under the cursor).
- Global Enter/Esc (mac installs global monitors): Windows equivalent would be a low-level keyboard hook — **out of scope for v1**; Done/Cancel buttons + focus-on-show are sufficient. Document in README.

### Editor / output size limits

`ScreenshotEditorWindow` renders via `SoftwareBitmap` → XAML `Image`, which is backed by a D3D texture with a **16384 px** max dimension. A 50 000-px-tall panorama (mac default) will fail to display. Options for open question 1.

## Tests (`TinyClips.Core.Tests/PanoramaStitcherTests.cs`)

Port every mac test with synthetic frames (40×100 gradient with `(row*7 + x*13) % 251`, BGRA):

- `StitchesKnownVerticalShift` (2 frames, shift 20 → height 120)
- `RejectsFramesWithoutCredibleAlignment` → `AlignmentFailed`
- `SuppressesStationaryFooterCopies` (footer 5 rows, check pixel at y=95 and y=119)
- `EnforcesPeakMemoryBudget` → `MemoryLimit`
- `KeepsPartialResultWhenMemoryLimitIsReached` / `…WhenOutputHeightIsReached`
- `PrefersSmallestShiftOnRepeatingContent` (period 30)
- `AlignsSmallScrollSteps` (shift 4 → height 104)
- `AlignsPeriodicContentAcrossShiftSizes` (320×900, shifts 6/37/120/480)
- `AreMeaningfullyDifferent` true/false cases
- `PanoramaFrame.RowLuma` uses BGRA channel order (red-only vs blue-only rows differ as expected)
- `Finish` with a single frame → `NoMovement`; with zero frames → `NoFrames`

No tests for the WGC session (hardware) — consistent with existing `ContinuousCaptureSession`.

## Docs / changelog

- `windows/CHANGELOG.md`: "Added scrolling capture (Screenshot picker → Scroll / `P`): stitches a scrolled region into one tall image."
- `windows/README.md`: feature list + picker shortcut table; note Enter/Esc require the panel focused.
- Update `docs/` parity table if one lists scrolling capture as mac-only.

## Validation

```powershell
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64 -p:TinyClipsStoreBuild=true
dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug
```

Manual: long web page at normal speed (seamless); page with sticky header + footer (no repeats); cancel discards; Done after 1 frame → "no movement" error; memory limit message with a huge region at 150 % DPI; editor disabled → direct save + toast; mixed-DPI secondary monitor region.

## Work breakdown (suggested PR order — can be one PR or split Core/App)

1. Core models + `PanoramaFrame` + `PanoramaAccumulator` + `PanoramaStitcher` + tests.
2. `ScrollingCaptureService` (WGC loop) + DI registration.
3. Picker mode/button/key.
4. `ScrollingCaptureWindow`.
5. `App.xaml.cs` orchestration + shared `PresentCapturedScreenshotAsync` refactor.
6. Changelog/README, manual validation pass.

## Decisions (2026-08-21)

1. **Max output height (adaptive cap)**: Win2D/XAML textures cap at 16 384 px, which affects the editor's preview *and* Save/Export. When `ShowScreenshotEditor` is on, the capture auto-stops at 16 384 px ("Maximum height reached, saving what was captured") so the editor can open it; when the editor is off (direct save) the mac default of 50 000 px applies.
2. **Picker entry**: inline "Scroll" button after Window, `P` key, screenshot mode only.
3. **No-movement timeout**: none. A live test showed a blinking caret inside the region trips a repaint-without-scroll timeout while the user lines up Done; the capture stays open until Done/Cancel or a size/frame/memory limit. (`NoMovement` is still reported when Done is pressed after a single frame.)
4. **Hotkey**: none (mac parity; avoids ZoomIt chords).
5. **Scope**: single PR.
