# StudioWindowCheck

Opens the app's Studio editor window in a process of its own and checks what it does. The
window's source files (`Views\Studio`, `Controls\Studio`, `ViewModels\Studio`, `Models\Studio`,
`Services\Studio`) are compiled into this tool, so the real window is checked and not a copy,
with the real project store, preview engine, preview panel and exporter behind it. `TinyClips.App`
is never started, and nothing is packaged, registered or installed.

Every result is read back by the tool itself, in one of three ways:

- a screenshot of its own window, taken with Windows.Graphics.Capture. Each frame of the test
  clips carries its frame number as a strip of black and white cells (see
  `..\StudioPreviewCheck\README.md`), so a screenshot says which frame of each clip is shown and
  where;
- the window's UI Automation tree, read with the client a screen reader uses;
- the frames of an exported file, decoded with ffmpeg.

## Needs

- An x64 process. The tool refuses to run otherwise: see "What it does to the machine".
- ffmpeg and ffprobe on `PATH`: to check the test clips, to generate them when they are missing,
  and to decode what the editor exports.
- A graphics adapter with a hardware H.264 decoder and encoder, and a desktop session.

## Build and run

From the repository root. The tool is not in `TinyClips.Windows.slnx`.

```powershell
dotnet build windows\tools\StudioWindowCheck\StudioWindowCheck.csproj -c Debug -p:Platform=x64
windows\tools\StudioWindowCheck\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\StudioWindowCheck.exe
```

A full run takes between one and one and a half minutes. It exits with 0 when every check passed and with 1 when one
did not, after printing one `FAILED:` line for each; 2 is a usage error.

| Option | |
| --- | --- |
| `--only a,b` | only these groups |
| `--skip a,b` | leave these groups out |
| `--out <folder>` | where reports, trees and pictures go (default `out` next to the project) |
| `--media <folder>` | where the test clips are, or are generated (default `..\StudioPreviewCheck\out\media` when it has them, otherwise `media` in the out folder) |

Groups: `open transport inspector trim export close windows accessibility themes`.

## What it leaves behind

- `out\report-<time>.txt`: what was printed. `pass` and `FAIL` lines are checks; `note` lines are
  measurements that are reported and not judged.
- `out\timeline-<time>.txt`: what the checks did and when, with every attempt of the process to
  take the keyboard focus (see below) under the step that caused it, and the windows that were in
  front during the run.
- `out\window-light.png`, `out\window-dark.png`, `out\close-dialog-light.png`,
  `out\close-dialog-dark.png`: the whole window in each theme with a project open, and the question
  it asks on closing. For a person to look at.
- `out\tree-*.txt`: the UI Automation tree of the window in each state the `accessibility` group
  reads: opening, the editor in three layouts, exporting, the question on closing, a recording
  without a camera, and a project that cannot be opened.
- Everything the tool writes while it checks goes under one folder,
  `%TEMP%\TinyClipsStudioPreviewCheck-<pid>` (the name comes from StudioPreviewCheck's
  `TestFolder`, which is shared): the project store, with a folder per project, and `exports`,
  where exported videos go. The last check of a run is that the folder is gone. Settings are kept
  in memory. Nothing is read from or written to `%LOCALAPPDATA%\TinyClips`, a packaged app's
  `LocalState`, the Videos folder, or the app's crash log.

## What it does to the machine

Someone may be working on the machine while the tool runs, so:

- **Its windows never come to the front.** A WinUI window that moves the keyboard focus to one
  of its controls also asks Windows for the keyboard focus, and Windows answers that by
  activating the window. The editor moves the focus when a project has opened, when an export
  starts and ends, and a dialog does when it opens and closes. `Host\ForegroundGuard.cs`
  therefore replaces, in this process only, the functions that activate a window or give it the
  focus (`SetForegroundWindow`, `SetActiveWindow`, `SetFocus`, `SwitchToThisWindow`, and the
  system calls behind the first three) with ones that do nothing and count the call, sets a hook
  on the UI thread that refuses every activation, and marks each window as one the system does not
  activate. It tests itself before the first window exists, and the tool stops if a call gets
  through. That is why the process must be x64: the replacement is twelve bytes of x64 code.
- **It says whether one of its windows was ever in front.** `Host\ForegroundWatch.cs` has Windows
  report every change of the foreground window, and reads the foreground window about every
  1.5 ms besides. If a window of the tool is ever in front it is hidden at once and the run stops.
  The report gives the longest gap between two reads, which is a garbage collection stopping every
  thread of the process for a few tenths of a second; the changes Windows reports are queued, so
  none is lost in a gap.
- Windows are shown without activation, behind every other window, and not in the taskbar or
  Alt+Tab. Screenshots are of the tool's own windows only.
- **No input is sent**: no keys, no pointer. See "What stands in for a person".
- **No sound.** The preview is created with `StudioPreviewOptions.ForceMuted`. Audible playback is
  not checked.
- No system, display, theme or power setting is changed. The theme is the app's own setting,
  held in memory.

## What is not the app

- `Host\StandIns.cs` has what the tool leaves out of the app:
  - `WindowIcon`: the app's class writes to the app's crash log when the icon cannot be set. The
    tool's windows are not in the taskbar and need no icon.
  - `MemorySettings`: the app's settings, in memory. They start with the Studio preview switch on,
    because the cleanup that follows a closed window runs only then.
  - `TempClipStorage`: exported videos go to `exports` in the temp folder, not to the Videos
    folder.
  - `NoRecorder`: the cleanup service asks whether a Studio recording is running. None ever is.
  - `GuardedStore`: the real `StudioProjectStore`, which refuses a root outside the temp folder,
    counts delete attempts, and never lets a cleanup throw (the app's cleanup service writes a
    cleanup that threw to the crash log).
- `Host\ToolServices.cs` builds `StudioWindowService` the way the app does, with those five, and
  replaces `ActivateWindow`: a window is shown once, without activation; a second request for the
  same window, which in the app brings it to the front, is counted.
- A window is told that it is active with the message Windows sends for it (`WM_ACTIVATE`),
  because Windows never activates it. A WinUI window starts its compiled bindings at its first
  activation, and the editor puts the focus on a control only while it is the active window. The
  same message tells a window that it is no longer active, for the checks of what a window in the
  background does.
- The app sets its theme for the whole application when it starts, which also colours the
  caption buttons. The tool shows both themes in one run, so it sets each window's caption button
  theme itself; the rest of the theme comes from the window's own root element, as in the app.
- It is a Debug build run from its build output, and the app ships as a published, packaged
  build. Neither is compiled ahead of time. The tool could not be: its UI Automation client is
  used through classic COM interop.

## What stands in for a person

- **Buttons, check boxes, radio buttons, swatches and sliders** are operated through their UI
  Automation patterns (Invoke, Toggle, SelectionItem, RangeValue), which work on a window that is
  not in front. The window's own close button is the Close button of its title bar, invoked the
  same way, so the window's question on closing is the real one.
- **Combo boxes** are set by `SelectedIndex` on the UI thread. An open drop-down is a window of
  its own, in front of other windows, so none is ever opened.
- **Keys are not pressed.** Where a check says "what the Space key runs", it calls what the
  window's key handler calls: `StudioShortcuts.Resolve` with that key, then `StudioViewModel.Run`.
  That the key reaches the handler, and what a focused control does with it first, is not checked.
- **Nothing is dragged.** The camera in the preview, the handles of the trim bar and a slider's
  thumb are never moved with a pointer. A slider drag as one undo step is checked by calling what
  the slider's row calls when a pointer takes hold of it and lets go (`BeginGesture`,
  `EndGesture`), with the values set through UI Automation in between.

## How the checks are built

- `Checks\WindowChecks.cs` runs the groups on a thread of its own and comes to the UI thread only
  for what cannot be done from outside. `Editor` is one window on one project, with the project
  as the checks expect it to be after every edit they made. The expected project is what the
  layout of a picture is worked out from, with `StudioLayoutResolver`, independently of the
  window.
- `open`: a project with screen and camera, with a screen only, and with its recording missing.
  The camera's place is also found from pixels alone, as what changes in the picture when the
  camera is hidden, and compared with the overlay's handle.
- `transport`: Play, Pause, the frame steps and the playhead as a slider, the end of the kept
  range, and Play again from there.
- `inspector`: every control. For each edit the picture has to show the edit, Undo has to give
  back the picture from before and Redo the edited one. Two pictures of the same state are not
  compared pixel for pixel: the preview keeps the texture a video frame is copied into while it
  is between one and two times the size needed, so the same frame in the same place can be scaled
  from another size than last time, which shows along its edges. A picture counts as given back
  when at most a fifth of the pixels the edit changed, and a twentieth of the others, differ.
- `trim`: the two handles and the playhead as sliders, a step as a screen reader takes it, the
  limits, Start here and End here, and where the handles are drawn.
- `export`: the overlay, Cancel, what Esc runs, the window's close button while an export runs
  (on a square canvas, which takes longer and leaves time to answer), and a finished export of a
  trimmed range. The file is read with ffprobe and decoded with ffmpeg: every frame has to show
  the screen and camera frames of the trimmed range where the layout puts them, and one frame is
  compared with the preview's picture of it at a grid of points.
- `close`: the question on a project that was never exported and each of its four answers, the
  draft opened again, and an exported project. After an answer the tool waits 0.7 s before it
  presses the close button again: the window asks one question at a time, and passes over its
  close button until the last question has finished closing.
- `windows`: two projects in two windows, a project opened twice, and, as the last thing of a
  run, the app exiting with two editors open (after which the window service opens nothing).
- `accessibility`: every element in the window's content has a name, every control a person
  operates has an automation id, and every slider reports its value, range, step and text. A
  pane that cannot take the focus may be without a name; the two panes in which the framework
  hosts XAML in a window are left out.
- `themes`: the pictures, and that each is what its name says.
