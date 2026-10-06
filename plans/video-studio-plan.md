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
| Cleanup defaults | Sources kept 30 days after last opened, with a 10 GB cap. Since 5 October the cap counts only what cleanup may remove, which is exported projects; see "Decided on 5 October" |
| First-run look | A gradient background with padding |

### Decided on 5 October

Ten questions that had come up while building were put to the owner one at a time, each with what it costs and what breaks if it is left alone, and one more followed from an answer. In the order they were asked:

| Question | Decision |
|---|---|
| Choosing a layout while recording | Not built. Taken out of the plan. The format keeps its place for it |
| "Keep sources" and single-layer mode, both described and neither built | Build the control that keeps a project. Opening any video as a single layer goes to the backlog |
| Volumes on Windows | Mute only in this pull request. Two sound tracks are a follow-up of their own, made with the owner at the PC to record |
| The model for the Windows person cutout | Ship the 448 KB model with the API that is part of Windows, as built, and add a third-party notices file |
| The Mac App Store build and the hidden switch | The switch becomes a setting people can see, on both Mac builds: "Tiny Clips Studio (Preview)". The recommendation had been to compile the App Store build without Studio |
| The same on Windows? | The same switch in Windows Settings, now. The recommendation had been to leave Windows hidden until someone had used it |
| The storage limit once the drafts alone are over it | The limit counts only exported projects, which are what cleanup may remove |
| A way out for a draft the editor cannot show | "Save the screen recording" on each draft in Settings and next to the editor's error message, on both platforms |
| A project whose exported video is gone | Listed with the drafts again and marked, so that it can be opened, exported again or deleted |
| The regular Windows recorder's CPU path, which writes upside down | A small pull request of its own against main, once one real recording on that path confirms it |
| Three unit tests skipped on purpose | Kept skipped, with their reasons, until the preview handles a recording's own frame times |

What each of them took is in "Implementation status", under "The decisions of 5 October, built".

### Decided later on 5 October

Two more, asked the same way late that evening:

| Question | Decision |
|---|---|
| The fix for the recording that a failed start deleted: on this branch only, or on `main` as well | A small pull request of its own against `main`. It is #418, merged on 6 October |
| Esc in the Studio window, since #417 made it close the other editors | Esc closes Studio the way its close button does, behind the same setting. That was missing on Windows only: the Mac's Studio window has closed on Esc since 3 October, built then as a decision made while building, and the question did not say so. Built on Windows on 6 October; see "The evening of 5 October" |

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
  "exports": [ { "path": "TinyClips 2026-10-02 at 15.28.06.mp4", "exportedAt": "2026-10-02T22:41:00Z", "bytes": 48211304 } ]
}
```

`events.json` holds `clicks` (time, x, y, button), `cursor` samples (time, x, y), `cameraCorners` (the live corner changes both apps already track), and `markers`, the format's place for a layout chosen while recording. Nothing writes markers: that was taken out of the plan on 5 October.

### Cleanup of old sources

Defaults, all adjustable in Settings:

- Sources of an exported project are deleted 30 days after it was last in use, or sooner when the exported projects together pass 10 GB (the one opened longest ago first). Only what cleanup may remove is counted in the 10 GB. Drafts and kept projects are not: counted, they would use the room up, and every exported project would lose its sources the moment it was exported. The project opened last is never removed for the 10 GB, so that one project larger than the limit does not go the moment its editor closes. That rule is from 6 October and was not asked about; see "Open questions".
- A project that has never been exported (a draft) is never deleted automatically. Neither is a project whose exported video is no longer where it was saved: it is then the only copy of the recording, and is listed with the drafts again, marked. Still where it was saved means a file at the path that is as large as the video was when it was exported. A project remembers that size since 6 October, so that another recording saved under the name of a deleted video does not stand in for it.
- A project file that does not say when it was last opened is not removed until the project has been opened, which writes the time.
- "Keep this project" in the editor pins a project. Settings shows project storage and a Clean up now button.
- After cleanup the exported MP4 remains, and is then an ordinary video. Opening it in Studio again as a single layer, with the background, padding, zooms and cuts applied to the flattened video, was part of this design and is not built. It is in the backlog, where the same mode would open any existing MP4.

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

**Windows.** The recorder mixes both into one track while recording, so a Windows recording has nothing to set apart and the window shows no volumes. Separate volumes there need three things: the recorder writing two sound tracks in a Studio recording, the exporter mixing two tracks, and the preview playing two. The first of these changes the sound path of the recorder that ships today, and none of it can be tried on the machine this is written on without recording its user's sound and microphone. It is left until that can be done with the user. Decided on 5 October: Windows has Mute only in this pull request, and two sound tracks are a follow-up of their own, made with the owner at the PC to record.

### Scenes from the recording (Milestone 3)

Both apps let the camera be moved to another corner while a recording runs, and both already wrote those moves into the project's events. A regular recording has the camera drawn into it, so the move is simply in the video. A Studio recording kept the camera apart and then showed it in its first corner from start to end, whatever was done while recording.

A new project now gets a scene for each move: the camera goes to the corner it was moved to, at the time it was moved, and glides there the way it does into a scene split off in the editor. The scenes are ordinary scenes. They show on the scene lane and can be changed or deleted.

The rule is in section 9.1 of the project format, is the same on both platforms, and is held by shared fixtures. Its corners:

- Corners passed through in less than 0.3 seconds leave no scenes of their own. Only where the camera stayed gets one.
- A move that is taken back at once leaves nothing.
- A move in the last 0.3 seconds of the recording is left out, because its scene would be shorter than any the editor makes.

**Choosing a layout while recording** is not built, and since 5 October not planned. It would have needed keys of its own, since a click on the recording panel is itself in the recording. The format keeps its place for it (`markers` in the events, each a time and a layout), the rule that turns a marker into a scene with that layout is in both apps with its fixtures, and nothing writes one. A layout is chosen afterwards, in the editor, by splitting a scene.

### Person cutout (Milestone 4)

The camera picture can have everything but the people in it blurred, or taken away so that only the people stand in front of the screen. It is one choice in the Camera section, Background: Keep, Blur, or Remove. It belongs to the look, so saving a look as the default brings it to the next recording.

- **Blur** leaves the camera layer as it is: its shape, its border, its shadow.
- **Remove** leaves only the people, clipped by the layer's shape. The border and the shadow go, because they belong to a frame around the picture that is no longer there.

Which pixels are a person is for each platform to find, so the edge is not the same pixel for pixel on both. The drawing rule is in section 6.7 of the project format.

**macOS** uses the Vision framework's person segmentation in the compositor, for the preview and the export alike, at its middle quality setting. Nothing is added to the app. While a video plays, Vision steadies the edge from frame to frame; right after a seek the first masks can trail the picture.

**Windows** has nothing built in that finds people in a recording on an ordinary PC. Windows Studio Effects changes the picture of a live camera, and needs a neural processor and a driver from the PC's maker; the Windows AI imaging APIs need a Copilot+ PC. So it takes a segmentation model and something to run it.

*What is built.* The renderer draws a blurred or removed background from a picture of where the people are, in the preview, the export and the poster alike. What finds them is behind one small interface (`IStudioPersonFinder`). The finder that comes with it runs a model on the processor with `Windows.AI.MachineLearning`, the machine learning API that has been part of Windows since version 1809. That adds no package and no library to the app in either flavor. The one thing added is the model's file, which the app looks for at `Assets\Studio\selfie_segmentation.onnx` next to itself. Since 5 October the file is in the repository and ships with the app. In a build without it Windows keeps every background, which is what the format asks of a renderer that cannot find people, and the window does not offer the choice.

*The model* this was built and tried with is MediaPipe Selfie Segmentation, Google's 256 by 256 "general" model under the Apache License 2.0, as an ONNX file of 448 KB. Microsoft's PowerToys ZoomIt uses the same model through the same API for the background blur of its own webcam overlay, and the file in its repository is the one used here: 447,658 bytes, SHA-256 `DE212DABBC6266F0047711D1DFAE80900F7B596B9ED5F7665F3D1CF68C5443EE`, the same there on 4 and on 5 October 2026.

*Measured on the development PC* (8 cores, AMD graphics): finding the people takes 4 to 5 ms a frame on the processor, and the graphics card is no faster for a model this small. A 1080p frame whose camera background is blurred or removed takes about 10 ms to draw and read back, where one with it kept takes 2. The first frame takes about half a second more, once, to load the model. Over a minute of video, 1,800 frames, a process that only runs the model holds no more at the end than at the start; it did grow, by about 30 KB a frame, until the finder kept one binding for all its frames instead of making one for each. On one of MediaPipe's own test photographs the person is cut out cleanly, with a few specks left along an arm; the pictures have been looked at. Nothing has been tried on webcam footage, on ARM64, or on a PC without a graphics card that Windows can use.

*How the people are drawn.* The model's answer is used as it is, as how much of each pixel is a person, with no threshold. It is not steadied from frame to frame, so a frame always looks the same however the playhead got to it; an edge may flicker in a way the Mac's does not. The blur is Direct2D's Gaussian in its balanced setting, which measures 25.1 to 25.2 pixels where the format asks for 25.6.

*The choice of what runs the model* is the user's, since it decides what is added to the app:

| | Added to the direct download | Added to the Store package | |
|---|---|---|---|
| The API that is part of Windows (`Windows.AI.MachineLearning`), as built | The model, 0.4 MB | The model, 0.4 MB | Takes ONNX models up to opset 12 on Windows 11; this one is opset 11. Microsoft calls this API superseded by the next row and adds nothing to it any more |
| Windows ML in the Windows App SDK (`Microsoft.WindowsAppSDK.ML`, which has a 1.8 line) | About 41 MB unpacked and 16 MB in the download: ONNX Runtime 22 MB, DirectML 18 MB. About half without DirectML, which Microsoft describes and does not support | The model, 0.4 MB | The direct download carries its own copy of the Windows App SDK, so it would carry this too. The Store package uses the shared one. A current ONNX Runtime, still developed |
| ONNX Runtime from NuGet (`Microsoft.ML.OnnxRuntime`) | About 16 MB unpacked and 6 MB in the download, for each architecture | The same | The same engine at a version the app chooses |

The sizes are those of the libraries in the current packages on nuget.org, read from the packages' own lists of contents. The recommendation is the first row, which is what is built: it costs the app nothing but the model, and Microsoft ships the same pairing. Moving to one of the others later means writing one class of about 250 lines again. NativeAOT and trimming do not narrow the choice: the releases no longer use NativeAOT.

*Decided on 5 October: ship it.* The model's file is in `windows/src/TinyClips.App/Assets/Studio/`, and the build's packaging recipe lists it for the package. The Apache License asks whoever passes the file on to pass the licence on with it and to keep its notices. So `Assets/THIRD-PARTY-NOTICES.txt` ships next to it, saying what the file is, where it is from, that Tiny Clips changed nothing in it, the copyright line that MediaPipe's own source files for selfie segmentation carry, and the licence in full; and Settings › About has a **Third-party notices** card that names the model and shows that text. MediaPipe publishes no NOTICE file. The `NOTICE.md` of the PowerToys repository, where the ONNX conversion was taken from, had no entry for the model on 4 October; the rest of that repository was not searched. Who made the conversion is not known: the file names tf2onnx 1.8.4 as what produced it, and the notice says that. The Background choice in the Windows window, the card and its dialog have been compiled and never run, and the choice has never been tried on webcam footage.

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
- Capture: `VideoRecordingService` gets a Studio mode that skips drawing overlays, sends camera frames to a second `MfSinkWriterEncoder` stamped from the shared `RecordingTimeline`, takes the camera's frames at the size the camera is running at (never above 1080p; see "The recording path"), and records events. `IWebcamCaptureService` needs a per-frame callback (it exposes only the latest frame today). `MouseClickMonitor` adds the button and cursor sampling. Completion carries a project id so `App.OnRecordingCompleted` can route to Studio.
- App: `Views/Studio/StudioWindow.xaml`, `ViewModels/Studio/StudioViewModel.cs`, and `Controls/Studio/` for preview, timeline, and inspector, reusing `TrimBar` and `WindowChromeController`. Tray-first startup is unchanged, and the Store and direct flavors are identical.

## Milestones

Each milestone lands on both platforms before the next one starts. Studio is labeled Preview and is off until it is switched on in Settings. Until 5 October the switch was hidden; see "The Studio switch".

| Milestone | What you can do at the end |
|---|---|
| **M0 Foundations** | Nothing user-facing. The design doc and format spec are in the repo, fixtures exist, and an engine spike on each platform has confirmed the preview and export approach |
| **M1 Compose and export** | Record for Studio. Pick a background and padding, round the screen card, choose a camera shape and one of four layouts, drag the bubble, trim in and out, and export. Reopen the project later from the Clips Library |
| **M2 Crops and zooms** | Crop the screen or camera to a region. Add zoom segments by hand, then accept or adjust suggested zooms built from your clicks and cursor |
| **M3 Scenes and cuts** | Split the video into scenes with their own layouts and animated transitions. Cut out sections, change speed, and set volumes. A move of the camera while recording arrives as a scene. Switching layouts while recording was part of this milestone and was taken out on 5 October |
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
| `m3-win-live-markers`, `m3-mac-live-markers` | Each | Dropped on 5 October: live layout switching while recording is not built |
| `m4-mac-person-cutout` | macOS | Vision person segmentation in the compositor |
| `m4-win-person-cutout` | Windows | Evaluate the options, get a decision on the added dependency, then implement. (The options are evaluated and the one that adds no package is built. Since 5 October the model file it needs ships with the app) |

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

Nothing on macOS has been run on a Mac. This work was done on Windows, where the macOS code can only be compiled and unit tested by the pull request's `Build` workflow. Capture, the compositor, the preview, export, and the whole Studio window are unverified at runtime. Since 5 October anyone can switch Studio on in Settings, so whoever does is the first to run them.

Since it cannot be run here, the macOS code was read through twice more by reviewers that only read (5 October): once the composition, the compositor, the player and the export, and once the capture path, the project store and the window's lifetime. They found eight defects that follow from the code and the documented behavior of the frameworks, all fixed and compiled:

- The video ended where the screen last changed. The recorder writes a screen frame only when the screen changes, so a recording that ends with talk over a still screen has a screen track shorter than its sound, and the composition was cut to that track: the sound and the camera after it were dropped, in the preview and the export, while the timeline ran on. The composition now holds the last frame for what is missing.
- Zooms, moves between scenes and click rings could stand still over a still screen, because the compositor told AVFoundation that its picture does not change while the source frames stay the same.
- A pause made while a new canvas size was being applied was undone.
- A recording whose project could not be saved, and which could not be moved to the save folder either, was deleted with its folder. Windows had the same fault in the same place, also fixed.
- The outline of the region stayed on screen when the project folder could not be created.
- "After recording" stopped following the trimmer setting once it had been stored, though the old toggle, onboarding and older builds still write that setting.
- A press on Tiny Clips' own Stop or Pause panel was stored as a click of the recording.
- An edit made in the last half second before quitting was lost.

A fix made by reading is as unrun as the code it fixes. The reviewers also listed what only a Mac can settle; that list is in "Hands-on checklist" at the end of this plan.

**Described above and not built, on either platform.** Nothing opens a video in single-layer mode: both stores can make a project around a video that has no sources, but no command calls that, so "Open in Studio" is offered only for a video whose project still has its sources. Since 5 October that is in the backlog. The other thing this paragraph named, a control for keeping a project, is built: "Keep this project" in the inspector of both editors.

On Windows each piece has been run by a check tool on one PC (AMD graphics, Windows 11): the renderer, exporter and camera recorder by `StudioRenderCheck`, the preview engine with its panel by `StudioPreviewCheck`, and the editor window by `StudioWindowCheck`. Each reads its results back itself, from pixels, from decoded files, or from the UI Automation tree. The editor's behavior is in Core and unit tested.

`StudioWindowCheck` opens the real window on real projects in a process of its own, with the real preview and exporter, and works it through UI Automation: opening, playing, every inspector control with undo and redo, trimming, exporting, closing, and two windows at once. It found three defects in the window, which are fixed. A window in the background took the keyboard focus when its project had opened or its export ended. Five sliders could not be set to an end of their range by a screen reader. Three elements had no name. With the scene and cut controls it found a fourth: every editor that was closed stayed in memory (see "Closed editors and memory" under "Decisions made while building"). The last check of every run is now that none does.

The PC was in use, so the tool sends no input and its windows never come to the front, and the Tiny Clips app itself was never started. That leaves out everything a person does with their hands: no key was pressed, nothing was dragged, and no drop-down was opened. Also not yet seen on Windows: a real recording arriving in the editor, the preview's sound, the high-contrast themes, Narrator reading the window, display scales other than 150 percent, and the app around the window, which is how a recording or a draft gets to it. Since 5 October anyone can switch Studio on in Settings without any of that having been gone through.

One thing about real recordings in particular, because it decides how far the checks reach. The Windows recorder writes a screen frame at every tick of its pacer and stamps it with the wall clock a moment later. So the frames of a recording do not sit on an even grid: they are some milliseconds into their thirtieth of a second, by an amount that differs from one recording to the next and a little from frame to frame, and a tick the recorder misses leaves a gap. The camera's frames carry the camera's own times. What is known of real files is little. The encoder keeps each frame's time as it was given: the index of a camera track it wrote has its frames 0, 2 and 4 ms into their slots and a gap of a third of a second, exactly as they were handed to it. And the recorder's own report of the one real recording there is a trace of on this PC, five seconds at 30 frames a second, counts no missed tick and no dropped frame. Whether the offset of a recording's first frame survives in the file, which decides where its frames sit in their slots, and how often a longer recording or a busier PC misses a tick, is not known.

- **The export is checked with such a file.** `StudioRenderCheck` has a clip with gaps of half a second and of two seconds, a stretch with every other frame missing, and frames 5 to 7 ms off the grid, and reads every exported frame against what the recording has for that moment. What it has not got is a screen track that ends well before its sound. On Windows that is the last tick at most, since a frame is written whether the screen changed or not; it is what went wrong in the macOS composition, where a frame is written only when the screen changes.
- **The preview and the window are not.** Every clip `StudioPreviewCheck` and `StudioWindowCheck` play has one frame exactly at the start of every thirtieth of a second. The preview engine takes the frame a player shows for the one whose slot the clock is in, steps a frame at a time by asking the player for its next frame, and, since 5 October, tells the frames of playback apart by rules whose limits were measured on those clips (a frame handed over within 13 ms of the start of its slot is on time; the next frame has the next number). None of that has met a file with a gap or with frames off the grid. What it would do there has been worked out on the engine's own rules with a model of a player, which is not a run: where the frames sit 10 to 26 ms into their thirtieth of a second, which is half of the possible places, the first missing frame leaves the frames after it without a number for half a second, in which the playhead stands still, and the rest of that play is then told apart the old way; where they sit 22 to 30 ms in, up to a third of the frames get their number one frame late, which is a frame not drawn on time while a zoom or a scene change moves. A step into an empty slot shows the frame after the gap for about a seventh of a second, then the right one. The requirements this fails are in the unit tests, skipped, each with its reason (`StudioPreviewFrameNamerRecordingTests`). It was to be the first thing run when the PC was free. In the one free hour of 5 October the tools were run as they are, and none of them has such a clip. Since then checks that have such clips are written, and so is a mend, which is switched off; neither has run (see "The evening of 5 October"). Studio can be switched on meanwhile.

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

The Windows window has the zoom lane, the Zoom section and the crop sliders. `StudioWindowCheck` works them the way it works the rest of the window (112 of its 454 checks): it adds, selects, moves and deletes zooms through the lane, the keys and the inspector, reads what a screen reader is given and told, and reads from the preview which part of the screen is shown. In four full runs, the part shown while paused was at most 0.38 pixels from where the format puts it, a crop at most 0.61, and the part shown while a zoom moves in during playback at most 0.38. Ten faults put into the window's code on purpose were tried against the checks: nine failed a check at once, and the tenth, the lane's playhead line staying where it was, showed that nothing looked at the line, which a check now does. What the tool cannot do is unchanged: nobody pressed a key or dragged anything, and the high-contrast themes and a screen reader have not been tried. Exporting a zoomed project from the window has not been run either; the exporter's zooms are covered by `StudioRenderCheck`.

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
| Speed lane, Speed section in the inspector, the Speed button | Done. Compiled, never run | Done. Written on 5 October while no check tool could be run on the PC, and worked by `StudioWindowCheck` since that evening (71 checks), without a person at the controls |
| Volumes for the computer's sound and the microphone | Done: the recorder lists its sound tracks, the Audio section has a slider for each, and preview and export play them through an audio mix. The rules are unit tested. Compiled, never run | Not started, and not in this pull request (decided on 5 October). A Windows recording has one mixed sound track; see "Volumes" above |
| Moves of the camera while recording become scenes (section 9.1 of the format) | Done. 16 fixtures. The recorder hands the moves to the new project. Compiled, never run | Done. The same fixtures. The recorder hands the moves to the new project; that hand-over is two lines that no test reaches, and no recording has been made with it |
| Choosing a layout while recording | Not built, and taken out of the plan on 5 October. The format and the rule are in, with fixtures, and nothing writes a marker | The same |

| Milestone 4 piece | macOS | Windows |
|---|---|---|
| The drawing rule for a blurred or removed camera background (section 6.7 of the format) | Done | The same text |
| Drawing it | In the compositor, with Core Image. Compiled, never run | Done in the renderer, for the preview, the export and the poster. `StudioRenderCheck` measures it from pixels with a stand-in for what finds the people |
| Finding the people in a camera frame | Vision person segmentation in the compositor. Compiled, never run | Done, with the machine learning API that is part of Windows and a model file that ships with the app since 5 October. Tried on one photograph, never on webcam footage |
| Background: Keep, Blur, Remove in the Camera section | Done. The rule is unit tested. Compiled, never run | The rule is in the editor model and session, unit tested. The choice is in the Camera section and shows where the model's file is, which is every build since 5 October. Worked by `StudioWindowCheck` (5 checks), which tells the editor whether people can be found. A cut-out camera has not been drawn in the live window |

A project file with more than one scene is drawn with its transitions on both platforms. On the Mac the editor can now split a recording into scenes and change each one, in views that have been compiled and never run. The Windows window has the scene lane, Split and the Scene section, and its Layout and Camera sections show and change the scene the playhead is in. `StudioWindowCheck` works them the way it works the zooms (74 of its checks): it splits, moves, changes and deletes scenes through the lane, the keys and the inspector, reads what a screen reader is given and told, and reads each scene's picture against the layout. The six frames of one move are worked out a second time by hand from the format, since the preview and the layout share their code. Paused, the edge furthest from its place was 0.6 pixels from it. Playing into a scene, every picture the preview draws is read; in three full runs no frame around the change was left out or late.

A project file with cuts in it is exported without them on both platforms. The rules for making and changing cuts are in both editor models, and playback in both editors jumps over them. The Mac has the controls for cuts, compiled and never run, and no preview there has played over a cut. The Windows window has the cut lane, Cut, the Cut section and a gap in the trim bar for each cut, worked by `StudioWindowCheck` (62 of its checks). The tool also plays over a cut with the real preview and reads every picture: one picture of the cut is drawn before the preview goes on to its end, and the picture then stands for about 0.15 seconds. The export has neither. On Windows `StudioRenderCheck` works out by hand where each layer is on its way, measures the test pattern inside the screen and the camera there, looks for their outlines, and compares a fading layer with what is under it. The frames have been looked at. On the Mac the same rules are in the layout, which the fixtures cover, and in the compositor, which has only been compiled.

For the Windows scene and cut controls, 61 faults were put into the window's code on purpose, one at a time, to see whether `StudioWindowCheck` fails: 37 by the controls' author and 24 by the reviewer in other places of the same code. Every one fails a check now. Two of the author's failed none at first: one because the framework moves the focus to the same button by itself, which the check now tells apart, and one that showed the code it removed was not needed, which is taken out. Three full runs of the tool on the merged code passed all 385 checks it had then.

A project file with speed changes in it is exported with them on both platforms, silent where the speed is not the recording's own. On Windows `StudioRenderCheck` exports one project with a stretch at twice the speed and one with a stretch at half the speed and another at four times the speed across a cut and up to the end. For both, the frame of the screen and of the camera that every output frame must show was worked out by hand, and so was where each tone of the sound must be; the tool also listens to the stretches that must be silent and compares how long the sound and the picture last. Six faults put into the exporter on purpose each failed these exports. On the Mac the composition is scaled with AVFoundation's own call, which has only been compiled. The rules for making and changing speed changes are in both editor models. The Mac has the controls for them and a preview that is written to play them, all compiled and never run. The Windows window has the controls too, written while no check tool could be run on the PC and worked by `StudioWindowCheck` since the evening of 5 October. The Windows preview does not play a speed change at its speed yet, so there the preview and the export disagree; the editor asks the preview for the rate already, and the engine does not act on it. Speeds have only been exported from clips six seconds long.

### Known problems on Windows

Found by the check tools, and open:

- **Three checks of the editor window fail, the same three in every run since the evening of 5 October.** With the camera a rounded rectangle and its corners set to fully round, one of three points near a corner is still covered by the camera. And twice, once in the scene group and once in the cut group, the preview rests on the right frame after playing, but the edges of the test pattern are not all where the layout puts them: in the camera's picture five of them are not found where they should be, which leaves too few to tell which part of the clip is shown, and in the screen's picture one edge is 0.85 px from its place where 0.75 is allowed. All three passed on the morning of 5 October. Since then the speed lane was added, which leaves the preview less room (in one and the same check the screen recording was drawn 820×461 then and is 766×431 now), and `main` was merged. Whether the app draws something wrong at that size, or the checks measure wrongly at it, is not known. They were found in the last hour the PC was free, and nothing has been run since to find out. What their author expects the next run to show is written down beforehand; see "The evening of 5 October".
- **A frame taken for the one after it: mended on the clips the checks have.** The preview knew which frame a picture is only from where its player says it is at the moment the picture arrives. When the app had been held up for some tens of milliseconds, a picture that arrived late was taken for the next frame: after a pause at that moment the playhead named the frame after the one shown, and while playing one scene was drawn with the picture of one frame and the layout of the next. The engine now tells the frames apart as they are handed over, and shows a frame it cannot tell only where the scene comes out the same whichever frame it is (`windows/docs/studio-preview.md`). In its own checks the playhead and the picture agreed after every one of 6,200 pauses, 3,800 of them with the process held up in one of seven ways, and none of 24,663 scenes of a zoom had the layout of another frame. With every kind of hold-up at once, 2 of 300 pauses returned with the two a frame apart and were right a moment later. What that leaves:
  - Two rare ways in which a frame still gets a wrong number: about once in 130,000 frames while the garbage collector is at work, cause not found, and about once in 60,000 when the whole process is stopped from outside at random. The checks count both and pass on them unless told to judge them.
  - Those runs were made in the engine author's copy, with the editor window and the renderer as they were before the scene and cut controls. On the merged code the engine's own checks passed once in full, on the evening of 5 October (305 of 305).
  - A reviewer who only read the change found three faults in corners the checks do not go to, fixed since (the checks written for the first two pass, and neither has been run without its fix): after the graphics device is lost, nothing was drawn while the playhead was outside the camera's time range; Pause and Play called one right after the other while the preview was busy drawing left it standing still until the next pause; and the same two calls from two threads could leave it playing without a picture.
  - The clips of all those checks have their frames on an even grid, which a recording has not (see "Where each platform stands").
- **Two editors that could not open.** In one early full run, two editors opened one after the other each said that the screen recording could not be decoded, and the next one opened. The cause is not known, and it has not happened again: not in 10,200 opens made on purpose in every way that seemed likely, one of which failed, with a lost graphics device and not this error. The engine now opens once more, three quarters of a second later, when a player reports that it cannot decode before its first frame; that could be tried with a made-up failure only. The tool keeps the error codes and the engine's trace of the seconds before, which it did not then.
- **A frame past the end.** When the preview plays up to the end of the video, or Play is pressed at the end, it can show the frame after the last one for a moment. Seen in 3 of 64 runs of the window's transport checks, all of them while the PC was busy with other tools and builds. The engine's own checks have since played 64 times up to the end of a video that ends in a cut, without a frame past it. The window check that saw it passed in each of the three full runs of the evening of 5 October, which says little about something seen 3 times in 64.
- **The camera a frame beside the screen.** While playing, the camera's picture is now and then one frame ahead of the screen's or behind it: in 16 of 86 play-throughs into a scene, in one to three of about eighty pictures each. The two tracks are played by two players on one clock, which the engine spike measured on matching frames 98.6 to 99.95 percent of the time and never more than one apart. The tool notes it and fails at two frames.
- **What is left of a closed editor.** The window, its preview and its decoders go when an editor is closed. Its project and editing state can stay in memory for a while, held by the framework and not by the app: until the app shows its next window, when the editor was closed right after it played; or until it happens again, when the editor was closed within a second of a button getting the keyboard focus a second time, while the framework waits to show that button's tooltip. It is one editor's state at most and does not add up. A window with nothing but a button and a tooltip does the same, so the app is left as it is. The second case needs a key press to happen in the app, which no tool here sends.

### The recording path: read, never run

Nothing may capture the screen of the PC this was written on, and no Mac has run the code. So on both platforms the code that records for Studio has recorded nothing in its present form, and the changes this branch makes to the recorders that ship have not recorded anything either. On 5 October two reviewers that only read went through every shipping file the branch changes, one on each platform, with two questions: what is different for someone who never switches Studio on, and what is wrong in the Studio recording path. That evening what `main` had changed in the Windows recorder was merged into it by hand, after that reading; see "The evening of 5 October".

**With Studio off** neither found a way in which a video, a GIF or a screenshot comes out differently. On both platforms every added condition takes the branch that was there before, and at startup with Studio off no folder is made, no project is read, and no timer or cleanup is started. Each difference they did find is listed under "Changes outside Studio". Two cost something. On the Mac the Clips Manager made a Studio key for every video it drew, and making one asks the disk whether the path is a folder (so Apple documents it for `NSURL`; the cost was not measured); it now makes none while there are no links. On Windows a recorded click takes 32 bytes where it took 16, so a recording with click rings, which copies the list of clicks for every frame, copies twice as much; that matters only after some thousands of clicks in one recording.

**Mended after that reading, and not run:**

- **The camera's track ended before the screen's**, on both platforms. At stop the camera was stopped and its file finished first, and the screen went on recording meanwhile. Outside its own time the camera is not drawn (section 6.5 of the format), so at the end of every Studio recording with a camera, the camera would have gone for the last tenths of a second. In a Studio recording the camera now goes on until the screen track is finished. An ordinary recording keeps its order, in which the last frames have no camera either; that is older than this work and not changed.
- **On Windows a camera that gives its frames no time** was live in its bubble throughout and missing from the project, without a word. Its frames are now stamped with the time they arrive, and a recording whose camera left no track says so in a notification.

**Open on Windows, each read from the code and none measured:**

- **Camera frames can wait in memory without limit.** The camera's encoder is handed frames as fast as the camera makes them, and the writer is told not to hold the caller back. An encoder slower than the camera leaves frames of about 8 MB each waiting, and a recording that runs out of memory is lost whole. A hardware encoder will keep up; the software one on a slow or busy PC may not. A limit has to be set above what a healthy encoder holds at any moment, which nobody has measured, and the recorder's check feeds its frames faster than a camera does, so a limit put in blind would as likely break the check as mend the fault. To be measured first: the recorder check with the software encoder, at a camera's pace, watching memory.
- **The first camera picture stands still** while the camera's encoder starts. The encoder is created when the first frame arrives, which is after the clock has started; that frame is written at zero and the next one after however long the start took: some tenths of a second, more where a hardware encoder is tried first and refused. The mend is to create it where the screen's encoder is created, before the clock starts.
- **A camera whose clock is not the PC's** still leaves no track. Only a missing time is handled; the recording now says that the camera is missing.
- **The camera is recorded in the mode it is running in.** Tiny Clips opens a camera shared and read-only, as the regular recorder always has, which lets it use a camera another app has open and gives it no right to change the camera's mode. The track is therefore as large as the camera's mode at that moment and never above 1080p, and a camera that starts in a small mode gives a small track. In a corner bubble that shows little; Studio can show the camera over half the canvas or all of it. The size is written to `webcam-diagnostics.log` ("Camera format … delivering …"). No real camera has been looked at.
- **A draft the editor cannot show had no way out.** A draft had two buttons, Open and Delete, here and on the Mac. The case to try is a recording made with HEVC on a PC that has an HEVC encoder and no HEVC decoder: the regular trimmer could not show such a video either, but that video is in the save folder, and a draft is in the app's data. The same holds for any recording the preview or the exporter fails on, and the preview has never played a real one. Since 5 October every draft, and an editor that cannot show its project, can save the screen recording as an ordinary video; see "The decisions of 5 October, built".
- **A recording that can be saved neither as a project nor as an ordinary video** stays in its project folder for a day, as on the Mac. The Mac says so in its message. Windows lists it under Recent captures like any other and says nothing of the day.
- **When the editor window cannot be opened** after a recording, the app stops, as it does when the trimmer cannot be opened. The project is kept and is listed under the drafts.

### The app around the editor: read, never run

On 5 October a fifth reviewer that only reads went through what the Windows app does around the editor with Studio switched on: how a recording or a draft gets to a window, what cleanup does while windows open and close, what the Clips Library is told, and where an error goes. It found two faults that follow from the code and five that probably do. Each was read in the code before anything was changed. Mended, in Core wherever a unit test could hold the rule, and on the Mac where the Mac had the same fault:

- **Cleanup could take a project from under an editor that had just opened it.** The list of projects in use is made before a cleanup starts, and reading every project takes a moment; a project opened in that moment was not in the list. The store now asks again for each project just before it deletes it, while it is locked, and an editor reads its project through the same lock before it opens any of its files. On the Mac the store keeps the open projects itself. Unit tested on both.
- **A save that failed while a window was closing was told to nobody**, because the window that would have shown it was going. The app now shows it as a notification. Compiled, never run.
- **A fresh export had no "Open in Studio…" in a Clips Library that was open**, because the video appeared before the project had its link to it. The exporter now writes under a name of its own, and the video gets its name and the project its link in one step. Unit tested.
- **An export could replace another file** that had been given the same name while the video was being made, which takes a file name template without the time in it. A finished video now never takes the place of a file: it gets the next free name, and after five names the export fails and says why. On the Mac, where the export is written in place, the export stops instead of removing the other file. Unit tested on Windows, compiled on the Mac.
- **A Studio recording that fell back to an ordinary video said nothing**, so a recording made for Studio was simply not in Studio. The app now says which of two things happened: it was kept as a regular video, or it is listed under Recent captures and kept for a day. Compiled, never run.
- **"Last opened" was written when an editor opened and never again**, so a project whose editor stayed open for longer than projects are kept was removed by the cleanup that follows the editor closing. It is now written again at close. Unit tested on Windows; in the view model on the Mac.
- **A project that was removed since the Clips Library last looked** opened an editor with the file system's own error and a path. It now says that the project is no longer stored on this PC and that a video exported from it is not affected. Unit tested.

Left as it was: with the switch off, projects were neither shown nor cleaned up, and Reset All Settings switches Studio off. The visible switch of the same day says under itself what is kept. Renaming or archiving a video in the Clips Library while Studio was off lost the video's link to its project until 6 October. The link is kept now, on both platforms.

The Windows unit tests were run once without the four Core fixes: 9 of the 79 tests that cover those rules fail, the ones that state them.

### The decisions of 5 October, built

All of it was written the same day, with the PC in use, so nothing with a window was started. What a unit test can hold is unit tested on both platforms. Every control is compiled and has never been run.

| Decision | macOS | Windows |
|---|---|---|
| The limit counts what cleanup may remove | In the cleanup rules. Unit tested | The same rule. Unit tested |
| A project whose exported video is gone is kept and listed with the drafts | In the store's summaries and the cleanup rules, unit tested. The line that marks it in Settings › Video is compiled, never run | The same, unit tested. The line in Settings › General is compiled, never run |
| Keep this project | The store's call is unit tested. The check box in the inspector's new Project section, and the view model's call, are compiled, never run | The store's call and the editor session's are unit tested. The check box is compiled, never run |
| Save the screen recording | The copy is unit tested: it never replaces a file and leaves nothing behind when it fails. The button in the editor's message and the one on each draft are compiled, never run | The same, unit tested, with a copy that can be cancelled and that reads beside an editor that has the file open. Both buttons are compiled, never run |
| Projects that cannot be read, listed with the drafts | The list is unit tested. Its rows are compiled, never run | The same |
| The Studio switch in Settings | Compiled, never run | The setting is unit tested, and so is the view model behind the switch, the line under it and the drafts list. The controls themselves are compiled, never run |
| The model and its notice | Not needed: the Mac uses the Vision framework | The file and the notice are in the package, read from the build's packaging recipe. The About card and its dialog are compiled, never run |

The Windows unit tests were also run with six of these rules taken out together: the limit counting everything, a project whose videos are gone being removable, an editor never saying that there is a recording to save, an unreadable project having no recording to find, a finished file replacing whatever has its name, and keeping a project counting as a change to it. 18 of the 146 tests in the classes concerned fail, the 18 that were listed beforehand as the ones that state those rules.

What only a run can show, most likely first: whether the switch in Windows Settings moves the keyboard focus or the scroll position when the cards under it appear; whether three buttons fit a draft's row in the narrowest Settings window; whether the message of an editor that cannot show its project still takes the focus now that a button is inside it; whether the third-party notices can be read in their dialog; and on the Mac, all of it.

### The Studio switch

Studio is off on both platforms until it is switched on, and since 5 October the switch is a setting people can see: **Tiny Clips Studio (Preview)**, in Settings › Video on macOS, on both Mac builds, and in Settings › General on Windows. Until then it was hidden: a `defaults` key on the Mac, and on Windows a stored setting that no control set, or an environment variable.

- With the switch off, nothing else of Studio is visible and recordings follow the existing path unchanged. The switch and the lines that say what Studio is are the one thing a person who never uses Studio now sees of it.
- Switching it off deletes nothing. The projects stay where they are and are not cleaned up while it is off, because the rules that would delete them are not shown then. A line under the switch says how many projects are kept and how much room they take. Where Studio was never used there is no project folder, and reading for that line makes none.
- The switch is the setting `studioPreviewEnabled` on both platforms, as before, so a Mac on which the `defaults` key was set has Studio switched on. Reset All Settings switches it off.
- On Windows the environment variable `TINYCLIPS_STUDIO_PREVIEW` is gone. It forced Studio on whatever the setting said, so with it the switch in Settings could not have switched Studio off.
- Choosing Open in Studio leaves the trimmer switch as it was, on both platforms, so that switching Studio off puts a recording back to what it was before Studio was tried. Until 6 October the choice turned the trimmer off, and it stayed off. A choice of Save or Open trimmer is the trimmer switch by another name: each of the two shows what the other was set to.

What the visible switch changes about what is known: everything under "Where each platform stands" that has been compiled and never run can now be reached by anyone who flips a switch that says Preview. On the Mac that is all of Studio. On Windows it is the recorder for Studio, the app around the editor, and what was written on 5 October. The owner chose this over two more careful ways, which had been the recommendation: compiling the App Store build without Studio, and leaving Windows hidden until someone had used it.

The switch itself has been compiled and never run, on both platforms.

### The evening of 5 October: `main` merged, the tools run again, the app not started

**`main` had moved by six pull requests (#411 to #416), all in the Windows app**, and seven files conflicted. They were merged by hand as `c81a8ea`:

- **The recorder (`VideoRecordingService`).** `main`'s order of preparing and its stop are kept, and what a Studio recording does differently is put back into them. Three things are different from before the merge. The stop lets go of the pipeline before the project is made, which forgets the timeline and where the camera was, so both are read first and handed to the project. Where the recorded picture starts on the desktop, which the clicks and the pointer are measured from, now comes from `main`'s capture geometry: the region after it was clipped to the display, where it was the region as asked for. And a recording on the CPU path that cannot be finished lets go of its Studio state too; what was written stays in its folder, which has no project in it, and the storage cleanup removes such a folder after a day. **None of this has recorded anything.**
- **The Settings view model.** `main`'s new unit tests compile that one file by itself, without the app's services, and Studio's part of it needs three of them. Studio's part is now in `SettingsViewModel.Studio.cs`, which those tests do not compile; the file they do compile reaches it through two partial methods. A view model made the way those tests make it has no Studio services, and says so in a sentence if Studio's lists are touched. Studio's part had no unit tests of its own at that point. It has 24 since later that evening; see below.
- The encoder's description, the constructors of the Settings and Clips Library windows, the README, and one `using`. Four places did not conflict and no longer compiled. The one worth knowing is the render check's measurement of the regular recorder's CPU path, which called a method `main` replaced and now calls the encoder as the recorder does.

After it, 1,711 Core tests pass and 3 are skipped as before, `main`'s 25 tests of the Settings view model pass, both flavours of the app and the four tools build without warnings, and both workflows are green on the pull request (macOS: both schemes, 311 tests).

**The owner then left the PC for about an hour and said the tools and the app could be run.** In that hour each tool ran in full on `c81a8ea`:

| Tool | Result |
|---|---|
| `StudioRenderCheck` | 208 of 208, beside the three limits it reports as known on every run. With the model from the repository, its people checks: 25 of 25 |
| `StudioPreviewCheck` | 305 of 305, once. The two checks that had never run passed by themselves first |
| `StudioWindowCheck`, the speed group, which had never run | 76 of 76 |
| `StudioWindowCheck`, in full, three times | 451 of 454 each time, the same three failing (see "Known problems on Windows") |
| `StudioWindowCheck`, memory only | 5 of 5 |

**The Tiny Clips app was not started.** It was prepared: a copy of the build's package layout under a package name of its own (`TinyClips.StudioCheck`), so that its settings and data would be its own, and the Tiny Clips that is installed on the PC, which belongs to other work, would not be touched. It was registered, and before it was started the owner was back at the PC. It was unregistered again without having run. So what "The recording path: read, never run" and "The app around the editor: read, never run" say still holds, and of the recorder it now holds for code that was merged by hand as well.

**Later that evening: unit tests for Studio's part of the Settings view model.** It is the logic behind the switch, the drafts list and its buttons, none of which anyone has run. The tests of the view model now compile both of its files and the three Studio classes the second one needs, and 24 of them are about Studio's part: the switch and what is saved with it, the After recording choice that stands in for the trimmer switch, the storage rules, the list of recordings that only a project holds, Delete, Save recording, Clean up now, and what a closed window lets go of. The projects are real ones, in a store on a temp folder. `main`'s own tests of the view model pass with Studio's part compiled in. 47 faults were put into Studio's part one at a time, in a scratch copy, and each fails a test. The Settings window that is bound to all of it has still never been shown.

**And `main` moved once more: #417, which makes Esc close the screenshot editor and the trimmers.** Two conflicts, in the Windows changelog and README, both sides kept. Merged as `f738c80` and checked in a clean checkout: 1,727 Core tests pass and 3 are skipped, the 50 tests of the Settings view model pass, and both flavours of the app and the four tools build without warnings. It leaves Studio the one editor that Esc does not close, which was put to the owner; see "Decided later on 5 October".

**The merged recorder, read again.** Nothing could record, so a sixth reviewer that only reads went through the recorder as merged, against both of its parents, with three questions.

- **What is different for an ordinary recording?** 26 places, in the recorder and in four files beside it. 15 change nothing, and 11 are harmless: fields that are written, and one more listing of the monitors at the start. Nothing is caught that `main` does not catch, nothing gets out that `main` catches, and no field is reset at another moment.
- **Did the merge lose, double or misplace anything of the Studio mode?** No. What the merge commit says of itself holds in all seven points. The reviewer found three faults in the stop of a Studio recording all the same, one of them in the catch the merge added. They are mended and not run:
  - A stop that failed before the screen track was finished left the camera on. The camera of a Studio recording goes on until then, where an ordinary recording stops it first. A stop that fails now stops the camera before the error goes on its way.
  - When a recording on the CPU path could not be finished while it was being discarded, its folder stayed for a day. It is deleted now.
  - A stop that failed part of the way and was tried again made a project without its clicks.
- **Anything else a first recording would hit?** Two faults, both in the regular recorder and both on `main`: the one about click rings that was known, and one that deletes a video. See "Found in the regular Windows recorder".

The mends add three places an ordinary recording passes through: the path that is forgotten at the start, which is the point of it; a call that returns at once where there is no Studio camera; and a catch whose condition is never true without one. The Core tests after them: 1,732 pass and 3 are skipped.

**The fix went to `main`.** Asked, the owner chose a small pull request of its own: #418, made from `main` by a second session, with the one line, the five tests and a changelog entry, and merged by him on 6 October. `main` was merged back here as `315b8e2`; the tree after that merge is the tree before it.

**The recorder's tests were then changed here.** As first written they made a start fail on its first read of a setting. A start begins by emptying the diagnostics log, `%LOCALAPPDATA%\TinyClips\Temp\webcam-diagnostics.log`. On the PC this was written on that is the folder the installed Tiny Clips writes to, so each run of those tests emptied that app's log, as the start of a recording does. (Two observations, not a test: the app's capture trace in that folder was last written 22 seconds after the app started, and its package folder has no `TinyClips` folder. It is a development registration; a Tiny Clips from the Store may log under its package folder, which was not looked at.) I had written that the tests touch nothing outside themselves, and that was wrong. Here they now cancel the start at the recorder's first look at its token, which is before the log. That also shows the second way into the fault by a run, where it had only been read: four of the five fail on `main` as it was before #418. The copy of the tests that #418 put on `main` is of the first kind; #418's description names the write as a side effect. On 6 October a small pull request of its own, #420, was opened to put these tests on `main`: one test file, made by a second session, which ran the class once (five pass) and read the log's size and time before and after (the same). It was opened without asking, while the owner was away, and is his to merge or close.

**What the two tools' authors wrote that evening and night.** They could write and build, and run nothing. All of it is compiled, and the unit tests named are the only part that has run.

- **The preview and a recording's own frame times.** The engine can read each clip's frame times from the file's index when it opens, and count in frames of the file: `StudioPreviewOptions.FrameTimesFromFile`. It is off, which is how the app runs. With it off no index is read and every changed expression comes to the value it had; that was read line by line by its author and again by me, and the engine's earlier unit tests pass unchanged. With it on, by a model of a player, every frame has its number at once, wherever it sits in its slot and with or without an empty slot. There are 112 new unit cases, among them the reader of the index, which refuses what it cannot be sure of and did not throw on 3,000 corrupted ones. A model is not a player. Three things the mend rests on can only come from a run: whether a file keeps the time of its first frame, whether the times in the index are the times a player goes by, and what a paused player shows before its first frame. The switch stays off until then. The check tool has a group of checks for it, a table that measures 23 clips, and a script of 40 steps in the order they should run.
- **Read on the way, and mended by neither.** Where a recording's frames sit around the middle of their slots, the export's rule shows one frame twice and the next one never, in a stretch where the recorder missed nothing: a judder in the export. By the recorder's code that takes a recording paused at an unlucky moment. Whether the export's rule should change is left until a real recording shows how its frames sit. A pause inside a stretch without frames rests on the frame before the stretch, so playing on plays the stretch again. And the last frame of a recording whose frames sit late is in no slot of the export.
- **Looking for the people once a frame** is prepared behind a second switch that is off (`StudioPreviewOptions.StampPictures`): the preview would tell the renderer which camera frame it is handing over, so that the people in it are looked for once and not at every draw. It was first written without a switch, on the belief that the app has no model. The app has had one since 5 October, so it got a switch.
- **Speed in the preview is not built.** How a player behaves at another rate has to be measured first, and the experiment for that is written.
- **The window check has 35 new checks**, the ones that were owed for what was built on 5 October and for dragging while the preview plays: something dragged in a scene while it plays (13), Keep this project (6), an editor that cannot show its project and its Save the screen recording (8), a save that fails while the window closes (3), an export whose name is taken while it is written (1), and four pictures. 50 faults are prepared, 26 for these and the 24 for the speed controls. A full run has 497 checks when no part ends early.
- **The three failing checks judge as they did.** Each now keeps the lines and the pictures it judged by. Their author wrote down what it expects the next run to show, so that the run can prove it wrong: the two about the test pattern's edges are the checks' doing, by arithmetic (at the smaller preview a slanted band of the test picture lies on the lines the reader takes an edge from), and the corner is not explained. Three other ways of judging are written behind an option that is off, and one of them lets something pass that fails today; whether any becomes the tool's way is decided after the run.
- **Three things about the app that the new checks write down, left as they are:** Keep this project cannot be switched in the window while an export runs, like everything else under the export's overlay; Undo's button is disabled in the middle of the first drag of a fresh project; and the message bar of a closing window may speak once more when the last save fails.

With all of it, 1,844 Core tests pass and 3 are skipped, the 50 tests of the Settings view model pass, and both flavours of the app and the four tools build without warnings.

**Esc closes the Studio window on Windows.** Written on 6 October by the author of the window check, from the owner's answer and the Mac's window.

- **The rule** is in Core with the other keys (`StudioShortcuts`). Each press takes the first of these that applies: with Ctrl, Shift or Alt it does nothing; a key that is held does nothing at all; while an export runs it stops the export, and that is all; in text being edited, while a drop-down list is open, and in the middle of a drag it does nothing; otherwise it asks the window to close. One unit test goes through every state an Esc press can arrive in, 1,024 of them, against that order written out again.
- **The words** are beside the other editors' (`EditorEscape`): "Close Studio?", that the edits are saved with the project and that it can be opened again from the Clips Library, and where the confirmation is switched off.
- **The window has one path for "the user asks to close"**, which the close button and Esc both take, so that Esc cannot close what the close button would have asked about. A recording that was never exported gets the question it gets from the close button, once, whatever the setting says. A project that is open and was exported gets "Close Studio?" while the setting is on, and closes at once while it is off. A window that cannot show its project, or is still opening it, closes at once. That is the Mac's rule. Which question it is, is decided in Core as well (`StudioCloseQuestions`), with a unit test over all 24 states a request to close can arrive in and one for each thing that must never happen.
- **The window's part is compiled and has never run.** 29 checks are written for it in the window check, with 32 faults; a full run of that tool now has 526 checks when no part ends early. The tool hands the window the key the way its key handler does. The key itself has never been pressed in the window, by a tool or by a person.
- **Faults put into the two rules and the words, one at a time:** 23, each of which failed exactly the unit tests named for it beforehand.

**The Esc change was then read through by a reviewer**, as someone about to press the key for the first time, because nothing of the window can run. It found no way to close a recording without its question, no flag that stays set, no second dialog, and nothing wrong with a window that closes inside its own key handler. It found two faults, both in what had been pushed an hour before, and two gaps:

- **A held Esc could stop an export it was not meant for.** The close button during an export, the question answered with Esc, which is "Keep exporting", and the key held a little too long: the repeats came to the window and stopped the export. The order had the export before the held key, as my brief for it did. A held Esc now does nothing at all: the press it belongs to has done what there was to do, perhaps somewhere else. The Mac's window got the same test of the event that day; that is compiled and not run.
- **The window could put the keyboard focus under an open question.** When an export starts or ends the window moves the focus, to Cancel or to Export, and it did so without looking whether a question was open. Enter or Space then pressed what was under the question, and Esc never reached it. The author of the window code had pointed at this, and I had left it. With the close button waiting while a question is open, there was no way out by keyboard. The three full runs of 5 October have a trace of it that proves nothing: an older check prints where the focus was a third of a second into an export with the question open, and read the question's own button once and Cancel export, which is under the question, twice. The focus is now placed only while no question is open, a request that waited is placed when the question has been answered, and while a question is open the window runs no key.
- **The choice of question had no unit test**, because it was in the window. It is in Core now, as above.
- **A screen recording that was being saved when its window closed was saved and reported to nobody**: not announced, not listed under Recent captures. Older than Esc; the close button had it. The editor's close now waits for a copy that is under way, so that the app is still listening when it reports.

All of that is compiled and, in the window and the view model, not run. Left as it is: a copy that fails after its window has closed is told to nobody. The Settings card for the confirmation names the screenshot editor and the trimmers and not Studio, on purpose while Studio is a preview and the card is in front of everyone; it has to name Studio when Studio stops being one. A held Esc opens Esc's question, and the question takes the repeat for Cancel, so it comes and goes once.

After it, 1,866 Core tests pass and 3 are skipped, and both flavours of the app and the four tools build without warnings.

**The code for the decisions was read through as well**, on 6 October, by two more reviewers that only read, one for each platform: what was written on 5 October for the limit, the drafts, Keep this project, Save the screen recording and the switch. The owner was away with a slide show on the PC's screen, and had not said that the tools may run, so nothing ran. What they found is mended on both platforms. On Windows it is built and unit tested. On the Mac it was written without a compiler and is compiled and unit tested by the workflow. **None of it has been run.**

- **Windows: a failure dialog in Settings could end the app.** The buttons Save recording and Delete wait for a copy or a delete and then show a dialog. If another dialog was open by then, WinUI threw, nothing caught it, and the process ended, with a recording that was running. Their dialogs now go through one helper that catches, and a failure that cannot be shown in a dialog comes as a notification. That includes one after Settings was closed, which was told to nobody.
- **Both: trying Open in Studio left the trimmer off.** See "The Studio switch". The Mac's reviewer had looked at this and found nothing; the Windows reviewer found it; it was in both, from my own design. With Studio on and chosen nothing changes: no trimmer opens, also not for a recording made without Studio. An earlier rule went with it: on Windows, turning the trimmer switch on while Studio was off used to turn a stored choice of Studio into Open trimmer, and on the Mac the trimmer toggle of the onboarding wizard did. The choice of Studio now stays whatever the switch does.
- **Both: cleanup could remove a project whose own video was gone.** A deleted video, and another recording saved under its name, counted as the video being there. See "Cleanup of old sources". What it costs: a video that is changed in place after the export, in another program or by a tag given in Explorer, counts as gone as well, so its project is kept until someone deletes it, and is listed with the drafts. The line on such a row says "or has been changed since". The link from a video to its project, which Open in Studio goes by, still goes by the path alone.
- **Both: one exported project larger than the limit went when its editor closed**, and **a project file without the time it was last opened went at the first cleanup.** Both are rules now, in section 12 of the format.
- **Both: a video that was renamed or archived while Studio was off lost its project.** The project then counted as holding the only copy, for good. The link follows the video now, which is the one thing the app writes into a project while Studio is off. Where Studio was never used there is no folder to read. On Windows a video that was marked while Studio was on no longer offers Open in Studio once it is off, and choosing it at that moment says why nothing opens.
- **Mac: cleanup ran while Studio was switched off**, from a closing editor and from a launch that had read the switch once. The cleanup asks the switch itself now.
- **Mac, in Settings › Video.** The drafts list hears when an editor opens, closes or exports, so Delete is unavailable for exactly the drafts that are open. The question before a delete is an alert of the app's own, where Return does nothing and Esc cancels; it was a dialog hung on a row of the form, which may not have been shown. Save Screen Recording says what the video was saved as, under the list; with the settings as they come nothing showed. The saved video is dated when it is saved, because the Clips Manager sorts and archives by the day a file was made, and a copy carries the recording's. A failure to save after the editor's window has closed is shown, where it was dropped. If Keep This Project cannot be written, the check box goes back. VoiceOver is told the result of Clean Up Now, and the buttons of a row name the row by its date as well.
- **Windows: the cleanup service had no test of its own.** It has three: it does nothing while Studio is off, removes what the rules select and says so, and leaves a project that is open or being recorded into.
- **Windows: Settings now says that uninstalling Tiny Clips, or resetting it, deletes every project**, drafts included. See "Open questions".

On Windows each rule was taken out once to see its tests fail: the service's look at the switch, its list of what is in use, the limit sparing the project opened last, the size, the project without a time, and writing the size. Each failed the tests written for it and no other. The Mac's tests are the same cases and pass on the runner; nobody has seen them fail.

Left as they are, and known:

- Settings and the Clips Library can wait on a network drive: the store looks whether each exported video is there while it holds its lock.
- The same recording can be copied twice at once, from its row in Settings and from its editor. Each copy gets a name of its own.
- A half-written export file (`.tcexport`) is swept only when Studio is on at launch.
- On the Mac, project folders are walked on the main thread on several paths, and the Studio menu stays in the menu bar after the switch goes off until the menu is next rebuilt.
- On the App Store build, a video in a folder the app may no longer read looks the same as one that is gone, and the line on its row says so.
- A time in a project file that is there and cannot be read: the Mac reads it as 1970, Windows calls the project unreadable. The format only says what a missing one reads as. Seen while reading; neither reviewer raised it.

After it, 1,883 Core tests pass and 3 are skipped, the 53 tests of the Settings view model pass, both flavours of the app and the four tools build without warnings, and the Mac's 318 tests pass on the runner.

### Decisions made while building

- **A failed project save keeps the recording.** If the project cannot be saved when a Studio recording stops, the screen track is kept as an ordinary video.
- **Drafts.** A recording kept as a draft has no exported file, so it does not appear in the Clips Manager. The drafts are listed in Settings, where they can be opened or deleted: under Video on macOS and under General on Windows.
- **Deleting an exported video leaves its export link in place, and the project then counts as a draft again.** Until 5 October such a project still counted as exported, and cleanup removed its sources later, which left nothing of the recording. Now a project none of whose exported videos is where it was saved is never removed by cleanup, and is listed with the drafts, marked, where it can be opened, exported again or deleted. The link stays so that a video that comes back, from the Recycle Bin or with the drive it is on, is linked again; both stores have a call that removes a link, and nothing uses it. A video on a drive that is not connected counts as gone for as long as that lasts, which keeps the project: the safe side of not knowing. What this costs: someone who exports, shares and then deletes the video keeps the project until they delete it in Settings. Since 6 October a file at the path counts only when it is as large as the exported video was.
- **The storage limit spares the project opened last**, and **Open in Studio is stored apart from the trimmer switch.** Both were decided on 6 October without asking, from what the read-through found. The first is one line in each platform's cleanup rules and is in "Open questions". The second changes what two switches in Settings › Video do to each other, and has no setting of its own.
- **Cleanup can be switched off.** Zero days keeps projects until they are deleted by hand, and zero gigabytes means no storage limit.
- **Events during pauses.** Clicks and cursor samples from before the first frame or during a pause are not recorded. Cursor samples are capped at 60 per second, drop consecutive duplicates, and are steps, not points to interpolate between.
- **Drawing rules** are in section 6.7 of `docs/studio-project-format.md`: sRGB with gamma-space blending, no color conversion of screen pixels, the shadow model, where the border goes, and the click ring geometry.
- **Camera size on Windows.** The camera track is recorded at the camera's own aspect, fitted inside 1920×1080 and never enlarged.
- **Where projects are kept on Windows.** The installed app is packaged, so its projects are in the package's own folder, `%LOCALAPPDATA%\Packages\<package family>\LocalState\TinyClips\Projects`. Only an unpackaged run uses `%LOCALAPPDATA%\TinyClips\Projects`. The package's folder goes with the package: uninstalling Tiny Clips, or resetting it in Windows Settings, deletes every project, drafts included. Settings says so since 6 October, and whether projects should be kept somewhere else is in "Open questions".
- **Windows editor behavior lives in Core.** `StudioEditorModel` (edits and undo), `StudioEditorSession` (loading, transport, autosave, export, closing) and the preview and export contracts are in `TinyClips.Core`, so the editor's rules are unit tested and the window only binds to them.
- **Windows preview and export.** The preview engine is in Core (`Studio/Preview`), not in the app as planned; the app has only the panel it draws into. The preview owns one Direct3D device per editor window and each export creates its own, so neither shares a device with a recording in progress. The exporter writes through its own sink-writer wrapper rather than the recorder's `MfSinkWriterEncoder`.
- **Frame rate.** `sources.screen.frameRate` is the rate the recording was set to, on both platforms. The rate a media library reads from the file is an average of unevenly spaced frames and can be far lower.
- **Small differences between the two exporters**, accepted for now. Windows samples each output frame at its middle, drops a partial last frame, and keeps NTSC rates exact. macOS samples at the frame's start, keeps the partial frame, and rounds the rate up. Windows will not open or export a project whose camera file is missing; macOS exports it without the camera.
- **The picture at the trim end.** With the playhead at the trim end, the preview shows the picture at that instant. An export holds the frames before it, so that picture is one or two frames past the last one the video keeps. This was seen on Windows. The Mac's code seeks the recording to the same instant, so it should show the same.
- **Color on Windows.** Media Foundation's encoders convert with BT.601 up to 576 lines and BT.709 above, whatever the stream says, so an export is tagged with the matrix that was really used.
- **Windows without graphics hardware.** Exports on the software adapter sample linearly, which measured 43 to 58 frames per second against 16 to 21 for the high-quality sampler.
- **Keyboard focus on Windows.** The editor puts the focus on Play when a project has opened, on Cancel while an export runs, and on Export when it ends, but only while its window is the active one. Otherwise it waits until the user comes back to the window, because asking for the focus brings a window to the front. A click that brings the window back decides the focus itself.
- **The window check tool uses one package the app does not**, `Interop.UIAutomationClient`, to read the window as a screen reader does. The tool is not in the solution and is not shipped.
- **Windows keys and closing.** Esc stops a running export. Until 6 October it did nothing otherwise, because the Windows trimmer did not close on Esc either; since #417 it does, and so does Studio (see the next point). Closing a project that was never exported asks Export, Keep as draft, or Cancel; Delete is a separate button in the dialog and never the default.
- **What the owner's sentence about Esc left open, on Windows.** "Esc closes Studio the way its close button does, behind the same setting" was read to match the Mac's window wherever the two can match. The question about a recording that was never exported is the confirmation, and the setting does not take it away. "Close Studio?" is asked only of a project that is open and has been exported. A window that cannot show its project, or is still opening it, closes at once. Esc with Ctrl, Shift or Alt does nothing, and neither does a key that is held down, so that the Esc that stopped an export does not go on to close the window, and the Esc that answered "Keep exporting" does not stop the export. A drop-down list keeps the key only while it is open: after a choice made with the mouse the focus stays on the drop-down, and Esc must not be dead there. Esc lets go of no selection first. And one thing changed for the close button as well: while any question is open it waits for the answer. Until then it closed the window from under the "An export is still running" question when the export had ended behind it, which nobody has seen happen.
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
- **Keys during a drag.** While something is being dragged, the keys that change the project do nothing: Delete, 1 to 4, I, O, S, X, Z, R, Undo, Redo and Export. Space and the arrows still work. A layout key during a drag of the camera would take its handle away under the pointer, and Delete would take away the zoom, cut or speed change the pointer is holding, after which the rest of the drag moves the one after it. The two platforms tell a drag in different ways:
  - On the Mac, by a mouse button being held. Such a key is taken, so that the menu does not act on it either. Compiled, never run.
  - On Windows, by a drag's undo step being open: from the first change a slider makes, from the first move of the camera or of a block on a lane (past the few pixels that make a press a drag), and from the press on a trim handle or on the pad that sets where a zoom looks. The rule is in the shortcut table and unit tested (the tests were run once without it: 16 of 92 fail); the one line that tells the table of the drag is in the window and has not run. What is left: a key pressed after the pointer has gone down on the camera or on a block of a lane, and before it has moved, still acts. Delete there removes the block, and the drag then moves the one after it. One Undo puts both back.
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
- **The model's file ships since 5 October**, by the owner's decision: one file of 448 KB under the Apache License 2.0, with its notice next to it and in Settings › About. See "Person cutout".
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
- **macOS keys.** Single-key shortcuts (Space, arrows, I, O, 1 to 4) are handled by the Studio window after focused controls have passed on them, so they are not taken from text fields or focused buttons. Esc follows the app's shared rule for closing editors. Since 6 October a held Esc does nothing in the Studio window, as on Windows and as in the screenshot editor.
- **Editor windows get a Dock icon.** While a Studio window is open on macOS, Tiny Clips shows its Dock icon and menu bar, as it does for the screenshot editor.
- **macOS layout.** Files directly inside `mac/TinyClips/Studio/` use Foundation only so their logic can be unit tested anywhere. Rendering is in `Studio/Rendering/` and the UI in `Views/Studio/`.
- **Keeping a project is not an edit.** "Keep this project" is written into the project at once, where the cleanup reads it. It is not on the undo stack: undoing an edit must not let go of a project, and a project must not have to be saved for the pin to hold. The rules do not make it wait for an export to end either; in the Windows window the check box cannot be reached while one runs, like everything else under the export's overlay.
- **A screen recording saved out of a project is a copy, and an ordinary video.** The project is left as it is and knows nothing of the copy, so a draft stays a draft. The copy is made under a name of its own in the save folder and renamed when it is whole, so that no half-written video is ever seen under a video's name. It never takes the place of a file, and gets the next free name when its own was taken while it was being copied.
- **Projects that cannot be read are listed with the drafts.** What was decided is a way out for a draft the editor cannot show. A project whose `project.json` is damaged, or was written by a newer version, was in no list at all: cleanup leaves it alone and the drafts list passed over it, so its recording was exactly where only a file manager finds it. It now has a row of its own, "Unreadable project", with Save recording where the recording is there, with Delete, and without Open. This goes one step past what was asked.
- **Where an exported video is looked for on the Mac App Store build.** That build may read a save folder the user chose only once it has opened the folder with the permission it stored, which the app does the first time it needs the folder. Cleanup and the drafts list now ask for the folder first. A video in a folder the app may no longer read counts as gone, and its project is kept.
- **Switching Studio off stops cleanup.** While Studio is off its storage settings are not shown, so nothing they would delete is deleted. The switch says what is kept instead.

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

Every shipping file the branch changes, as the two reviewers of "The recording path" listed them. With Studio off each takes the path it took before, except where this says otherwise.

macOS:

- `CaptureManager`: a Studio branch where a recording starts, stops and restarts. Launch reads the switch and starts the cleanup only when it is on.
- `CaptureSettings`: five new keys (`studioPreviewEnabled`, `videoAfterRecording`, `studioDefaultLook`, `studioSourceRetentionDays`, `studioStorageCapGigabytes`). `showTrimmer` keeps its key and its meaning and is still what the recorder reads, so nothing is migrated and an older version reads what it always read. Reset All Settings clears the five as well, the switch among them.
- `MouseClickOverlayProcessor`: a click also keeps which button it was, which the overlays ignore, and can take its time from a clock that only a Studio recording passes in.
- `VideoRecorder`: remembers which sound inputs it added, for the volumes.
- `BrandingOverlayProcessor`: one function is no longer private.
- `StartRecordingPanel`, `VideoSettingsSection`, `SettingsView`: Record for Studio, the After recording choice and the Studio settings, each of them built only with the switch on. Since 5 October the switch itself is in Settings › Video for everyone, with a few lines about what Studio is. Showing it reads the project folder once, where there is one, for the line that says what is kept while Studio is off.
- `ClipsManagerWindow`: Open in Studio…, and a video that is moved to the archive keeps its link. While there are no links, nothing is looked up.
- `TinyClipsApp`, `ScreenshotEditorScene`, `TinyClipsActivationPolicy`: a Studio window counts as an editor for the Dock icon, and quitting saves open projects.
- The Xcode project: 27 new sources in both app targets and 6 test files in the test target. No new entitlement, framework or Info.plist key.
- The `Build` workflow also builds the `TinyClipsMAS` scheme, and both workflows run when `shared/studio/**` changes.
- `CHANGELOG.md`: the Unreleased section describes Studio as an early preview that is off by default, and says how to switch it on.

Windows:

- `VideoRecordingService`: the Studio mode, merged by hand on 5 October with what `main` changed in the same file. An ordinary recording reads one or two flags more for each frame, and lists the monitors once at its start also when click rings are off. One change is on purpose and not Studio's: a start forgets the video of the recording before it as its first step, so that a start that fails can no longer delete that video (see "Found in the regular Windows recorder").
- `MfSinkWriterEncoder`: three new parameters. `enableHardwareTransforms` is on by default, which is what the encoder always did; `topDownMemoryFrames` and `keepFrameTimes` are off by default. Only the Studio camera recorder sets any of them; the regular recorder's calls are unchanged.
- `RecordingTimeline`: keeps each pause, for the camera track and the events. The numbers it gives are the same.
- `MouseClickMonitor`, `MouseClickSample`: a click also keeps its button and its time on the system clock, 32 bytes where it was 16.
- `WebcamCaptureService`, `IWebcamCaptureService`, `WebcamFrameSizing`: an event for each camera frame and a switch to keep the camera's own shape. With no listener and the switch off the camera is opened as before.
- `CaptureSettings`: `VideoAfterRecording` beside `ShowTrimmer`, which stays what the stop path reads; five Studio keys; Reset writes those too.
- `App.xaml.cs`: the way to the editor after a recording, six more services, the cleanup 15 seconds after launch (never with Studio off), and `ShowTextRecognitionNotification` renamed `ShowMessageNotification` because Studio reuses it.
- Settings (General and Video), the recording setup bar and the Clips Library menu: Studio controls that are built and stay collapsed with the switch off. The Settings view model reaches Studio's part of itself, which is in a file of its own, through two partial methods. Since 5 October the switch itself is a card in Settings › General for everyone. Showing General reads the project folder once, where there is one, for the line that says what is kept while Studio is off, and makes none.
- The project store is constructed with the recorder at startup. It reads where the app's data folder is and creates nothing.
- The project file ships the model for the person cutout and its notice since 5 October: 448 KB and 14 KB more in every package, Studio on or off. Settings › About has a Third-party notices card for everyone. And three more `InternalsVisibleTo`, for the check tools.
- `.gitattributes`: `*.onnx` is marked binary, so that no checkout changes the model's bytes.
- `windows/CHANGELOG.md`: the Unreleased section describes Studio as an early preview that is off by default, and says how to switch it on. What's New links to that file.

### Found in the regular Windows recorder

One of these is mended, on this branch and since #418 on `main`. The others are left alone.

**A start that failed deleted the recording before it. Mended here and on `main`.** The recorder keeps the path of the video it last saved, for a discard that arrives after the stop. The cleanup after a failed start deletes whatever that path names, and until a start has named a file of its own, that is the previous recording: saved, finished, and already shown to its owner. The deletion says nothing, and fails only when something has the file open in a way that forbids it.

- In the released 1.8.2, and back to 1.7.4 at least, it takes a capture that cannot start. A window that was closed after it was picked would be one way; that has not been tried.
- On `main` since #411 it also takes no more than cancelling the countdown while the recorder is still getting ready. The app prepares the recorder while the countdown runs, on the same token, and the preparation now checks for cancellation three times before it names its file. How long getting ready takes has not been measured; with the camera on it includes starting the camera. This has not been released.
- With Studio there was more to lose. When a project cannot be saved, its screen recording is kept as an ordinary video or, failing that, left in its folder for a day, and both were open to the same deletion.

A start now forgets the path before it does anything else. Five unit tests, the first the recorder has, make a start fail before anything is asked of Windows: as first written on its first read of a setting, and since 6 October by cancelling it (see "The evening of 5 October"). Four of them fail without the fix: on this branch, and on `main` as it was and the code of 1.8.2, where a copy of them without Studio's parameter was run. What a run has not shown: the tests put the recorder into the state a finished recording leaves by writing its private field, since no recording could be made, and nobody has stopped a countdown in the app. Both are read from the code. The mend went to `main` as #418 on 6 October. The released 1.8.2 has the fault until the next release.

**The CPU path writes upside down.** `StudioRenderCheck` reproduces the regular recorder's CPU path without capturing anything: it creates the encoder the way the recorder does and hands it frames the way the recorder does. Since #412 that is the encoder's own `WriteVideo`, and the measurement was made again through it on 5 October, with the same result. That path is used when the GPU recording pipeline is switched off or cannot start. On the development PC (AMD encoder) the file it wrote was upside down in every frame, for H.264 and HEVC. A real recording made that way has not been looked at. A Studio screen track recorded on that path would have the same fault, and the check tool reports it as known. The Studio camera track had the same cause and is fixed with `topDownMemoryFrames`. The regular recorder was not changed, because it is shipping code outside this work. Decided on 5 October: the same fix there gets a small pull request of its own against main, once one real recording on that path has confirmed the fault. That recording has to be made with the owner at the PC, since nothing here may capture the screen.

**Click rings in a video are drawn late.** Read from the code on 5 October by a reviewer and again by me, and not run. The clock a click is stamped with starts when the recorder is prepared, which is when the countdown begins. The clock of the frames starts when the countdown is over, and leaves pauses out. A ring is drawn where the two numbers meet, so every ring comes after its click by the time between the two starts and by all the time the recording had been paused. With the countdown as it is by default, three seconds, that is close to three seconds. A click made during the countdown is drawn as a ring at the start of the video, for the same reason. It takes click rings in videos to be switched on, which they are not by default. GIF recordings start both clocks together. Studio recordings are not affected: their clicks are put on the recording's own timeline when the project is made, from a time on the system clock that every click carries since this branch, and that is what a mend of the regular recorder would use. Not changed here.

**Quitting while a recording is being finished.** Quitting waits for the recorder to stop. When a stop is already under way that wait returns at once, and the app goes while the file is still being finished. Older than this branch; a Studio recording takes longer to stop, so the moment in which it can happen is longer.

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
| Regressions in normal recording | The fast path is untouched. Studio capture is a separate mode behind the Preview switch |
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
- Opening any existing video in Studio as a single layer, with a background, padding, zooms and cuts applied to the flattened video. Both project stores can already make a project around such a video, and no command does. Part of the first design, moved here on 5 October

## Open questions

Three, from 6 October. None of them holds up work.

1. **Where should Windows keep its projects?** They are in the installed app's own folder, which Windows deletes when the app is uninstalled or reset, drafts included. Settings says so now. The other way is a folder of the user's own, under Videos for instance, which outlives an uninstall and is in plain sight.
2. **Should the storage limit spare the project opened last?** It does since 6 October, because the read-through found that one exported project larger than the limit was removed the moment its editor closed. The limit can then be passed by that one project. Decided without asking; it is one line in each platform's rules.
3. **May the check tools run while the owner is away from the PC?** They were stopped on 5 October because they use the screen and the machine. Leave was given once, for one absence. The rule I would suggest: after 15 minutes without input and with no slide show or full-screen app in front, stopping at the first key or mouse move. Until there is an answer they do not run, and every run that is owed stays owed ("Where it stands" in the pull request).

The two that were open on the evening of 5 October, Esc in the Studio window and the fix for `main`, were answered that same evening.

Everything else is settled: the name, price, cleanup defaults, and first-run look in "Decisions confirmed" above, and the questions that came up while building in "Decided on 5 October" and "Decided later on 5 October" under it.

What is left is work that waits for the PC or for a Mac, not for an answer:

- **The regular Windows recorder's CPU path** gets its own pull request once a real recording on that path has confirmed the fault. See "Found in the regular Windows recorder", which also has two faults found by reading it and left alone: click rings drawn late, and quitting while a recording is being finished.
- **Windows volumes** are a follow-up: two sound tracks in a Studio recording, made with the owner at the PC. See "Volumes".
- **Esc in the Studio window** is built on Windows, and no key has been pressed in that window yet.
- **Every control written on 5 October has been compiled and never run, and the Tiny Clips app has never been started with this code.** See "The decisions of 5 October, built", "The app around the editor: read, never run" and "The evening of 5 October".

One thing for the owner of the App Store account, which the visible switch does not settle by itself. Guideline 2.3.1(a), as it read on 5 October 2026: "Don't include any hidden, dormant, or undocumented features in your app; your app's functionality should be clear to end users and App Review. All new features, functionality, and product changes must be described with specificity in the Notes for Review section of App Store Connect (generic descriptions will be rejected) and accessible for review." The switch makes Studio visible and reachable for review. The notes for review still have to say what it is.

## Validation

- Windows changes: `dotnet restore windows/TinyClips.Windows.slnx`, `dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64`, `dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug`, plus the Store flavor build when project files change.
- macOS changes: the unit tests and both schemes (`TinyClips`, `TinyClipsMAS`), run locally or by the `Build` workflow on the pull request. A macOS item is not done until that build is green.
- Every user-facing milestone updates the root `CHANGELOG.md` (macOS), `windows/CHANGELOG.md`, both READMEs, the in-app Guide, the Windows What's New window, and `windows/docs/accessibility-release-gate.md`.

## Hands-on checklist

**Nobody has done any of this.** It is what a person with the app in front of them should do, in this order, and where a step goes somewhere no check tool has been, it says so. It was in the pull request's description until 6 October.

**Windows**

First with the switch off, against the released build, since this is what every user gets: the tray icon comes as before and there is no `TinyClips\Projects` folder in the app's data; a region recording with the webcam, click rings, both sounds and one pause looks and sounds the same; the recording setup bar has no gap where the hidden toggle is; Settings › Video looks as before, and the trimmer switch still decides what happens after a recording. Two things are new for everyone: a **Tiny Clips Studio (Preview)** card in Settings › General, and a **Third-party notices** card in Settings › About. Open the notices and see whether they can be read, with the keyboard as well. And one thing `main` has since #418, which no one has tried in the app: record a video and let it be saved, start another, and stop it while the countdown runs. The first video must still be in the save folder.

1. Settings › General: switch on **Tiny Clips Studio (Preview)**. Nobody and nothing has ever pressed this switch. Watch whether the cards under it (storage, Clean up now, the drafts) come without the page jumping or the keyboard focus going elsewhere, and whether Settings › Video now has After recording where the trimmer switch was. Later, with a project or two on the disk, switch it off: a line under it should say how many projects are kept and how much room they take, and switching it on again should bring everything back.
2. Settings › Video › After recording: Open in Studio. Record a region with the camera on, with some clicks and some sound, and move the camera to another corner while recording. This is the first recording the present code makes, so look at what it wrote before anything else (the project is a folder under `TinyClips\Projects` in the app's data, and `screen.mp4` in it plays in any player). In `project.json`, the camera's `startOffset + duration` should be at least the screen's `duration`, and there should be a second scene from the moment the camera was moved. In `events.json`, clicks in the four corners of the region should be near (0,0), (1,0), (0,1) and (1,1), also on a scaled display and on one left of the primary, and there should be none from a pause. In `camera.mp4`, the first half second should move and the last second should be there. In `webcam-diagnostics.log`, look for the size the camera delivered and for `hardware encoder unavailable`. Then unplug the camera in the middle of one recording, use a virtual camera for another, and record one with HEVC and open it.
3. In the editor: play (with sound), scrub, step frames, trim with the handles and with I and O, switch layouts with 1 to 4, drag the camera bubble, drag every inspector slider and undo it (one drag should be one undo step), change the background and the canvas shape. The corner move should be there as a second scene on the scene lane.
4. Zooms and crops: add a zoom with Z, drag it and its ends on the lane, change its scale and where it looks, switch it to follow the pointer, and try Suggest zooms. Move the four crop sliders for the screen and for the camera.
5. Before anything else in the editor, since no check has done it: play a real recording, pause it at a few places, step frame by frame through a few seconds, and watch the playhead and the time while it plays. A playhead that stands still for half a second, or a picture that steps back when you pause, is the first of the known problems on Windows. Compare a few frames around a click with the export.
6. Scenes and cuts: press S to split, give the new scene another layout with 1 to 4, switch how it is entered between a cut and a move, and drag the line between two scenes on the lane. Press X for a cut, drag it and its ends, and play over it. Close the editor right after pressing a button with the keyboard, and after playing. Then play from before a split and drag the camera, or the Size slider, while it plays: playback should stop with the first move, only the scene it stopped in should change, and one Undo should take the whole drag back.
7. Speed (worked by the window check through UI Automation, never by hand): press R, choose each of the six rates, drag the block and each of its ends on the lane, use the Start and End buttons in the Speed section, and delete it with Delete. The preview will not change its pace; the time above the timeline should. Check the smallest size of the window, both themes, and whether Left and Right inside the six rates stay there. What the reviewer that read this code would look at first: the six rates fill their three columns downwards, so the top row reads 0.25×, 1.5×, 4×; Right on 4× and Left on 0.5× probably do nothing, while every other arrow sets the rate at once, one undo step each; a rate that is refused, chosen by keyboard, leaves the focus on the refused button while the dot goes back; a speed change 10 to 25 px wide is as round as it is wide, and when selected shows little of its fill; after R the Speed section is not scrolled into view; and at a larger text size the Background choice may wrap to two columns.
8. Export. The video should appear in the save folder, in Recent captures, and in the Clips Library, with the camera upright, the sound in step, the zooms and scenes where the preview showed them, the cuts out of the picture and the sound, and a faster or slower stretch at its speed and silent.
9. Open in Studio… from the Clips Library and from Recent captures. Close a never-exported recording and try Export, Keep as draft, and Delete. Check the drafts list in Settings › General. Each row there has Open, Save recording and Delete…: save one, look for the video in the save folder, and see that the project is still listed. Try it in the narrowest Settings window, where three buttons may not fit the row.
10. Keep this project (never run): in the editor's inspector, under Project, tick it, export, and close the editor. In the project's `project.json` set `lastOpenedAt` to a date two months back, then press Clean up now in Settings › General. The project should still be there, and Open in Studio… should still be offered for its video. Untick it, set the date back once more, and Clean up now should remove the project and leave the video.
11. A project that lost its video (never run): export a project, delete the exported video in Explorer, and open Settings › General. The project should be back under Studio drafts with a line that says its exported video is gone, and Open should still work.
12. An editor that cannot show its project (never run): with Settings › General open and a draft listed, delete the first character of that draft's `project.json`, then press Open on its row. The editor should say that the project can't be opened and offer **Save the screen recording**; press it and look for the video in the save folder. Open Settings again: the draft should now be listed as a project that cannot be read, with Save recording and Delete, and without Open.
13. Camera background, with the model that is in the package now (never run in the live window, and never on webcam footage): in a recording with the camera, Camera › Background: Blur, then Remove, paused and playing. Watch the edge around hair and hands for flicker, and the processor while it plays. Then export and compare.
14. Repeat a short recording with the GPU recording pipeline off, with HEVC, and at 60 fps.
15. Dark theme, High Contrast, Narrator, and moving the window to a display with another scale.
16. The direct build as well as the Store flavor.
17. Esc (never pressed, in a window no key has been pressed in):
   - What the reviewer of this code would try first. With a project that was exported, tap Esc once, briefly: the question must still be there after the key is up, Esc again must take it away, and a third tap must bring it back. Repeat with the focus on Play, after a click on the preview, on the closed Canvas drop-down right after choosing from it with the mouse, and with its list open, where only the list should close.
   - Esc, then Enter, should close the window. Hold Esc down: the question should come and go once, and the window stay. Shift+Esc should do nothing. While the question is open, the title bar's X should do nothing until the question is answered.
   - A recording that was never exported: Esc should ask Export, Keep as draft or Delete, also with **Confirm before closing editors with Esc** switched off in Settings › General, and should ask only that. Press Esc once more while a window still says that it is opening.
   - With the confirmation switched off, Esc should close an exported project at once. In a window that cannot show its project (step 12), Esc should close it without a question, with the setting on as well.
   - During an export, Esc should stop it; keep the key held until well after the export is gone, and the window should stay. Then Esc twice quickly during an export: the question about the recording should have the keyboard, so that Enter is Export and Esc is Cancel.
   - The questions and the keyboard. Ctrl+E and at once the X: with "An export is still running" open, Enter should keep exporting and not stop the export, and so should Esc; hold Esc for a second there, and the export should go on. Then the X during an export, and wait until the export has ended behind the question: Enter, Space and Esc should each answer the question and press nothing under it, and the X should do nothing until it is answered.
   - A project that cannot be shown, with a long recording: Save the screen recording, and at once the X; and again with Esc. When the copy is done the video should be announced and listed under Recent captures.
18. What the read-through of 6 October changed (never run):
   - With **Open trimmer after recording** on, switch Studio on, choose Open in Studio, and switch Studio off again: the trimmer switch should be on, and a recording should open the trimmer. Switch Studio on once more: After recording should still say Open in Studio.
   - Export a project and look into its `project.json`: the export should have `bytes`, the size of the video. Delete the video and save any other video under its name: the project should be under Studio drafts, with the line that says its video is gone or has been changed. Put the exported video back and the project should leave the list.
   - Set the storage limit to 1 GB, export a project larger than that, and close its editor: the project should stay. With a second, older one, the older one should go.
   - With Studio switched off, rename an exported video in the Clips Library. Switch Studio on: the video should still offer Open in Studio…. With the Clips Library open, switch Studio off and choose Open in Studio… on a video that still shows it: the status line should say that Studio is switched off.
   - In Settings › General, make a copy fail (a save folder that cannot be written to), press Save recording on a draft, and at once Delete… on another row. The app must not close, and the failure should come as a notification while the question is open.
   - The Project storage card should say that uninstalling deletes the projects.

**macOS**

First with the switch off, against the released build: no `Application Support/TinyClips` folder appears (in the container for the App Store build; the folder named after the bundle identifier belongs to the single-instance lock, not to Studio), no Studio menu, and none of the five Studio keys in `defaults read`; a region, a window and a full screen recorded with rings, camera, both sounds and a pause look and sound the same; a GIF with rings; a screenshot with Show in Dock off (the Dock icon should come with the editor and go with it); the start panel is the same size with the same controls and VoiceOver order; Settings › Video has the old trimmer switch and one new one, Tiny Clips Studio (Preview), with nothing else of Studio; the Clips Manager with a few hundred videos scrolls as before and has no Open in Studio…; a video from Recent Captures opens the trimmer; quitting leaves no crash report. Reset All Settings also switches Studio off.

1. Settings › Video: switch on **Tiny Clips Studio (Preview)**. Nobody has ever pressed it. The rest of the Studio settings should appear under it, and After recording should take the place of the trimmer switch.
2. Steps 2 to 4, 6, 7 and 9 to 12 above (the switch, the drafts list and its buttons are under Settings › Video; the project files are in `Application Support/TinyClips/Projects`). Also check the compositor's colours against the source recording, and that the preview refreshes while paused and dragging.
3. Scenes: the corner move from the recording should be a second scene on the lane. Press S to split, give the new scene another layout, and switch how it is entered between a cut and a move. Drag a scene's start on the lane.
4. Cuts: press X, drag the cut and its ends, and play over it. A few frames of the cut may show before the jump.
5. Speed: press R, pick each rate, and play over the stretch. It should be silent there and the playhead should keep up. Try 8× and 0.25×.
6. Volumes: record with the computer's sound and the microphone, and move each slider while playing. Then mute.
7. Camera background: Blur, then Remove, in each layout and each camera shape, paused and playing, and right after a seek. This is the code most likely to need work.
8. Export a project with all of these and compare it with the preview. Check that the sound is in step after a stretch at another speed.
9. What the two reviewers could not settle by reading, most likely first:
   - Record 20 seconds with microphone and camera, keep the pointer still for the last 10, and stop with the hotkey. The editor's length, where playback stops, and the export's length should agree, with the last screen frame held to the end. This is the fix most likely to need work: it leans on AVFoundation accepting a 1/600 second piece stretched over the gap.
   - Put a 2× speed change in the middle with a clap after it, export, and check the clap against the lips.
   - While paused, move Padding: the picture must change on every step. While playing, drag a volume slider and watch for hitches.
   - With Remove on, pause, click far along the timeline, and compare the edge of the cutout with the same frame reached by playing.
   - Drag the playhead fully right: the last frame, not black.
   - Keys with nothing focused, and again after clicking a slider, a picker, and inside the inspector: Space, the arrows, I, O, S, X, R, Z, 1 to 4, Delete, Esc, ⌘Z, ⇧⌘Z, ⌘E, ⌘W.
   - Hold Esc down with a project that was exported: the question should come once, and not again while the key is held. Close a window in front of an editor that is exporting with Esc and keep the key held: the export should go on.
   - Stop a recording while another app is in front: the editor should come to the front with its menu.
   - The question that closing asks has no Cancel. Choose Export from it, click close again, and leave the second alert open until the export ends; then press each button.
   - Quit while an export runs, and quit within half a second of an edit: the edit should be kept; the export is cut off.
   - Record with the microphone denied, and with the computer's sound muted throughout: each volume slider should move the right sound.
   - Pause a recording for five seconds with the camera on, then check lips and click rings after the pause.
   - A region on a 1× display placed left of or above a 2× one: check the click rings.
   - A thirty-minute recording: time how long the editor takes to open. A 4K recording at 60 frames a second: watch the Stop panel for stutter.
   - Memory after closing an editor that exported a project with scene changes.
   - Compare one exported frame with the same frame of `screen.mp4` for color.
10. What the third reviewer, of the editor's views, would try first, and what its fixes need:
   - With two scenes, play from before the split and drag the camera, or the Size slider: playback should stop with the first move and only that scene should change.
   - Hold the camera's handle and press 1, then let go: nothing should happen. Hold a zoom on its lane and press Delete: nothing. Then choose another canvas shape: the preview should change shape.
   - Drag any inspector slider and press ⌘Z once: the value should be back where the drag began, not one step short of it.
   - With Full Keyboard Access on, the arrows on a focused slider must not step a frame as well.
   - Move a slider by arrow key and by VoiceOver: a step under half of the slider's own is undone by its notches, and Horizontal offset may move in jumps of several percent.
   - Scroll the inspector with the pointer over a slider: no value should change.
   - Press the scene blocks at the right of the scene lane; the lane may be only as wide as its widest block.
   - Hover over and drag the camera's handle while the picture plays under it.
   - Watch the processor while playing with the inspector open: the whole window is worked out again up to 60 times a second.
   - The Layout control should fit the inspector, also with scroll bars set to always show.
   - Drag the camera near a corner, then choose that same corner under Position: the offsets should clear.
   - Hold a stepper's arrow for a few seconds, then count the ⌘Z it takes to get back: one for each repeat, which is left as it is.
11. What the read-through of 6 October changed (written without a compiler, never run):
   - With **Open trimmer after recording** on, switch Studio on, choose Open in Studio (Preview), and switch Studio off again: the trimmer toggle should be on and a recording should open the trimmer, with Save immediately as it was. Switch Studio on once more: After recording should still say Open in Studio.
   - Settings › Video, Studio Drafts: with a draft open in an editor its Delete… should be unavailable, and become available when the editor closes, without Settings being closed in between. Delete… should ask in an alert in which Return does nothing and Esc cancels.
   - Save a draft's screen recording: "Saved as" and the name should appear under the list, and the video should be at the top of the Clips Manager, dated today, and not in the archive.
   - With VoiceOver, press Clean Up Now: the result should be spoken.
   - Export a project, delete the video, and save another video under its name: the project should be under Studio Drafts with the line that says its video is gone or has been changed.
   - With Studio switched off, let the Clips Manager archive an exported video (or set Archive after to one day): after switching Studio on, the archived video should still offer Open in Studio….
   - Switch Studio off while an editor is open, then close the editor: nothing should be cleaned up.
