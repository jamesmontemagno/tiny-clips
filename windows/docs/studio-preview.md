# Studio live preview

The picture in the Studio editor: both clips of a project decoded and drawn live. The contract is
`IStudioPreview`; the design comes from `windows/spikes/StudioEngineSpike/FINDINGS.md`.

## Pieces

| Piece | What it is |
| --- | --- |
| `StudioPreviewEngine` (Core, `Studio/Preview`) | The `IStudioPreview`. One frame-server `MediaPlayer` per clip on one `MediaTimelineController`; each copies its frames into a texture; a render thread draws the scene with `StudioSceneRenderer` on the engine's own `StudioGraphicsDevice`, always under that device's `Gate`. |
| `StudioPreviewFactory` | The `IStudioPreviewFactory`. What `OpenAsync` returns is the engine, which a surface needs. |
| `StudioPreviewSeekPolicy`, `StudioPreviewPosition`, `StudioPreviewTimeline`, `StudioPreviewCopyTargets` | Pure and unit tested: when the clock is moved and started (the class comment lists the rules and what was measured for each), which frame is reported as the position, frame arithmetic, and the size of the textures the players copy into. |
| `IStudioPreviewSurface`, `StudioPreviewPanel` (App, `Controls/Studio`) | What the engine draws into: it asks for a texture and its pixel size, draws, and tells the surface to present. The panel is a `SwapChainPanel` with a composition swap chain on the engine's device, sized in physical pixels. |

## What it does

- `Position` is always the start of a frame of the screen clip. When `Seek` returns it is the
  frame that contains the time, clamped, and until the picture has got there nothing else is
  reported: no frame shown before the call is reported after it. Then it is the frame shown.
- A paused `Seek` shows its frame on both clips; the picture changes once, when both are on it.
  One position change is in flight at a time and the newest request wins. A seek whose frame
  does not come is repaired by going to another frame and back. One frame forward steps the players.
- `Seek` then `Play` starts at the seek's frame. `Seek` while playing stops the clock, goes there
  and plays on. At the end playback stops by itself, and `Position` is the last frame at once.
- When `Pause` returns, the clock is stopped, what the players had handed over is drawn, and
  `Position` is the frame the picture stays on. The first position change after the clock ran
  holds the picture until the players have been quiet for 40 ms: a player can first hand over
  the frame it had ready for playback, with the new position on it.
- `UpdateProject` swaps the project and asks for a redraw; calls are coalesced. Sources and
  `Edits` are ignored: the caller applies the trim. The camera is hidden outside its own time
  range. The screen clip plays its sound unless `project.Audio.Muted`; the camera never does.
- A lost graphics device is rebuilt once, at the same position; if that fails, or a player stops
  decoding, `Failed` is raised, once. Without graphics hardware the device is WARP.

## Rules for a caller

- Every member of the engine may be called from any thread. Events are raised one at a time on a
  thread of the engine's own, never under a lock: switch to the UI thread before touching
  controls. A handler may call the engine, including `DisposeAsync`.
- `PositionChanged` is raised when `Position` changes (at a `Seek` that asks for another frame,
  for each frame played, at the end) and once more when a seek has landed, after its picture was
  drawn. `IsPlaying` is what was asked for: it changes inside `Play` and `Pause`, and at the end.
- `OpenAsync` throws `FileNotFoundException` when the screen file, or the camera file of a project
  with a camera, is missing, and `InvalidDataException` when a file cannot be decoded.
- When `DisposeAsync` completes, the players are closed, the media files are no longer open (the
  project folder can be deleted), the surface has released its swap chain, and no event follows.
- `StudioPreviewPanel` is used on the UI thread. Give it a rectangle with the canvas' aspect on
  whole pixels. XAML placed in it is drawn over the picture. Unloading it detaches it from the
  engine; loading it again attaches it again.

```csharp
await session.LoadAsync();                            // StudioEditorSession opens the preview
panel.Attach((StudioPreviewEngine)session.Preview!);  // UI thread, once the session is ready
panel.Detach();                                       // UI thread, when the window closes
await session.CloseAsync();                           // disposes the preview; then the folder may go
```

## Checking it

```powershell
dotnet build windows\tools\StudioPreviewCheck\StudioPreviewCheck.csproj -c Debug -p:Platform=x64
windows\tools\StudioPreviewCheck\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\StudioPreviewCheck.exe
```

It needs ffmpeg and ffprobe on `PATH`, takes about ten minutes, makes no sound, and exits with 1
when a check fails; its README says more. Unit tests: `StudioPreview*Tests` in `TinyClips.Core.Tests`.

## Measured

Five full runs (205 checks each) on an AMD Radeon 860M, 16 logical processors, 150 % scale, doing
other work; 1920x1080 screen and 1280x720 camera at 30 fps; renderer of commit `e58a56c`.

| | |
| --- | --- |
| Paused `Seek` to the picture drawn, 300 random positions | mean 68–72 ms, p95 106–121 ms, max 205–507 ms; all pixel-exact, one picture change each |
| Step forward one frame | mean 10 ms, p95 17 ms (as a seek: 58–63 ms; a step back: 61–63 ms). 7 of 300 single steps were made as seeks, because a stepped player had not yet drawn its frame again for the clock |
| `Seek` + `Play` to the first frame drawn | mean 53–61 ms; `Seek` while playing: 153–164 ms. From the last frame, or after playback ran into the end: 115–183 ms, because a player at the end of its stream is first sent to another frame |
| `Position` after `Seek`, read in a handler, on a second thread and without pause | 316 repetitions a run; no frame from before the call in 24,962 reads by the first two and 11.8 billion by the third |
| `StudioEditorSession` on the engine | Space at the end of the kept range replays it in 200 of 200 (with `Position` as it was before that guarantee, 16 of 40 were sent back at once); its delete after closing succeeds at the first attempt in 60 of 60 |
| Playback at 1x, 10 s | every frame drawn; clips one frame apart 0.02–0.21 % of the time, never more |
| `Pause()` call | mean 1.3–2.2 ms, max 9–32 ms. The picture stayed after it in 850 of 850 pauses; without the 40 ms rule the next frame flashed in 5 of 920 |
| `UpdateProject` to the scene drawn | mean 1.2–1.4 ms, max 2–8 ms; 200 calls in a row draw 2 scenes |
| Open; `DisposeAsync` | mean 326–343 ms, max 488–1035 ms; mean 29–31 ms, max 70–89 ms |
| 20 open/close cycles | GPU memory and threads flat; about 12 kernel handles stay per cycle (Windows: two bare `MediaPlayer`s on a `MediaTimelineController`, used the same way, leave 11) |
| On WARP | the same checks pass; a paused seek takes 259–261 ms on average, a step 18–19 ms |

**Open problem on the software adapter.** After the preview was merged with the editor window, one
more full run passed 199 of 200 checks: the open on WARP failed with `SourceNotSupported`. Twenty
further runs of the `device` and `software` groups together failed twice, once with the same failed
open and once with the picture on frame 1 instead of frame 0 after opening. The `software` group
alone passed three times out of three, and no check on the graphics hardware failed in any of
these runs. The cause is not known. On this PC the WARP checks are a mix that a real PC does not
have, with the players decoding on the graphics hardware and the engine drawing on WARP, so the
fault may not occur on a PC without graphics hardware. That has not been shown.

Not measured: audible playback and audio sync (every check runs muted), a real device loss or
display scale change, a PC without graphics hardware, the app's own recordings, the packaged app.
