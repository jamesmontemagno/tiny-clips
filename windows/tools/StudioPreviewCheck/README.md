# StudioPreviewCheck

Checks the Studio live preview: the engine in `windows\src\TinyClips.Core\Studio\Preview` and the
app's `StudioPreviewPanel`, whose source files are compiled into this tool, so the real panel is
checked and not a copy. Every result is read back from pixels: each frame of the test clips
carries its frame number as a strip of black and white cells, and the tool reads that strip out of
what the engine drew or out of a screenshot of its own window.

How the preview works and what a caller has to observe is in `windows\docs\studio-preview.md`.

## Needs

- ffmpeg and ffprobe on `PATH`, to generate the test clips on the first run (about 16 MB, kept
  in `out\media`; delete the folder to make them again).
- A graphics adapter with a hardware H.264 decoder. The window checks need a desktop session.

## Build and run

From the repository root. The tool is not in `TinyClips.Windows.slnx`.

```powershell
dotnet build windows\tools\StudioPreviewCheck\StudioPreviewCheck.csproj -c Debug -p:Platform=x64
windows\tools\StudioPreviewCheck\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\StudioPreviewCheck.exe
```

A full run takes about ten minutes. It exits with 0 when every check passed and with 1 when one
did not, after printing one `FAILED:` line for each; 2 is a usage error.

| Option | |
| --- | --- |
| `--quick` | fewer repetitions; a smoke run, not the source of the numbers in the docs |
| `--only a,b` | only these groups |
| `--skip a,b` | leave these groups out |
| `--no-window` | leave out the checks that open a window |
| `--no-headless` | leave out the checks that need no window |
| `--out <folder>` | where clips, reports and pictures go (default `out` next to the project) |
| `--trust-first-frames` | open every preview without a window the way the engine opened before it stopped believing what the players hand over first. The checks of the first picture are then expected to fail: it shows that they notice |
| `--investigate <name>` | an experiment instead of the checks; see below |

Groups without a window: `open seek step seekplay position editor play pause end update camera
mute surface dispose device software`. Groups with one: `window-exact window-playback
window-resize window-scale window-blocked window-reload window-engines window-devicelost`.

## Experiments

`--investigate <name>` runs an experiment instead of the checks. Nothing is judged; it prints what
it measures, and the report is written as for a run. They are how it was found out what a
`MediaPlayer` does when its frames are wanted on another graphics adapter than the one it decodes
on, and they are kept so that the same can be asked of a PC with two graphics adapters, or with
none. Everything is muted, and no window is opened.

| | |
| --- | --- |
| `--investigate opens` | Opens many previews one after the other and says for each how long it took, what the picture and the players' textures showed, which scenes drawn in the 0.3 s after it opened showed something else, and what the engine reports about its opening. `--device software` (the default) or `hardware`; `--count N` (60); `--camera late`, `start`, `none` or `mixed` (the default); `--trust-first-frames` opens them the way the engine used to, and `--alternate` switches between the two ways, so that both are measured under the same conditions; `--keep-first-frames` makes the engine deaf to first frames a player offers again, so that it has to take them itself |
| `--investigate cycles` | Opens and closes many previews one straight after the other, as the `dispose` group does: closed while playing, during a seek, right after opening or during a seek followed by Play, in turn, and the folder deleted at once. It prints what goes wrong and keeps the trace of every preview that did not open. `--device hardware` (the default) or `software`; `--count N` (200); `--trust-first-frames` |
| `--investigate decoding` | Opens one preview and plays it twice for `--seconds N` (12), with and without a surface: the adapters with the identifiers the system's GPU counters use, the graphics and media modules the open loaded, the memory the process holds on the first adapter, and the processor time of playing. It prints its process id and the `Get-Counter` line that shows from outside which adapter's engines the process keeps busy. `--device software` or `hardware` |
| `--investigate players` | `MediaPlayer`s set up as the engine sets them up, without the engine. `--device software`, `hardware` or `both` (the default); `--count N` repetitions (4); `--scenario` one of: `first-copy` (what each copy leaves in the texture, over the first copy and five position changes), `second-device` (a player that has settled on one device is given a texture on a second device of the same kind), `while-opening` (one player's first frame is copied while the other is still opening), `both-ready` (both have a first frame before either is copied), `settle` (the clock is moved at various times after the first copy), `before-source` (a player is given a texture before it has a frame), `reopen` (a player that has moved is given its source again) |

## What it leaves behind

- `out\report-<time>.txt`: what was printed. `pass` and `FAIL` lines are checks; `note` lines are
  measurements that are reported and not judged, because they depend on what else the machine is
  doing (latencies, dropped frames, how long two clips were a frame apart). The last note of a
  run says how many of its previews opened only at the second attempt, the first having failed
  in a way that may pass (a lost graphics device, or a player that failed after every player had
  handed over a frame); the traces of those are kept with the failures.
- `out\window.png`: the first screenshot of the window.
- `out\failures\<time>\`, named like the report of the run it belongs to and only there when a
  check of that run failed: for a check that found the wrong picture or the wrong position, the
  surface and both players' textures as PNG files, and the engine's trace of what the players
  reported and what was asked of them. For a preview that did not open, the trace alone: there
  is no engine left to take pictures from. Every preview of the tool is opened with a trace, so
  this holds for all groups. The first twelve of a run are kept, and the `FAIL` line names the
  files.
- Project folders are made under `%TEMP%\TinyClipsStudioPreviewCheck-<pid>` and deleted at the end;
  the last check of a run is that they are gone.

## What it does to the machine

- **No sound.** Every player is forced mute and to volume zero. One check has to see
  `project.Audio.Muted` arrive at the screen player's `IsMuted`; it runs with a screen file that
  has no audio track, still at volume zero. Audible playback and audio sync are therefore not
  checked by this tool.
- The window is shown without being activated, behind every other window, and not in the taskbar
  or Alt+Tab. Screenshots are taken with Windows.Graphics.Capture of that window only. No input is
  sent. At the end the tool says whether its window was ever in front.
- A change of display scale is simulated with a transform on the panel's parent; a lost graphics
  device is simulated inside the engine. No system setting is changed.

## How the checks are built

- `Media\TestMedia.cs` generates `screen.mp4` (1920x1080, 12 s, 30 fps, with a tone-burst audio
  track), `camera.mp4` (1280x720, 12 s, no audio) and `camera-short.mp4` (6 s). `Media\TestFolder.cs`
  writes a project folder with copies of them for each check.
- `Checks\` holds the checks without a window. The engine draws into an offscreen surface
  (`OffscreenSurface` in `Harness.cs`), and a recorder reads the frame numbers of every scene it
  draws through the engine's after-render hook. A seek has landed when a scene with both clips
  on the frame has been drawn; `Position` says nothing about that, because the engine reports a
  frame from the moment it is asked for.
- The `open` group opens 24 more previews, of three kinds of project in turn (the camera 0.2 s
  late, the camera from the start, no camera), and looks at every scene drawn in the tenth of a
  second after each one opened. A player that has just opened sometimes hands over a blank
  frame, and one open says little about something that happens about once in six. It also makes
  a player fail while a preview opens, after every player has handed over a frame: once, which
  the engine answers by opening once more, and twice, which fails the open. A file that is not a
  video has to fail at the first attempt.
- The `position` group (`HeadlessChecks.Position.cs`) is about that promise: once `Seek()` has
  returned, `Position` is the frame asked for and never again one from before the call. It reads
  `Position` in a `PositionChanged` handler, on a second thread that takes each event up a
  little later (as a UI thread does), and on a thread that reads without pause, and judges every
  read that began after `Seek()` had returned. The last part plays an editor session that parks
  at the end of its kept range and replays it on Space. The reading thread keeps one processor
  busy for the two minutes the group takes.
- The `editor` group (`HeadlessChecks.Editor.cs`) does the same with the editor's own session
  class, `StudioEditorSession` from `TinyClips.Core`: on a project store in the temp folder, with
  its preview opened through the factory, and with a thread of the tool's as the thread the
  session lives on. Only the exporter is a stand-in. It trims, presses Space at the end of the
  kept range (some of the presses timed to fall just before the seek that parked the playhead
  lands, which is when a stale position would send the session back), plays into the end of the
  recording, pauses and steps, edits, and closes with and without deleting the project.
- The `pause` group also looks for a frame that a player had ready when the clock stopped: it
  comes out as the first answer to the next seek, with that seek's position on it. Half of the
  pauses are timed to fall where that can happen, 16 to 32 ms after a scene was drawn.
- The `software` group makes the engine draw on the software adapter (WARP), which is where it
  draws on a PC without usable graphics hardware. **On a PC that has graphics hardware this is
  not a PC without it.** Every player starts decoding on the graphics hardware and moves to the
  software adapter when its first frame is copied there, and until it has settled it hands over
  frames that are blank or a frame off; another player that is still opening at that moment
  fails. The engine holds the first frames back until every player has one and compares the
  players' pictures before it believes them, and the group checks that: one preview with seeks,
  steps, a lost device and playback, then 15 opened one after the other, then 6 with the engine
  made deaf to first frames that are offered again, so that it has to take them itself (which
  no player has made necessary so far). The group finds out
  which kind of PC it runs on the way the engine does, by asking for a device on the graphics
  hardware, and says so in its first line; the note below the open says what the players' first
  frames looked like, and whether that is what such a PC is expected to do. On a PC without
  graphics hardware the players start on the software adapter and have nowhere to move from; no
  check here can show that on a PC with graphics hardware. A check of this group that does not
  hold leaves the pictures and the engine's trace in the run's folder under `out\failures`, as
  the other groups do.
- The `device` group also loses the device while a preview opens: once, which is answered by
  opening once more, and twice, which fails the open.
- The `dispose` group also closes a preview while this tool holds one of its recordings open, as
  another program that plays the video does. A recording in the project folder is waited for
  (5 s, after which the engine says that it gave up): that is the control. A screen recording
  outside the folder, the user's own video, is not waited for.
- `Windowed\` holds the window checks. The tool writes four corner markers with 1 px stripes and
  the scene's number into every scene before it is presented; a screenshot then says whether the
  picture is on screen pixel for pixel, where it is, and which scene it shows. Screenshots are
  delivered a little after the system composed them, so one that shows an older scene than the
  last one drawn is passed over.
- `Checks\HeadlessChecks.Investigate.cs` holds the experiments described under Experiments above.
- Windows leaves kernel handles behind for every `MediaPlayer` and `MediaTimelineController` ever
  created (about 12 per open and close of a preview). The handle check therefore measures two
  bare players on a controller first, used the way the engine uses them, and holds the engine to
  that figure plus a margin of 3. Handles of the thread pool and of I/O in progress are left out
  of the comparison, because they come and go by a dozen whatever the process does.
