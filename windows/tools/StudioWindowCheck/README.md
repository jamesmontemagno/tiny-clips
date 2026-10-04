# StudioWindowCheck

Opens the app's Studio editor window in a process of its own and checks what it does. The
window's source files (`Views\Studio`, `Controls\Studio`, `ViewModels\Studio`, `Models\Studio`,
`Services\Studio`) are compiled into this tool, so the real window is checked and not a copy,
with the real project store, preview engine, preview panel and exporter behind it. `TinyClips.App`
is never started, and nothing is packaged, registered or installed.

Every result is read back by the tool itself, in one of these ways:

- a screenshot of its own window, taken with Windows.Graphics.Capture. Each frame of the test
  clips carries its frame number as a strip of black and white cells (see
  `..\StudioPreviewCheck\README.md`), so a screenshot says which frame of each clip is shown and
  where. For zooms and crops the edges of the test picture are found as well, which says which
  part of a clip is shown and how large: see "Which part of a clip a picture shows";
- the window's UI Automation tree, read with the client a screen reader uses, and the events the
  window sends that client: the sentences it asks to have read out, and which item of a list
  became the selected one;
- the frames of an exported file, decoded with ffmpeg;
- every scene the preview engine draws while it plays through a zoom, copied out of the texture
  it was drawn into through the engine's hook for check tools (`AfterRender`).

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

A full run takes about two minutes. It exits with 0 when every check passed and with 1 when one
did not, after printing one `FAILED:` line for each; 2 is a usage error.

| Option | |
| --- | --- |
| `--only a,b` | only these groups |
| `--skip a,b` | leave these groups out |
| `--out <folder>` | where reports, trees and pictures go (default `out` next to the project) |
| `--media <folder>` | where the test clips are, or are generated (default `..\StudioPreviewCheck\out\media` when it has them, otherwise `media` in the out folder) |
| `--held-up` | in the `zoom` group, play through a zoom that moves in a second time while the tool holds its own process up: see "A frame that comes late" |

Groups: `open transport inspector trim export close windows accessibility themes zoom crop`.

## What it leaves behind

- `out\report-<time>.txt`: what was printed. `pass` and `FAIL` lines are checks; `note` lines are
  measurements that are reported and not judged.
- `out\timeline-<time>.txt`: what the checks did and when, with every attempt of the process to
  take the keyboard focus (see below) under the step that caused it, and the windows that were in
  front during the run.
- `out\window-light.png`, `out\window-dark.png`, `out\close-dialog-light.png`,
  `out\close-dialog-dark.png`: the whole window in each theme with a project open, and the question
  it asks on closing. For a person to look at, as are the next two.
- `out\window-zooms-light.png`, `out\window-zooms-dark.png`: the whole window with three zooms on
  the lane, the middle one selected. One zoom follows the pointer and one is a suggestion, so
  both marks a block can carry are in the picture.
- `out\zoom-section-light.png`, `-2.png`, `-3.png`, and the same for `dark`: the inspector
  scrolled to its Zoom section with a zoom selected. The section is higher than the inspector, so
  there are three: from its heading, from the focus pad, and from its end.
- `out\zoom-preview.png`: the window right after Z added a zoom, with the preview zoomed.
  `out\zoom-moving-in.png`: a screenshot taken while the preview played through a zoom moving in.
- `out\tree-*.txt`: the UI Automation tree of the window in each state the `accessibility` group
  reads: opening, the editor in three layouts, exporting, the question on closing, a recording
  without a camera, and a project that cannot be opened; and, from the `zoom` group, with a zoom
  selected and with suggested zooms.
- `out\tab-order.txt`: the tab stops of the window with a zoom selected, in the order the focus
  moves through them.
- `out\open-failure-<time>-<n>.txt`, only when a window could not open its project: what the
  preview failed with, each exception with its error code, and what the preview engines wrote
  to their trace in the eight seconds before. The window itself shows one sentence, and the
  failed check says where this file is. See "An open that fails".
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
  Alt+Tab. Screenshots are of the tool's own windows only. Ending the capture of a window is a
  call into the system, and it was once seen not to return: a run stood still in it for six
  minutes. The tool makes that call on a thread of its own, goes on without it after 5 s, and
  says in the report how often that happened.
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
  window's key handler calls once it has mapped the key: `StudioWindow.RunShortcut`, which asks
  `StudioShortcuts.Resolve` what the key means in the window as it is, and runs that. That the
  window maps the Z and Delete keys is checked on `StudioWindow.MapKey`. For the arrow keys, Home
  and End on the zoom lane, the lane's own `HandleKey` is called, which is what its key handler
  calls. That a key reaches a handler, and what a focused control does with it first, is not
  checked.
- **Nothing is dragged or pressed with a pointer.** The camera in the preview, the handles of the
  trim bar and a slider's thumb are never moved. A slider drag as one undo step is checked by
  calling what the slider's row calls when a pointer takes hold of it and lets go
  (`BeginGesture`, `EndGesture`), with the values set through UI Automation in between. A press
  and a drag on the zoom lane and on the focus pad are checked by calling what their pointer
  handlers call with a place (`PressAt`, `DragTo`, `EndPress`). Which element a real pointer
  lands on, and that the element keeps the pointer while it is down, is not checked.
- **The focus is moved inside the window only.** The window never has the keyboard. Where the
  focus goes is read from XAML's own focus manager: the order of the tab stops by asking it to
  move the focus to the next stop over and over, and where the focus lands after a button
  switches itself off or goes away by putting it on the button first. Each of these asks
  Windows for the keyboard, and each request is refused and counted like every other.
- **No screen reader runs.** What one would be told is heard by a listener for UI Automation
  events inside the tool. It hears every event twice, a few milliseconds apart on two threads,
  including those of the framework's own controls, so the checks hold what the window's zoom
  lane sends against what a framework list sends for the same thing. Whether a screen reader
  hears an event once was not seen.

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
- `zoom`: the lane above the trim bar, Add zoom, what Z and Delete run, the lane's keys, its
  blocks as list items, Previous and Next, the Zoom section with every control of the selected
  zoom, the focus pad, the Start and End buttons, presses and drags on the lane, a zoom that
  follows the pointer, a zoom that moves in while the preview plays, suggested zooms, and undo and
  redo across an add, a move and a delete. Wherever a zoom changes the picture, the picture is
  read: see below.
- `crop`: the four crop sliders of the screen and of the camera, where an edge stops, a drag as
  one undo step, Reset crop and its undo, a zoom inside a crop, and where the keyboard focus goes
  after Reset crop. After each step the preview has to show the part of the clip the crop leaves.

## Which part of a clip a picture shows

A check that a picture is zoomed or cropped has to tell the right picture from a wrong one, and
colours do not: a picture of the wrong part of the screen is made of the same colours. So
`Checks\ZoomPicture.cs` finds the straight edges of the test clips, whose places in a clip are
known (between the colour bars, around the colour patches, and along the frame strip), and
compares where each is in the picture with where the wanted part of the clip puts it. The wanted
part is worked out by hand in a comment at each check, from the project format, not asked of the
layout code.

- An edge is placed by the brightness of the pixels across it. A video keeps the brightness of
  every pixel and the colour of every other one only, so the colours next to an edge are a
  mixture, while the brightness changes at the edge itself.
- An edge is read along every line that crosses it inside the picture, and counts only when at
  least two lines, and more than half of them, agree to three quarters of a pixel. The test clips
  move things across the picture (a striped band, dots, a line), and a line with one of those on
  it disagrees with the others. A wrong part of the clip moves an edge the same way on all of
  them.
- A picture passes when the frame strip reads the frame (where the strip is in the wanted part),
  at least two upright edges a fifth of the layer apart and two level edges 24 pixels apart were
  found, at most a third of the edges that should be there are missing, and no edge is further
  from its place than 0.75 pixels of the picture, or half a pixel of the clip where the clip is
  magnified so far that this is more.
- What a check cannot tell from the right picture is said in a comment at the check. All the
  level edges of the screen clip are in its top left, so a part without the frame strip and with
  only the patches places the picture down the screen less surely than across. A zoom that looks
  at the middle of the screen is what a preview that ignored the focus would show too.
- That the checks can fail was tried on a copy of the tree, with two faults put into
  `StudioLayout.cs` one after the other. With a held window that ignores where the zoom looks,
  21 checks failed: all 14 that read a picture of a zoom which does not look at the middle of
  what it is in, the focus pad's rectangle, the count of frames drawn while a zoom moves in
  (their numbers could not be read), and five checks of suggested zooms, which are worked out
  with the same function. With a zoom that moves in at an even pace, the check of eases that are
  shortened failed, every scene of the zoom that moves in, the count of frames, and the
  screenshots taken while it moved; the check half way through an ease passed, as its comment
  says it would.

## A frame that comes late

While the preview plays, the `zoom` group copies every scene the engine draws and reads which
frame it shows and which part of the screen. Each has to show the part the format gives for the
frame it shows.

With `--held-up` the same is done a second time while the tool gives its garbage collector work:
a new buffer of eight megabytes for every screenshot, as the tool did before it took screenshots
into one buffer. Each collection stops every thread of the process, the preview's among them,
for some tens of milliseconds, as other things do to an app on a busy PC. The scene that is
drawn after such a stop has shown the picture of one frame with the part of the screen of the
next one: see the report of the run. Without the option the tool keeps out of the preview's way:
it makes its buffers and has the garbage collector run before the playing starts, and takes its
screenshots into one buffer. That play-through is the one the group's other numbers are about.

With the engine as it is now the `--held-up` check fails: 11 of about 538 scenes were wrong in
the runs made while it was written, and 2 of 90 in one run made after it was merged. It is kept
as the way to see that fault, and is not part of a run without the option.

## An open that fails

Every editor the tool opens gets its preview from the app's factory with two differences: the
sound is forced off, and the engines write their trace into memory (`Host\PreviewOpenLog.cs`).
When a window says that its project cannot be opened, the check "the editor becomes ready"
fails with the sentence the window shows, the exceptions behind it with their error codes, and
the path of a file with the trace of the seconds before.

This is here because it happened once, in one of six full runs on 4 October 2026: two editors
opened one after the other, each 2 ms after the one before it was closed, both said "The screen
recording could not be decoded (DecodingError)", and the next one, 0.4 s later, opened. At that
time the tool kept nothing but the sentence. It did not happen again in 24 runs of
`--only inspector,trim,export`, which open the same three editors in the same order.
