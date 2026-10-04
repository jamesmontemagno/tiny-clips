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

### Zooms and crops in the editor (Milestone 2)

The same design on both platforms. The rule behind every control is in the editor model (`StudioEditorModel` on each platform, and `StudioEditorSession` on Windows) and is unit tested there. The window shows state and passes input on.

**Zoom lane.** A lane above the trim bar, on the same time scale, with one block for each zoom.

- A block shows its scale ("2×"), a mark when it follows the pointer, and a mark when it is a suggestion that has not been changed. It is never drawn narrower than 10 px, so a short zoom in a long recording can still be pressed.
- Pressing a block selects its zoom and moves the playhead to where it was pressed. Pressing an empty part of the lane selects nothing and moves the playhead there.
- Dragging a block moves the zoom. Dragging the first or last 6 px of a block that is at least 24 px wide moves that end, and the playhead follows it as it does a trim handle. A drag is one undo step.
- A lane without zooms says how to add one.
- To a screen reader each block is an item named like "Zoom 2×, 12.0 to 16.5 seconds", says whether it is selected, and can be pressed.

**Add zoom** (also Z) adds a zoom at the playhead and selects it. Where a zoom already is, that one is selected instead. A zoom starts unzoomed, so a paused playhead then moves to where the new zoom has finished moving in; otherwise adding one would look as if nothing had happened.

**Zoom section in the inspector.**

- Previous and Next step through the zooms and show each where it has moved in. Between them: "Zoom 2 of 5" and its times. This is how a zoom is selected without a pointer.
- For the selected zoom: Scale (1× to 5×); Looks at (a point, or the pointer); the focus pad with Horizontal and Vertical sliders under it; Start and End, each with buttons that step 0.1 s and one that sets it to the playhead; Ease in and Ease out (0 to 3 s); and Delete (also the Delete key).
- The focus pad stands for the screen, or for its crop. It shows the part the zoom holds as a rectangle and the point it looks at as a dot, and dragging in it moves the point. The two sliders do the same.
- Suggest zooms replaces the suggested zooms with new ones worked out from the clicks and says how many there are. Remove suggestions takes them away. Each is one undo step.
- A recording without clicks, such as one of a window, has Suggest zooms disabled with the reason next to it. One without pointer positions has "the pointer" disabled the same way.

**Crops.** The Screen and Camera sections each have four sliders, Left, Top, Right and Bottom, for how much of the frame is cut off that edge (0 to 95%), and Reset. The preview shows the result as a slider moves.

**Not in this pass:** crop handles on the canvas, and dragging the zoomed picture in the preview to move the focus. The inspector controls are their equivalents and stay when those arrive. The timeline does not magnify, so short zooms in a long recording sit close together on the lane; Start and End in the inspector are exact.

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
| Studio capture mode | Done | Done. Checked with the recording benchmark and `tools/StudioRenderCheck` |
| Renderer and exporter | Done | Done. Checked with `tools/StudioRenderCheck` |
| Live preview | Done (part of the renderer) | Done. Checked with `tools/StudioPreviewCheck`; one open problem on the software adapter (see `windows/docs/studio-preview.md`) |
| Studio window | Done | Done. Not yet run with the real preview and exporter |
| Settings, Record for Studio, reopening projects | Done | Done |

Nothing on macOS has been run on a Mac. This work was done on Windows, where the macOS code can only be compiled and unit tested by the pull request's `Build` workflow. Capture, the compositor, the preview, export, and the whole Studio window are unverified at runtime. Until someone has run them, Studio stays off on macOS.

On Windows the pieces under the window have each been run by a check tool on one PC (AMD graphics, Windows 11): the renderer, exporter and camera recorder by `StudioRenderCheck`, and the preview engine with its panel by `StudioPreviewCheck`. Both read their results back from pixels. The editor's behavior is in Core and unit tested. The window itself was run only while it was being built, with stand-ins for the preview and the exporter. The finished window has not been run, because the PC this was built on was in use and the app could not be installed or started there. Not yet seen on Windows at all: a real recording arriving in the editor, the preview's sound, dragging the camera bubble and the trim bar with a pointer, the dark and high-contrast themes, and Narrator. Until someone has gone through those, Studio stays off on Windows.

| Milestone 2 piece | macOS | Windows |
|---|---|---|
| Spec and fixtures for zooms and zoom suggestions (sections 6.8 and 8 of the format) | Done. 13 layout fixtures and 10 suggestion fixtures | The same files |
| Zooms in the layout, with a zoom that follows the pointer | Done. Passes the fixtures | Done. Passes the fixtures |
| Zoom suggestions from clicks | Done. Passes the fixtures | Done. Passes the fixtures |
| Zooms in the preview and the export | The compositor passes the events to the layout. Compiled only | The renderer and the exporter pass the events to the layout. No check tool draws a zoom yet |
| Editing operations for zooms and crops, with undo | Done in the editor model. Unit tested | Done in the editor model and session. Unit tested |
| What the lane and the inspector need: moving a whole zoom, stepping through the zooms, the focus pad, a crop as what it cuts off each edge | Done in the editor model. Unit tested | Done in the editor model. The session also keeps the selected zoom. Unit tested |
| Zoom lane, crop handles, inspector controls | Not started | Not started |

A project file with zooms in it is drawn with them on both platforms, but nothing in either app can make or change a zoom or a crop yet.

### Hidden switch

Studio is off by default on both platforms until it has been verified there.

- macOS: `defaults write com.tinyclips.app studioPreviewEnabled -bool YES` (`com.refractored.tinyclips` for the Mac App Store build).
- Windows: the `studioPreviewEnabled` setting, or the environment variable `TINYCLIPS_STUDIO_PREVIEW=1`. A packaged launch does not pass the caller's environment to the app, so a build from source is started with `winapp run <output folder> --manifest <output folder>\AppxManifest.xml --output-appx-directory <output folder>\AppX --with-alias`. For an installed build, `setx TINYCLIPS_STUDIO_PREVIEW 1` followed by a restart of the app should do it; that route has not been tried.

With the switch off, no Studio UI is visible and recordings follow the existing path unchanged.

### Decisions made while building

- **A failed project save keeps the recording.** If the project cannot be saved when a Studio recording stops, the screen track is kept as an ordinary video.
- **Drafts.** A recording kept as a draft has no exported file, so it does not appear in the Clips Manager. The drafts are listed in Settings, where they can be opened or deleted: under Video on macOS and under General on Windows.
- **Deleting an exported video leaves its export link in place.** The project then still counts as exported, so the cleanup rules remove its sources later. Removing the link would turn it back into a draft that is never cleaned up.
- **Cleanup can be switched off.** Zero days keeps projects until they are deleted by hand, and zero gigabytes means no storage limit.
- **Events during pauses.** Clicks and cursor samples from before the first frame or during a pause are not recorded. Cursor samples are capped at 60 per second, drop consecutive duplicates, and are steps, not points to interpolate between.
- **Drawing rules** are in section 6.7 of `docs/studio-project-format.md`: sRGB with gamma-space blending, no color conversion of screen pixels, the shadow model, where the border goes, and the click ring geometry.
- **Camera size on Windows.** The camera track is recorded at the camera's own aspect, fitted inside 1920×1080 and never enlarged.
- **Where projects are kept on Windows.** The installed app is packaged, so its projects are in the package's own folder, `%LOCALAPPDATA%\Packages\<package family>\LocalState\TinyClips\Projects`. Only an unpackaged run uses `%LOCALAPPDATA%\TinyClips\Projects`.
- **Windows editor behavior lives in Core.** `StudioEditorModel` (edits and undo), `StudioEditorSession` (loading, transport, autosave, export, closing) and the preview and export contracts are in `TinyClips.Core`, so the editor's rules are unit tested and the window only binds to them.
- **Windows preview and export.** The preview engine is in Core (`Studio/Preview`), not in the app as planned; the app has only the panel it draws into. The preview owns one Direct3D device per editor window and each export creates its own, so neither shares a device with a recording in progress. The exporter writes through its own sink-writer wrapper rather than the recorder's `MfSinkWriterEncoder`.
- **Frame rate.** `sources.screen.frameRate` is the rate the recording was set to, on both platforms. The rate a media library reads from the file is an average of unevenly spaced frames and can be far lower.
- **Small differences between the two exporters**, accepted for now. Windows samples each output frame at its middle, drops a partial last frame, and keeps NTSC rates exact. macOS samples at the frame's start, keeps the partial frame, and rounds the rate up. Windows will not open or export a project whose camera file is missing; macOS exports it without the camera.
- **Color on Windows.** Media Foundation's encoders convert with BT.601 up to 576 lines and BT.709 above, whatever the stream says, so an export is tagged with the matrix that was really used.
- **Windows without graphics hardware.** Exports on the software adapter sample linearly, which measured 43 to 58 frames per second against 16 to 21 for the high-quality sampler.
- **Windows keys and closing.** Esc stops a running export and does nothing otherwise, because the Windows trimmer does not close on Esc either. Closing a project that was never exported asks Export, Keep as draft, or Cancel; Delete is a separate button in the dialog and never the default.
- **Background swatches** are each platform's own screenshot editor presets. The Windows list has `slate`, which the Mac's does not. A project stores a preset's colors with its id and is drawn from the colors.
- **What a zoom is.** A zoom changes which part of the screen its card shows. The card, the camera, and the canvas stay where they are, so a zoom never changes the size of the exported video. Zooms do not overlap; one that starts on the number another ends on is chained to it, and the picture moves from the first place to the second without opening out in between.
- **Following the pointer** means looking at the pointer's mean position over the second around each frame. That is a pure function of the time, so the preview and the export agree, and a seek shows the same picture as playing to that time.
- **Suggestions are a proposal.** They are worked out from clicks alone (2× on each click, held while the clicks stay in the middle of the window, moving on when one lands elsewhere) and marked as suggested. Asking again replaces the suggested zooms and leaves alone every zoom the user made or changed. A suggestion that would overlap one of the user's zooms is not made.
- **Editing rules for zooms**, the same on both platforms and unit tested on each: a new zoom lasts 3 seconds or until the next zoom or the end of the recording, and looks at where the pointer is; a zoom is never shorter than 0.3 seconds and never overlaps a neighbour; an end dragged against a neighbour takes exactly the neighbour's number, and so does a whole zoom moved against one; changing a suggested zoom makes it the user's own, and an edit that changes nothing does not.
- **The selected zoom** stays on its zoom while the list changes around it. An edit to the selected zoom says where the zoom went. Undo and redo cannot, so when the list differs in that one zoom only it is taken to be the same zoom, and otherwise the selection goes to the zoom that shares the most time with it, or to none. On Windows this is in the session and unit tested; on the Mac the same steps are in the view model, which the tests cannot reach, over a tested function.
- **Crops.** The editor only stores valid crops. A rectangle that is not one is made valid by its size first and its position second, so a rectangle dragged past an edge stops there with its size. A saved look still never carries a crop.
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
- On Windows, `MfSinkWriterEncoder` has two new switches, `topDownMemoryFrames` and `keepFrameTimes`, both off by default. Only the Studio camera recorder turns them on; the regular recorder's calls are unchanged.

### Found in the regular Windows recorder and left alone

`StudioRenderCheck` reproduces the regular recorder's CPU path without capturing anything: it creates the encoder the way the recorder does and hands it frames through the recorder's own buffer code. That path is used when the GPU recording pipeline is switched off or cannot start. On the development PC (AMD encoder) the file it wrote was upside down in every frame, for H.264 and HEVC. A real recording made that way has not been looked at. A Studio screen track recorded on that path would have the same fault, and the check tool reports it as known. The Studio camera track had the same cause and is fixed with `topDownMemoryFrames`. The regular recorder was not changed, because it is shipping code outside this work. The same fix there is a small change that is waiting for a decision.

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
