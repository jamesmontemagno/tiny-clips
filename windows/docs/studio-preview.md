# Studio live preview

The picture in the Studio editor: both clips of a project decoded and drawn live. The contract is
`IStudioPreview`; the design comes from `windows/spikes/StudioEngineSpike/FINDINGS.md`.

## Pieces

| Piece | What it is |
| --- | --- |
| `StudioPreviewEngine` (Core, `Studio/Preview`) | The `IStudioPreview`. One frame-server `MediaPlayer` per clip on one `MediaTimelineController`; each copies its frames into a texture; a render thread draws the scene with `StudioSceneRenderer` on the engine's own `StudioGraphicsDevice`, always under that device's `Gate`. |
| `StudioPreviewFactory` | The `IStudioPreviewFactory`. What `OpenAsync` returns is the engine, which a surface needs. |
| `StudioPreviewSeekPolicy`, `StudioPreviewPosition`, `StudioPreviewTimeline`, `StudioPreviewCopyTargets`, `StudioPreviewProof`, `StudioPreviewOpenFailure`, `StudioPreviewFiles` | Pure and unit tested: when the clock is moved and started (the class comment lists the rules and what was measured for each), which frame is reported as the position, frame arithmetic, the size of the textures the players copy into, when the pictures of players that came from another graphics adapter are believed, which failures of an open are worth a second attempt, and which files a closing preview waits for. |
| `IStudioPreviewSurface`, `StudioPreviewPanel` (App, `Controls/Studio`) | What the engine draws into: it asks for a texture and its pixel size, draws, and tells the surface to present. The panel is a `SwapChainPanel` with a composition swap chain on the engine's device, sized in physical pixels. |

## What it does

- A preview opens paused on frame 0, with every clip on it. That picture is the answer to a
  seek the engine makes itself, by way of another frame: what a player hands over when it has
  just opened is not always a picture (see *Players and graphics adapters*).
- `Position` is always the start of a frame of the screen clip. When `Seek` returns it is the
  frame that contains the time, clamped, and until the picture has got there nothing else is
  reported: no frame shown before the call is reported after it. Then it is the frame shown.
- A paused `Seek` shows its frame on both clips; the picture changes once, when both are on it.
  One position change is in flight at a time and the newest request wins. A seek whose frame
  does not come is repaired by going to another frame and back. One frame forward steps the players.
- `Seek` then `Play` starts at the seek's frame. `Seek` while playing stops the clock, goes there
  and plays on. At the end playback stops by itself, and `Position` is the last frame at once.
- When `Pause` returns, the clock is stopped, what the players had handed over is drawn, and
  `Position` is the frame the picture stays on (one case in which it is not is the open problem
  at the end). The first position change after the clock ran holds the picture until the
  players have been quiet for 40 ms: a player can first hand over the frame it had ready for
  playback, with the new position on it.
- `UpdateProject` swaps the project and asks for a redraw; calls are coalesced. Sources and
  `Edits` are ignored: the caller applies the trim. The camera is hidden outside its own time
  range. The screen clip plays its sound unless `project.Audio.Muted`; the camera never does.
- A lost graphics device is rebuilt once, at the same position. If that fails, or a player stops
  decoding, `Failed` is raised, once. Without graphics hardware the device is WARP.
- A preview that fails while it opens is opened once more when what went wrong may pass: a
  graphics device was lost, or a player failed after every player had handed over a frame, which
  shows that the files can be decoded. Anything else makes `OpenAsync` throw at once.

## Rules for a caller

- Every member of the engine may be called from any thread. Events are raised one at a time on a
  thread of the engine's own, never under a lock: switch to the UI thread before touching
  controls. A handler may call the engine, including `DisposeAsync`.
- `PositionChanged` is raised when `Position` changes (at a `Seek` that asks for another frame,
  for each frame played, at the end) and once more when a seek has landed, after its picture was
  drawn. `IsPlaying` is what was asked for: it changes inside `Play` and `Pause`, and at the end.
- `OpenAsync` throws `FileNotFoundException` when the screen file, or the camera file of a project
  with a camera, is missing, `InvalidDataException` when a file cannot be decoded, and
  `InvalidOperationException` when there is no graphics device to draw with or it was lost at
  both attempts.
- When `DisposeAsync` completes, the players are closed and have let go of the media files in
  the project folder (the folder can be deleted), the surface has released its swap chain, and no
  event follows. While another program has one of those files open the engine cannot tell whose
  it is, and completes after 5 s. A screen recording outside the project folder
  (`Sources.Screen.External`) is the user's own video: the engine does not wait for it, and
  never opens it without sharing to see whether a player still has it.
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

Five full runs on an AMD Radeon 860M, 16 logical processors, 150 % scale, doing other work;
1920x1080 screen and 1280x720 camera at 30 fps; renderer of commit `e58a56c`. The three rows on
opening and on WARP are from the four full runs (215 checks each in the last two) and the loops
of opens that were made after the way a preview opens was changed: see *Players and graphics
adapters*.

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
| Open; `DisposeAsync` | mean 328–339 ms in three runs and 481 ms in one made while the machine was busier, max 736–1088 ms; mean 27–48 ms, max 71–229 ms. By project, over 900 opens: 334 ms with the camera late, 327 ms with the camera from the start, 278 ms without a camera. The last two took 90–100 ms less before the first position was spent on every open |
| The first picture after opening, on the hardware | no scene with anything but the first frames in 1,380 opens made in a row, nor in the 24 of each full run. With the engine as merged, a blank screen picture was drawn for a moment in 92 of 520 opens of a project without a late camera |
| 20 open/close cycles | GPU memory and threads flat; about 12 kernel handles stay per cycle (Windows: two bare `MediaPlayer`s on a `MediaTimelineController`, used the same way, leave 11) |
| On WARP, on this PC, which has graphics hardware | the same checks pass; a paused seek takes 259–296 ms on average, a step 19 ms, an open 0.9–1.0 s. 1,896 opens: none failed and none showed a wrong picture. With the engine as merged: 38 failed and 8 showed a wrong picture in 260 with the camera late |

## Players and graphics adapters

After the merge the checks on the software adapter failed in 3 of 24 runs: twice the preview did
not open (`SourceNotSupported`), once it opened on frame 1 instead of frame 0. The cause, measured
with `StudioPreviewCheck --investigate players`, `decoding` and `opens` on a PC with one graphics
adapter (AMD Radeon 860M):

A `MediaPlayer` decodes on a Direct3D device of its own, which it creates on the default adapter
when it opens. Nothing tells it where its frames are wanted until its first
`CopyFrameToVideoSurface`.

| The texture it copies into is | What the player does |
| --- | --- |
| on the adapter the player is on | The first copy is the picture and nothing is rebuilt: 14 of 14 on the hardware, and 16 of 16 for a player that had settled on one device and was given a texture on a second device of the same adapter, hardware or WARP. |
| on another adapter: the player on the hardware, the texture on WARP | The player moves over. Its first copy leaves the texture transparent (14 of 14). It rebuilds its pipeline at the next position change, and its answer to that comes about 300 ms later and is black (32 of 32). Until then a copy can be empty, or a frame or two ahead of the position it reports. After that its frames are right and it no longer decodes on the hardware: while two clips play, the hardware's video engines are idle and the process uses 1.2–1.4 processor cores, against 0.1. |
| on another adapter, while another player is not ready | A player that is still opening fails with `MF_E_INVALIDMEDIATYPE` (`MediaFailed`, `SourceNotSupported`) 0.4–7 ms after the other's first copy. One that has opened and has no frame yet never gets one. Two players that each have a frame ready come through (20 of 20). |

The failed opens were the third row: the camera's first frame was copied while the screen clip
was still opening. The picture on frame 1 was the second: the picture was let go on a copy from
the middle of the move. Both need the players on one adapter and the engine on another, and on
this PC only the WARP checks make that: `SoftwareDevice` puts the engine on WARP, and the players
start on the hardware as they always do. With the engine as merged (`--trust-first-frames`), 38
of 260 such opens failed and 8 showed a wrong picture with the camera 0.2 s late; 8 of 30 failed
with the camera from the start; and every preview without a late camera that did open showed a
blank picture (52 of 52), which the checks on WARP had not tried.

**On the graphics hardware** players and engine are on one adapter, and neither failure occurs:
no open failed that way and no picture was on a wrong frame in 2,400 opens, 900 of them with the
engine as merged. Two other things were found there:

- A blank frame just after opening. With the engine as merged, a scene with a blank screen
  picture was drawn within 60 ms of the open in 92 of 520 opens of a project whose camera does
  not start late; the right scene followed within 20 ms. The player's first frame, or its answer
  to the first position, was blank. A project with a late camera was not affected (0 of 260):
  its first position was already spent on another frame, because of the offset.
- Opens that failed for no lasting reason: three in about 5,000 previews opened one straight
  after the other. Twice a player's first copy took 93 ms and failed with
  `DXGI_ERROR_DEVICE_REMOVED`; no display driver event was logged. Once the camera's player
  failed (`DecodingError`, `MF_E_INVALIDREQUEST`) during the first position change, after it had
  handed over its first frame. The open that followed each succeeded. The causes are not known.

What the engine does about them:

- The first position after opening is spent on another frame, on every adapter and for every
  project, so the first picture is one the players were sent to. It adds 90–100 ms to the open
  of a project without a late camera.
- On the software adapter (`StudioGraphicsDevice.IsSoftware`), the one case in which the engine
  can know that the players may be elsewhere, it takes no first frame until every player has
  one, and looks at what each first copy left. If one left nothing, the players are sent
  between two frames until two rounds in a row leave the same pictures in their textures, and
  only then to frame 0. A player that does not offer its first frame again has it taken.
- A preview that fails while it opens is opened once more, with new players on a new device,
  when what went wrong may pass: a graphics device was lost, or a player failed after every
  player had handed over a frame. A file no frame comes out of fails at the first attempt, as
  before. All of that is checked with failures the checks make themselves. Every run of the
  checks ends with the number of previews that needed a second attempt, and keeps their traces.

The run that failed, the `device` and `software` groups together, started after two to five
minutes without a run: 64 of 64 passed, the last 32 with the engine as it is now, and none of
their previews needed a second attempt. With the engine as merged, 12 of 12 failed the checks as
they are now, and 3 of those 12 the checks as they were (one open failed, two first pictures
were wrong).

Not verified, because this PC cannot produce it:

- **A PC without graphics hardware.** There a device asked for on the hardware is the Basic
  Render Driver, for the players as for the engine, so the first row of the table should apply
  and the first frames should not come out empty. The nearest evidence is the second WARP device
  of that row (8 of 8). The `software` group says in its first line which kind of PC it ran on,
  and below the open what the players' first frames looked like.
- **A PC with two graphics adapters**, should a `MediaPlayer` there start on another adapter than
  the one `D3D11CreateDevice` gives the engine. The engine would be on hardware and would not
  look at the first frames. `--investigate players` and `--investigate opens --device hardware`
  show what such a PC does.
- **A real device loss**, and a device rebuilt on another adapter than the one that was lost.

Also not measured: audible playback and audio sync (every check runs muted), a real display
scale change, the app's own recordings, the packaged app.

## Open problem: `Position` one frame ahead of the picture after `Pause`

Which frame a texture holds is known only from the player's position at the moment the player
announces the frame. A frame that is announced late while the clock runs is taken for the next
one. While playing, the frame after it puts that right. When `Pause` comes at that moment,
`Position` stays one frame ahead of the picture until the next seek: the clock is then set to the
middle of the frame `Position` names, which is inside the frame the player already believes it
shows, so the player hands nothing over.

Seen once, in the pause check of a quick run: the render thread had stalled for 139 ms on a busy
machine, both players were behind, and `Pause` came 7 ms after the stall ended. `Position` was
frame 45 and the picture frame 44, on both clips. That is one pause in about 500 of that check
over the runs so far. It is the engine as merged; the way a preview opens has nothing to do
with it. Not changed here: the cure is to send the players to another frame and back after every
pause, two position changes with the picture held, and that is a decision about every pause.
