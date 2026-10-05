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

### Scenes in the editor (Milestone 3)

The same design on both platforms, with the rules in the editor model and unit tested there, as for zooms.

**The current scene.** Every moment of the recording is in exactly one scene, so a scene is not selected the way a zoom is: the current scene is the one the playhead is in. The layout buttons, the bubble and side-by-side controls, dragging the bubble in the preview, and the keys 1 to 4 all change the current scene. Undo and redo need no selection to restore. A drag that changes the current scene stops playback with its first change, in a recording with more than one scene, so the whole drag stays in the scene it began in.

**Scene lane.** A lane above the zoom lane, on the same time scale, with one block for each scene.

- A block shows the name of its layout, and the current scene's block is marked. Pressing a block moves the playhead to where it was pressed, which makes that scene the current one.
- Dragging the first 6 px of a block, except the first block's, moves where that scene starts. The playhead follows, and a drag is one undo step.
- To a screen reader each block is an item named like "Scene 2 of 3, Side by side, 12.0 to 30.5 seconds", and says whether it is the current one.
- A recording without a camera has one scene and no lane, because every layout without a camera is the screen alone.

**Split** (also S) starts a new scene at the playhead. It is a copy of the current scene, entered by moving for 0.35 seconds, so nothing looks different until one of the two is changed. A paused playhead then moves to where the new scene has been entered. Otherwise a change of layout would show nothing, because at its first instant a scene still looks like the one before.

- Both halves have to last at least 0.3 seconds, and the current scene has to be at rest at the playhead: a scene is not split while its layers are still moving into place. Where a split is not possible the button is disabled and says why.

**Scene section in the inspector.**

- Previous and Next move the playhead from scene to scene, each to where that scene has been entered. Between them: "Scene 2 of 3" and its times.
- Start, with buttons that step 0.1 s and one that sets it to the playhead. Not for the first scene, which starts at 0.
- Entered by: a cut, or moving, with how long the move takes (0.1 to 2 s). Not for the first scene, which has nothing to move from. A move is never longer than its scene; when the scene is shorter than the time asked for, the section says how long the move really is.
- Delete removes the current scene, and the scene before it then lasts until the next one. Deleting the first scene hands its time to the second. The only scene cannot be deleted. The Delete key does the same while a block of the lane has the keyboard focus.

**Limits.** A scene made or moved in the editor is never shorter than 0.3 seconds. A start dragged against a neighbor stops 0.3 seconds from it. Scenes in a project file that are shorter, out of order, or past the end of the recording are put in order when the project is opened, as section 6.1 of the format reads them, and those that start at or after the end of the recording are dropped.

**The preview inside a move.** A paused playhead inside a move shows the layers on their way, as the export will. The bubble's handle is drawn where the current scene has the bubble at rest, because that is what dragging it changes.

**Where the Mac differs.** The blocks of the lane cannot take the keyboard focus there, so the Delete key keeps meaning "delete the selected zoom"; a scene is deleted from the inspector or the Studio menu. The line between two blocks can be dragged from either side.

### Cuts in the editor (Milestone 3)

A cut is a stretch of the recording that the video leaves out. It is a range on the timeline with a start and an end, like a zoom, and it is made, selected, moved, and deleted the same way. Nothing else moves when a cut is made: zooms, scenes, and the trim are stored in recording time and stay where they are.

**Cut lane.** A lane under the zoom lane, on the same time scale, with one block for each cut. The clip bar under it shows the same stretches as gaps.

- Pressing a block selects its cut, dragging it moves the cut, and dragging one of its ends changes where the cut starts or stops. A drag is one undo step.
- To a screen reader each block is an item named like "Cut, 12.0 to 16.5 seconds", and says whether it is selected.

**Cut** (also X) starts a cut at the playhead and selects it. It lasts 1 second, or until the next cut or the end of the recording when that comes sooner. Where a cut already is, that one is selected instead. The usual next step is to move the playhead to where the video should pick up again and choose End: At Playhead, or to drag the end of the block there.

**One selection.** A zoom or a cut is selected, never both: selecting one lets go of the other, and Delete removes whichever is selected.

**Cut section in the inspector.** Previous and Next step through the cuts. Between them: "Cut 2 of 3", its times, and how long it is. With a cut selected: Start and End, each with buttons that step 0.1 s and one that sets it to the playhead, and Delete Cut, which puts the stretch back.

**Playing.** Playback jumps over cuts, as the exported video does. A paused playhead can be inside a cut and shows the picture there, so its ends can be judged, and stepping by frames goes through it. The time display counts the video's time, so it stands still inside a cut. The jump in the preview is not exact to the frame: a few frames of a cut can show while playing. The export is exact.

**Limits.** A cut is at least 0.1 seconds long. Cuts do not overlap: an end dragged against another cut stops there, and two cuts that touch play as one. At least 0.1 seconds of video has to stay, counting the trim, so an edit to a cut or to the trim that would leave less is not made. A cut outside the trim does nothing and is kept, so widening the trim brings it back. Cuts in a project file that overlap or are out of order are put in order and joined when the project is opened.

### Speed in the editor (Milestone 3)

A speed change is a stretch of the recording that the video plays faster or slower. It is a range on the timeline with a start, an end, and a rate, and it is made, selected, moved, and deleted the way a cut is. Nothing else moves when one is made: zooms, scenes, cuts, and the trim are stored in recording time and stay where they are. What changes is how long the video is. Everything inside the stretch passes at its rate: the picture, a zoom moving in, a move between scenes, the click rings.

**Sound.** A stretch at another speed plays without sound, in the export and in the preview. The Speed section says so. Sound that keeps its pitch at another speed needs a time-stretching step on both platforms and is not in this version.

**Speed lane.** A lane under the cut lane, on the same time scale, with one block for each speed change. A block shows its rate, such as 2×.

- Pressing a block selects it, dragging it moves it, and dragging one of its ends changes where it starts or stops. A drag is one undo step.
- To a screen reader each block is an item named like "Speed 2×, 12.0 to 16.5 seconds", and says whether it is selected.

**Speed** (also R) starts a speed change at the playhead and selects it. It plays twice as fast and covers 2 seconds of the recording, or up to the next speed change or the end of the recording when that comes sooner. Where one already is, that one is selected instead.

**One selection.** A zoom, a cut, or a speed change is selected, never two of them: selecting one lets go of the others, and Delete removes whichever is selected.

**Speed section in the inspector.** Previous and Next step through the speed changes. Between them: "Speed change 2 of 3", its times, and how much of the recording it covers and how long that plays, such as "4.5 seconds, plays in 1.1 seconds". With one selected: the rate, as a choice of 0.25×, 0.5×, 1.5×, 2×, 4×, and 8×; Start and End, each with buttons that step 0.1 s and one that sets it to the playhead; and Delete, after which the stretch plays at the recording's own speed again.

**Playing.** The preview plays each stretch at its rate, without sound there. Like the jump over a cut, the change of speed in the preview is not exact to the frame; the export is. The time display counts the video's time, so inside a faster stretch it runs at half the pace of the playhead or less.

**Limits.** A speed change covers at least 0.1 seconds of the recording. Speed changes do not overlap: an end dragged against another one stops there, and two may touch. A project file may hold any rate from 0.25 to 8; the editor shows it as it is until one of its six is chosen. At least 0.1 seconds of video has to stay. A faster stretch makes the video shorter, and so does taking a slower one away, so an edit to a speed change, to a cut, or to the trim that would leave less is not made. A speed change outside the trim does nothing and is kept. Speed changes in a project file that overlap or are out of order are put in order when the project is opened, exactly as an export reads them, so opening a project and saving it does not change how it plays.

### Volumes (Milestone 3)

A recording can have the computer's sound and the microphone in one file. Where each is in a sound track of its own, the video can have each at its own volume, from silent to as recorded. Nothing makes a track louder than it was recorded: that would need a limiter to keep it from clipping, and is left for later.

**What the project says.** `sources.screen.audioTracks` lists what each sound track of the screen file holds, in the file's order: `system`, `microphone`, or `mixed`. `audio.systemVolume` and `audio.microphoneVolume` are the two volumes. A track that holds both, a track the project says nothing about, and every track of a file the list does not fit play as recorded. Mute still takes all sound away.

**Audio section in the inspector.** Mute, and under it a slider for each kind of sound the recording has in a track of its own: System audio and Microphone, in steps of 5 percent. A recording with one mixed track, or one made before the list existed, shows Mute alone. A drag is one undo step. The preview plays with the volumes as they are set, without rebuilding anything.

**macOS.** The recorder already writes the computer's sound and the microphone as two tracks. It now tells the project which is which, and only when the file really has one track for each sound it set out to record. Preview and export play each track at its volume through an audio mix.

**Windows.** The recorder mixes both into one track while recording, so a Windows recording has nothing to set apart and the window shows no volumes. Separate volumes there need three things: the recorder writing two sound tracks in a Studio recording, the exporter mixing two tracks, and the preview playing two. The first of these changes the sound path of the recorder that ships today, and none of it can be tried on the machine this is written on without recording its user's sound and microphone. It is left until that can be done with the user.

### Scenes from the recording (Milestone 3)

Both apps let the camera be moved to another corner while a recording runs, and both already wrote those moves into the project's events. A regular recording has the camera drawn into it, so the move is simply in the video. A Studio recording kept the camera apart and then showed it in its first corner from start to end, whatever was done while recording.

A new project now gets a scene for each move: the camera goes to the corner it was moved to, at the time it was moved, and glides there the way it does into a scene split off in the editor. The scenes are ordinary scenes. They show on the scene lane and can be changed or deleted.

The rule is in section 9.1 of the project format, is the same on both platforms, and is held by shared fixtures. Its corners:

- Corners passed through in less than 0.3 seconds leave no scenes of their own. Only where the camera stayed gets one.
- A move that is taken back at once leaves nothing.
- A move in the last 0.3 seconds of the recording is left out, because its scene would be shorter than any the editor makes.

**Choosing a layout while recording** uses the same rule. The format has a place for it (`markers` in the events, each a time and a layout), and a marker becomes a scene with that layout. Nothing writes markers yet: that needs a control in each app's recording panel, and it only pays off with keys for it, since a click on the panel is itself in the recording. Which keys is a question for the user.

### Person cutout (Milestone 4)

The camera picture can have everything but the people in it blurred, or taken away so that only the people stand in front of the screen. It is one choice in the Camera section, Background: Keep, Blur, or Remove. It belongs to the look, so saving a look as the default brings it to the next recording.

- **Blur** leaves the camera layer as it is: its shape, its border, its shadow.
- **Remove** leaves only the people, clipped by the layer's shape. The border and the shadow go, because they belong to a frame around the picture that is no longer there.

Which pixels are a person is for each platform to find, so the edge is not the same pixel for pixel on both. The drawing rule is in section 6.7 of the project format.

**macOS** uses the Vision framework's person segmentation in the compositor, for the preview and the export alike, at its middle quality setting. Nothing is added to the app. While a video plays, Vision steadies the edge from frame to frame; right after a seek the first masks can trail the picture.

**Windows** has nothing built in that finds people in a recording on an ordinary PC. Windows Studio Effects changes the picture of a live camera, and needs a neural processor and a driver from the PC's maker; the Windows AI imaging APIs need a Copilot+ PC. So it takes a segmentation model and something to run it.

*What is built.* The renderer draws a blurred or removed background from a picture of where the people are, in the preview, the export and the poster alike. What finds them is behind one small interface (`IStudioPersonFinder`). The finder that comes with it runs a model on the processor with `Windows.AI.MachineLearning`, the machine learning API that has been part of Windows since version 1809. That adds no package and no library to the app in either flavor. The one thing to add is the model's file, which the app looks for at `Assets\Studio\selfie_segmentation.onnx` next to itself. **The file is not in the repository.** Without it Windows keeps every background, which is what the format asks of a renderer that cannot find people, and nothing in the window offers the choice yet.

*The model* this was built and tried with is MediaPipe Selfie Segmentation, Google's 256 by 256 "general" model under the Apache License 2.0, as an ONNX file of 448 KB. Microsoft's PowerToys ZoomIt uses the same model through the same API for the background blur of its own webcam overlay, and the file in its repository is the one used here. It was kept in a temporary folder, outside the repository.

*Measured on the development PC* (8 cores, AMD graphics): finding the people takes 4 to 5 ms a frame on the processor, and the graphics card is no faster for a model this small. A 1080p frame whose camera background is blurred or removed takes about 10 ms to draw and read back, where one with it kept takes 2. The first frame takes about half a second more, once, to load the model. Over a minute of video, 1,800 frames, a process that only runs the model holds no more at the end than at the start; it did grow, by about 30 KB a frame, until the finder kept one binding for all its frames instead of making one for each. On one of MediaPipe's own test photographs the person is cut out cleanly, with a few specks left along an arm; the pictures have been looked at. Nothing has been tried on webcam footage, on ARM64, or on a PC without a graphics card that Windows can use.

*How the people are drawn.* The model's answer is used as it is, as how much of each pixel is a person, with no threshold. It is not steadied from frame to frame, so a frame always looks the same however the playhead got to it; an edge may flicker in a way the Mac's does not. The blur is Direct2D's Gaussian in its balanced setting, which measures 25.1 to 25.2 pixels where the format asks for 25.6.

*The choice of what runs the model* is the user's, since it decides what is added to the app:

| | Added to the direct download | Added to the Store package | |
|---|---|---|---|
| The API that is part of Windows (`Windows.AI.MachineLearning`), as built | The model, 0.4 MB | The model, 0.4 MB | Takes ONNX models up to opset 12 on Windows 11; this one is opset 11. Microsoft calls this API superseded by the next row and adds nothing to it any more |
| Windows ML in the Windows App SDK (`Microsoft.WindowsAppSDK.ML`, which has a 1.8 line) | About 41 MB unpacked and 16 MB in the download: ONNX Runtime 22 MB, DirectML 18 MB. About half without DirectML, which Microsoft describes and does not support | The model, 0.4 MB | The direct download carries its own copy of the Windows App SDK, so it would carry this too. The Store package uses the shared one. A current ONNX Runtime, still developed |
| ONNX Runtime from NuGet (`Microsoft.ML.OnnxRuntime`) | About 16 MB unpacked and 6 MB in the download, for each architecture | The same | The same engine at a version the app chooses |

The sizes are those of the libraries in the current packages on nuget.org, read from the packages' own lists of contents. The recommendation is the first row, which is what is built: it costs the app nothing but the model, and Microsoft ships the same pairing. Moving to one of the others later means writing one class of about 250 lines again. NativeAOT and trimming do not narrow the choice: the releases no longer use NativeAOT.

*What a yes takes:* putting the model's file in `windows/src/TinyClips.App/Assets/Studio/` (the project picks it up from there), and saying in the app and the repository that it is there and under which licence, which the Apache License asks for and which Tiny Clips has no place for yet. The Background choice in the Windows window is written and shows only where the file is; it has been compiled and never run.

## Platform architecture

### macOS

- New `mac/TinyClips/Studio/` folder: `StudioProject` and `StudioEvents` (Codable), `StudioProjectStore`, `StudioLayoutResolver`, `StudioTimeMap`, `StudioAutoZoom`, `StudioCompositionBuilder`, `StudioCompositor`, `StudioExporter`. The UI is `Views/StudioWindow.swift` plus canvas, timeline, inspector, and view model files, split the way the screenshot editor is.
- Preview and export share one path. `StudioCompositionBuilder` turns a project into an `AVMutableComposition` (screen track, camera track placed by its start offset, both audio tracks) and an `AVVideoComposition` with a custom compositor. `StudioCompositor` grows out of `BrandingOverlayProcessor.WebcamOverlayCompositor` and draws each frame with Core Image. `AVPlayer` uses it for live preview and `AVAssetExportSession` uses it for export.
- Capture: a Studio branch in `CaptureManager.stopRecordingFlow` skips the two flatten passes and moves the screen and camera files into the project. The existing camera-minus-screen first-frame offset is stored as `camera.startOffset`, and the default trim-in reproduces today's leading trim. `MouseClickMonitor` adds the mouse button and cursor sampling (`NSEvent.mouseLocation` on a timer, which needs no new permission).
- Conventions: `StudioWindow` follows the capture-window rules (an `NSWindow` hosting SwiftUI, a completion callback with the double-fire guard, menu commands through the responder chain as `TrimmerMenuCommands` does). View models use `ObservableObject` and `@Published`. Studio is free, so there is no `#if APPSTORE` gating, and Sparkle is not involved; both schemes get the same code.
- The Xcode project does not use synchronized folders, so every new file needs `project.pbxproj` entries for both app targets.

### Windows

- New `TinyClips.Core/Studio/`: models as records with a `JsonSerializerContext` (written so that it also works compiled ahead of time, which the release build was until 1.8.2), `StudioProjectStore`, `StudioLayoutResolver`, `StudioTimeMap`, `StudioAutoZoom`, `StudioSceneRenderer`, `StudioExporter`.
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
| `m4-win-person-cutout` | Windows | Evaluate the options, get a decision on the added dependency, then implement. (The options are evaluated and the one that adds no package is built; the model file it needs waits for the decision) |

## Implementation status

This section records what was built and how it differs from the plan above. It is updated as milestones land.

### Where each platform stands

| Milestone 1 piece | macOS | Windows |
|---|---|---|
| Project format, store, cleanup rules, layout resolver, time map | Done. Passes the shared fixtures | Done. Passes the shared fixtures |
| Studio capture mode | Done | Done. Checked with the recording benchmark and `tools/StudioRenderCheck` |
| Renderer and exporter | Done | Done. Checked with `tools/StudioRenderCheck` |
| Live preview | Done (part of the renderer) | Done. Checked with `tools/StudioPreviewCheck`, on clips whose frames sit on an even grid, which a recording's do not (see below, and "Known problems on Windows") |
| Studio window | Done | Done. Run by `tools/StudioWindowCheck` with the real preview and exporter, without a person at the controls |
| Settings, Record for Studio, reopening projects | Done | Done |

Nothing on macOS has been run on a Mac. This work was done on Windows, where the macOS code can only be compiled and unit tested by the pull request's `Build` workflow. Capture, the compositor, the preview, export, and the whole Studio window are unverified at runtime. Until someone has run them, Studio stays off on macOS.

Since it cannot be run here, the macOS code was read through twice more by reviewers that only read (5 October): once the composition, the compositor, the player and the export, and once the capture path, the project store and the window's lifetime. They found eight defects that follow from the code and the documented behavior of the frameworks, all fixed and compiled:

- The video ended where the screen last changed. The recorder writes a screen frame only when the screen changes, so a recording that ends with talk over a still screen has a screen track shorter than its sound, and the composition was cut to that track: the sound and the camera after it were dropped, in the preview and the export, while the timeline ran on. The composition now holds the last frame for what is missing.
- Zooms, moves between scenes and click rings could stand still over a still screen, because the compositor told AVFoundation that its picture does not change while the source frames stay the same.
- A pause made while a new canvas size was being applied was undone.
- A recording whose project could not be saved, and which could not be moved to the save folder either, was deleted with its folder. Windows had the same fault in the same place, also fixed.
- The outline of the region stayed on screen when the project folder could not be created.
- "After recording" stopped following the trimmer setting once it had been stored, though the old toggle, onboarding and older builds still write that setting.
- A press on Tiny Clips' own Stop or Pause panel was stored as a click of the recording.
- An edit made in the last half second before quitting was lost.

A fix made by reading is as unrun as the code it fixes. The reviewers also listed what only a Mac can settle; that list is in the pull request under "How to test".

**Described above and not built, on either platform.** Nothing lets you set "Keep sources": the rule is in both project stores and their cleanup, without a control. And nothing opens a video in single-layer mode: both stores can make a project around a video that has no sources, but no command calls that, so "Open in Studio" is offered only for a video whose project still has its sources.

On Windows each piece has been run by a check tool on one PC (AMD graphics, Windows 11): the renderer, exporter and camera recorder by `StudioRenderCheck`, the preview engine with its panel by `StudioPreviewCheck`, and the editor window by `StudioWindowCheck`. Each reads its results back itself, from pixels, from decoded files, or from the UI Automation tree. The editor's behavior is in Core and unit tested.

`StudioWindowCheck` opens the real window on real projects in a process of its own, with the real preview and exporter, and works it through UI Automation: opening, playing, every inspector control with undo and redo, trimming, exporting, closing, and two windows at once. It found three defects in the window, which are fixed. A window in the background took the keyboard focus when its project had opened or its export ended. Five sliders could not be set to an end of their range by a screen reader. Three elements had no name. With the scene and cut controls it found a fourth: every editor that was closed stayed in memory (see "Closed editors and memory" under "Decisions made while building"). The last check of every run is now that none does.

The PC was in use, so the tool sends no input and its windows never come to the front, and the Tiny Clips app itself was never started. That leaves out everything a person does with their hands: no key was pressed, nothing was dragged, and no drop-down was opened. Also not yet seen on Windows: a real recording arriving in the editor, the preview's sound, the high-contrast themes, Narrator reading the window, display scales other than 150 percent, and the app around the window, which is how a recording or a draft gets to it. Until someone has gone through those, Studio stays off on Windows.

One thing about real recordings in particular, because it decides how far the checks reach. The Windows recorder writes a screen frame at every tick of its pacer and stamps it with the wall clock a moment later. So the frames of a recording do not sit on an even grid: they are some milliseconds into their thirtieth of a second, by an amount that differs from one recording to the next and a little from frame to frame, and a tick the recorder misses leaves a gap. The camera's frames carry the camera's own times. What is known of real files is little. The encoder keeps each frame's time as it was given: the index of a camera track it wrote has its frames 0, 2 and 4 ms into their slots and a gap of a third of a second, exactly as they were handed to it. And the recorder's own report of the one real recording there is a trace of on this PC, five seconds at 30 frames a second, counts no missed tick and no dropped frame. Whether the offset of a recording's first frame survives in the file, which decides where its frames sit in their slots, and how often a longer recording or a busier PC misses a tick, is not known.

- **The export is checked with such a file.** `StudioRenderCheck` has a clip with gaps of half a second and of two seconds, a stretch with every other frame missing, and frames 5 to 7 ms off the grid, and reads every exported frame against what the recording has for that moment. What it has not got is a screen track that ends well before its sound. On Windows that is the last tick at most, since a frame is written whether the screen changed or not; it is what went wrong in the macOS composition, where a frame is written only when the screen changes.
- **The preview and the window are not.** Every clip `StudioPreviewCheck` and `StudioWindowCheck` play has one frame exactly at the start of every thirtieth of a second. The preview engine takes the frame a player shows for the one whose slot the clock is in, steps a frame at a time by asking the player for its next frame, and, since 5 October, tells the frames of playback apart by rules whose limits were measured on those clips (a frame handed over within 13 ms of the start of its slot is on time; the next frame has the next number). None of that has met a file with a gap or with frames off the grid. What it would do there has been worked out on the engine's own rules with a model of a player, which is not a run: where the frames sit 10 to 26 ms into their thirtieth of a second, which is half of the possible places, the first missing frame leaves the frames after it without a number for half a second, in which the playhead stands still, and the rest of that play is then told apart the old way; where they sit 22 to 30 ms in, up to a third of the frames get their number one frame late, which is a frame not drawn on time while a zoom or a scene change moves. A step across a missing frame may show the frame after the one the export has there. The requirements this fails are in the unit tests, skipped, each with its reason (`StudioPreviewFrameNamerRecordingTests`). It is the first thing to run when the PC is free, and to mend before Studio is switched on.

An earlier version of this section said that every clip the tools make is evenly spaced and that a Windows recording has a frame only when the screen changes. Both were wrong: the first is true of two of the three tools, and the second of the Mac.

| Milestone 2 piece | macOS | Windows |
|---|---|---|
| Spec and fixtures for zooms and zoom suggestions (sections 6.8 and 8 of the format) | Done. 13 layout fixtures and 10 suggestion fixtures | The same files |
| Zooms in the layout, with a zoom that follows the pointer | Done. Passes the fixtures | Done. Passes the fixtures |
| Zoom suggestions from clicks | Done. Passes the fixtures | Done. Passes the fixtures |
| Zooms in the preview and the export | The compositor passes the events to the layout. Compiled only | Drawn, exported and postered zooms are measured from pixels by `StudioRenderCheck`, and zooms and crops in the live preview by `StudioWindowCheck`, paused and while playing |
| Editing operations for zooms and crops, with undo | Done in the editor model. Unit tested | Done in the editor model and session. Unit tested |
| What the lane and the inspector need: moving a whole zoom, stepping through the zooms, the focus pad, a crop as what it cuts off each edge | Done in the editor model. Unit tested | Done in the editor model. The session also keeps the selected zoom. Unit tested |
| Zoom lane, Zoom section in the inspector, crop sliders, the Z and Delete keys | Done. Compiled, never run | Done. Run by `StudioWindowCheck`, without a person at the controls |
| Crop handles on the canvas, and dragging the zoomed picture to move the focus | Left out of this pass | Left out of this pass |

A project file with zooms in it is drawn with them on both platforms. On the Mac the editor can make and change zooms and crops; that UI has been compiled and never run, and no zoom has been rendered there. The Windows renderer and exporter draw a zoom where the format says: `StudioRenderCheck` finds four edges of its test pattern in each frame and they are within a quarter of a pixel of their places, and the frames have been looked at.

The Windows window has the zoom lane, the Zoom section and the crop sliders. `StudioWindowCheck` works them the way it works the rest of the window (115 of its 385 checks): it adds, selects, moves and deletes zooms through the lane, the keys and the inspector, reads what a screen reader is given and told, and reads from the preview which part of the screen is shown. In four full runs, the part shown while paused was at most 0.38 pixels from where the format puts it, a crop at most 0.61, and the part shown while a zoom moves in during playback at most 0.38. Ten faults put into the window's code on purpose were tried against the checks: nine failed a check at once, and the tenth, the lane's playhead line staying where it was, showed that nothing looked at the line, which a check now does. What the tool cannot do is unchanged: nobody pressed a key or dragged anything, and the high-contrast themes and a screen reader have not been tried. Exporting a zoomed project from the window has not been run either; the exporter's zooms are covered by `StudioRenderCheck`.

| Milestone 3 piece | macOS | Windows |
|---|---|---|
| Spec and fixtures for scenes entered with a morph (section 6.9 of the format) | Done. 22 layout fixtures | The same files |
| Moving and fading layers in the layout | Done. Passes the fixtures | Done. Passes the fixtures |
| Drawing them in the preview and the export | The compositor draws a fading layer as one with its shadow. Compiled only | Drawn, exported and postered moves are measured from pixels by `StudioRenderCheck`. In the live preview, `StudioWindowCheck` reads every picture drawn while it plays into a scene |
| Editing operations for scenes, with undo: the scene the playhead is in, splitting, deleting, moving a start, how a scene is entered | Done in the editor model, which is unit tested, and the view model | Done in the editor model and session, with the S key and Delete on a scene in the key rules. Unit tested |
| Scene lane, Scene section in the inspector, the Split button | Done. Compiled, never run | Done. Run by `StudioWindowCheck`, without a person at the controls |
| Cuts in the export | The composition is built from the stretches that are kept, picture and sound. Compiled only | Done. `StudioRenderCheck` exports a project with a cut and reads every frame and the sound back |
| Editing operations for cuts, with undo: adding, moving, and deleting a cut, one selection shared with the zooms, and playback that jumps over cuts | Done in the editor model, which is unit tested, and the view model | Done in the editor model and session, with the X key and Delete on a cut in the key rules. Unit tested |
| Cut lane, Cut section in the inspector, the Cut button | Done. Compiled, never run | Done, with a gap in the trim bar for each cut. Run by `StudioWindowCheck`, without a person at the controls |
| Spec and fixtures for speed (section 7 of the format) | Done. 14 time map fixtures | The same files |
| Speed in the export | The composition is scaled piece by piece, and a faster or slower piece gets no sound. Compiled only | Done. `StudioRenderCheck` exports two projects with faster and slower stretches and reads every frame and the sound back |
| Editing operations for speed changes, with undo: adding, moving, and deleting one, its rate, and one selection shared with the zooms and the cuts | Done in the editor model, which is unit tested, and the view model | Done in the editor model and session, with the R key and Delete on a speed change in the key rules. Unit tested |
| Speed in the preview | The player is set to the rate of the stretch it is in, and is silent there. Compiled only | Not started. The preview plays every stretch at the recording's own speed |
| Speed lane, Speed section in the inspector, the Speed button | Done. Compiled, never run | Done. **Compiled, never run**: written on 5 October while no check tool could be run on the PC. 72 checks are written for it in `StudioWindowCheck` and have never run either |
| Volumes for the computer's sound and the microphone | Done: the recorder lists its sound tracks, the Audio section has a slider for each, and preview and export play them through an audio mix. The rules are unit tested. Compiled, never run | Not started. A Windows recording has one mixed sound track; see "Volumes" above |
| Moves of the camera while recording become scenes (section 9.1 of the format) | Done. 16 fixtures. The recorder hands the moves to the new project. Compiled, never run | Done. The same fixtures. The recorder hands the moves to the new project; that hand-over is two lines that no test reaches, and no recording has been made with it |
| Choosing a layout while recording | The format and the rule are in, with fixtures. No control in the recording panel writes a marker yet | The same |

| Milestone 4 piece | macOS | Windows |
|---|---|---|
| The drawing rule for a blurred or removed camera background (section 6.7 of the format) | Done | The same text |
| Drawing it | In the compositor, with Core Image. Compiled, never run | Done in the renderer, for the preview, the export and the poster. `StudioRenderCheck` measures it from pixels with a stand-in for what finds the people |
| Finding the people in a camera frame | Vision person segmentation in the compositor. Compiled, never run | Done, with the machine learning API that is part of Windows. It needs a model file that is not in the repository: a decision for the user. Tried with that file on one photograph |
| Background: Keep, Blur, Remove in the Camera section | Done. The rule is unit tested. Compiled, never run | The rule is in the editor model and session, unit tested. The choice is in the Camera section and shows only where the model's file is. **Compiled, never run**, and so are the 5 checks written for it |

A project file with more than one scene is drawn with its transitions on both platforms. On the Mac the editor can now split a recording into scenes and change each one, in views that have been compiled and never run. The Windows window has the scene lane, Split and the Scene section, and its Layout and Camera sections show and change the scene the playhead is in. `StudioWindowCheck` works them the way it works the zooms (74 of its checks): it splits, moves, changes and deletes scenes through the lane, the keys and the inspector, reads what a screen reader is given and told, and reads each scene's picture against the layout. The six frames of one move are worked out a second time by hand from the format, since the preview and the layout share their code. Paused, the edge furthest from its place was 0.6 pixels from it. Playing into a scene, every picture the preview draws is read; in three full runs no frame around the change was left out or late.

A project file with cuts in it is exported without them on both platforms. The rules for making and changing cuts are in both editor models, and playback in both editors jumps over them. The Mac has the controls for cuts, compiled and never run, and no preview there has played over a cut. The Windows window has the cut lane, Cut, the Cut section and a gap in the trim bar for each cut, worked by `StudioWindowCheck` (62 of its checks). The tool also plays over a cut with the real preview and reads every picture: one picture of the cut is drawn before the preview goes on to its end, and the picture then stands for about 0.15 seconds. The export has neither. On Windows `StudioRenderCheck` works out by hand where each layer is on its way, measures the test pattern inside the screen and the camera there, looks for their outlines, and compares a fading layer with what is under it. The frames have been looked at. On the Mac the same rules are in the layout, which the fixtures cover, and in the compositor, which has only been compiled.

For the Windows scene and cut controls, 61 faults were put into the window's code on purpose, one at a time, to see whether `StudioWindowCheck` fails: 37 by the controls' author and 24 by the reviewer in other places of the same code. Every one fails a check now. Two of the author's failed none at first: one because the framework moves the focus to the same button by itself, which the check now tells apart, and one that showed the code it removed was not needed, which is taken out. Three full runs of the tool on the merged code passed all 385 checks.

A project file with speed changes in it is exported with them on both platforms, silent where the speed is not the recording's own. On Windows `StudioRenderCheck` exports one project with a stretch at twice the speed and one with a stretch at half the speed and another at four times the speed across a cut and up to the end. For both, the frame of the screen and of the camera that every output frame must show was worked out by hand, and so was where each tone of the sound must be; the tool also listens to the stretches that must be silent and compares how long the sound and the picture last. Six faults put into the exporter on purpose each failed these exports. On the Mac the composition is scaled with AVFoundation's own call, which has only been compiled. The rules for making and changing speed changes are in both editor models. The Mac has the controls for them and a preview that is written to play them, all compiled and never run. The Windows window has the controls too, written while no check tool could be run on the PC: compiled, with their checks, and never run. The Windows preview does not play a speed change at its speed yet, so there the preview and the export disagree; the editor asks the preview for the rate already, and the engine does not act on it. Speeds have only been exported from clips six seconds long.

### Known problems on Windows

Found by the check tools, and open:

- **A frame taken for the one after it: mended on the clips the checks have, and not yet run on the merged code.** The preview knew which frame a picture is only from where its player says it is at the moment the picture arrives. When the app had been held up for some tens of milliseconds, a picture that arrived late was taken for the next frame: after a pause at that moment the playhead named the frame after the one shown, and while playing one scene was drawn with the picture of one frame and the layout of the next. The engine now tells the frames apart as they are handed over, and shows a frame it cannot tell only where the scene comes out the same whichever frame it is (`windows/docs/studio-preview.md`). In its own checks the playhead and the picture agreed after every one of 6,200 pauses, 3,800 of them with the process held up in one of seven ways, and none of 24,663 scenes of a zoom had the layout of another frame. With every kind of hold-up at once, 2 of 300 pauses returned with the two a frame apart and were right a moment later. What that leaves:
  - Two rare ways in which a frame still gets a wrong number: about once in 130,000 frames while the garbage collector is at work, cause not found, and about once in 60,000 when the whole process is stopped from outside at random. The checks count both and pass on them unless told to judge them.
  - Those runs were made in the engine author's copy, with the editor window and the renderer as they were before the scene and cut controls. On the merged code the engine is compiled and unit tested. No check tool has run there: the PC has been in use since.
  - A reviewer who only read the change found three faults in corners the checks do not go to, fixed since and also not run: after the graphics device is lost, nothing was drawn while the playhead was outside the camera's time range; Pause and Play called one right after the other while the preview was busy drawing left it standing still until the next pause; and the same two calls from two threads could leave it playing without a picture.
  - The clips of all those checks have their frames on an even grid, which a recording has not (see "Where each platform stands").
- **Two editors that could not open.** In one early full run, two editors opened one after the other each said that the screen recording could not be decoded, and the next one opened. The cause is not known, and it has not happened again: not in 10,200 opens made on purpose in every way that seemed likely, one of which failed, with a lost graphics device and not this error. The engine now opens once more, three quarters of a second later, when a player reports that it cannot decode before its first frame; that could be tried with a made-up failure only. The tool keeps the error codes and the engine's trace of the seconds before, which it did not then.
- **A frame past the end.** When the preview plays up to the end of the video, or Play is pressed at the end, it can show the frame after the last one for a moment. Seen in 3 of 64 runs of the window's transport checks, all of them while the PC was busy with other tools and builds. The engine's own checks have since played 64 times up to the end of a video that ends in a cut, without a frame past it. The window check that saw it has not been run on the merged code.
- **The camera a frame beside the screen.** While playing, the camera's picture is now and then one frame ahead of the screen's or behind it: in 16 of 86 play-throughs into a scene, in one to three of about eighty pictures each. The two tracks are played by two players on one clock, which the engine spike measured on matching frames 98.6 to 99.95 percent of the time and never more than one apart. The tool notes it and fails at two frames.
- **What is left of a closed editor.** The window, its preview and its decoders go when an editor is closed. Its project and editing state can stay in memory for a while, held by the framework and not by the app: until the app shows its next window, when the editor was closed right after it played; or until it happens again, when the editor was closed within a second of a button getting the keyboard focus a second time, while the framework waits to show that button's tooltip. It is one editor's state at most and does not add up. A window with nothing but a button and a tooltip does the same, so the app is left as it is. The second case needs a key press to happen in the app, which no tool here sends.

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
- **The picture at the trim end.** With the playhead at the trim end, the preview shows the picture at that instant. An export holds the frames before it, so that picture is one or two frames past the last one the video keeps. This was seen on Windows. The Mac's code seeks the recording to the same instant, so it should show the same.
- **Color on Windows.** Media Foundation's encoders convert with BT.601 up to 576 lines and BT.709 above, whatever the stream says, so an export is tagged with the matrix that was really used.
- **Windows without graphics hardware.** Exports on the software adapter sample linearly, which measured 43 to 58 frames per second against 16 to 21 for the high-quality sampler.
- **Keyboard focus on Windows.** The editor puts the focus on Play when a project has opened, on Cancel while an export runs, and on Export when it ends, but only while its window is the active one. Otherwise it waits until the user comes back to the window, because asking for the focus brings a window to the front. A click that brings the window back decides the focus itself.
- **The window check tool uses one package the app does not**, `Interop.UIAutomationClient`, to read the window as a screen reader does. The tool is not in the solution and is not shipped.
- **Windows keys and closing.** Esc stops a running export and does nothing otherwise, because the Windows trimmer does not close on Esc either. Closing a project that was never exported asks Export, Keep as draft, or Cancel; Delete is a separate button in the dialog and never the default.
- **Background swatches** are each platform's own screenshot editor presets. The Windows list has `slate`, which the Mac's does not. A project stores a preset's colors with its id and is drawn from the colors.
- **What a zoom is.** A zoom changes which part of the screen its card shows. The card, the camera, and the canvas stay where they are, so a zoom never changes the size of the exported video. Zooms do not overlap; one that starts on the number another ends on is chained to it, and the picture moves from the first place to the second without opening out in between.
- **Following the pointer** means looking at the pointer's mean position over the second around each frame. That is a pure function of the time, so the preview and the export agree, and a seek shows the same picture as playing to that time.
- **Suggestions are a proposal.** They are worked out from clicks alone (2× on each click, held while the clicks stay in the middle of the window, moving on when one lands elsewhere) and marked as suggested. Asking again replaces the suggested zooms and leaves alone every zoom the user made or changed. A suggestion that would overlap one of the user's zooms is not made.
- **Editing rules for zooms**, the same on both platforms and unit tested on each: a new zoom lasts 3 seconds or until the next zoom or the end of the recording, and looks at where the pointer is; a zoom is never shorter than 0.3 seconds and never overlaps a neighbour; an end dragged against a neighbour takes exactly the neighbour's number, and so does a whole zoom moved against one; changing a suggested zoom makes it the user's own, and an edit that changes nothing does not.
- **The selected zoom** stays on its zoom while the list changes around it. An edit to the selected zoom says where the zoom went. Undo and redo cannot, so when the list differs in that one zoom only it is taken to be the same zoom, and otherwise the selection goes to the zoom that shares the most time with it, or to none. On Windows this is in the session and unit tested; on the Mac the same steps are in the view model, which the tests cannot reach, over a tested function.
- **Entering a scene.** A scene is cut to, or its layers move into place from where the scene before had them, over at most 2 seconds and never longer than the scene itself. A layer both scenes have moves in a straight line and changes size and corner radius on the way; the camera keeps its picture cropped to the shape it has at each instant. A layer only one of them has stays where that scene has it and fades. So entering the camera layout still shows the screen for a moment, fading out under the camera as it grows.
- **A layer fades as one.** Its shadow, its border and its click rings are put together with it first, and the whole is laid on the frame. Fading each part by itself is simpler to draw and was tried first on Windows: the card's own shadow then shows through the card, which makes it look darker halfway than at either end.
- **A camera that changes shape** moves as a rounded rectangle. A circle is one whose radius is half its side. A squircle reaches further into the corners of its box, so it counts as a rounded rectangle with a radius of 0.22 of its side: on a bubble 389 pixels across, the outline then moves by under 4 pixels as the move starts, where half the side would move it by about 45.
- **The current scene is the one the playhead is in.** There is no selected scene. The layout controls, the bubble and the keys 1 to 4 change the scene under the playhead, and a change of current scene is reported to the window like a change of selection.
- **A drag in a scene stops playback.** Every change goes to the scene the playhead is in at that moment, and a drag is many changes. While the recording played, a drag of the bubble, or of the Size, offset, Camera share or Move takes slider, ran on into the next scene when the playhead got there, and changed that one too. Found by reading the Mac's views; the Windows session had the same rule. Now the first change such a drag makes stops playback where the picture is, and the drag stays in that scene. A change that stands alone (a key, a choice from a list) leaves playback alone, and so does a drag in a recording with one scene, or of something the whole recording has, such as the padding. On Windows the rule is in the session and unit tested; the tests fail without it. On the Mac it is in the view model, compiled and never run.
- **Undo in the middle of a drag.** It takes back what the drag has done so far, and the drag goes on as one step. Before, it ended the drag's undo step, and every further move of the pointer became a step of its own. The same for Redo. In both editor models, unit tested on each.
- **Keys during a drag, on the Mac.** While a mouse button is held, the keys that change the project are taken and do nothing: Delete, 1 to 4, I, O, S, X, Z, R, and Command-Z, Shift-Command-Z and Command-E. Space and the arrows still work. A layout key during a drag of the camera would take its handle away under the pointer, after which the drag might never be told that it ended. Compiled, never run. Windows has no such rule. Its overlay ends a drag of the camera when the handle is hidden, through the pointer it loses then; that has not been tried with a key. What is also left there: Delete, pressed while a zoom, cut or speed change is being dragged, removes it, and the rest of the drag moves the one after it. One Undo puts both back.
- **Editing rules for scenes**, the same on both platforms and unit tested on each: a split makes a copy that is entered by moving for 0.35 seconds; both halves last at least 0.3 seconds; a scene is not split while it is still moving into place, or in a recording without a camera; deleting a scene gives its time to the one before, and deleting the first gives it to the second; a start stays 0.3 seconds clear of the start before it and of its own end; the first scene always starts at 0 and is never entered by moving. Opening a project puts its scenes in order and drops those that start at or after the end of the recording.
- **A cut is a range, like a zoom.** It is added at the playhead with a length of one second and then adjusted, it is selected, moved and deleted the way a zoom is, and a zoom or a cut is selected, never both. Cuts are stored in recording time, so a cut moves nothing else.
- **Editing rules for cuts**, the same on both platforms and unit tested on each: a cut is never shorter than 0.1 seconds and never overlaps another; two that touch play as one; at least 0.1 seconds of video has to stay, so an edit to a cut or to the trim that would leave less is not made; a cut outside the trim is kept. Opening a project puts its cuts in order and joins those that overlap.
- **What a speed change is.** A stretch of the recording with a rate from 0.25× to 8×, stored in recording time like a cut. Zooms, moves between scenes, and click rings stay tied to the recording, so inside a faster stretch they pass faster too. A stretch at another speed has no sound: sound that keeps its pitch needs a time-stretching step that neither exporter has. Where two entries in a project file overlap, the one that starts first in the recording counts, wherever the trim is, so the speed at a moment of the recording does not change when the trim moves.
- **Editing rules for speed changes**, the same on both platforms and unit tested on each: a new one plays twice as fast and covers 2 seconds, or up to the next one or the end of the recording; one never covers less than 0.1 seconds and never overlaps another; a rate of 1 is not a speed change, and deleting it is how a stretch goes back to the recording's own speed; at least 0.1 seconds of video has to stay, which a faster rate, a move, and even deleting a slower stretch can break, so those are not made then. Opening a project keeps exactly the entries an export counts, so opening and saving does not change how it plays.
- **Volumes go down only.** The computer's sound and the microphone each have a volume from silent to as recorded, and only where the recording has them in tracks of their own. Making a track louder than it was recorded is left out, because it needs a limiter to keep it from clipping. On Windows there is nothing to set yet: its recorder mixes both into one track, and changing that touches the sound path of the recorder that ships today (see "Volumes").
- **What was done while recording arrives as scenes.** A Studio recording used to show the camera in its first corner throughout, even when it had been moved while recording. Each move now starts a scene that the camera glides into, 0.35 seconds as for a scene split off in the editor, where a regular recording jumps. Corners passed through in under 0.3 seconds are skipped, since the editor makes no scene shorter than that.
- **A removed background takes the border and the shadow with it.** With only the people left, a border and a shadow would be drawn around a frame that is not there. The shape still clips the picture. A blurred background changes nothing else about the layer.
- **One quality for the person cutout on the Mac**, Vision's middle setting, in the preview and in the export, so that an export shows the edge the preview showed. The finer setting may be worth it for exports; that needs eyes on a Mac.
- **On Windows the people are found on the processor, with what Windows has.** The research into this recommended Windows ML from the Windows App SDK, on the grounds that the app has that SDK already. That holds for the Store package only: the direct download bundles the SDK and would grow by about 41 MB. The API that is part of Windows adds nothing but the model, and is what PowerToys ZoomIt uses with the same model. It runs on the processor because the graphics card measured no faster and is busy decoding, drawing and encoding.
- **The model's file is not committed.** The plan left what is added to the app for the person cutout to the user. With this choice that is one file of 448 KB under the Apache License 2.0, and a notice of it. Everything else is in, and inert without the file.
- **The same edge however the playhead got there, on Windows.** Each frame's people are found from that frame alone. Steadying the edge over several frames, as MediaPipe's own pipeline and ZoomIt do, would make a frame look different after a seek than after playing up to it, and an export different from the preview.
- **The project says what its sound tracks hold only when that is certain.** The Mac recorder lists them when the finished file has exactly one sound track for each sound it set out to record. An input that never got a sample may or may not have become a track; then the project lists nothing and every track plays as recorded.
- **Playing over a cut** is done by the editor, which sends the preview on to the end of the cut when playback reaches it. The export is exact; the preview can show a few frames of the cut first.
- **Stepping is read out.** Previous and Next, for zooms, scenes and cuts, say nothing of where they land by themselves, and the text between them is not read when it changes. So the editor reads out where it landed: "Zoom 2 of 5, 2×, 12.0 to 16.5 seconds", "Scene 2 of 3, Side by side, 12.0 to 30.5 seconds", "Cut 2 of 3, 12.0 to 16.5 seconds". On Windows a button that sets a time also reads out the new time; on the Mac the stepper speaks for itself.
- **A press on an empty part of the zoom lane or the cut lane selects nothing**, neither a zoom nor a cut.
- **The Windows timeline.** The scene, zoom, cut and speed lanes are one control with a kind of block each, on the time scale of the trim bar under them, and each lane is one stop for the Tab key. The line between two scenes can be taken within 6 pixels on either side. A cut is hatched inside a dashed outline, so that it differs from a zoom by more than its color, and the trim bar shows a gap where each cut is; the gaps are only drawn, and the cut lane is where a cut is pressed and read. A speed change has round ends, a flat tint of the text color and a mark for faster or slower, so that it differs from both by its shape; the Mac's is an orange rectangle with a hare or a tortoise. Its rate is chosen from six radio buttons in three columns, where the Mac has a segmented control. The smallest window stays 980 by 640 with the fourth lane, where the Mac's grew by the lane's height. None of this has been seen on a screen: it was decided from the XAML.
- **Why Split is off is written next to the button only while paused.** While the preview plays, the playhead passes a place where no scene can be split at every start of a scene, and a sentence that came and went there pushed the inspector down and up. That was seen on Windows; the Mac's inspector had the same sentence and got the same rule after a review that read its views. The reason can be had at any time from the Split button above the timeline: it is that button's description on Windows and its help on the Mac.
- **Where the focus goes when a button switches itself off, on Windows.** To the nearest button that still does something: after Split in the transport row to Add zoom, Cut, or Play, never on to the buttons that trim; after Delete cut to Next cut, Previous cut, or Cut at playhead; Previous and Next hand over to each other at the ends.
- **Closed editors and memory, on Windows.** The code the XAML compiler writes for a window with compiled bindings adds a handler to the window's Activated event and never removes it, and that kept every closed editor in memory with all it showed. The editor now takes the handler off when it closes. It has to find it by name, because code-behind cannot name the class it belongs to; should a later compiler name it otherwise, the leak comes back and only the window check says so. A slider's range value, which UI Automation holds on to once a screen reader has asked for it, knows its slider only weakly now. What the framework still holds of a closed editor is under "Known problems on Windows", and is left alone.
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
  - NativeAOT: every mode ran from the AOT build without changes. (The app's releases have since stopped using NativeAOT, so this no longer decides anything.)
  - Not measured: sound from the preview players, real recordings as input, and a real change of monitor DPI.
- **macOS**: not run. It needs a Mac.

### Changes outside Studio

- The macOS `Build` workflow also builds the `TinyClipsMAS` scheme, and both workflows run when `shared/studio/**` changes.
- `TinyClipsActivationPolicy.resolve` takes `hasOpenEditors`, which covers screenshot editors and Studio windows.
- On Windows, `ShowTextRecognitionNotification` was renamed `ShowMessageNotification` because Studio reuses it.
- On Windows, `MfSinkWriterEncoder` has two new switches, `topDownMemoryFrames` and `keepFrameTimes`, both off by default. Only the Studio camera recorder turns them on; the regular recorder's calls are unchanged.

### Found in the regular Windows recorder and left alone

`StudioRenderCheck` reproduces the regular recorder's CPU path without capturing anything: it creates the encoder the way the recorder does and hands it frames through the recorder's own buffer code. That path is used when the GPU recording pipeline is switched off or cannot start. On the development PC (AMD encoder) the file it wrote was upside down in every frame, for H.264 and HEVC. A real recording made that way has not been looked at. A Studio screen track recorded on that path would have the same fault, and the check tool reports it as known. The Studio camera track had the same cause and is fixed with `topDownMemoryFrames`. The regular recorder was not changed, because it is shipping code outside this work. The same fix there is a small change that is waiting for a decision.

### Found in the Clips Library window and left alone

The Clips Library window uses compiled bindings in its window the way the Studio window does, and the code generated for it has the same handler on the window's Activated event that kept every closed Studio window in memory. So a Clips Library window that is closed probably stays in memory with what it showed, and each time it is opened adds another. Nothing was run to confirm it: the finding is the generated code and what the same code did in the Studio window. It is shipping code outside this work and is not changed. The fix would be the one the Studio window got, a few lines when the window closes.

## Risks and how the plan handles them

| Risk | Handling |
|---|---|
| Windows preview: two frame-server players staying in sync through seeks and frame steps, and presenting from Core's device | `m0-win-engine-spike` chooses between a Win2D swap chain panel on the shared device and raw `SwapChainPanel` interop. It was also checked with an AOT publish, which mattered when the plan was written; the releases no longer use NativeAOT |
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
- Windows mixes system audio and microphone into one track. Separate volumes there need a capture change, which is not made yet (see "Volumes").

## Dream backlog (not planned yet)

- A separately rendered cursor: smoothing, size, hide when idle; motion blur on zooms
- Device frames around the screen card (browser, phone)
- Annotations over time ranges, reusing the screenshot editor's arrows, text, emoji, and redaction
- Captions from speech, silence removal, background music, intro and outro cards, keystroke display
- Blurred-screen and wallpaper backgrounds, named look presets
- Export to GIF and social size presets
- Using the Studio renderer for ordinary macOS recordings, replacing up to three encode passes with one

## Open questions

The name, price, cleanup defaults, and first-run look are settled in "Decisions confirmed" above. Open, each written up where it belongs:

- **Windows person cutout: ship the model file?** One file of 448 KB (MediaPipe Selfie Segmentation, Apache License 2.0) and a notice of it. Everything else is built. See "Person cutout (Milestone 4)".
- **Windows volumes:** the recorder would have to keep the computer's sound and the microphone in two tracks. See "Volumes".
- **Choosing a layout while recording:** which keys. See "Scenes from the recording".
- **The regular Windows recorder's CPU path** writes upside down on the development PC; the fix is small and waits for a yes. See "Found in the regular Windows recorder and left alone".

## Validation

- Windows changes: `dotnet restore windows/TinyClips.Windows.slnx`, `dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64`, `dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug`, plus the Store flavor build when project files change.
- macOS changes: the unit tests and both schemes (`TinyClips`, `TinyClipsMAS`), run locally or by the `Build` workflow on the pull request. A macOS item is not done until that build is green.
- Every user-facing milestone updates the root `CHANGELOG.md` (macOS), `windows/CHANGELOG.md`, both READMEs, the in-app Guide, the Windows What's New window, and `windows/docs/accessibility-release-gate.md`.
