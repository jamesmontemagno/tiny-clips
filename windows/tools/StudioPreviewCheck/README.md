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

Groups without a window: `open seek step seekplay position editor play pause end update camera
mute surface dispose device software`. Groups with one: `window-exact window-playback
window-resize window-scale window-blocked window-reload window-engines window-devicelost`.

## What it leaves behind

- `out\report-<time>.txt`: what was printed. `pass` and `FAIL` lines are checks; `note` lines are
  measurements that are reported and not judged, because they depend on what else the machine is
  doing (latencies, dropped frames, how long two clips were a frame apart).
- `out\window.png`: the first screenshot of the window.
- `out\failures\`: for a check that found the wrong picture or the wrong position, the surface and
  both players' textures as PNG files, and the engine's trace of what the players reported and
  what was asked of them.
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
- The `software` group makes the engine create the software adapter (WARP), which is what a PC
  without graphics hardware gets. The players still decode with this PC's graphics hardware,
  so it says that the engine and the renderer work on that device, not how fast such a PC is.
- `Windowed\` holds the window checks. The tool writes four corner markers with 1 px stripes and
  the scene's number into every scene before it is presented; a screenshot then says whether the
  picture is on screen pixel for pixel, where it is, and which scene it shows. Screenshots are
  delivered a little after the system composed them, so one that shows an older scene than the
  last one drawn is passed over.
- Windows leaves kernel handles behind for every `MediaPlayer` and `MediaTimelineController` ever
  created (about 12 per open and close of a preview). The handle check therefore measures two
  bare players on a controller first, used the way the engine uses them, and holds the engine to
  that figure plus a margin of 3. Handles of the thread pool and of I/O in progress are left out
  of the comparison, because they come and go by a dozen whatever the process does.
