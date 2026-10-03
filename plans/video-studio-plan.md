# Tiny Clips Studio: a compositing editor for video recordings (macOS + Windows)

The feature is named **Tiny Clips Studio**. This plan, the UI, and the code call it "Studio" for short.

## Problem and approach

Today a TinyClips video is flattened as it is made. Windows draws the camera, click rings, and branding into every frame while recording. macOS records the screen and camera separately, but merges them in up to two re-encode passes as soon as you stop. By the time the trimmer opens there is a single video, and the only edits left are trim, speed, and mute.

Studio keeps the pieces apart. A recording made for Studio is saved as a **project**: a clean screen track, a separate camera track, and the cursor and click data. Studio arranges those over a background, lets the arrangement change over time, and renders the final MP4 only on export. The project stays editable afterward.

## Decisions confirmed

| Question | Decision |
|---|---|
| Platforms | Both. One shared design, built in parallel, milestone by milestone |
| "Cut outs" | Shaped layers and crops first; person cutout (camera background removal) later |
| Re-editable after export | Yes. App-managed projects, with automatic cleanup of old sources |
| Trimmer | Stays. Studio is a separate window, and an "After recording" setting picks Save, Trimmer, or Studio |
| Layout over time | Scenes (segments, each with its own layout) plus a separate zoom lane. The first version has one scene |
| Zooms | Manual segments first, then auto-zoom suggestions from clicks and cursor that you accept or adjust |
| When to capture editable sources | Only when Studio is the after-recording choice, or "Record for Studio" is on in the pre-record panel. Other recordings keep today's path untouched |
| Name | Tiny Clips Studio |
| Price | Free on every build. No Pro gating on the Mac App Store |
| Cleanup defaults | Sources kept 30 days after last opened, with a 10 GB cap on total project storage |
| First-run look | A gradient background with padding |

## Where the code is today

| | macOS | Windows |
|---|---|---|
| Screen | `VideoRecorder` (ScreenCaptureKit into `AVAssetWriter`). Cursor baked in. System audio and microphone are separate audio tracks | `VideoRecordingService` (WGC into a GPU texture pool into `MfSinkWriterEncoder`). Cursor baked in. One mixed audio track |
| Camera | `WebcamRecorder` writes a companion `-webcam.mp4` | `WebcamCaptureService` frames are drawn into each screen frame. There is no camera file, and camera resolution follows the overlay size preset (at most 1280×720) |
| Compositing | After stop: `MouseClickOverlayProcessor`, then `BrandingOverlayProcessor` (a custom `AVVideoCompositing` compositor using Core Image). Each is a re-encode | Live: `GpuOverlayCompositor` (Direct2D), or the CPU compositors on the fallback pipeline |
| Click data | `MouseClickEvent` (time, location) | `MouseClickSample` (time, x, y) |
| Editing | `VideoTrimmerWindow`: `AVPlayer` plus `AVAssetExportSession` (trim, speed, remove audio) | `VideoTrimmerWindow`: `MediaPlayerElement` plus `MediaComposition` (trim, remove audio; speed applies to the preview only) |
| Reusable look | Screenshot editor background presets, padding, and frame presets (`ExportBackgroundStyle`, `ExportFramePreset`) | The same vocabulary in `Controls/ScreenshotEditor/EditorModels.cs` |

macOS already has separate sources and a compositor to grow from, so it mostly needs to stop flattening. Windows needs a new capture mode plus a preview and export engine.

## Shared design

### Building blocks

- **Canvas**: the output frame. Its aspect is Auto (the screen plus padding) or one of the screenshot editor's frame presets (1:1, 4:3, 16:9, 3:4, 9:16).
- **Background**: the screenshot editor's solid and gradient presets, plus a custom image.
- **Screen layer**: the screen track shown as a card, with padding, corner radius, shadow, and an optional crop.
- **Camera layer**: shape (circle, rounded rectangle, squircle, rectangle), size, position, mirror, border, shadow, and an optional crop. Person cutout arrives in M4.
- **Layouts**: Screen only, Screen with camera bubble, Side by side (stacked on portrait canvases), Camera only.
- **Scenes**: consecutive time segments, each with one layout. Moving between scenes is a cut or a short animated morph. M1 has a single scene covering the whole video.
- **Zoom lane**: segments that magnify the screen content inside its card. Each has a scale, a fixed focus point or follow-cursor, and an ease in and out.
- **Edits**: trim in and out (M1), cuts and speed (M3), mute and volume.
- **Look**: canvas, background, and layer styling can be saved as the default for new projects. Until you save one, new projects open with a gradient background and padding.

### Project format and storage

A project is a folder holding `project.json`, `events.json`, `screen.mp4`, `camera.mp4` (when there was a camera), and `poster.jpg`. The full schema and the layout math are in [docs/studio-project-format.md](../docs/studio-project-format.md); the JSON below is a sketch.

- Windows: `%LOCALAPPDATA%\TinyClips\Projects\<id>\`, next to the existing `Temp` folder.
- macOS: `Application Support/TinyClips/Projects/<id>/`, which is inside the sandbox container on the App Store build.

Format rules:

1. Times are seconds on the pause-adjusted recording timeline ("source time"). Scenes, zooms, and cuts are stored in source time, so adding a cut does not shift anything else. A small `StudioTimeMap` converts between source time and output time.
2. Positions and sizes are normalized to 0–1, so a project is resolution independent. Click and cursor points are normalized to the captured rectangle at record time, which keeps DPI and Retina conversions inside the recorders where they already live.
3. Both files carry `schemaVersion`, and unknown fields survive a save.
4. Each export is recorded in `project.json`. That list is how the Clips Library finds the project behind a video.

```json
{
  "schemaVersion": 1,
  "sources": {
    "screen": { "file": "screen.mp4", "width": 3440, "height": 1440, "frameRate": 30, "duration": 92.4 },
    "camera": { "file": "camera.mp4", "width": 1920, "height": 1080, "startOffset": 0.21 }
  },
  "canvas": { "aspect": "auto", "padding": 0.06, "background": { "style": "gradient", "preset": "ocean" } },
  "screen": { "cornerRadius": 0.02, "shadow": 0.5, "crop": null },
  "camera": { "shape": "circle", "mirror": true, "crop": null },
  "scenes": [ { "start": 0, "layout": "bubble", "bubble": { "anchor": "bottomRight", "size": 0.24 }, "transition": { "kind": "cut" } } ],
  "zooms":  [ { "start": 12.0, "end": 16.5, "scale": 2.0, "focus": { "mode": "point", "x": 0.31, "y": 0.62 }, "origin": "manual" } ],
  "edits":  { "trimStart": 0.21, "trimEnd": 92.4, "cuts": [], "speed": [] },
  "exports": [ { "path": "TinyClips 2026-10-02 at 15.28.06.mp4", "exportedAt": "2026-10-02T22:41:00Z" } ]
}
```

`events.json` holds `clicks` (time, x, y, button), `cursor` samples (time, x, y), `cameraCorners` (the live corner changes both apps already track), and later `markers` for live layout switches.

### Cleanup of old sources

Defaults, all adjustable in Settings:

- Sources of an exported project are deleted 30 days after it was last opened, or sooner when total project storage passes 10 GB (oldest first).
- A project that has never been exported (a draft) is never deleted automatically.
- "Keep sources" pins a project. Settings shows project storage and a Clean up now button.
- After cleanup the exported MP4 remains. Opening it in Studio still works in single-layer mode: background, padding, zoom, and cuts apply to the flattened video, but the camera can no longer be rearranged. The same mode opens any existing MP4.

### One layout engine, two implementations

`StudioLayoutResolver` is a pure function. It takes a project, a source time, and a canvas size, and returns the rectangles, crops, radii, and opacities to draw. It is written once in Swift and once in C# from a single written spec.

Shared golden fixtures in `shared/studio/fixtures/` (a project plus the expected output at sample times) are loaded by both test suites, so the platforms cannot drift without a test failing. `StudioTimeMap` and the auto-zoom heuristic get the same treatment. This fits the existing test rules: deterministic logic only on macOS, Core tests on Windows.

### Capture flow

- A new video setting, **After recording**, offers Save, Trimmer, or Studio. It replaces the `showTrimmer` / `ShowTrimmer` boolean (on migrates to Trimmer, off to Save).
- The pre-record panel gains **Record for Studio**, which defaults from that setting.
- With it on, the recorder writes a clean screen track (no camera, click rings, or branding; the cursor stays baked in for now), a camera track at the camera's own resolution, and `events.json`.
- On stop the project is finalized and Studio opens straight away with the default look. Nothing is rendered before you start editing.
- Closing Studio without exporting asks whether to Export, Keep as draft, or Delete. Export renders into the normal save folder and then follows the existing save handling (clipboard, notification, Recent Captures, Clips Library).
- With it off, nothing changes from today.
- If the camera fails, the project is screen-only, matching today's fallback.

### Studio window

```
+----------------------------------------------------------------+
| clip name            aspect: Auto v                  [ Export ] |
+------------------------------------------+---------------------+
|                                          | Layout  [S][B][=][C] |
|             preview canvas               | Background           |
|   drag/resize camera, drag zoom focus,   | Screen  crop corners |
|   crop handles                           | Camera  shape size   |
|                                          | Zoom    Audio        |
+------------------------------------------+---------------------+
| > 00:12.4 / 01:32      [Split] [Add zoom] [Cut]                |
| Scenes | Bubble         | Side by side      | Camera |         |
| Zoom   |      [ 2x ]            [ 1.5x ]                       |
| Clip   [======== trim ===============================]         |
+----------------------------------------------------------------+
```

- Every drag on the canvas has an inspector equivalent with numeric fields.
- Every timeline item is keyboard reachable and has an accessible name and value, such as "Zoom 2×, 12.0 to 16.5 seconds".
- Keys: Space to play or pause, Left and Right to step a frame, I and O for trim in and out, S to split a scene, Z to add a zoom, 1–4 for the layout, Delete, undo and redo, and Ctrl/Cmd+E to export.
- Undo and redo are a snapshot stack over the project value.

## Platform architecture

### macOS

- New `mac/TinyClips/Studio/` folder: `StudioProject` and `StudioEvents` (Codable), `StudioProjectStore`, `StudioLayoutResolver`, `StudioTimeMap`, `StudioAutoZoom`, `StudioCompositionBuilder`, `StudioCompositor`, `StudioExporter`. The UI is `Views/StudioWindow.swift` plus canvas, timeline, inspector, and view model files, split the way the screenshot editor is.
- Preview and export share one path. `StudioCompositionBuilder` turns a project into an `AVMutableComposition` (screen track, camera track placed by its start offset, both audio tracks) and an `AVVideoComposition` with a custom compositor. `StudioCompositor` grows out of `BrandingOverlayProcessor.WebcamOverlayCompositor` and draws each frame with Core Image. `AVPlayer` uses it for live preview and `AVAssetExportSession` uses it for export.
- Capture: a Studio branch in `CaptureManager.stopRecordingFlow` skips the two flatten passes and moves the screen and camera files into the project. The existing camera-minus-screen first-frame offset is stored as `camera.startOffset`, and the default trim-in reproduces today's leading trim. `MouseClickMonitor` adds the mouse button and cursor sampling (`NSEvent.mouseLocation` on a timer, which needs no new permission).
- Conventions: `StudioWindow` follows the capture-window rules (an `NSWindow` hosting SwiftUI, a completion callback with the double-fire guard, menu commands through the responder chain as `TrimmerMenuCommands` does). View models use `ObservableObject` and `@Published`. Studio is free, so there is no `#if APPSTORE` gating, and Sparkle is not involved; both schemes get the same code.
- The Xcode project does not use synchronized folders, so every new file needs `project.pbxproj` entries for both app targets.

### Windows

- New `TinyClips.Core/Studio/`: models as records with a `JsonSerializerContext` (the release build is NativeAOT), `StudioProjectStore`, `StudioLayoutResolver`, `StudioTimeMap`, `StudioAutoZoom`, `StudioSceneRenderer`, `StudioExporter`.
- `StudioSceneRenderer` draws with Direct2D on the shared D3D11 device, as `GpuOverlayCompositor` does, and reuses its camera brush, click ring, and branding badge code. It lives in Core because Win2D is app-only.
- Preview (`Controls/Studio/StudioPreview` in the app): two `MediaPlayer`s in frame-server mode, one per track, locked together by a `MediaTimelineController`. Each frame is copied to a texture, drawn by the renderer, and presented through a swap chain panel.
- Export: Media Foundation source readers decode both tracks, the renderer draws each output frame, and the existing `MfSinkWriterEncoder` encodes. Because this works frame by frame, it can apply speed to the output, which the current trimmer cannot.
- Capture: `VideoRecordingService` gets a Studio mode that skips drawing overlays, sends camera frames to a second `MfSinkWriterEncoder` stamped from the shared `RecordingTimeline`, requests the camera at up to 1080p, and records events. `IWebcamCaptureService` needs a per-frame callback (it exposes only the latest frame today). `MouseClickMonitor` adds the button and cursor sampling. Completion carries a project id so `App.OnRecordingCompleted` can route to Studio.
- App: `Views/Studio/StudioWindow.xaml`, `ViewModels/Studio/StudioViewModel.cs`, and `Controls/Studio/` for preview, timeline, and inspector, reusing `TrimBar` and `WindowChromeController`. Tray-first startup is unchanged, and the Store and direct flavors are identical.

## Milestones

Each milestone lands on both platforms before the next one starts. Studio is labeled Preview and hidden by default on a platform until that platform finishes M1.

| Milestone | What you can do at the end |
|---|---|
| **M0 Foundations** | Nothing user-facing. The design doc and format spec are in the repo, fixtures exist, and an engine spike on each platform has confirmed the preview and export approach |
| **M1 Compose and export** | Record for Studio. Pick a background and padding, round the screen card, choose a camera shape and one of four layouts, drag the bubble, trim in and out, and export. Reopen the project later from the Clips Library |
| **M2 Crops and zooms** | Crop the screen or camera to a region. Add zoom segments by hand, then accept or adjust suggested zooms built from your clicks and cursor |
| **M3 Scenes and cuts** | Split the video into scenes with their own layouts and animated transitions. Cut out sections, change speed, and set volumes. Switch layouts live while recording and have them arrive as scenes |
| **M4 Person cutout** | Remove or blur the camera background |

## Todos

"Shared" items gate both platform tracks.

| Id | Platform | Work |
|---|---|---|
| `m0-design-doc` | Shared | Commit this design as `plans/video-studio-plan.md` and the schema as `docs/studio-project-format.md` |
| `m0-shared-fixtures` | Shared | `shared/studio/fixtures/` with the first layout fixtures; add the folder to both CI path filters |
| `m0-win-engine-spike` | Windows | `windows/spikes/StudioEngineSpike`: frame-server preview, source-reader export, second encoder for the camera |
| `m0-mac-engine-spike` | macOS | Throwaway prototype of live preview through a custom compositor |
| `m1-win-project-store`, `m1-mac-project-store` | Each | Project and event models, project store, export links, cleanup policy, tests |
| `m1-win-editable-capture`, `m1-mac-editable-capture` | Each | Studio capture mode: clean screen track, camera track, events |
| `m1-win-layout-engine`, `m1-mac-layout-engine` | Each | `StudioLayoutResolver` and `StudioTimeMap`, passing the shared fixtures |
| `m1-win-renderer-export`, `m1-mac-renderer-export` | Each | Renderer and exporter |
| `m1-win-studio-window`, `m1-mac-studio-window` | Each | Studio window: preview, inspector, trim, draggable bubble, undo, export, accessibility |
| `m1-win-integration`, `m1-mac-integration` | Each | After recording setting, Record for Studio toggle, routing, Open in Studio from the library and recents, storage settings, docs and changelog |
| `m2-shared-zoom-spec` | Shared | Spec and fixtures for crops, zoom segments, and the auto-zoom heuristic |
| `m2-win-crop-zoom`, `m2-mac-crop-zoom` | Each | Crops and manual zoom lane |
| `m2-win-auto-zoom`, `m2-mac-auto-zoom` | Each | Auto-zoom suggestions and follow-cursor |
| `m3-shared-scenes-spec` | Shared | Spec and fixtures for scenes, transitions, cuts, and speed |
| `m3-win-scenes-cuts`, `m3-mac-scenes-cuts` | Each | Scene lane, transitions, cuts, speed, volumes |
| `m3-win-live-markers`, `m3-mac-live-markers` | Each | Live layout switching while recording |
| `m4-mac-person-cutout` | macOS | Vision person segmentation in the compositor |
| `m4-win-person-cutout` | Windows | Evaluate the options, get a decision on the added dependency, then implement |

## Implementation status

This section records what was built and how it differs from the plan above. It is updated as milestones land.

### Where each platform stands

| Milestone 1 piece | macOS | Windows |
|---|---|---|
| Project format, store, cleanup rules, layout resolver, time map | Done. Passes the shared fixtures | Done. Passes the shared fixtures |
| Studio capture mode | Done | Done. Checked with the recording benchmark |
| Renderer and exporter | Done | In progress |
| Live preview | Done (part of the renderer) | Not started. The design is settled by the engine spike |
| Studio window | Done | In progress |
| Settings, Record for Studio, reopening projects | Done | Settings, the Record for Studio toggle, and cleanup are done. Opening a project waits for the window |

Nothing on macOS has been run on a Mac. This work was done on Windows, where the macOS code can only be compiled and unit tested by the pull request's `Build` workflow. Capture, the compositor, the preview, export, and the whole Studio window are unverified at runtime. Until someone has run them, Studio stays off on macOS.

### Hidden switch

Studio is off by default on both platforms until it has been verified there.

- macOS: `defaults write com.tinyclips.app studioPreviewEnabled -bool YES` (`com.refractored.tinyclips` for the Mac App Store build).
- Windows: the `studioPreviewEnabled` setting, or the environment variable `TINYCLIPS_STUDIO_PREVIEW=1`. A packaged launch does not pass the caller's environment to the app, so start it with `winapp run <output folder> --manifest <output folder>\AppxManifest.xml --output-appx-directory <output folder>\AppX --with-alias`.

With the switch off, no Studio UI is visible and recordings follow the existing path unchanged.

### Decisions made while building

- **A failed project save keeps the recording.** If the project cannot be saved when a Studio recording stops, the screen track is kept as an ordinary video.
- **Drafts.** A recording kept as a draft has no exported file, so it does not appear in the Clips Manager. On macOS the drafts are listed in Settings › Video, where they can be opened or deleted. Windows needs the same list before Studio is switched on there.
- **Deleting an exported video leaves its export link in place.** The project then still counts as exported, so the cleanup rules remove its sources later. Removing the link would turn it back into a draft that is never cleaned up.
- **Cleanup can be switched off.** Zero days keeps projects until they are deleted by hand, and zero gigabytes means no storage limit.
- **Events during pauses.** Clicks and cursor samples from before the first frame or during a pause are not recorded. Cursor samples are capped at 60 per second, drop consecutive duplicates, and are steps, not points to interpolate between.
- **Drawing rules** are in section 6.7 of `docs/studio-project-format.md`: sRGB with gamma-space blending, no color conversion of screen pixels, the shadow model, where the border goes, and the click ring geometry.
- **Camera size on Windows.** The camera track is recorded at the camera's own aspect, fitted inside 1920×1080 and never enlarged.
- **Where projects are kept on Windows.** The installed app is packaged, so its projects are in the package's own folder, `%LOCALAPPDATA%\Packages\<package family>\LocalState\TinyClips\Projects`. Only an unpackaged run uses `%LOCALAPPDATA%\TinyClips\Projects`.
- **Windows editor behavior lives in Core.** `StudioEditorModel` (edits and undo) and the preview and export contracts are in `TinyClips.Core`, so the editor's rules are unit tested and the window only binds to them.
- **macOS preview.** The preview always plays the whole recording. Trim and mute are applied by the transport and by export, so changing them does not rebuild the player. Only a change of canvas shape does.
- **macOS keys.** Single-key shortcuts (Space, arrows, I, O, 1 to 4) are handled by the Studio window after focused controls have passed on them, so they are not taken from text fields or focused buttons. Esc follows the app's shared rule for closing editors.
- **Editor windows get a Dock icon.** While a Studio window is open on macOS, Tiny Clips shows its Dock icon and menu bar, as it does for the screenshot editor.
- **macOS layout.** Files directly inside `mac/TinyClips/Studio/` use Foundation only so their logic can be unit tested anywhere. Rendering is in `Studio/Rendering/` and the UI in `Views/Studio/`.

### Engine spikes

- **Windows** (`windows/spikes/StudioEngineSpike`, findings in its `FINDINGS.md`):
  - Preview: two frame-server `MediaPlayer`s on one `MediaTimelineController` delivered every frame of both clips at 1x and kept them on matching frames 98.6 to 99.95 percent of the time, never more than one frame apart. A paused seek occasionally delivers no frame, so every paused position change goes through a one-at-a-time seek policy that detects a lost seek and repairs it.
  - Export: Media Foundation source readers with the shared Direct3D device give textures Direct2D can draw without a copy. With offline encoder settings a 2560×1440 export ran at 131 to 238 frames per second on the test machine.
  - Presenting: a plain `SwapChainPanel` with a DXGI composition swap chain and Win2D's `CanvasSwapChainPanel` behaved alike. The plain panel is used, because the one shared renderer draws straight into its back buffer.
  - Recording: a second hardware encoder for a 1920×1080 camera track cost the screen track nothing measurable, with no dropped frames.
  - NativeAOT: every mode ran from the AOT build without changes.
  - Not measured: sound from the preview players, real recordings as input, a real change of monitor DPI, and a packaged AOT build.
- **macOS**: not run. It needs a Mac.

### Changes outside Studio

- The macOS `Build` workflow also builds the `TinyClipsMAS` scheme, and both workflows run when `shared/studio/**` changes.
- `TinyClipsActivationPolicy.resolve` takes `hasOpenEditors`, which covers screenshot editors and Studio windows.
- On Windows, `ShowTextRecognitionNotification` was renamed `ShowMessageNotification` because Studio reuses it.
## Risks and how the plan handles them

| Risk | Handling |
|---|---|
| Windows preview: two frame-server players staying in sync through seeks and frame steps, and presenting from Core's device under NativeAOT | `m0-win-engine-spike` chooses between a Win2D swap chain panel on the shared device and raw `SwapChainPanel` interop, and is checked with an AOT publish |
| Windows: a second hardware encode for the camera while recording | The spike measures it with `RecordingPerformanceMonitor`. The fallback is a software encode or 720p for the camera |
| macOS: refreshing the composited frame while paused and dragging, and Core Image speed on Retina 4K and 5K | `m0-mac-engine-spike`. The fallback is previewing at reduced scale |
| The Swift and C# engines drift apart | Shared golden fixtures that run in both CI workflows |
| Disk use roughly doubles for Studio recordings | Capture is opt-in, sources are cleaned up, and Settings shows the storage used |
| Studio grows into a full video editor | Scenes instead of keyframes, and backlog items stay in the backlog |
| Regressions in normal recording | The fast path is untouched. Studio capture is a separate mode behind the Preview flag |
| Timeline editing is hard to make accessible | The keyboard model and inspector equivalents are M1 requirements, and the Windows accessibility release gate gains Studio rows |

Known limits to state up front:

- Window captures record no click or cursor data on either platform today, because windows move. Auto-zoom is unavailable there; manual zoom works.
- The cursor is baked into the screen track, so zooming enlarges it.
- GIF recordings keep the GIF trimmer. Studio is video only.
- Windows mixes system audio and microphone into one track. Separate volumes there need a capture change, planned in M3.

## Dream backlog (not planned yet)

- A separately rendered cursor: smoothing, size, hide when idle; motion blur on zooms
- Device frames around the screen card (browser, phone)
- Annotations over time ranges, reusing the screenshot editor's arrows, text, emoji, and redaction
- Captions from speech, silence removal, background music, intro and outro cards, keystroke display
- Blurred-screen and wallpaper backgrounds, named look presets
- Export to GIF and social size presets
- Using the Studio renderer for ordinary macOS recordings, replacing up to three encode passes with one

## Open questions

None. The name, price, cleanup defaults, and first-run look are settled in "Decisions confirmed" above.

## Validation

- Windows changes: `dotnet restore windows/TinyClips.Windows.slnx`, `dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64`, `dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug`, plus the Store flavor build when project files change.
- macOS changes: the unit tests and both schemes (`TinyClips`, `TinyClipsMAS`), run locally or by the `Build` workflow on the pull request. A macOS item is not done until that build is green.
- Every user-facing milestone updates the root `CHANGELOG.md` (macOS), `windows/CHANGELOG.md`, both READMEs, the in-app Guide, the Windows What's New window, and `windows/docs/accessibility-release-gate.md`.
