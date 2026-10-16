# Studio live preview

The picture in the Studio editor: both clips of a project decoded and drawn live. The contract is
`IStudioPreview`; the design comes from `windows/spikes/StudioEngineSpike/FINDINGS.md`.

## Pieces

| Piece | What it is |
| --- | --- |
| `StudioPreviewEngine` (Core, `Studio/Preview`) | The `IStudioPreview`. One frame-server `MediaPlayer` per clip on one `MediaTimelineController`; each copies its frames into a texture; a render thread draws the scene with `StudioSceneRenderer` on the engine's own `StudioGraphicsDevice`, always under that device's `Gate`. |
| `StudioPreviewFactory` | The `IStudioPreviewFactory`. What `OpenAsync` returns is the engine, which a surface needs. |
| `StudioPreviewSeekPolicy`, `StudioPreviewFrameNamer`, `StudioPreviewStillness`, `StudioPreviewPosition`, `StudioPreviewTimeline`, `StudioPreviewCopyTargets`, `StudioPreviewProof`, `StudioPreviewOpenFailure`, `StudioPreviewFiles`, `StudioPreviewFrameTimes` | Pure and unit tested: when the clock is moved and started, and which frame a player hands over while it runs (each class comment lists its rules and what was measured for them), over which frames the scene comes out the same whichever of them the picture is, which frame is reported as the position, frame arithmetic, the size of the textures the players copy into, when the pictures of players that came from another graphics adapter are believed, which failures of an open are worth a second attempt, which files a closing preview waits for, and when each frame of a file begins, read from the file's index (switched off: see *Counting in frames of the file*). |
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
  and plays on; the picture stays as it is until the frame asked for is there. At the end
  playback stops by itself, and `Position` is the last frame at once.
- While the clock runs, the engine works out which frame each player hands over: a player does
  not say (see *Which frame a texture holds*). A frame it can tell is drawn and reported in one
  go. A frame it cannot tell is reported to nobody, and drawn only where the scene comes out
  the same whichever frame it is.
- When `Pause` returns, the clock is stopped, `Position` is the frame the screen's picture
  shows, and `Position` does not change afterwards: a frame of that playback which has not
  reached the picture by then never does, and neither does one a player hands over once the
  clock has been stopped. A camera that stopped a frame apart is then brought onto the frame
  that goes with the screen's. The first position change after the clock ran holds the
  picture until the players have been quiet for 40 ms: a player can first hand over the frame
  it had ready for playback, with the new position on it.
- `UpdateProject` swaps the project and asks for a redraw; calls are coalesced. Sources and
  `Edits` are ignored: the caller applies the trim. The camera is hidden outside its own time
  range. The screen clip plays its sound unless `project.Audio.Muted`; the camera never does.
  The screen clip's player is at the project's volume (`audio.volume`, as it is used: 0 to 1),
  set when the preview opens and whenever the project changes, also while it plays. The
  options a check runs with (`ForceMuted`, `ZeroVolume`) keep every player at zero instead.
- A camera whose background is blurred or removed (`camera.cutout`) has its people found by the
  renderer, with the model the app ships. The engine gives the renderer every frame without
  saying which picture it is (`StudioGpuVideoFrame.Stamp` is 0), so the people are looked for
  afresh at every draw: also at every redraw of a paused preview, which is every move of a
  slider. The engine can say which picture a frame is, with the count of pictures that have been
  put into the clip's textures, which changes exactly when the picture does and is never 0; the
  renderer then looks at a picture once (`StudioPreviewOptions.StampPictures`). **That is
  switched off.** It was written on 5 October, nothing has run with it, and a stamp that stayed
  while the picture changed would show an old camera frame. `StudioPreviewCheck --only people`
  switches it on and decides, with the faults made against it.
- Everything plays at the recording's own speed. `SetPlaybackRate` is not built: see *Other
  speeds* below.
- A lost graphics device is rebuilt once, at the same position. If that fails, or a player stops
  decoding, `Failed` is raised, once. Without graphics hardware the device is WARP. After a
  rebuild the scene is drawn again when every clip that is part of it has its picture back. A
  camera outside its own time range is not waited for: its player is parked and hands nothing
  over, and the scene has no use for it there. (Read from the code and mended on 5 October.
  The check for it was first run that evening, with the mend in, and passed. It has not been
  run without the mend, which is the only thing that would show that it catches the fault.)
- A preview that fails while it opens is opened once more, 750 ms later, when what went wrong
  may pass: a graphics device was lost, a player failed after every player had handed over a
  frame, which shows that the files can be decoded, or a player said before that that it could
  not decode what it had opened (`DecodingError`). A file that is not a video, or that no frame
  comes out of, makes `OpenAsync` throw at the first attempt.

## Rules for a caller

- Every member of the engine may be called from any thread. Events are raised one at a time on a
  thread of the engine's own, never under a lock: switch to the UI thread before touching
  controls. A handler may call the engine, including `DisposeAsync`.
- `Pause` and `Play` may follow each other at once. `Pause` waits for the render thread only so
  long (a tenth of a second for a pass and half a second for the round it is in); when it has
  given up and `Play` comes before the render thread has looked, the render thread finds a
  clock that `Pause` stopped and playing wanted, and starts over from the frame shown. (Until
  5 October it found nothing changed, and the preview stood still with `IsPlaying` true until
  the next pause or seek. Read from the code and mended. The check for it was first run that
  evening, with the mend in, and passed. It has not been run without the mend, which is the
  only thing that would show that it catches the fault.)
- `PositionChanged` is raised when `Position` changes (at a `Seek` that asks for another frame,
  for each frame played, at the end) and once more when a seek has landed, after its picture was
  drawn. While playing, `Position` never goes back, and it moves only to a frame the engine can
  tell: on a PC that is held up it can stand still while the picture goes on, for a quarter of
  a second in the checks and for half a second at the most, after which the frames are
  reported by their position. `IsPlaying` is what was asked for: it changes inside `Play` and
  `Pause`, and at the end.
- Take `Position` after `Pause` has returned, not before: it is then the frame the picture
  stays on, and it is final. Two things can still happen to the picture afterwards, and neither
  changes `Position`. A camera that stopped a frame apart is brought onto the frame that goes
  with the screen's. And a frame whose number the engine had inferred is fetched anew, which
  changes the picture only if the inference was wrong. On a PC that is held up, `Pause` can
  take the picture back to the last frame the engine could tell: *Which frame a texture holds*
  says how often and how far.
- `OpenAsync` throws `FileNotFoundException` when the screen file, or the camera file of a project
  with a camera, is missing, `InvalidDataException` when a file cannot be decoded, and
  `InvalidOperationException` when there is no graphics device to draw with or it was lost at
  both attempts. What a player said is underneath: its error code is the `HResult` of the
  `InnerException` (`0xC00D36C4` for a file that is not a video), and of
  `StudioPreviewFailedEventArgs.Exception` when a player fails after the preview has opened.
  A file that no frame comes out of has no inner exception: no player said anything.
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

It needs ffmpeg and ffprobe on `PATH`, takes about fifteen minutes, makes no sound, and exits
with 1 when a check fails; its README says more, also about the options that hold the process up
while it checks and about the ones that bring an old fault back to show that a check notices.
Unit tests: `StudioPreview*Tests` in `TinyClips.Core.Tests`.

## Measured

Six full runs of 299 checks each, on an AMD Radeon 860M, 16 logical processors, 150 % scale,
with other check tools and builds taking their turns on the PC; 1920x1080 screen and 1280x720
camera at 30 fps. **Every clip has one frame exactly at the start of every thirtieth of a
second, which a recording made by the app has not:** see *A recording's own frame times* below
before relying on any number here for a real recording. The six runs were made in the engine's
own copy, before it was merged with the editor window's scene and cut controls, and the numbers
here are theirs. On the merged code there has been one full run, on 5 October at commit
`c81a8ea`: all of its 305 checks passed, and of the 146 previews it opened none needed a second
attempt. What is said of opens by project, of the first picture and of opens on WARP beyond a
full run's own is from the loops of opens made when the way a preview opens was changed: see
*Players and graphics adapters*. Pauses and scenes while the process is held up are under
*Which frame a texture holds*, cuts and scene changes in the last section.

| | |
| --- | --- |
| Paused `Seek` to the picture drawn, 300 random positions | mean 67–70 ms, p95 108–116 ms, max 208–282 ms; all pixel-exact, one picture change each |
| Step forward one frame | mean 9–10 ms, p95 16–17 ms (as a seek: 58–62 ms; a step back: 60–70 ms). Of the 60 single steps of a run, with the clock brought along after each, none or one was made as a seek, because a stepped player had not yet drawn its frame again for the clock |
| `Seek` + `Play` to the first frame drawn | mean 54–64 ms; `Seek` while playing: 153–161 ms. From the last frame, or after playback ran into the end: 117–196 ms, because a player at the end of its stream is first sent to another frame |
| `Position` after `Seek`, read in a handler, on a second thread and without pause | 316 repetitions a run; no frame from before the call in 5,000 reads by the first two and 2.3 billion by the third, in every run |
| `StudioEditorSession` on the engine | Space at the end of the kept range replays it in 40 of 40 (with `Position` as it was before that guarantee, 16 of 40 were sent back at once); after its pause `Position` is the frame the picture stays on and a step shows the next, 20 of 20; its delete after closing succeeds at the first attempt, 12 of 12; the same in every run |
| Playback at 1x, 10 s | every frame drawn; clips one frame apart at most 0.15 % of the time, never more |
| `Pause()` call, nothing in the way | mean 0.8–2.7 ms, max 9–17 ms. No other frame was shown after any of the 170 pauses of a run |
| `UpdateProject` to the scene drawn | mean 1.1–1.3 ms, max 2–6 ms; 200 calls in a row draw 2 scenes |
| Open; `DisposeAsync` | mean 352–370 ms, max 925–1188 ms; mean 29–33 ms, max 54–102 ms. None of the 144 previews of a run needed a second attempt. By project, over 900 opens: 334 ms with the camera late, 327 ms with the camera from the start, 278 ms without a camera |
| The first picture after opening, on the hardware | no scene with anything but the first frames in 1,380 opens made in a row, nor in the 24 of each full run. With the engine as first merged, a blank screen picture was drawn for a moment in 92 of 520 opens of a project without a late camera |
| 20 open/close cycles | GPU memory and threads flat; about 12 kernel handles stay per cycle (Windows: two bare `MediaPlayer`s on a `MediaTimelineController`, used the same way, leave 12) |
| On WARP, on this PC, which has graphics hardware | the same checks pass; a paused seek takes 253–266 ms on average, a step 19 ms, an open 0.85–1.1 s. 1,896 opens: none failed and none showed a wrong picture. With the engine as first merged: 38 failed and 8 showed a wrong picture in 260 with the camera late |

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
- Two editors in a row that did not open, in the editor window's own checks: `DecodingError`
  both times, each within about 0.3 s of being asked for, 0.42 s apart; the third, 0.41 s after
  the second, opened. Only the sentence was kept. To make it happen again, 7,900 previews were
  opened in those circumstances (`--investigate reopens`): 0 to 20 ms after another had been
  closed, while another was closing, and beside one that stayed open, at rest or playing, each
  with a surface attached. One did not open: 0.26 s into the open a copy of the screen's
  player failed with `DXGI_ERROR_DEVICE_REMOVED`, its first copy having taken 0.1 s; and at
  the second attempt, which the engine then made at once, that player's first copy took 86 ms
  and failed the same way, 0.3 s after the first failure. The preview opened next was fine,
  with its first copies a quarter of a second after that; again no display driver event was
  logged. No `DecodingError` came up. Nor is it the number of players: 40 previews open at
  once, 67 players, all opened.

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
  when what went wrong may pass: a graphics device was lost, a player failed after every
  player had handed over a frame, or a player said before that that it could not decode what
  it had opened (`DecodingError` or `Unknown`). The second attempt is made 750 ms after the
  first failed: what takes a device away has been seen to outlast 0.3 s, and the two editors
  that did not open were 0.42 s apart. A file that is not a video (`SourceNotSupported`), or
  that no frame comes out of, fails at the first attempt as before: after 0.2 to 0.5 s for
  a file that is not a video, and after 10 s, which is how long the engine waits for a first
  frame, for one whose index is whole and whose pictures are noise. A player that says at
  both attempts that it cannot decode costs 1.1 to 1.8 s before the user is told, the wait
  included. All of that is checked with failures the checks make themselves, and that is
  also its limit: no file and no circumstance made a player report `DecodingError` before
  its first frame, so whether the wait would have opened those two editors is not known.
  A file that is cut off and keeps its index opens; a `Seek` into the part that is missing
  comes to rest after 0.4 s with `Position` on the frame asked for and the picture still on
  the frame it showed before, and `Failed` is not raised. Every run of the checks ends with
  the number of previews that needed a second attempt, and keeps their traces.
- A failed open keeps what the player said: see `OpenAsync` under *Rules for a caller*.

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

Also not measured: audible playback and audio sync (every check runs muted), so also a player
at the project's volume, and whether the player's volume and the exporter's multiplier are
the same scale (the plan's "The volume on Windows" has what was read about it), a real display
scale change, the app's own recordings, the packaged app.

## Which frame a texture holds

A player says nothing about the frame it hands over. All there is to go by is the position it
reports at that moment, and the engine as it was first merged took every frame for the one its
position named. That is right while nothing holds the process up. When something does, the
hand-over runs late and finds the position moved on. Of 39,000 frames handed over by two bare
players (`--investigate names`), 4 % were one to five frames older than their position said
with the garbage collector stopping every thread for 60 ms three times a second, 1 to 2 % with
the render thread keeping the device for 40 to 150 ms, 5 to 8 % with the whole process stopped
for 30 to 130 ms, and none with nothing in the way. None was newer in that measurement; the
two ways in which a frame can be newer than its position came to light later, and are below.
Two faults came of it:

- **After `Pause`, `Position` ahead of the picture, and staying so.** When the last frame taken
  was such a frame, the frame that really had its number came after the pause and was left out,
  and the players, sent to the frame `Position` named, were on it already and handed nothing
  over. With the frames taken that way again (`--believe-positions`) and the garbage
  collector at work, 14 of 200 pauses returned so and 12 stayed so.
- **A scene with the picture of one frame and the layout of the next**, wherever the layout
  moves. Taken that way again, 72 of 2,840 scenes of a zoom that moves in fitted the layout
  of the frame after their own.

A third fault has another cause, and showed only with every processor of the PC kept busy:

- **A frame handed over after the clock had been stopped, taken for a frame of playback.** The
  clock the players share stops at once, and its position is what a player reports. A player
  whose threads are kept waiting goes on by itself for a moment, and hands over the frame that
  comes due in that moment with the position the clock stopped on, which names the frame
  before it. The engine has always left out what a player hands over after the stop, but it
  noted when that was only once the clock had been told and its own thread had got on. A frame
  that set out in between passed for playback, and for the frame before it; `Pause` then
  returned with `Position` one frame behind the picture: once in 400 pauses with every
  processor busy. With that order again (`--stop-noted-late`), the processors busy and the
  stopping thread held up for a moment, 12 and 9 of 300 pauses returned so, and none of 300 with
  the order as it is.

### What the engine does

- **It tells the frames apart** (`StudioPreviewFrameNamer`, one for each clip): from when a
  hand-over began, what the position named then, after the copy and when the player had its
  thread back, how long the copy took, and what the garbage collector did meanwhile. The rules
  and what each was measured against are in the class comment. Those that follow from the
  order of the frames give a number the engine relies on. Those that rest on what a player
  does while nothing but this process holds it up give a number that is *inferred*: good
  enough to draw by, not to rest on. A frame the rules say nothing about has no number. How
  often a number of either kind was wrong is under *What it costs, and where it ends*.
- **A frame with a number** is taken into its clip's picture, and the seek policy and
  `Position` are told, in the same round of the render thread: the picture and what is said
  of it do not part.
- **A frame without a number is told to nobody.** It is shown, from a texture of its own, only
  where the scene comes out the same whichever frame it is: always for the camera, whose frame
  decides nothing about the scene, and for the screen while nothing in the scene changes from
  frame to frame (`StudioPreviewStillness`: no zoom or scene change on the move, no ring of a
  click showing). Otherwise it is kept back, and drawn a moment late if the frame after it
  gives it its number. When no frame has had a number for half a second, as on a PC that
  cannot keep up, the frames are shown under the number of their position and marked unsure.
- **`Pause` rests on a frame with a number.** It stops the clock, lets the hand-overs in
  progress and one round of the render thread finish, puts every clip back on its last frame
  with a number, and returns. `Position` is that frame, at once and for good; a frame that
  comes later is left out.
- **A frame whose number is inferred or unsure is fetched anew when the clock stops on it**:
  the players are sent to another frame and back with the picture held, as for a frame that
  was lost. It is the same frame as a rule and nothing is seen. Should the number have been
  wrong, the picture becomes the frame `Position` names, and not the other way round.
- **A `Seek` while playing** leaves the pictures as they are until the frame asked for is
  there. A clip that shows a frame without a number then has its frame fetched anew wherever
  the clock is put. So does the end of the recording, where the clock stops by itself.
- **What a player hands over once the clock has been stopped is left out**, until the engine
  asks the players for something (`StudioPreviewHandOverKinds`). The engine notes the moment
  before it tells the clock, in `Pause` and where it stops the clock itself to go somewhere,
  so nothing that sets out afterwards can pass for playback. A frame that sets out between
  that moment and the clock's stopping is left out with the rest, and the picture rests on the
  frame before it.

### Measured

`StudioPreviewCheck --only stalls` pauses a playing preview again and again while the process
is held up (the tool's README says how each kind of hold-up is made), reads `Position` and each
clip's picture the moment `Pause` has returned and again at rest, and reads the number of every
frame the players handed over from the copy itself. On the PC of *Measured* above, with other
check tools and builds taking their turns on it, and with the engine as it is:

| Held up by | Pauses | Wrong when `Pause` returned / at rest | `Pause()` call | Until picture and `Position` are final | Until the engine is at rest | Picture taken back | Frame fetched anew, and at rest then |
| --- | --- | --- | --- | --- | --- | --- | --- |
| nothing | 2,400 | 0 / 0 | mean 1.3 ms, max 19 | mean 33 ms, p95 190, max 295 | mean 123 ms, max 298 | never | never |
| the garbage collector, every 150 to 450 ms | 550 | 0 / 0 | mean 3.0 ms, max 109 | mean 108 ms, p95 340, max 471 | mean 136 ms, max 302 | before 7 pauses, by at most 5 frames | after 183 (33 %): mean 253 ms, max 476 |
| slow draws | 550 | 0 / 0 | mean 11.1 ms, max 158 | mean 91 ms, p95 320, max 493 | mean 147 ms, max 396 | before 10 pauses, by at most 4 frames | after 69 (13 %): mean 284 ms, max 508 |
| the whole process stopped from outside | 1,000 | 0 / 0 | mean 4.3 ms, max 148 | mean 145 ms, p95 404, max 522 | mean 152 ms, max 378 | before 52 pauses, by at most 5 frames | after 390 (39 %): mean 299 ms, max 537 |
| the stopping thread held up | 600 | 0 / 0 | mean 25.4 ms, max 51 | mean 58 ms, p95 209, max 366 | mean 148 ms, max 378 | never | never |
| every processor busy | 400 | 0 / 0 | mean 11.7 ms, max 131 | mean 68 ms, p95 293, max 461 | mean 169 ms, max 438 | before 8 pauses, by at most 9 frames | after 15 (4 %): mean 341 ms, max 476 |
| every processor busy, and the stopping thread held up | 300 | 0 / 0 | mean 59.9 ms, max 144 | mean 119 ms, p95 352, max 563 | mean 227 ms, max 444 | before 1 pause, by at most 3 frames | after 11 (4 %): mean 470 ms, max 567 |
| every processor busy, and the garbage collector | 400 | 0 / 0 | mean 14.9 ms, max 235 | mean 108 ms, p95 363, max 576 | mean 180 ms, max 576 | before 14 pauses, by at most 10 frames | after 63 (16 %): mean 338 ms, max 580 |
| collector, slow draws and stops, all at once | 300 | 2 / 0 | mean 20.4 ms, max 226 | mean 244 ms, p95 523, max 646 | mean 195 ms, max 486 | before 19 pauses, by at most 9 frames | after 173 (58 %): mean 351 ms, max 660 |

*Wrong* is a pause after which `Position` is not the frame read from the screen's picture, or
after which, at rest, the camera is not on the frame that goes with it. *Until final* runs from
the call to the last scene drawn after it, which as a rule shows what the scene before it
showed. *At rest* is when the engine has nothing left to do: the camera brought onto its
frame, the 40 ms of the first position change over, and a frame that was to be fetched anew
fetched. A `Seek` to the next frame after every one of these pauses showed that frame on both
clips. In the two rows with the stopping thread held up, the call includes the delay the
check itself puts into it, 6 to 30 ms.

| Held up by | Frames of playback | Without a number | Wrong number | By position: wrong | Handed over after the stop, and left out |
| --- | --- | --- | --- | --- | --- |
| nothing | 47,262 | 2 (0.0 %) | 0 | 1 (0.0 %) | in 59 pauses, up to 2 ms after the call; 2 a later frame than the position named |
| the garbage collector, every 150 to 450 ms | 10,702 | 55 (0.5 %) | 0 | 468 (4.4 %) | in 20 pauses, up to 2 ms after the call |
| slow draws | 10,513 | 185 (1.8 %) | 0 | 153 (1.5 %) | in 8 pauses, up to 2 ms after the call |
| the whole process stopped from outside | 21,609 | 1,276 (5.9 %) | 1, across a stop of the process | 909 (4.2 %) | in 44 pauses, up to 127 ms after the call; 1 a later frame than the position named |
| the stopping thread held up | 11,815 | 0 (0.0 %) | 0 | 0 (0.0 %) | in 17 pauses, up to 1 ms after the call; 1 a later frame than the position named |
| every processor busy | 8,513 | 97 (1.1 %) | 0 | 53 (0.6 %) | in 21 pauses, up to 40 ms after the call; 1 a later frame than the position named |
| every processor busy, and the stopping thread held up | 6,445 | 53 (0.8 %) | 0 | 31 (0.5 %) | in 28 pauses, up to 15 ms after the call; 6 a later frame than the position named |
| every processor busy, and the garbage collector | 8,228 | 266 (3.2 %) | 0 | 245 (3.0 %) | in 44 pauses, up to 39 ms after the call; 12 a later frame than the position named |
| collector, slow draws and stops, all at once | 6,004 | 591 (9.8 %) | 4, from rule 3 | 611 (10.2 %) | in 15 pauses, up to 96 ms after the call |

*By position: wrong* is what the engine as it was first merged would have got wrong. The wrong
numbers are the subject of *What it costs, and where it ends*.

- **2,400 pauses with nothing in the way and 3,800 with the process held up in one of
  seven ways: `Position` and the picture agreed after every one**, when `Pause` returned and
  at rest. With the collector, slow draws and stops of the whole process all at once, which
  no PC does, 2 of 300 returned with them a frame apart, and none stayed so.
- **Scenes while the layout moves** (`--only zoom`): a zoom moves in over three seconds, and
  every scene the engine draws is read back, the frame from the picture's strip and the edges
  of the strip's cells to a tenth of a pixel, and held against the layout of that frame; one
  frame on, the edges are up to five pixels away. None was wrong of 24,663 scenes, 18,922 of
  them in the move, over 204 plays made directly and through `StudioEditorSession`, with
  nothing in the way, the collector, slow draws, stops of the process and busy processors.
  `StudioWindowCheck --only zoom --held-up`, which first showed the fault, passed 12 of 12
  times on this engine.
- **A frame handed over after the stop.** With nothing in the way a player handed a frame over
  once the clock had been stopped in 59 of 2,400 pauses, within 2 ms of the call. With every
  processor busy it did so in 93 of 1,100 pauses, up to 40 ms after the call, and 19 of those
  frames were later ones than the stopped position named. All of them were left out.
- **The 40 ms rule** is for a player that hands over, with the first position after the clock
  stopped, the frame it had ready for playback. With the rule, no scene drawn after any of the
  pauses above showed another frame. Without it (`--no-quiet-rule`), the engine as first
  merged showed such a frame after 5 of 920 pauses; the engine as it is, after none of
  1,700: no player handed over two frames for that position. Why not is not known, and the
  rule stays.

### What it costs, and where it ends

What it costs, all of it only while the process is held up:

- **`Pause` can take the picture back.** If the scene was showing a frame without a number
  when `Pause` came, it returns on the last frame with one: before 1 to 5 % of the pauses in
  the first table, by 10 frames at the most. With nothing in the way, never.
- **`Pause` takes longer.** It waits for the hand-overs under way and for one round of the
  render thread: mean 1.3 ms with nothing in the way, 3 to 15 ms held up, 235 ms at the
  most.
- **A frame is fetched anew after the pause** where its number was inferred: after a third of
  the pauses with the collector at work. The picture is held meanwhile, and the engine is at
  rest after a quarter to a third of a second in place of an eighth. A `Seek` made meanwhile
  takes its place.
- **While the layout moves, a frame without a number is not drawn**, or drawn a moment late,
  when the frame after it gives it its number. Of the 90 frames of the zoom's move, at least
  87 were drawn in every play with the collector at work (collections of 60 to 90 ms), 81
  with slow draws, 80 with every processor busy and 70 with the process stopped from
  outside. Longer collections cost more, and most of that is the players': after one of 150
  to 180 ms the move was three or four frames short, and the stretch before it, where every
  frame handed over is drawn, two.
- **`Position` stands still while the frames have no number**, and the picture goes on where
  the scene is still. `PositionChanged` then comes late, by a quarter of a second at the most
  in the checks and by half a second at the most by construction, and a caller that acts on a
  position, as the editor does at a cut, acts that much later.

Where it ends:

- **Everything at once.** With the collector, slow draws and stops of the whole process
  together, 2 of 300 pauses returned with `Position` and the picture a frame apart, and
  4 of 5,413 numbers were wrong, all from rule 3. At rest they agreed: the number being
  inferred, the frame was fetched anew.
- **Rule 3 is right nearly every time.** It takes a hand-over that the garbage collector kept
  waiting for the frame that was next in line. Once in 130,000 frames handed over with only
  the collector at work it was the frame after that one. The cause is not known; a collection
  begun within 40 millionths of a second of the copy before did not bring it about
  (`--investigate tails`, 300 tries). A scene drawn with such a frame has the layout of the
  frame before its own, for one frame. A pause that lands on it returns a frame apart, and the
  fetch puts it right.
- **A stop of the whole process in the instant a player announces a frame**, within about a
  fifth of a millisecond of it. When the process runs again the player puts the frame two
  after the announced one in its place, and the hand-over that was under way gives it the
  number of its position, by a rule the engine relies on. Seen once in 60,000 frames with
  the process stopped at random twice a second, and 5 times in 540 plays with a stop aimed at
  the first frames of playback (`--investigate starts`). The picture and its number are then
  two frames apart until the frames have numbers again: 40 and 70 ms in the two plays looked
  at. Nothing the engine can observe tells such a hand-over from one that waited for the
  graphics device, which keeps its frame, also when the process is stopped during the wait
  (120 of 120, `--investigate waits`). Telling it would take a witness that the process
  ran; none is built. The hand-overs themselves could be one: in each of the five the copy
  began 118 to 132 ms after the hand-over had set out, as long as the process had stood, three
  times with nothing in its way and twice behind the other clip's hand-over, which had stood
  as long. A garbage collection, a slow draw and busy processors do not stop a player, and
  did not bring it about. A pause in that time would return two frames apart; the players are
  then on a later frame than `Position` names, so the engine's move to that frame should put
  the picture right, which is reasoned and was not seen.
- **A PC that cannot keep up at all.** When no frame has had a number for half a second, the
  frames are shown under the number of their position and called unsure, so that the picture
  does not stand still, and a frame the clock stops on is fetched anew. That is built and unit
  tested; no run of the checks got there.
- **Another PC.** What the rules rest on was measured on one: that a player looks for a frame
  every hundredth of a second, and the thresholds that follow from it.

The checks read the number of every frame, and judge them all but two kinds, which they count
and print apart: a wrong number from rule 3, of which a check may show one, or one in 20,000;
and one given to a hand-over in which a stop of the process began, which the tool knows of
and the engine does not. `--judge-limits` judges those too. Judged so, about one full run in
30 would fail on one of the two.

### A recording's own frame times: not played yet

Everything above was measured on clips made by ffmpeg, with one frame exactly at the start of
every slot. A recording made by the app is not like that, and no check has played one:

- The recorder stamps a screen frame with the wall clock, read a moment after its pacer's tick
  (`GpuCaptureSession.ProduceFrame`). The pacer keeps an even grid of its own, which starts
  when it starts. So the frames sit some milliseconds into their slot: by an amount that is
  the same through a recording, differs from one recording to the next, and changes where the
  recording was paused; and by a millisecond or two more that differs from frame to frame.
- A tick the recorder misses leaves its slot empty.
- The camera's frames carry the camera's own times, counted from its first frame.

The encoder keeps those times as they are given: the index of a camera track it wrote for
`StudioRenderCheck` has the frames 0, 2 and 4 ms into their slots and a gap of a third of a
second, as they were handed over. How often a recording misses a tick is not known. The
recorder counts them (`pumpOverruns` and `exhaustedDrops` in its performance report), and its
report of one real recording of five seconds has none. Whether the offset of the first
screen frame survives in the file, which decides how far into their slots the frames sit, is
not known either.

The exporter is checked with such a file (`StudioRenderCheck`, "a recording that dropped
frames"). The preview engine takes the frame a player shows for the one whose slot the clock
is in, and the rules above lean on the grid in two places: a frame is on time when it is
handed over within 13 ms of the start of its slot, and the frame after a frame has the next
number.

What that does to such a file has been worked out, not run: the namer itself, fed by a model
of a player that keeps up (it looks every 10 ms, hands over one frame at a time and never
before its time, and reports the clock's position), with nothing holding the process up. 600
slots a play, ten plays with the looks a millisecond later each time, each frame up to 2 ms
late at random. At 30 frames a second:

| The frames sit this far into their slot | No slot empty | One slot empty |
| --- | --- | --- |
| 0 to 8 ms | every frame has its number at once | one to three frames get their number a hand-over late or not at all, then as before |
| 10 to 20 ms | every frame has its number at once | no frame has a number for 15 or 16 frames, half a second, in which `Position` stands still; the rest of the play is shown by position and called unsure (3 of 10 plays at 10 ms, 9 or 10 of 10 from 12 ms on) |
| 22 to 26 ms | 3 to 33 % of the frames get their number from the hand-over after them | the same half second, and unsure after it (10 of 10 plays, 7 of 10 at 26 ms) |
| 28 to 32 ms | 2 to 33 % get their number a hand-over late | up to 8 frames in a row without a number, then as before |

At 60 frames a second: every frame at once up to 4 ms into the slot; from 5 to 15 ms, 2 to
33 % a hand-over late; and with one slot empty, 30 frames in a row without a number where the
frames sit 6 to 10 ms in.

Why: the frame after an empty slot is handed over with a position that names the frame two on,
which is no number; after that the least it can be goes up by one a frame and so does the name,
so "the next number" never fits again, and the rule that gives the numbers back asks for a
frame within 13 ms of the start of its slot. And a frame that sits within a look of the end of
its slot is handed over now in its own slot and now in the next.

A number that comes a hand-over late is a frame not drawn on time while the layout moves.
Shown by position and unsure is how the engine took every frame before these rules, which is
right while nothing holds the process up.

Two things the model does not reach, read from the code. A step forward asks the player for
its next frame and takes it for the frame one on; across an empty slot that is the frame two
on, and whether the engine then takes it for one on or falls back to a seek depends on the
position the player reports. And a frame that sits past the middle of its slot plays under
its slot's number, while a pause, a seek and the export show the frame before it there,
because they look at the middle of the slot: that is older than these rules.

The model is in `StudioPreviewFrameNamerRecordingTests`. Its tests of what should hold and
does not are skipped, each with its reason: they describe the engine as the app runs it. They
are to be made to pass, or shown wrong with a real player, before the preview is relied on for
recordings. What follows makes them pass by the model; no player has played with it yet.

### Counting in frames of the file: built, switched off, not played yet

`StudioPreviewOptions.FrameTimesFromFile` makes the engine read, while it opens, when each
frame of each clip begins: from the file's index, without decoding anything
(`StudioPreviewFrameTimes`). It then counts in frames of the file:

- A frame handed over is the frame whose time the player's position has reached, and the next
  frame is the next one of the file. A slot the recorder left empty is no skipped number, and
  where a frame sits in its slot does not matter to its number.
- The frame of the timeline it is shown under, which is what `Position` reports and what the
  scene is laid out for, is the first one whose middle the frame has begun by. That is the
  frame the export shows it in, and the frame a pause or a seek rests on with that picture: a
  frame plays under the number it rests under. "Begun by" is the export's own rule to the
  100 ns unit: the frame's time is at or before the middle, with nothing allowed for rounding
  (see *How exact the rule is* below).
- A seek knows which frame of the file a frame of the timeline shows. A frame of the timeline
  that has no frame of its own shows the one before it: no picture is owed for it, it is
  reached at once, and the scene is drawn again for it, since its layout may be another. One
  frame forward steps a player only where that shows the next frame of the file, and a detour
  goes to a frame of the timeline that shows another frame of the file.
- The camera's frame rate, which its rules go by, is the rate its frames usually come at. Frames
  by length, which is what the probe reads, is wrong for a camera that stalled or gave half its
  frames in low light.
- A file whose index it cannot read, or whose times it cannot be sure of, is played on the
  grid as before. The engine's diagnostics say which, and why (`FrameTimes`). What that is,
  is in *What the reading of an index refuses* below.

By the model above every frame then has its number at once, wherever it sits in its slot and
with or without an empty slot, and plays under the number a pause shows it under
(`StudioPreviewFrameNamerRecordingTests`, the theories that begin `WithTheFilesFrameTimes`).
Given frame times that are on the grid, every answer is the grid's
(`StudioPreviewRecordingTimesTests`), and the seek policy's own tests run unchanged.

#### What the reading of an index refuses

The index is read whole or not at all. The file can be a video from anywhere, and what the
reader does with one that is damaged, or made to wear a reader out, was gone through on
6 October after a review of the code found a way to end the process with a file of less than
a megabyte: parts one inside the other, which the reader went into without a limit until its
stack was used up. Now:

- Each part is looked for where it belongs and nowhere else: `moov` holds `mvhd` and the
  tracks; a track holds `edts`, with `elst` in it, and `mdia`; `mdia` holds `mdhd`, `hdlr` and
  `minf`; `minf` holds `stbl`; `stbl` holds `stts` and `ctts`. Nothing in the reader calls
  itself, so no file can take it deeper than that.
- What a file can ask for is counted: 4096 parts in the file and in each part of the index,
  64 tracks, 4,320,000 frames (ten hours at 120 a second) before any room is made for them,
  and an index of 256 MB.
- A part that does not fit the part it is in, bytes left over at the end of a part, one of
  the parts above met in another place or met twice, a table that is shorter or longer than
  the number of entries it says it has, a header cut short or of a kind that is not known:
  the index is damaged and the file is refused. Before, the reading of a part stopped without
  a word at a part that did not fit, so that the offsets after it were lost and the times of
  decoding passed for the times of showing; and a table too short to hold its count was taken
  for no table.
- A fragment (`moof`) anywhere in the file, before the index or after it, whether the index
  says so (`mvex`) or not. A file with a fragment after an index that does not announce it is
  not one a writer makes by the standard; it is refused all the same, and finding it costs a
  look at the name and length of every part of the file.
- Two indexes, and two video tracks: which of two a player shows is not known. (Until
  6 October the first was taken.)
- What a track is, is said by the handler of its media. A second one, which QuickTime puts
  beside a track's data, is not asked: before, the last one met decided, and such a file was
  refused as having no video.
- As before: an edit list in more than one part, at another speed, with a stretch of nothing
  in the middle or that begins inside the video; a frame shown before its track begins; two
  frames at one time; no frames; no time units.

One thing stays possible and cannot be told from the file: a part the reader has no use for
whose length takes in the part after it hides that part.

Three kinds of file are read by the book and not by anything seen here. The app's recorder
writes none of them: a stretch of nothing before the video (an edit list with a lead); frames
stored out of the order they are shown in, with an edit list that starts the track at the
first frame shown; and the same without an edit list, where the first frame is then taken to
be shown two frames in. If a player counts from the first frame shown, every number for the
last kind is two low. The check tool has three clips for it (`screen-reordered` and the two
beside it); they have never been made.

How this stands: 101 unit tests on bytes (`StudioPreviewFrameTimesTests`, in two files), among
them 6000 files with their parts moved about at random, each of which has to be refused or
read to exactly the times it had (5157 and 843). Seventeen faults put into the reader, the
timeline and the seek policy one at a time in a private copy: every one makes a test fail
that says what is wrong, two of them only after a test was added for them. And the reader's
own code over the index of 244 files on this PC: 238 that Media Foundation wrote for
`StudioRenderCheck` and the exporter, and six of ffmpeg's. All were read, the 238 in 12 ms a
file at the median and 35 at most. The 238 are seven files written 34 times over, each with
a frame every thirtieth of a second from zero. So they show that the stricter rules refuse
nothing those two writers make, and they show nothing about a recording, which none of them
is. That is the unit tests' host reading bytes: no check tool, and no player.

#### How exact the rule is

Until 6 October a table of frame times allowed what the grid allows at a frame's edge, a
thousandth of a frame (333 units of 100 ns at 30 a second). On the grid that is needed: the
grid multiplies seconds by a rate, and a frame's start is not a number it can hold. A table
holds every start as a number, and with the allowance a frame that began up to 33 millionths
of a second after the middle of a slot counted as showing there, where the export shows the
frame before it. In a file from Media Foundation's writer, whose frames begin at whole
30000ths of a second, that is one of the thousand places in a slot a frame can begin at. The
table now allows nothing, and the unit tests hold the frame it gives for every slot against
the export's own reckoning
(`StudioRenderingMath.BuildFramePlan` and `SecondsToMfTicks`, and the reader's rule written
out from `StudioVideoSource.GetFrame`), at the middle of a slot and one unit either side.

What that leaves, known and not measured in this engine:

- The times are rounded to the nearest unit, which is how the export's reader was measured to
  hand them out (the spike: 0, 333333, 666667 for RGB output). A player was measured to change
  frames within one unit of a frame's start, and to report a step's position cut off
  (666666). So a time within one unit of a frame's start can be on the other side for a
  player. The middle of a slot is not such a time in a recording's screen track: a frame
  begins at the middle or a whole 30000th of a second from it. A step's answer is taken for
  the next frame whichever side its position is on. While playing, a frame handed over in the
  very unit in which it begins would be named one low. That is a guess at once in a hundred
  thousand frames, from hand-overs that were measured to begin anywhere in the first 13 ms of
  a frame. `--investigate recordings --scenario players` now says, in units, whether any
  position came before its frame's time.
- A camera's offset that is not a whole number of units puts the player one unit from the
  time the export asks for: the export takes the offset off in seconds and rounds once, and a
  player's clock and offset are each in whole units. A camera frame that begins in that unit
  is one slot earlier in the preview's reckoning. It is pinned as it is
  (`ACameraOffsetWithAFractionOfAUnit...`).

**It is off, and the app runs as before.** It was written on 5 October while no check tool
could be run, and it rests on things only a run can say:

1. That the times in the index are the times a player goes by. `StudioPreviewCheck
   --investigate recordings --scenario files` holds the engine's reading against what Media
   Foundation's reader hands out, for every clip.
2. Whether the time of a recording's first frame survives in its file. Read from the recorder's
   code, a recording that was not paused has no frame in its first slot and its frames a couple
   of milliseconds into theirs: the pacer is started a moment after the timeline's zero and
   ticks first one interval later. If the file keeps that, the first frame begins after the
   middle of the first slot, and what a paused player shows before any frame has begun decides
   whether the preview of a recording opens on a picture. The export shows the first frame there.
3. What a player hands over for a position inside the frame it shows already, which is every
   seek to a slot that has no frame of its own.
4. Whether Media Foundation honours an edit list and the offsets of frames stored out of
   order, which of two video tracks a player shows, and whether it plays a file whose index is
   damaged. The first is what the three clips above are for; the other two decide nothing
   while such files are refused.
5. That the scene is drawn again when the players are brought to a frame of the timeline that
   shows the picture already there (a slot without a frame of its own, inside a zoom). It is
   one line in the engine's loop that no unit test reaches, because it needs the loop and its
   players: `--only recordings --frame-times file` holds it (the rests inside the zoom), and
   the fault `R3` takes it out.

What remains, with the times or without:

- **A frame the export shows in no frame.** Where a frame begins after one middle and the next
  begins before the next middle, the export shows the first of the two not at all, and the
  frame before it twice. The preview plays every frame of the file, that one under the number
  of the frame after it, and a pause on it rests on the frame after it. It takes frames that
  sit around the middle of their slot: by the recorder's code, a recording that was paused at
  an unlucky moment.
- **A stretch without frames.** `Position` follows the frames the screen hands over. Through a
  stretch without one it stands on the frame before the stretch while the sound runs on, and
  a pause in it rests there, so that playing on plays the stretch again. For a slot or two
  that is nothing. For a file with seconds between two frames it is not right. The mend would
  be to let the clock move the scene and the position through such a stretch, which the frame
  times make possible and which is not built.
- **The last frame of a recording** begins after the middle of the timeline's last frame when
  the frames sit late in their slots, and is then in no frame of the export.

The checks are written and have not run: `StudioPreviewCheck --only recordings`, and
`--investigate recordings --scenario files`, `players` and `table` (the tool's README). They
are to be run with and without `--frame-times file`, and a full run with it, before the switch
is turned on.

### Other speeds: not built

The engine has no `SetPlaybackRate` of its own yet: a project's speed changes are played at the
recording's own speed, and the editor's session calls into the contract's empty body. The way
to another speed is the clock's own rate (`MediaTimelineController.ClockRate`), which the spike
measured at a half and at twice the speed, set before the clock was started. Setting it is the
small part. What tells the frames of playback apart takes the clock to run at 1, read from the
code:

- The rule for a hand-over the garbage collector kept waiting (rule 3) allows a gap of one
  frame's length, taken as time on the wall clock. At another speed a frame lasts that divided
  by the rate.
- "On time" (rule 5, and what ends the doubt after an inference) is 13 ms into a frame, which
  is time of the recording: the position moves that far in 13 ms divided by the rate. From
  about 2.5 times the speed on, at 30 frames a second, it is longer than a frame and tells
  nothing; and a player that looks every hundredth of a second then has a frame due at every
  look, so that a look that went by without one is no longer something to conclude from.
- A player looks for a frame a hundred times a second (measured at the recording's own speed),
  so from about 3.3 times the speed on it cannot hand over every frame of a recording at 30 a
  second. The rules that number a frame need it to be the next one, so nearly every frame would
  be without a number, `Position` would stand still for half a second, and after that every
  frame would be shown under its position's number and called unsure. A position read a moment
  after the player looked names the frame after the one handed over whenever a frame began in
  between, which at eight times the speed is no longer rare.

So playing at 4 and at 8 needs a rule the engine does not have, and that rule needs to be
measured first. `StudioPreviewCheck --investigate rates` sets the clock's rate from outside
(`StudioPreviewEngine.SetClockRateForExperiment`, for that experiment only) and says, for each
rate, what the players hand over and what the rules for a clock at 1 make of it. It was written
on 5 October while no check tool could be run, and has not run.

## Cuts, scene changes and zooms, through the editor's session

The groups `cuts` and `scenes` and half of `zoom` drive a `StudioEditorSession` on the engine
without a window, with a thread of the tool as the session's thread.

**A cut while playing.** The session learns of a cut when a position inside it arrives, and
then seeks past it. So the first frame of the cut is drawn, stands while the seek lands, and
playback goes on with the first frame after the cut:

| Playing over a cut | Crossings | Frames of the cut drawn | The picture stands still for | Last frame before the cut to the first after it |
| --- | --- | --- | --- | --- |
| nothing in the way | 132 | 1 every time | mean 125 ms, p95 up to 145, max 152 | mean 157 ms, max 186 |
| the garbage collector | 30 | mean 1.1, at most 2 | mean 128 ms, p95 174, max 196 | mean 160 ms, max 236 |
| slow draws | 30 | mean 1.3, at most 3 | mean 136 ms, p95 263, max 293 | mean 172 ms, max 324 |
| every processor busy, and the stopping thread held up | 30 | mean 2.7, at most 6 | mean 214 ms, p95 263, max 288 | mean 277 ms, max 476 |

The session asks for the jump 5 ms after the first frame of the cut was drawn, on average,
and 47 ms after it when every processor is busy, which is why more of the cut is drawn then.
No frame of the cut is drawn once the jump has landed, the playhead never rests inside a cut,
and where the video ends in a cut the session stops with its playhead at `PlaybackEnd` and no
frame past it drawn (64 plays). Paused, `StepFrames` goes a frame at a time into the cut, out
of it at its end and back, `Scrub` into it shows the frame there, and `TogglePlayback` inside
it plays from the frame the video goes on with.

Two things would make the jump better, and neither is built. The session could ask for it one
frame early, when the frame before the cut arrives: no frame of the cut would be drawn, and
the standing still would remain. To remove that, the frames after the cut would have to be
ready before playback gets there, on a second set of players.

**A scene change.** Bubble, side by side from 4 s and bubble again from 8 s, each entered by a
move of 0.35 s. Paused before, inside and after each move, by `Scrub` and by `StepFrames`, both
clips are within 0.3 px of where the format puts them for the frame shown (360 frames).
Playing through the moves, every scene has the layout of its own frame: 900 scenes with
nothing in the way and every frame of the moves drawn, 942 with the collector at work and at
least 8 of the 10 frames of a move drawn.

**A zoom while playing**, through the session, is counted with the scenes under *Which frame a
texture holds*.
