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
  where. For zooms, crops and scenes the edges of the test picture are found as well, which says
  which part of a clip is shown, how large and where: see "Which part of a clip a picture shows"
  and "Where a scene puts a layer";
- the window's UI Automation tree, read with the client a screen reader uses, and the events the
  window sends that client: the sentences it asks to have read out, and which item of a list
  became the selected one;
- the frames of an exported file, and the samples of its sound, decoded with ffmpeg;
- every picture the preview engine draws while it plays through a zoom, into a scene, over a
  cut and through a speed change, copied out of the texture it was drawn into through the
  engine's hook for check tools (`AfterRender`);
- what an editor asks its preview to play at, and when, written down on its way to the preview
  engine: see "Speed changes".

**Last run on 7 October 2026.** Everything below that says it was written on 5 or 6 October
"and not run" has run now, for the first time that day: see "The first runs, on 7 October
2026" under "The inspector as a rail with one panel on show", and "Three checks on a smaller
preview". Seven full runs were made that day. The first, of the checks as they had been
written, had 7 of 551 fail (two parts ended early). The checks were mended, and of the six
runs after that four had one check fail, a different one in three of them, and two had none:

- 1 of 559: the trim bar's Start handle was read 188.6 px from its place. Not explained; the
  check has said more about itself since, and has not failed again.
- 1 of 559, twice: the zoom's block was read between getting its place and getting its
  width. The check's, and mended with the handles of 7 October.
- all 559 passed.
- 1 of 573, with the handles and their checks in: one view model was still in memory at the
  end, of a window that Esc had closed, and neither the window nor its inspector. That is
  how "A closed window that stays in memory" describes a window that is closed while a
  tooltip waits, which is the framework's. It was not looked into further, and it is the
  first time a full run ended on it.
- all 573 passed, in 355 s: the last run of the day, of the tool and the editor as they
  were committed.

None of the checks that failed on 7 October was failed by a fault of the editor's: each was
the check's, or the PC being busy, except the first of the four above, which is open. The
one fault of the editor's that the day found came before any check: the window could not be
made (e40ad26).

Where a section below says "not run", "has not run" or "no run has made that check yet", it
was written before 7 October and is left as it was written, as the record of what was
expected. What the runs then showed is in the two places named above.

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

A full run took about four minutes and fifty seconds in each of three runs on 5 October
2026, with 454 checks, before the checks of that evening were added; the `speed` group alone
took 65 s.
It exits with 0 when every check passed and with 1 when one did not, after printing one
`FAILED:` line for each; 2 is a usage error.

| Option | |
| --- | --- |
| `--only a,b` | only these groups |
| `--skip a,b` | leave these groups out |
| `--out <folder>` | where reports, trees and pictures go (default `out` next to the project) |
| `--media <folder>` | where the test clips are, or are generated (default `..\StudioPreviewCheck\out\media` when it has them, otherwise `media` in the out folder) |
| `--held-up` | in the `zoom` group, play through a zoom that moves in a second time while the tool holds its own process up: see "A frame that comes late" |
| `--late-drags <n>` | in the `scene` group, how often a drag is begun in the last frame of a scene (default 20, at least 2): see "Something dragged while the preview plays" |
| `--as-before` | judge the three checks that failed on 5 October 2026 as they did then, and say what the tool's way would have read: see "Three checks on a smaller preview". (`--as-prepared`, which asked for the tool's way before it was that, is taken and does nothing) |
| `--memory` | run none of the groups: open and close windows with one thing done to each, and say which are still in memory afterwards: see "A closed window that stays in memory" |

Groups: `open transport inspector trim export close windows accessibility themes zoom crop scene cut speed`.

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
  with its Zoom panel on show and a zoom selected. The panel is higher than the inspector, so
  there are three: from its top, from the focus pad, and from its end.
- `out\zoom-preview.png`: the window right after Z added a zoom, with the preview zoomed.
  `out\zoom-moving-in.png`: a screenshot taken while the preview played through a zoom moving in.
- `out\window-timeline-light.png`, `-light-smallest.png`, and the same for `dark`: the whole
  window on a recording with a camera that has three scenes, two zooms, two cuts and two speed
  changes, a faster and a slower one, the second cut selected, at the size the window opens
  with (1180 × 760) and at the smallest size it can be given (980 × 640).
- `out\scene-side-by-side.png`: the window with the playhead in a second scene that is side by
  side. `out\scene-moving-in.png`: paused half way through the move into it.
  `out\scene-looks-light.png`, `-dark.png`: the scene lane with the playhead in the second of
  three scenes.
- `out\cut-looks-light.png`, `-light-selected.png`, and the same for `dark`: the window with one
  cut of four seconds and one zoom, the cut not selected and selected.
- `out\speed-looks-light.png`, `-light-selected.png`, and the same for `dark`: the window with
  four speed changes, two long ones with their rate written on them and two short ones with
  their mark alone, a faster and a slower one of each, over a zoom and a cut of the same
  length; and the same with the first speed change selected.
- `out\project-section-light.png`, `-dark.png`: the window with the inspector's Project panel
  on show, where Tiny Clips badge, Keep this project and Save as default look are.
  `out\cannot-be-shown-light.png`, `-dark.png`: the window of a project whose file cannot be
  read, with the button that saves its screen recording.
- `out\lane-handles-zoom.png`, `-cut.png`, `-speed-change.png`: the window with a block too
  narrow for handles inside its ends selected on that lane, so that its two handles stand
  outside it. For a person to look at; nothing reads them.
- `out\panel-scene-light.png` and so on, one for each of the nine panels of the inspector in
  each theme: the window with that panel on show, from its top, with one of three zooms
  selected. Each panel is first laid out when it is first shown, and these are for a person
  to look at each of them once.
- `out\failed-<name>.png`, and for most of them `out\failed-<name>.txt`, only when a check
  that reads a picture did not hold: the screenshot the check read, and beside it every line
  that was read across an edge with what it found there, or the numbers of the points that
  were compared. The failed check says the name. A run does not remove the ones of the run
  before it.
- `out\tree-*.txt`: the UI Automation tree of the window in each state the `accessibility` group
  reads: opening, the editor in three layouts, exporting, the question on closing, a recording
  without a camera, and a project that cannot be opened; from the `zoom` group, with a zoom
  selected and with suggested zooms; from the `scene` group, with three scenes; from the `cut`
  group, with a cut selected; from the `speed` group, with a speed change selected; and from
  the `open` group, of a project that cannot be shown and offers its recording
  (`tree-cannot-be-shown.txt`).
- `out\tab-order.txt`, `out\tab-order-scenes.txt`, `out\tab-order-cuts.txt`,
  `out\tab-order-speed.txt`, `out\tab-order-project.txt`, `out\tab-order-cannot-be-shown.txt`:
  the tab stops of the window with a zoom selected, with three scenes, with a cut selected,
  with a speed change selected, of the inspector through its Project panel, and of the
  window of a project that cannot be shown, in the order the focus moves through them. The
  inspector shows one panel at a time, so each of these lists is put together from a walk
  with each panel on show: see "The inspector as a rail with one panel on show".
- `out\tree-<state>-<panel>.txt`: where the window's UI Automation tree is saved for a state
  of an editor, there is one file for each panel of the inspector, for the same reason.
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
  The report gives the longest gap between two reads and how much of it was a garbage collection,
  which stops every thread of the process: about a tenth of a second in a full run. The changes
  Windows reports are queued, so none is lost in a gap.
- Windows are shown without activation, behind every other window, and not in the taskbar or
  Alt+Tab. Screenshots are of the tool's own windows only. Ending the capture of a window is a
  call into the system, and it was once seen not to return: a run stood still in it for six
  minutes. The tool makes that call on a thread of its own, goes on without it after 5 s, and
  says in the report how often that happened.
- **One drop-down list is opened**, for about half a second, in the `close` group: see "Combo
  boxes" under "What stands in for a person". Activating it is refused like every other
  activation. Whether it shows in front of anything is read while it is open, and fails a
  check if it does. No run has made that check yet.
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
    folder. It writes down every name it gives out. A check can have the next name it gives
    out taken, by a file it writes under that name before handing it back, and can have
    something done in the middle of the next request for a name: both stand for a file that
    something else saves while a video is being made. It can have every name taken from then
    on as well, for a file that finds no free name however often it asks.
  - `NoRecorder`: the cleanup service asks whether a Studio recording is running. None ever is.
  - `GuardedStore`: the real `StudioProjectStore`, which refuses a root outside the temp folder,
    counts delete attempts, and never lets a cleanup throw (the app's cleanup service writes a
    cleanup that threw to the crash log). A check can have it refuse the next request to keep
    a project, once, and every save of one project until told otherwise, each with an
    `IOException` and a sentence of the check's, as a disk that is full does. And a check can
    have it say, once, that a project's screen recording is somewhere else: at a pipe
    (`Host\SlowRecording.cs`) that hands out the first half of the recording's bytes to
    whoever opens it as a file, and the rest, and the end of the file, when the check says
    so. The copy that the editor makes of the recording is then under way for as long as the
    check needs to do something in the middle of it. Nothing of a pipe is to be seen.
- `Host\ToolServices.cs` builds `StudioWindowService` the way the app does, with those five, and
  replaces `ActivateWindow`: a window is shown once, without activation; a second request for the
  same window, which in the app brings it to the front, is counted. What the window service
  hands the app is written down: every export it reports as finished, every screen recording
  it reports as saved from a project that cannot be shown, and every error it reports for a
  window that is closing or gone (`ErrorReported`), as the kind of the error and its sentence. In the
  app the first two finish the clip as any saved video is, and the third is shown as a
  notification; none of that is in the tool.
- **Whether people can be found in a camera picture** is the tool's to say
  (`ToolServices.PeopleCanBeFound`). The app answers that for each editor it opens by whether
  the model that finds people is next to it (`StudioPersonFinders.IsAvailable`). No such file is
  next to the tool, and none is put there. The answer is no, as in the app today, except for the
  windows the check of the camera's Background choice opens. Nothing finds people in the tool
  either way, so no picture is read for that choice.
- **A preview that is watched.** For a project a check names before it opens it
  (`ToolServices.PlaybackRates.Watch`), the editor is given the preview engine inside
  `Host\WatchedPreview.cs`, which writes down every request for a playback rate, with where the
  engine said it was and whether it was playing, and passes everything on. The preview panel is
  handed the engine itself. Every other window of the tool has the engine without anything in
  front of it, as the app has.
- A window is told that it is active with the message Windows sends for it (`WM_ACTIVATE`),
  because Windows never activates it. A WinUI window starts its compiled bindings at its first
  activation, and the editor puts the focus on a control only while it is the active window. The
  same message tells a window that it is no longer active, for the checks of what a window in the
  background does.
- A window is told that it has the keyboard (`WM_SETFOCUS`), and that it no longer has
  (`WM_KILLFOCUS`), only where a check needs the framework to ask or tell a control about a
  focus that moves: see "The focus is moved inside the window only" under "What stands in for
  a person". Nothing takes the keyboard.
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
- **Combo boxes** are set by `SelectedIndex` on the UI thread, and their lists stay closed,
  with one exception: for what Esc does while a list is open (`close`), the canvas drop-down
  is opened through UI Automation, as a screen reader opens it, for as long as it takes to
  hand the key over, and closed again the same way. Whether an open list is a window of its
  own, and whether such a window is in front of anything, had not been looked at before that
  check was written; the tooltip of a window in the background, which the framework makes the
  same way, is a window of its own and is not in front (see "A closed window that stays in
  memory"). So the check reads what the open list was to the system, and a check of its own
  fails if it was in front.
- **Keys are not pressed.** Where a check says "what the Space key runs", it calls what the
  window's key handler calls once it has mapped the key: `StudioWindow.RunShortcut`, which asks
  `StudioShortcuts.Resolve` what the key means in the window as it is, and runs that. For Esc
  the method is also told that Ctrl, Shift or Alt is held and that the key is being held, which
  the window's handler reads from the keyboard and from the key event: that reading is not
  run. Two presses of Esc in quick succession are made by handing the window the second one
  on the UI thread, in the window's own turn in which the export that the first one stopped
  is gone: from a handler for the view model's `PropertyChanged`, added after the window's
  own. That the window maps the R, S, X, Z and Delete keys is checked on
  `StudioWindow.MapKey`. For the arrow
  keys, Home and End on a lane, the lane's own `HandleKey` is called, which is what its key
  handler calls. Delete on the scene lane is checked with the focus put on the lane inside the
  window, which is what the window's key handler asks about. That a key reaches a handler, and
  what a focused control does with it first, is not checked. Neither is the first thing the
  window's handler does with a key: while a question is open it returns at once
  (`if (_isPromptOpen) { return; }` in `StudioWindow.OnRootKeyDown`), so that no key runs
  anything under a question. `RunShortcut`, which the checks call, comes after that line, so
  nothing in this tool runs it: it has no check and no fault.
- **Nothing is dragged or pressed with a pointer.** The camera in the preview, the handles of the
  trim bar and a slider's thumb are never moved. A slider drag as one undo step is checked by
  calling what the slider's row calls when a pointer takes hold of it and lets go
  (`BeginGesture`, `EndGesture`), with the values set through UI Automation in between. A press
  and a drag on each of the four lanes and on the focus pad are checked by calling what their
  pointer handlers call with a place (`PressAt`, `DragTo`, `EndPress`). A drag of a trim handle
  that goes on to where the trim is refused is checked by what the bar asks the editor for: a
  gesture with one request for each move. What a press on the trim bar does to the selection is
  checked by what the bar asks for as well: its playhead and a handle are set through UI
  Automation, which makes the bar raise the request that a press on it and on a handle raises,
  and the gesture that a press on a handle begins and its release ends is begun and ended on
  the view model. The bar's own pointer handlers are never run. Which element a real pointer
  lands on, and that the element keeps the pointer while it is down, is not checked.
- **A drag while the preview plays** is made the same way. The view model is told that a
  gesture begins, as the overlay's handle and a slider's row tell it when a pointer takes hold
  (`BeginGesture`); the values are then set a frame's time or two apart, the camera's place by
  `MoveBubbleTopLeft`, which the handle calls for every move, and a slider's through the
  property its slider is bound to; and the gesture is ended. Right before the first change the
  tool keeps the UI thread busy for 70 ms, as it is on a busy PC: the preview has shown two
  frames more by then than the editor has heard of. A drag in the last frame of a scene is
  begun on the UI thread right after the view model reports the playhead in that frame, in
  every other try after 40 ms of the same. A zoom is held on its lane, for the keys that wait
  while something is dragged, by `PressAt` and `DragTo`. That the first move of a real pointer
  on a slider's thumb or on the handle reaches the view model as a gesture is not checked.
- **The focus is moved inside the window only.** The window never has the keyboard. Where the
  focus goes is read from XAML's own focus manager: the order of the tab stops by asking it to
  move the focus to the next stop over and over, and where the focus lands after a button
  switches itself off or goes away by putting it on the button first. Each of these asks
  Windows for the keyboard, and each request is refused and counted like every other.
  **In a window without the keyboard the framework moves the focus and says nothing about
  it to the controls:** it asks none before the focus comes or goes (`GettingFocus`,
  `LosingFocus`) and tells none afterwards (`GotFocus`, `LostFocus`). That is the
  framework's rule (`CFocusManager::CanRaiseFocusEventChange` in its source), and it was seen
  on 7 October 2026: a handler of the inspector for `GettingFocus` was never called in a run
  that moved the focus into the rail dozens of times. So where a check is about what one of
  those four does, the window is told that it has the keyboard for as long as the check
  needs it, with the messages Windows sends for that (`WM_SETFOCUS`, `WM_KILLFOCUS`, to the
  window inside it that the framework asks the keyboard for), as it is told that it is
  active. The keyboard itself stays where it is. One check does that: the focus put on an item
  of the rail that is not the chosen one (`inspector`). Everywhere else the window has not
  been told, and a handler for one of the four would not run; the Studio window has one, the
  rail's. A group
  of radio buttons is one stop and is listed by the group: which of its buttons the Tab key
  lands on is the group's doing for a focus that comes from the keyboard, and a focus moved
  this way stays on its first button. Where the window itself puts the focus, which it does
  after everything else it has to do, is read once the UI thread has come to what waits at
  that priority. What is given the focus under a question that is open is noted by a handler
  for `GotFocus` on the window's content: a question is drawn in a layer of its own, so its
  own buttons are not among what is noted. **That handler has never been called:** the two
  checks that use it (`close`) do not tell the window that it has the keyboard, so "nothing
  under the question was given the focus" is true of every run whatever the window does.
  What those checks show is where the focus is, read from the focus manager, once the window
  has done what it had waiting. Telling the window for them was not tried: whether a
  question behaves the same in a window that has been told is not known.
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
  camera is hidden, and compared with the overlay's handle. Then a project whose file cannot
  be read (`Checks\CannotBeShown.cs`): see "A project that cannot be shown".
- `transport`: Play, Pause, the frame steps and the playhead as a slider, the end of the kept
  range, and Play again from there.
- `inspector`: every control. For each edit the picture has to show the edit, Undo has to give
  back the picture from before and Redo the edited one. Two pictures of the same state are not
  compared pixel for pixel: the preview keeps the texture a video frame is copied into while it
  is between one and two times the size needed, so the same frame in the same place can be scaled
  from another size than last time, which shows along its edges. A picture counts as given back
  when at most a fifth of the pixels the edit changed, and a twentieth of the others, differ.
  The camera's shapes are told apart by three points near the corner of the camera's rectangle,
  which a shape covers or leaves to the picture without the camera. That picture is the one
  taken right before the camera's checks, by going to the layout Screen and back. An earlier
  one is taken at the layout Screen, a note says what each corner check reads against either,
  and the checks go by the earlier one only with `--as-before`: see "Three checks on a smaller
  preview".
  Then the camera's Background choice (`Checks\CameraBackground.cs`): that it is not there
  where people cannot be found, nor in a recording without a camera; that Keep, Blur and Remove
  are radio buttons by those names between Mirror and Border, one tab stop together; that Blur
  and Remove set the project and each is one undo step; the note while the background is
  removed; and that the choice goes and comes with the camera's other styling, is saved with
  the project, and is part of a saved look. No picture is read for it: what a blurred or a
  removed background looks like is the renderer's, and `StudioRenderCheck` checks it.
  Then Keep this project (`Checks\KeepProject.cs`): see the section of that name.
- `trim`: the two handles and the playhead as sliders, a step as a screen reader takes it, the
  limits, Start here and End here, and where the handles are drawn.
- `export`: the overlay, Cancel, what Esc runs, the window's close button while an export runs
  (on a square canvas, which takes longer and leaves time to answer), and a finished export of a
  trimmed range. The file is read with ffprobe and decoded with ffmpeg: every frame has to show
  the screen and camera frames of the trimmed range where the layout puts them, and one frame is
  compared with the preview's picture of it at a grid of points. Then an export whose name is
  taken by another file while it runs (`Checks\ExportName.cs`): see "An export whose name is
  taken".
- `close`: the question on a project that was never exported and each of its four answers, the
  draft opened again, and an exported project. After an answer the tool waits 0.7 s before it
  presses the close button again: the window asks one question at a time, and passes over its
  close button until the last question has finished closing. Then the close button after an
  export has ended behind the question about it: see "The close button while a question is
  open". Then what Esc does (`Checks\ClosingByEscape.cs`): see "Esc closes the window". Then
  a save that fails (`Checks\ClosingUnsaved.cs`): see "A save that fails as a window closes".
- `windows`: two projects in two windows, a project opened twice, and, as the last thing of a
  run, the app exiting with two editors open (after which the window service opens nothing).
- `accessibility`: every element in the window's content has a name, every control a person
  operates has an automation id, and every slider reports its value, range, step and text. A
  pane that cannot take the focus may be without a name; the two panes in which the framework
  hosts XAML in a window are left out.
- `themes`: the pictures, and that each is what its name says. For the window with scenes,
  zooms, cuts and speed changes, at both sizes: the ten items of the transport row are on one
  row in their order, the four lanes and the trim bar are one above the other and equally wide,
  all of it whole inside the window, and the canvas is at least 400 × 225. For the Project
  panel: Tiny Clips badge, Keep this project and Save as default look are whole in the picture,
  one under the other. For each panel of the inspector: its name is over the inspector and a
  control it always has is in the window, which says the picture is of that panel and
  nothing about what it looks like. For a project that cannot be shown: the button is whole
  in the picture, under the message, and enabled.
- `zoom`: the lane above the trim bar, Add zoom, what Z and Delete run, the lane's keys, its
  blocks as list items, Previous and Next, the Zoom panel with every control of the selected
  zoom, the focus pad, the Start and End buttons, presses and drags on the lane, a zoom that
  follows the pointer, a zoom that moves in while the preview plays, suggested zooms, and undo and
  redo across an add, a move and a delete. Wherever a zoom changes the picture, the picture is
  read: see below.
- `crop`: the four crop sliders of the screen and of the camera, where an edge stops, a drag as
  one undo step, Reset crop and its undo, a zoom inside a crop, and where the keyboard focus goes
  after Reset crop. After each step the preview has to show the part of the clip the crop leaves.
- `scene`: a recording without a camera, which has no scene controls; one scene; Split by S and
  by both buttons, where it is refused and why; a layout for each scene; everything that follows
  the playhead into another scene; the picture on both sides of a line and frame by frame
  inside a move; a scene that is cut to; the transition's Duration; the three Start buttons; Previous and Next;
  the lane's keys, its blocks as list items, presses and drags on it; what the scene the
  playhead is in looks like, in both themes; Delete with the focus on the lane and Delete scene;
  a scene that is come into while the preview plays, with what that costs the UI thread; the
  handle on the camera, which is the current scene's; names, tab order, and undo and redo; and
  what is dragged while the preview plays (`Checks\SceneDragging.cs`): see "Something dragged
  while the preview plays".
- `cut`: the empty lane; Cut by X and by both buttons, where a cut is already and where none
  fits; the lane's keys and its blocks as list items; Previous and Next; the six buttons of
  Start and End; that a zoom or a cut is selected and never both; Delete and Delete cut; undo
  and redo; names and tab order; presses and drags on the lane; what a press on each row of the
  timeline does to the selection (the next point); what a cut looks like, in both themes; the
  gaps in the trim bar, read from its pixels; a trim that the cuts do not allow; the time and
  the picture with the playhead inside a cut; playing over a cut; Play pressed inside a cut; and
  an export of a project with cuts and scenes, decoded frame by frame.
- `speed`: see "Speed changes".
- A press and the selection, in `cut`: one project with two scenes, two cuts and two zooms.
  With a cut selected, the zoom lane is pressed where it is empty, right above that cut, and
  nothing is selected afterwards; and the other way round, with a zoom selected and the cut lane
  pressed right below it. `StudioEditorSession.SelectNothing` is what both lanes call for it:
  selecting no zoom would leave a selected cut. Then, with the cut selected and again with the
  zoom, a press on a scene, the playhead moved along the trim bar, and a handle of the bar
  taken and let go where it is: each leaves the selection as it is.
- At the end of every run, whatever groups it had: no window that was closed is still in memory.
  See "A closed window that stays in memory".

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

## Where a scene puts a layer

A scene of a project is a layout: where the screen recording and the camera are on the canvas,
and which part of its recording each shows there. `Checks\LayoutPicture.cs` reads a picture
against the layout of one frame, with the reading described above, once for each layer: the
edges of the layer's test clip have to be where the layer's rectangle and its part put them, and
its frame strip has to read its frame there. For the screen recording that is the frame the
picture should show, and for the camera the frame that goes with it, which the tool works out
from the camera's later start.

- Where the layers should be is asked of `StudioLayoutResolver`, with the project as the checks
  expect it and the instant the frame stands for, which is its middle. It is not worked out by
  hand as the part a zoom shows is: the resolver is what the format's fixtures and
  `StudioRenderCheck` hold the renderer against. The preview draws from it as well, so a fault
  in it would be in a picture and in what the picture is read against alike. For the move into a
  scene, one check therefore works the rectangles out a second time, from section 6.9 of the
  project format: the two scenes at rest, and a share k = u × u × (3 − 2u) of the way from the
  one to the other, where u is how much of the move has passed. Two faults put into a copy of
  `StudioLayout.cs` were caught by it: see "Faults that were tried".
- A layer is read only where it shows: inside its rounded outline and its border, and for the
  screen recording not where the camera lies over it. A layer that only one of two scenes has
  fades while the other scene is entered by moving, and is not read while it does: what shows
  through it is in its colours.
- The tolerance is the one above, 0.75 pixels. Paused, the furthest edge of the pictures that
  were read for a scene was 0.60 px from its place, and no further inside a move. From one frame
  of a move to the next the screen's rectangle moves by 56 to 109 px, so a picture that showed a
  frame where a neighbouring frame has its layers would be that far off.
- Two frames of the screen clip read further off than 0.75 px at one edge, paused as well as
  playing: at frame 90 the top of the lime patch by 0.85 px, and at frame 142 the bottom of the
  blue patch by 0.97 px. The clips are ffmpeg's moving test picture with the landmarks drawn over
  it, and in those frames what moves in it lies against the edge on enough lines to move it. A
  check of a play-through therefore looks again at every frame of which the drawn picture was
  right but for one or two edges, with the preview paused on that frame, and passes it when the
  paused picture has the same edges as far off, to a quarter of a pixel. A layer that was drawn
  in the wrong place has all its edges off, and differs from the paused picture. The picture a
  play-through starts from is such a paused picture, and it is judged as any picture at rest
  is, with no edge let off: playing over a cut starts at frame 90, and since the window
  without a camera has a screen of 1019 × 573, the size at which that frame reads so, that
  check fails. What is wrong then says that the picture is in its layout but for that edge.
  With `--as-prepared` such a picture passes, and a note says which edge and by how much (see
  "Three checks on a smaller preview").
- Frames of an exported video may be half a pixel further off, 1.25 px: they have been through
  an encoder once more than the preview's picture. The furthest edge in the 75 frames of the
  export that is decoded was 1.13 px from its place.

## A frame that comes late

While the preview plays, the `zoom` group copies every scene the engine draws and reads which
frame it shows and which part of the screen. Each has to show the part the format gives for the
frame it shows. A scene is here the engine's word for one picture it draws (`DrawScene`), and
the reports of the `zoom` group use it so. The `scene` and `cut` groups, where a scene is a part
of the project, say picture.

With `--held-up` the same is done a second time while the tool holds its own process up: about
twice a second it has the garbage collector run, which stops every thread of the process, the
preview's among them, as other things do to an app on a busy PC. A run of the collector takes
as long as what it has to look through, so the tool first makes small objects for it, half a
million at a time, until one run takes fifty milliseconds: 4.5 million when this was written.
The scene that is drawn after such a stop has shown the picture of one frame with the part of
the screen of a later one: see the report of the run. Without the option the tool keeps out of
the preview's way: it makes its buffers and has the garbage collector run before the playing
starts, and takes its screenshots into one buffer. That play-through is the one the group's
other numbers are about.

The option used to need nothing of that. Until the windows a run had closed left memory (see
"A closed window that stays in memory"), every run of the collector in this process took some
tens of milliseconds by itself, and later in a run some hundreds, and the option only made the
collector run more often. With the windows gone a run of it takes a few milliseconds, and held
up the old way the check passed: so the stops are now made on purpose.

With the engine as it is now the `--held-up` check fails: 5 of 89 scenes of the move were wrong
in the run made when the stops were made on purpose, each drawn 68 to 132 ms after the scene
before it, with the part of the screen of the frame after it or of one up to three frames on.
Held up the old way, 11 of about 538 scenes were wrong in the runs made while the option was
written, and 2 of 90 in one run after it was merged. It is kept as the way to see that fault,
and is not part of a run without the option.

## Playing into a scene and over a cut

The `scene` group plays from 2.5 s over the line at 4.0 s into a second scene, which is side by
side and entered by moving for 0.35 s. The `cut` group plays from 3.0 s over a cut from 4.0 to
5.0 s. Every picture the engine draws on the way is copied and read against the layout of the
frame it shows (`Checks\ScenePlaying.cs`, `Checks\CutPlaying.cs`).

- Which frame a picture shows is read from the screen's frame strip, looked for where each of
  the frames around the last one has the screen. A picture that is wrong is then held against
  the layouts of the eight frames before and after its own, so the report says when a frame was
  drawn with the layout of another instant, and of which.
- The preview has a player for each recording. A picture in which the camera shows the frame
  before or after the one that goes with the screen's frame is counted in a note and not
  failed; with the camera two frames off it fails.
- While the time is measured the tool asks the window nothing through UI Automation: such a
  question is answered on the UI thread and takes it some tens of milliseconds. It follows the
  playhead through the view model's own notification instead.
- **What a change of scene costs.** When the playhead comes into another scene, everything that
  shows the current scene is refreshed: the lane's selected item, the Scene panel, Layout, the
  camera's controls and the handle in the preview. The check records what the view model
  reports, and has another thread ask the UI thread a question every millisecond. The current
  scene has to be reported once, nothing may ask for everything to be refreshed, the four lanes
  may not place their blocks again, and the UI thread may not take a tenth of a second to
  answer. On a quiet machine it took 15 to 19 ms, against 1.5 to 2.5 ms for the slowest frame
  before the change. About 10 ms of that is the first showing of the controls that only the new
  scene has, since a control is built when it is first shown: with those built ahead of time
  the same change took 6 to 9 ms in twenty-one runs. The window does not build them ahead of
  time. It did for a while, because one run had measured 105 ms. In that run the preview's
  frames had stood still for as long, which a busy UI thread does not make them do, and a run
  of the garbage collector does: it stops every thread. The time the collector holds the UI
  thread between a question and its answer is now taken off, and reported next to the result.
  What is left is still the time on the clock, and on a machine that is busy with something else
  every answer comes late: 72 ms was seen, with 18 ms for the slowest frame before the change.
  So a time above a tenth of a second is let pass when the frames before the change were slow
  enough to account for it, at fifteen times their slowest.
- **The pace.** Of the 30 frames around the line, how many were never drawn and how many came
  more than a frame's time late, against the 29 frames before them. Those begin after the
  frame that was on screen when Play was pressed: the preview does not draw that one a second
  time and goes on with the next, which a note says in every run. Two more are allowed, and
  the check names the frames that were left out. A machine that is busy with something else
  leaves frames out anywhere: in 7 of 86 play-throughs one to three frames were left out or
  late together, four times before the line and three times around it, where the most was two
  left out and one late, which the check still lets pass. So that this can be told from the
  tool's own doing, the check says how often the tool's garbage collector ran while the
  preview played and for how long it held every thread; in the runs since it says so, the
  collector did not run once. With a preview that stands still for 170 ms on the first frame
  of the second scene, four frames were left out and the check failed: see "Faults that were
  tried".
- **Over a cut.** The session sends the preview on to the end of the cut when it hears that the
  playhead has come to it, which is after the first frame of the cut has been drawn. In 81 of 82
  runs one frame of the cut was drawn and in one run two, and the first frame after the cut came
  134 to 228 ms after the last one before it, where two frames that follow each other are 33 to
  41 ms apart. The check allows nine frames of the cut and half a second. The playhead may never
  be reported inside the cut, and the time that is shown may not go back, nor on by more than
  0.2 s.

Two things were seen of the preview engine while it played into a scene, neither of them the
window's doing:

- In 16 of 86 runs, one to three of the some eighty pictures that are read showed the camera's
  frame next to the one that goes with the screen's frame: 20 pictures of about 6,900 in all,
  15 with the frame after it and 5 with the frame before, anywhere in the playing and not at
  the line between the scenes. That is the note above.
- Once in 86 runs a picture showed screen frame 118 laid out as frame 120 is: the frame of one
  instant with the layout of another, two frames on. That is what the `zoom` group shows with
  `--held-up` (see "A frame that comes late"), here without the tool holding anything up. It
  fails the check.

## Speed changes

The group was written, and the checks around it changed, while the machine was not to be used
for anything but compiling. It ran for the first time on 5 October 2026 in the evening: all 76
of its checks passed, in a run of the group alone, which took 65 s, and in three full runs. So
the numbers its checks expect, which were worked out by hand from the editor's rules and the
test clips and are written out in a comment at each check, and the limits of the checks that
read pixels, which were set from the brushes and the sizes in the XAML and not from a
picture, fit the window as it is. What is still not known is whether each check can fail:
twenty-four faults are ready in a script (see "Faults that were tried") and none has been
tried.

What the group checks, in `Checks\Speed.cs`, `SpeedLane.cs` and `SpeedPlaying.cs`:

- **Adding.** The empty lane; R, Speed in the transport row and Add speed change in the
  Speed panel; where a speed change already is, and where the shortest does not fit; what is read
  out; the block's name; the panel for the selected one; and the time, which counts the video
  and so gets shorter for a faster stretch.
- **The lane.** That it is on the trim bar's time scale; its keys; its blocks as list items;
  presses and drags, by what the lane's pointer handlers call: a press on a block and on the
  empty lane, a drag of the body, of the handle at the start and of the one at the end,
  against a neighbour and against the ends of the recording, a block too narrow for handles
  inside its ends, and each drag as one undo step; then the handles themselves: see "The
  handles at the ends of a block".
- **The section.** Previous and Next with what they read out and where the focus goes at the
  ends; the choice of the six rates, each shown as its rate and named in words, with the
  block's mark and the video's length following a choice, each choice one undo step, and a
  rate chosen again changing nothing; the six buttons of Start and End; Delete and Delete speed
  change with where the focus goes; undo and redo across an add, a move, a rate, an end and a
  delete; the names a screen reader is given and the order of the tab stops.
- **One selection.** A zoom, a cut or a speed change is selected, never two: by Z and X, by
  selecting an item of each lane, and by a press on the empty part of each of the three lanes
  with something of another kind selected. A press on the scene lane and what the trim bar
  asks for leave a selected speed change selected.
- **Rates of a project file.** A rate that is none of the six is named on its block and none of
  the six is chosen for it; one past the fastest or under the slowest shows as that limit; a
  stretch at the recording's own speed is no speed change.
- **What a block looks like**, in both themes, from the pixels of a screenshot: its ends are
  round where a zoom's block has corners (how much further in the block begins a pixel and a
  half under its top than through its middle: 4.7 for half circles of a block 20 high, 0.9 for
  corners with a radius of 4); it is one flat colour, with none of its own, where a cut's is
  hatched; its outline is one line where a cut's is dashed; a faster and a slower block carry
  marks that are not the same picture; and the selected one's outline is twice as thick. What
  that cannot tell is in a comment at the check: the colours themselves, and whether the marks
  look like what they stand for. Contrast themes are not seen.
- **The trim bar.** A speed change leaves nothing out, so the tinted range goes on through it,
  and only a cut leaves a gap. The parts are read along the middle of the bar and along a row a
  pixel and a half under its top, which runs through the round corners of every part: two
  parts side by side leave a notch there. On that row a pixel counts as tinted when it is more
  than half as far from the bar's own colour as the most tinted pixel of the row, because the
  pixels of a corner are only partly tinted. The editor hands the bar the stretches the video
  keeps, which a speed change does not divide (`StudioTimeMap.Segments`), so the bar is also
  handed the same video in the pieces the time map plays it in, one for each stretch at one
  rate, four of which touch the one before: it has to draw two parts then as well. That second
  call is made by the tool and not by the window.
- **A trim and a rate that are refused.** With the whole recording at eight times the speed, a
  trim that would leave less than a tenth of a second of video is refused although more of the
  recording is left, and the handle has to report and be drawn where the video really ends. A
  rate that would do the same is refused, is not read out, and the choice goes back to the rate
  the stretch has.
- **Paused.** The time through a faster stretch, a cut and a slower stretch, against the
  video's time worked out by hand.
- **Playing.** See the next point.
- **An export**, of a recording with a camera, a trim, a cut and two speed changes. Every time
  in that project is a whole number of 120ths of a second, a quarter of a frame, so that the
  place in the recording an output frame shows is never on the line between two frames. The
  frame of the recording each of the 209 output frames has to show is worked out by hand in a
  comment: one by one, then every other frame at twice the speed, then one by one, after the
  cut every frame twice at half speed, then one by one. The file is read with ffprobe and
  decoded with ffmpeg, and each frame is read against the layout of the frame it has to be.
  Its sound is decoded as well. The test recording has a tone of a twentieth of a second at the
  start of every second: the tones of the seconds that play at the recording's own speed have
  to be where the video plays them, and the two stretches at another speed have to be silent.
  The sound is not listened to: its samples are read.

**What the editor asks the preview for.** The session tells the preview how fast to play before
it starts playing, when the playhead has come into or out of a speed change, and after the jump
over a cut. The preview engine in this tree takes the request and plays on at the recording's
own speed, and says nothing of it in its trace, so the request is written down by
`WatchedPreview`, which stands in front of the engine for the one project of this check. The
checks are about the requests: which rate, with the engine at which place and playing or not.
Playing into a stretch at twice the speed and out of it has to ask for 2 and then for 1, each
once and with the engine no more than six frames past the edge. Playing over a cut into a
stretch at half speed that starts where the cut ends has to ask for 0.5 as the engine is sent
on, and Play pressed inside a stretch has to ask before the engine plays.

How fast the picture and the playhead then go is measured (seconds of the recording for each
second on the clock, inside the stretch clear of its edges, and after it) and written into the
report as a note that no check depends on. `PreviewPlaysTheRate` at the top of
`Checks\SpeedPlaying.cs` returns false. Once the engine plays a rate, have it return true: the
same measurements are then two checks, that the picture and the playhead go at the stretch's
rate inside it and at the recording's own after it, each within a fifth. Nothing else has to
change. The checks of the pictures drawn on the way, of the playhead and of the time shown hold
for an engine that plays the rate as for one that does not.

## The handles at the ends of a block

A zoom, a cut and a speed change are made longer and shorter by dragging a handle at an end
of the block (#427). The rule is the editor model's, the same on the Mac
(`StudioEditorModel.GetLaneBlockPart`, `GetLaneHandleOutset`, `LaneBlockHasInsideHandles`,
unit tested in `StudioLaneHandleTests`): a handle is 8 wide; a block of 28 or more has one
inside each end; a narrower block has none, and is only moved, until it is selected, and
then has one outside each end and is that much wider to press. Before 7 October 2026 the
first and the last 6 of a block of 24 or more were its ends, and nothing was drawn there.

**Older checks, read against the new numbers.** The checks of a drag of an end press 3 in
from the end of a block that is 92 wide or more, which was an end and is one. The checks of
a block "narrower than 24" press a block of 10 and of 18.5 that is not the selected one, at
its first or second pixel: it was moved as a whole and is, and the checks say 28 now. No
check pressed 7 or 8 in from an end, or a block between 24 and 28 wide.

**New, four checks on each of the three lanes** (`Checks\LaneHandles.cs`, run at the end of
the lane's presses and drags in `zoom`, `cut` and `speed`, on the same window, with every
drag undone again):

1. *A block of 28 or more.* Pressed 8 in from its start and dragged, its start moves and its
   end stays; pressed 8.5 in, the whole block moves; and the same from its end. On the zoom
   from 7 to 9 s, and on the cut and the speed change from 4 to 4.45 s, which is 41.3 wide.
2. *A narrower block that is not selected.* A press 4 before its start and 4 after its end,
   where a selected one has its handles, takes hold of nothing: nothing is selected, and
   dragged on, nothing changes. On the zoom of 0.06 s, which is drawn 10 wide, and on the
   cut and the speed change of 0.2 s, 18.5 wide.
3. *The same block, selected.* Pressed 4 before its start and dragged, its start moves and
   its end stays; pressed 4 after its end, its end moves; pressed in its middle, it moves as
   a whole; and it stays the selected one.
4. *What is drawn.* On the block of 28 or more: handles inside its two ends, 8 wide each,
   read as the handles' own rectangles along the lane; faint at rest, stronger after what
   the block's handler for a pointer coming over it calls, as before after what the handler
   for a pointer leaving calls, and at their strongest while the block is selected. On the
   narrower block: none, then one outside each end once it is selected. The selected block
   is drawn over the others. The block's tooltip says where the handles are, or that the
   block has to be selected for them. And a screen reader is given each block as one item
   with nothing inside it, and UI Automation knows of no handle at all. The item's
   rectangle is the block's, and while a narrow block is selected it takes in the two
   handles outside it: 8 more on each side.

**Where a block is, for these checks, is what the lane itself holds** (`BlockOnLane`: the
block's place and width as the lane set them), and not its rectangle on the screen. The
first run of these checks pressed by the rectangle UI Automation gives, and seven checks
failed for it: that rectangle is in whole pixels of the screen, so a place 8 in from an end
was not 8 in; it takes in the handles outside a narrow selected block, so a place 4 before
it was 12 before the block; and it gets its width only when the window is next laid out.
The third is what failed an older check twice on 7 October, *the Start and End buttons move
the zoom's two times, and its block follows* (`zoom`): that check judges the lane's own
numbers now, and waits separately for the rectangle a screen reader is given, which for a
zoom of 0.3 s, 27.7 wide and selected, is the block with its two handles.

A picture of each lane with its narrow block selected is saved (`lane-handles-zoom.png`,
`-cut.png`, `-speed-change.png`), for a person to look at.

**What these cannot show.** No pointer is moved or pressed: that a real pointer over a
handle lands on the handle, that its shape is the one for resizing from side to side
(`ProtectedCursor` on the handle, set and never read back), that the block is told when a
real pointer comes over it, and that a drag of a real pointer takes hold of what a press
through the lane's own method does. "Faint", "stronger" and "strongest" are which of its
three states the handles are in, and no pixel is read for them: what the handles look like,
on each of the three kinds of block, in light, in dark and in a contrast theme, is for a
person. So is a handle outside a block that has a neighbour right next to it.

## Written on 5 October 2026 and not run

What follows was written in the evening of 5 October 2026, while the machine was not to be
used for check tools. It compiles without a warning. Of every sentence in this section that
says what a check does, read: what it is written to do. Every value a check expects was
worked out from the editor's code and is written out at the check; none has been seen in a
report. A check that fails on its first run is as likely to be wrong itself as to have found
something. Twenty-six faults for these checks are ready in a script and none has been tried:
see "Faults that were tried". If nothing ends a part early, the `scene` group has 13 checks
more, `open` 8, `inspector` 6, `themes` 4, `close` 3 and `export` 1.

### Something dragged while the preview plays

In `Checks\SceneDragging.cs`, in the `scene` group. A drag is many changes to the scene the
playhead is in, and while the recording plays the playhead may come into the next scene before
the drag is over. So the editor stops the preview at the first change of a drag, when the
recording has more than one scene, and the whole drag stays in the scene the picture stopped
in. How a drag is made without a pointer is in "What stands in for a person".

- **A drag stops the preview.** In a recording with three scenes, each with its camera at a
  place and of a size of its own: the camera's handle, the Size slider and the horizontal
  offset; in one with side-by-side scenes: Camera size and the transition's Duration. Each time the editor
  has to be paused right after the first change, on the frame the picture then stays on (read
  from the frame strips, not from the edges: the frame is not the check's to choose); the
  scene of that frame, and no other, has to have changed as the drag asked; and one Undo has
  to take the whole drag back.
- **What does not stop it.** The same two moves of the camera without a gesture around them,
  which are two undo steps; Padding in a gesture, which is the whole recording's and no
  scene's; and a drag of the camera in a recording with one scene. The preview has to play on
  through each and after it.
- **A drag that begins in the last frame of a scene.** Stopping the preview puts the playhead
  on the frame the picture stays on, which can be the first frame of the next scene. The scene
  that changes then has to be that one, with the new horizontal offset and its own vertical
  one. Twenty tries (`--late-drags` for more), every other one with the UI thread held up for
  40 ms first. A note says in how many the stop landed in the next scene; if it is none, the
  case was not met and the check has shown less than its name says.
- **Undo in the middle of a drag** takes back what the drag did so far, in the editor and in
  the picture, leaves the edit that was made before the drag, and leaves the drag open; what
  the drag does after it is one undo step. Undo is pressed by its button. The window offers
  that button in the middle of a drag only when there was something to undo before the drag:
  a note says what the button did with nothing to undo from before.
- **The keys wait.** While a zoom is held on its lane, what Delete, S, 1, Ctrl+Z and Ctrl+E
  run has to be nothing, and what Space and Right run has to play, pause and step. Once the
  pointer lets go, each of the five has to act again.

### Keep this project

In `Checks\KeepProject.cs`, in the `inspector` group. The check box is read through UI
Automation and the project file is read by the tool itself, as JSON, not through the store.

- Its name, state, description and tooltip; its place in the Project panel, under a heading
  Storage, after Tiny Clips badge and before Save as default look, in what a screen reader
  walks and among the tab stops, where the Project panel comes after Mute.
- Switching it writes `keepSources` into the file within 0.4 s, which is before the editor's
  own save of an edit would (0.6 s after the edit), and leaves Undo and Redo as they were. One
  Undo afterwards takes back the edit before it, and neither it nor the Redo changes the check
  box or what the file says once each has been saved.
- During an export. The window disables the whole editor under the export overlay, and the
  check box with it, so the check switches it through the check box when that is enabled and
  otherwise through the property the check box is bound to, and a note says which. The file
  has to say so at once, the export has to end, and the project has to list its video and
  still be as it was switched.
- Closed and opened again, the check box is as it was left.
- When the store refuses, the check box has to go back to what the file says, the message bar
  has to say why, nothing may be reported to the app, and the next switch has to work.

### A project that cannot be shown

In `Checks\CannotBeShown.cs`, in the `open` group. The project file is made one that this
version cannot read by setting its `schemaVersion` to 99; nothing else in the folder is
touched, and every check holds the folder against what it was before the window opened, file
by file, by length, time and hash. Four of these checks were changed on 6 October, when the
button stopped being disabled while it works: they are given here as they are now, and
"A button that is not disabled while it works" says what changed.

- The window says why, and has a button Save the screen recording that is enabled, described,
  shows its name, and is the next tab stop after the message; nothing of the editor is there.
  Its tree is audited like the other states.
- The button puts a copy of `screen.mp4`, byte for byte, where the tool's storage says videos
  go, under the name it gave; the status says "Saved as" and that name and is read out; the
  window service reports it once, and not as an export; and the button offers to save again.
- A second request made in the middle of the save, from where the save asks for its name,
  has to be over at once and ask for no name; the button is enabled meanwhile, shows
  "Saving…" and is described as saving, and the status says that the recording is being
  saved.
- With the name taken by another file by the time the copy is complete, the copy gets the
  next name and the other file is as it was.
- Where the recording cannot be written, the status says that it could not be saved and why,
  is read out, nothing is left behind, nothing is reported, and the button offers to save
  again.
- The close button closes the window without a question.
- Without a recording in the folder the button is not there, neither for a screen reader nor
  among the tab stops.

Five names taken one after the other, after which the app gives up with an error, are in the
check of a copy that fails after its window has closed. In a window that is still open they
are not covered.

### A save that fails as a window closes

In `Checks\ClosingUnsaved.cs`, in the `close` group. The tool's store refuses every save of
the project with a sentence of the check's.

- An edit and the close in one turn of the UI thread, as when the app exits right after an
  edit: the window has to close without a question, and the window service has to report the
  failed save once (`ErrorReported`), in the words the message bar would have had. A note says
  what a screen reader was sent from the closing window and what message its editor held; the
  bindings of the message bar are still alive while the window lets go of its project, so
  the bar may open unseen. That is not judged.
- The order a person meets it in: the edit fails to save while the window is open, which the
  window says in its message bar and does not report to the app; then the window is closed
  and kept as a draft, and the save it tries once more is reported once.

### An export whose name is taken

In `Checks\ExportName.cs`, in the `export` group, with the real exporter. The export is
promised a name that no file has; once its progress has reached 3 % the tool writes another
file under that name. The video then has to be whole under the next name (read with ffprobe:
its codec, size, rate and number of frames), the other file has to be as it was, the project
has to link to the name the video got, and the app has to be told once, of that name.

## Written on 6 October 2026 and not run

### Esc closes the window

In `Checks\ClosingByEscape.cs`, in the `close` group, after the question on a recording that
was never exported and before a save that fails. It was written while no check tool could be
run, like the section before this one, and is to be read the same way: it compiles, the two
rules behind it pass their unit tests (what the key means, `StudioEditorSessionShortcutTests`;
what the window asks before it closes, `StudioCloseQuestionsTests`; the words,
`EditorEscapeTests`), and not one check below has run. If nothing ends a part early, the
`close` group has 25 checks more, 21 for Esc and 4 for an export that ends behind the
question about it; the `export` group has 2 more, for the focus while the question about a
running export is open and after it; and the `open` group has 2 more, for a window that is
closed while its recording is being saved. Those eight are in the two sections after this
one.

Esc asks the window to close as its close button does, by the rule the Mac's window has.
Where closing asks a question of its own, that question is asked. Where a project is open
and closing asks nothing, which is a project that was exported, Esc asks whether it was
meant, with the question the screenshot editor and the trimmers ask
(`EditorEscapeConfirmation`) in Studio's words, while the setting "Confirm before closing
editors with Esc" is on, and closes the window at once while it is off. A window whose
project is not open, because it cannot be shown or is still being opened, closes at once
whatever the setting says: the question says that the edits are saved, which is no sentence
for a window that opened nothing. The tool's settings are the app's settings in memory, so
the checks switch the real setting, and put it back.

The key is not pressed: each check hands `StudioWindow.RunShortcut` the key, its modifiers and
whether it is held, and reads what the window then shows. That the window maps the Esc key is
checked on `StudioWindow.MapKey`, like the other keys. A project that "was exported" lists an
export in its file, written there by the tool; no export is run for it.

- **A project that was exported, with the setting on.** While the video plays: the question
  has to be there, with the automation id the other editors' question has, the title "Close
  Studio?", Studio's sentence, Close Studio and Cancel and no third answer, and the focus on
  Close Studio; and the video has to stop. While it is open, what the window runs for Esc,
  pressed and held, has to be nothing, and the window's close button, pressed then, has to
  leave the one question and the window where they are. Cancel: the question goes, the
  project file is byte for byte as it was, and Next frame steps a frame on. Esc again and
  Close Studio: the window closes, and the project stays with its export listed.
- **The same with the setting off:** the window has to be gone, and no question asked.
- **A recording that was never exported.** With the setting off: the question its close
  button asks (Export, Keep as draft, Cancel, Delete recording), as the one question, and the
  video stops. With the setting on: that question again, and after Keep as draft the tool
  looks, until the window is gone, for a question with the other one's automation id or
  title. There must be none.
- **While an export runs**, with the real exporter. A key that is being held does nothing at
  all, also here: it may be the Esc that answered Keep exporting in the question about this
  export and was held a little too long, or one that closed a window in front. So the key is
  first handed over as a held one: what the window runs has to be nothing, and 0.6 s later
  the export has to be running still; a stopped export was gone within 0.4 s in the runs of
  5 October. Then as a press: what it runs has to be the stop of the
  export; the window stays, nothing is asked and no file is left. As a held one again, while
  the export stops and once it is gone: nothing, both times. Pressed anew, it has to ask what
  to do with the recording.
- **Esc twice in quick succession**, while a second export runs in the same window. The first
  press stops the export. The end of an export asks for the keyboard focus to go back to
  Export, which the window does after everything else it has to do; the second press is made
  before that, in the window's own turn in which the export is gone, and opens the question
  about the recording. Once the window has done what it had waiting, the focus has to be on
  one of the question's own answers: on Export under the question, it would take Esc away
  from the question, and Enter or Space would start an export under it. What was given the
  focus in the window itself from the first press on is in the detail and is not judged: the
  overlay of the export goes while it has the focus, before the question is open, and where
  the framework sends the focus then is its own doing. Cancel, and the focus that waited for
  the answer has to be on Export.
- **Esc that is not the window's:** held; with Ctrl, with Shift, with Alt, and with Ctrl and
  Shift; while the list of a drop-down is open; with the focus in a text box; and in a drag
  (`BeginGesture`), after which the same key has to ask. The editor has no text box. The tool
  puts one into the window's root grid for this check and takes it out again, so that the
  window's own test of whether text is being edited is the one that answers.
- **A drop-down.** With the focus on the canvas drop-down and its list closed, which is where
  the focus is left after a choice was made from it, Esc is the window's: on this project it
  has to ask, and the question is cancelled. With its list open, what the window runs for Esc
  has to be nothing: no question, the window stays, the list is still open and what is chosen
  in it is as it was. The list is opened and closed through UI Automation (`Expand`,
  `Collapse`), and is open for about half a second. It is the one list the tool opens. While
  it is open the tool reads whether it has a window more than before, whether the system
  keeps that window in front, and where it is among the windows on the desktop; a check of
  its own fails if the list was in front of the window that was in front. The drop-down has a
  tooltip, which is a window of its own as well, so the tool waits for it before it counts.
  In the app an open list takes Esc itself and closes, so the window should never be asked
  about that press. The check is of what the window answers if it is.
- **A window that cannot show its project** (its recording is missing), with the setting on
  and again with it off: the window has to be gone, as by its close button, and nothing may
  be asked. The tool looks for a question for as long as the window is there.

That the close button still does what it did is judged by the checks that were there. In
`close`: the question on a recording that was never exported, where the focus is in it,
Cancel, Keep as draft, the draft opened again, Delete, Export as the answer (cancelled, and
left to finish), and an exported project that closes without a question. In `export`: the
close button while an export runs, Keep exporting, and Stop and close. In `open`: the close
button of a window that cannot show its project. And the three of a save that fails as a
window closes.

Not covered, because a key is never pressed: that the key reaches the window's handler from
each control (a slider, a lane, a closed drop-down), and that it does not from an open list;
what the window reads from the keyboard
for Ctrl and Shift, and from the key event for Alt and for a key that is held; and what the
framework's dialog does with a held key. The first repeat of an Esc that opened a question
goes to the question, which is expected to take it for Cancel, so that a held Esc opens the
question and takes it away again. Not covered either: Esc in a window that is still opening
its project, which closes at once whatever the setting says, by the same condition as a
window that cannot show its project. A check of that would have to come between the window's
first picture and the end of the load.

Not covered, for the same reason: the other keys while a question is open. The focus is kept
on the question, so no key should reach the window then. One that does all the same is
passed over by the first line of the window's key handler (`StudioWindow.OnRootKeyDown`:
`if (_isPromptOpen) { return; }`), which is in the handler for the real key and comes before
anything this tool calls. It has no check, and no fault was written for it. It is for a
person to try: with a question open, Space, Ctrl+E, Ctrl+Z and Delete must do nothing under
it, and the question must still be there.

`--memory` has two windows more, both on a project that was exported: one that is asked by
Esc and told to stay, and one that is closed by Esc and Close Studio. The question is a
dialog that the window does not make itself.

### The close button while a question is open

In `Checks\Closing.cs`, in the `close` group, before the checks of Esc. No window closes from
under a question, and nothing under a question is given the keyboard. While a question is
open, the close button waits for its answer. That was so already wherever closing still had
its question to ask. It was not so in one place, where closing has come to ask nothing while
its question is open: the question about a running export, with the export ending behind it.
The close button, pressed again then, used to close the window from under that question.
Since 6 October it waits there too, and with Esc's question, which is asked only where
closing asks nothing, it would otherwise have been the usual case. The same place is where
the focus could go wrong: the end of an export asks for the keyboard focus to go back to
Export, and the window used to put it there whether a question was open or not. Now the
focus waits until the question has been answered.

- A recording trimmed to three seconds is exported, the close button is pressed while the
  export runs, and the question about it is asked. The start of an export asks for the focus
  as well, to go to Cancel, and with the close button pressed this soon the window comes to
  that after the question has opened. Once the window has done what it had waiting, **the
  focus** has to be on one of the question's answers. The tool then waits until the export
  has ended behind the question, the app has been told of the video, and the window has
  again done what it had waiting: the focus has to be on one of the question's own answers
  still, and nothing in the window itself may have been given it in all that time.
- **The close button**, pressed again then: the window and the one question have to stay.
- **Keep exporting**, which is the answer that leaves the window: the question goes and the
  window stays, and **the focus that waited** for the answer has to be on Export. The close
  button then closes the window at once, because the recording has been exported.

If the export has ended before the question could be asked, one check fails and says so, and
the four are not made.

Two checks more are in `Checks\Exporting.cs`, in the `export` group, at the older check of
the close button while an export runs, which presses the close button a third of a second
after Export. With the question open and the window having done what it had waiting, the
focus has to be on one of the question's answers, and not on Cancel export under it. After
Keep exporting it has to be on Cancel export, the one control that works while an export
runs.

A window of the tool is never the one in front, and is told that it is active with the
message Windows sends for it (see "What is not the app"), which is what lets the window put
the focus anywhere at all. So these checks can show where XAML has the focus inside the
window, as the older checks of the focus do. Whether a question that is open lets the focus
be taken from it at all is the framework's doing and has not been seen for certain: if it
does not, the fault that takes the wait away fails nothing, and that is then known. In two
of the three full runs of 5 October the detail of the older check read the focus on Cancel
export right after the question had come into the tree, and in the other on Keep exporting.
One reading cannot tell a question that had not taken the focus yet from one that had lost
it, which is why these checks read it again after the window has had its turn.

### A window closed while its recording is being saved

In `Checks\CannotBeShown.cs`, in the `open` group, after the checks of a project that cannot
be shown, once for the close button and once for Esc. Such a window closes at once, by
either. Until 6 October the app then stopped listening to the editor as soon as the window
was gone, so a recording that was still being copied was saved and never reported: it was in
the folder, and was neither announced nor listed. Now the editor has not closed before a copy
that is under way has said what came of it.

The copy of a test recording is over before a window can be closed. So the tool's store
says, once, that the recording is at a pipe (`Host\SlowRecording.cs`) which hands out the
first half of the recording's bytes and then waits. The button is pressed, and when the copy
has opened the pipe and taken the first half, the status has to say that the recording is
being saved. The window is closed then, by the Close button of its title bar or by what the
window does with Esc, and has to be gone. While the copy waits for the rest: the app must
have been told nothing, the project has to count as open still (so the cleanup that follows
a closed window has not had its turn), and no file may be under the video's name. Then the
pipe hands out the rest and the end of the file. The app has to be told once, of the name
the tool's storage gave; the copy has to hold what the recording holds, byte for byte; it has
to be the one new file where videos go; the project has to stop counting as open; its folder
has to be as it was before the window opened; and no error may have been reported.

Not covered: what the app does with the report (it finishes the clip and lists it, which is
not in the tool), and the app exiting during a copy, which waits for it for three seconds at
most, like everything else a closing window waits for. A copy that fails after its window
has closed was told to nobody when this was written; it is told to the app now, and has a
check: see "A button that is not disabled while it works".

### Two older checks, and a tooltip

Two older checks hand the window Esc to stop an export, one in `export` and one in `scene`.
Each now does so only while the export runs. Without an export the key used to run nothing;
now it would ask the window to close and leave a question open for the checks that follow.
The window that had the keyboard focus put on Play, for the check of a drag, is kept open
until the tooltip of Play has shown: see "A closed window that stays in memory".

### A button that is not disabled while it works

In `Checks\CannotBeShown.cs`, in the `open` group. Written later on 6 October 2026, without a
check tool, and to be read like the rest of this section: it compiles, the rule behind it
passes its unit tests (`StudioEditorSessionScreenRecordingTests`), and not one check below
has run. If nothing ends a part early, `open` has two checks more, and four that were there
judge something else than they did.

Until then the button that saves the screen recording was disabled while the recording was
copied. A button that is disabled while it has the keyboard focus cannot keep the focus: the
framework passes it on, and the next Enter or Space presses whatever got it. Now the button
stays enabled and keeps the focus. While the recording is copied it has the name it always
has, shows "Saving…" in the room its own label takes, and reads "The screen recording is
being saved." after its name, where it otherwise reads what it does. A press then saves
nothing more. A screen reader is told "Saving the screen recording…" at the press that
starts the copy and at every press while it runs. The saving itself moved from the window's
view model into the editor's session (`StudioEditorSession.SaveScreenRecordingAsync`), where
unit tests reach it.

- **Changed.** The first check also reads what the button shows. The two checks that ended
  with the button being enabled again now end with the button offering to save again:
  enabled, with its name, its description and its own label. The check of a second request
  in the middle of a save reads the button there: enabled, showing "Saving…", described as
  saving, with the status saying so. The second request has to be over at once and ask for
  no name, as before.
- **New: the button in the middle of a copy that lasts, read from outside.** The keyboard
  focus is put on the button, the store says once that the recording is at a pipe (see "A
  window closed while its recording is being saved"), and the button is pressed. When the
  first half of the recording has been taken, the button is read through UI Automation and
  the focus through XAML: the button has to be enabled and have the focus, with its name,
  the description of a save under way and "Saving…"; the status has to say that the
  recording is being saved; and a screen reader has to have been told so. Then it is pressed
  again: a screen reader has to be told the same once more, the button has to be as it was,
  and no second name may have been asked for. Then the pipe hands out the rest: the status
  has to name the video and be read out, the button has to offer to save again with the
  focus still on it, and the recording has to have been saved once, byte for byte.
- **New: a copy that fails after its window has closed.** As in "A window closed while its
  recording is being saved", with the close button, and with every name the tool's storage
  gives out taken by another file (`TempClipStorage.TakeEveryName`): the copy finds its name
  taken when it is complete, asks for another four times, and gives up. While the copy waits
  for the rest of the recording, the app must have been told nothing and the project has to
  count as open. Afterwards the window service has to have reported one error, of the kind
  `ScreenRecording`, in the words the line under the button would have had; nothing may have
  been reported as saved; the five files that took the names have to be the only new files
  where videos go; the project has to stop counting as open; and its folder has to be as it
  was. The error is then taken out of what the run counts at its end, like every error a
  check brings about on purpose.

What these checks can show, and what they cannot. The focus is XAML's own, in a window that
never has the keyboard (see "What stands in for a person"); the older checks of where the
focus goes when a button switches itself off read it the same way. Whether the framework
passes the focus on from a button that is disabled in such a window has not been seen. So of
the two things the fault "the button is disabled while the recording is being saved" should
fail, the button being enabled is the one to count on, and the focus is the one that matters.
No screen reader runs: the checks hold that the sentence is sent, and that the name and the
description are what UI Automation gives. What a screen reader says of them, and whether it
says the sentence once, is for a person to hear. The **Save recording** button of a row in
Settings › Studio behaves the same way and is not in this tool at all: what its row shows
and reads is unit tested (`SettingsViewModelStudioTests`), and its XAML and the sentence its
handler sends are compiled and nothing more.

Not covered: where the focus is after a copy that failed in a window that is still open (the
check of a recording that cannot be written reads the status and the button, not the focus);
what the app does with the error (it shows a notification, which is not in the tool); and
the app exiting during a copy.

### The inspector as a rail with one panel on show

In `Checks\InspectorPanels.cs` and `Checks\InspectorRail.cs`, and in most other files of the
checks. Written on 6 October 2026, in quiet mode, without a check tool, and to be read like
the rest of this section: it compiles, the rules behind it pass their unit tests
(`StudioInspectorPanelTests`, `StudioEditorSessionInspectorTests`), and not one check below
has run, neither the 31 new ones nor the older ones as they are now. If nothing ends a part
early, a full run has 559 checks where it had 528, all 31 more in `inspector`. Six of the
31 came later the same day, with what two readings of the code found: see "After a review"
and "After a second reading" at the end of this section.

The inspector no longer shows everything in one scroll. A rail down its outer edge has an
item for each panel (Scene, Background, Screen, Camera, Zoom, Cut, Speed, Audio, Project; a
recording without a camera has no Scene and no Camera), and the panel of the chosen item is
on show under its name. The other panels are collapsed: their controls are not in the window,
neither for the Tab key nor for a screen reader. Taking hold of something a panel edits shows
that panel by itself. The four crop sliders of the screen and of the camera are in a group
called Crop, which is closed while nothing is cropped. Many names changed with it (see
`windows\docs\studio-inspector-rail.md`).

**The one step that keeps the older checks as they were.** Every check asks for a control by
its automation id. `InspectorPanels.cs` has the one table of which panel holds which control
and which of them are inside a crop group (`Places`, 133 ids, and three prefixes for what the
inspector makes in code), and one step, `ShowWhatHolds`: it shows the panel that holds the
control, the way the rail does, through the editor, and opens the crop group the control is
in, the way a press on its header does. The step runs before every way the tool has of
finding a control of a window: `UiaElement.Find` and `FindRaw` on the window's element
(through `BeforeFind`, set when the tool opens a window), `Descendant`, which looks in the
window's own tree, and `FocusOn`. It does nothing for a control that is not the inspector's,
for a panel the recording does not have, and before the project is open. After it changed
something through UI Automation, it gives the tree up to 0.8 s to have the control.
`FindAsItIs` and `FindRawAsItIs` look without the step: the new checks use them, because
what the window shows by itself is what they are about.

What that step does to a check, to keep in mind when one fails:

- A control that is looked for is brought on show. A check that expects a control to be gone
  still means what it meant: the control's own panel is on show and does not have it.
- A crop group that is opened this way counts as opened by its header, and stays open from
  then on, with or without a crop. The older crop checks therefore see the sliders
  throughout, as they did. What a group does by itself is in the new checks.
- When the panel changes while the keyboard focus is inside the panel that goes away, or in
  the rail, the window puts the focus on the rail's item for the new panel. That is the
  window's rule, and it also applies when the tool is what changed the panel. Every older
  check that reads the focus was read for it: between putting the focus on a control and
  reading where it went, each of them looks only for controls of the same panel, or of none.
- Showing a panel puts its scroll back at the top.

**Two readings go through every panel.** `AuditState` reads the window with each panel on
show in turn and both crop groups open (`ReadEveryPanel`), so that every control a person
can bring up is read. Each reading is saved (`tree-<state>-<panel>.txt`), what is wrong in
any of them is reported once, and the sliders are counted once each, so the numbers of
sliders the checks expect are the ones they expected before: 19, 17, 10, 15 and 18. The
panel that was on show is shown again afterwards, the focus is put back if it was taken
along, and the crop groups are left as they were found. `TabStops` walks the window with
each panel on show in turn and puts the walks together (`StopsWithEachPanel`): the stops up
to the rail, the rail, each panel's stops in the order of the rail, and what every walk ends
with alike, which is what comes after the inspector. The rail is listed by its own id, and
the header of a crop group by the group's. `TabStopsAsItIs` is one walk of the window as it
is. A window that shows no inspector, as one whose project cannot be opened, is read and
walked as it is.

**Older checks that judge something else than they did.**

- Names. Add cut (the Cut panel's button and Cut in the transport row), Add speed change,
  Split scene, Transition with Instant and Animated, Transition duration, Zoom level, Focus
  with Fixed point and Follow pointer, Zoom-in time and Zoom-out time, Camera background,
  Screen crop left and its seven fellows, and the description and the tooltip of Delete cut
  and of Delete speed change, and the tooltip of the choice of speeds. In `Cuts.cs`,
  `Speed.cs`, `Scenes.cs`, `SceneSection.cs`, `ZoomSection.cs`, `Cropping.cs` and
  `CameraBackground.cs`.
- The order of the tab stops. With three scenes (`SceneSection.cs`): the rail once and
  before any panel; the Scene panel as Previous and Next scene, Layout, Split scene,
  Transition, Duration, the three Start buttons, Delete scene; then Show background, Click
  highlights, the three buttons that add, and Mute; and no item of the rail a stop of its
  own. With a zoom selected (`ZoomSection.cs`): Click highlights and the Crop group's header
  come after the screen's shadow and before the crop sliders, and Mute after Delete zoom.
  With a cut and with a speed change selected: the panel comes before Mute, where it came
  before Click rings.
- Keep this project (`KeepProject.cs`): its place is the Project panel, under a heading
  Storage, after Tiny Clips badge and before Save as default look, and in the order of the
  Tab key after Mute and Tiny Clips badge. The picture of it shows Tiny Clips badge above it,
  where it showed Mute audio.
- A recording without zooms, without cuts and without speed changes: the Zoom, the Cut and
  the Speed panel also have to say how to add the first one (`StudioZoomEmptyHint`,
  `StudioCutEmptyHint`, `StudioSpeedEmptyHint`), and once there is a cut that sentence has to
  be gone.
- The pictures of the Zoom panel (`Themes.cs`) find the panel by its new name. The window's
  two sizes are what they were, 1180 × 760 and 980 × 640 (`TimelinePictures.cs`), and the
  inspector is 88 wider in both, so the preview's column is 772 and 572 wide where it was
  860 and 660. The check of those pictures asks for a canvas at least 400 wide and 225 high.
- Playing into a scene (`ScenePlaying.cs`) puts the Scene panel on show before it plays,
  because it reads that panel while the preview plays and measures the UI thread's time.

**New, 31 checks, all in `inspector`** (`Checks\InspectorRail.cs`):

- *The rail of a recording with a camera* (7). It opens on Scene; the rail is a list called
  Inspector panels with nine items in their order, each a list item with the panel's name,
  what the panel holds as its description and an automation id, one of them selected. The
  list is the framework's, with one selection that goes with the focus and one stop for the
  Tab key. Each item, selected through UI Automation, shows its panel: the name over the
  inspector and the one selected item. With each panel on show, no control of another panel
  is in what a screen reader walks or in everything UI Automation knows of the window, and
  one that the panel always has is there; the Tab key comes to the rail once and goes on
  from it to that panel's first control, and no control of another panel and no single item
  of the rail is a stop. A screen reader is told each newly selected item by the list's own
  events, and no sentence. Taking the selection off the chosen item chooses nothing else.
- *What shows a panel* (5): the four buttons of the transport row that add something; what
  Z, X, R and S run where they add nothing; a zoom, a cut, a speed change and a scene
  selected on its lane through UI Automation, also when it is selected already; what Home,
  End, Left and Right run on a lane; and, by what the pointer handlers call, the camera
  dragged in the preview, a press on a scene's block, and Show scene, the button the Camera
  panel has where the scene hides the camera. Before each act another panel is put on show.
- *Where the keyboard focus goes* (8): from a slider of the panel that goes away to the
  rail's item for the new panel, with the act's own sentence read out and no other; from the
  rail to the rail's new item; not at all from Play; from Show scene, which is in the panel
  that goes away, to the rail's Scene item; from a slider of the selected zoom to the rail's
  Cut item when Add cut is pressed through UI Automation, where a cut is selected and where
  one is added; from a slider of the selected zoom to the rail's Zoom item when what Delete
  runs has deleted the zoom; Space, handed to the window the way a key on its way down is,
  plays and pauses with the focus on the rail's chosen item, and is left to the control with
  Ctrl, as a held key, on another item of the rail and on Play; and, in a window that is told
  it has the keyboard, a focus that is put on an item of the rail that is not the chosen one
  from outside the rail lands on the chosen one and changes no panel, while one that moves on
  inside the rail, or comes the way a press brings it, is left where it goes.
- *What leaves the panel alone* (1): Undo and Redo and what Ctrl+Z and Ctrl+Y run, what the
  keys 1 to 4 run, playing into a zoom, the playhead moved into a zoom, a cut, a speed change
  and another scene, a press on an empty part of three lanes, an item taken out of the
  selection, the focus put on a lane, and the selected zoom deleted. The Project panel, which
  nothing in the table shows, has to stay through each.
- *A recording without a camera* (3): it opens on Background with seven items; asked for
  Camera it shows Screen and asked for Scene it shows Background, and so does what S runs;
  through all seven panels there is no control of the Scene or of the Camera panel.
- *The crop groups by themselves* (7), in a project that comes with a cropped screen: the
  screen's group is open and the camera's closed, and each is a button with a name that can
  be expanded and collapsed, which is what the framework's expander tells UI Automation it
  is; Reset crop, pressed with the focus on it in a group whose header was never pressed,
  closes the group and the focus goes to its header, and Undo opens it again; a slider that
  brings the last edge back to nothing leaves the group open with the focus on that slider;
  an edge moved while a zoom is selected leaves the Screen panel on show and the zoom as it
  was, and one Undo takes the edge back and can be redone; Undo and Redo of the selected
  zoom's level, with the Screen panel on show, leave it on show; collapsed over a crop, with
  the focus on one of its sliders, the group says Cropped, as its description and as a word
  in its header, and the focus is on its header; the camera's group opens when it is
  expanded, its sliders say that the crop is the camera's, and collapsed without a crop it
  does not say Cropped.

What these checks can show, and what they cannot. The rail's Up, Down, Home and End are the
framework's list's own keys. The tool presses no keys, and unlike the lanes the rail has no
key handler of the app's to call, so that those four keys choose a panel is not checked: only
that the list is set up the way that makes them do it, and that selecting an item does.
Whether a press of Down selects is for a person to try. Space is not pressed either: the
check of Space on the rail runs what the window's handler for a key on its way down runs,
and that the key comes to that handler before the list has it is read from the framework's
source and from nothing else. The focus is XAML's own, in a window
that never has the keyboard, as in every check of where the focus goes. No screen reader
runs: that the selected item and the panel's name are what one says after a jump is for a
person to hear. Nothing judges what the rail looks like: that the chosen item is marked in
light, in dark and in high contrast, the two lines between its groups, its labels at a
larger text size (the rail is 88 wide and is meant to grow with its longest label, which
is "Background", so that none is cut; the panel then has that much less), and the rail
scrolling in a window too low for nine items. That the panel's
name is a heading is not read, because the tool does not read heading levels. A group that
opens or closes moves for a third of a second, which nothing looks at.

Not covered: the rail in a window at its smallest size; a panel shown by a jump while the
rail is scrolled; the Tab key from the last control of a panel to the timeline (the walks
show it, and no check names it); and Settings, which this tool does not open.

**After a review.** The code of the rail was read through by a second reader on the evening
of 6 October, still without a window. What that changed here, none of it run:

- The first check of the crop groups asked for a group. The framework's expander tells UI
  Automation that it is a button (`ExpanderAutomationPeer`), so the check as it was written
  could only fail. It asks for a button now.
- Three checks are new, one for each fault the reading found in the app. *Add cut through UI
  Automation, with the focus on a slider of the selected zoom*: the editor used to say the
  edit before the panel, the zoom's controls went away under the focus, and the focus was on
  Play before the inspector looked for it. *What Delete runs, with the focus on a slider of
  the selected zoom*: no panel changes, so nothing moved the focus, and it went to Play too.
  The window now looks after a key whether the control that had the focus is still there.
  *A crop slider that brings the last edge back to nothing*: the group followed its crop and
  closed under the slider.
- The check that Reset crop leaves the focus on the group's header was right and the app
  was not: the button is switched off before its group closes, so the focus had left it.
- The check that the focus alone chooses no panel put the focus on an item of the rail as
  the keyboard does. The app told a press from that by the device the window had seen last,
  which under this tool is wherever the pointer happens to be. It goes by how the focus
  comes now, so the check no longer depends on the pointer. (On 7 October, in the first run
  it ever had, this check failed, and the fault was the check's: the inspector's rule is
  never asked in a window that has not been told it has the keyboard. See "The first runs,
  on 7 October 2026" below.)
- What a key does to the focus is checked through the window's own method for a key, as
  everywhere here. A check that puts the focus on a control of a panel and then runs a key
  now also runs the window's look after the key.

**After a second reading.** Those mends were read through in their turn, the same evening.
The reader found no fault in them, and one in what they stand on. Half of it came with the
rail, and half is older:

- With a zoom selected, the window's sliders hand back to the editor what they have just
  been told to show, and the editor took that for an edit of the zoom. Moving an edge of the
  crop then showed the Zoom panel under the crop slider, and so did an Undo that changed the
  zoom. That half came with the rail. Where a zoom looks inside a crop is worked out from
  the crop, and handed back it was written into the zoom with its last digit changed: a
  second undo step, and no Redo after an Undo. That half is from 4 October, when the Zoom
  section was built, so it was in the window that this tool ran on 5 October. It needs a
  zoom selected over a cropped screen: without a crop a point inside the picture comes back
  as it went. One older check has that, *a zoom inside a crop*, and it reads the zoom's
  point to nine decimals, which a changed last digit passes. With that check's numbers the
  point comes back as it went (0.5 each way, in a crop of 0.52 that starts at 0.08 across
  and 0.03 down), so nothing was rewritten there for a check to see. No check moved a crop
  edge with a zoom selected. Two do now: *an edge moved while a zoom is selected*, and *Undo
  and Redo of the selected zoom's level with another panel on show*. The window no longer
  passes on a value that is the one on show, and the editor no longer shows a panel for an
  edit that changed nothing. Both are unit tested: the second in Core, the first since the
  same evening in the app's tests (`StudioViewModelBindingTests`), where stand-ins for the
  controls hand back what they are shown. On the code as it was before, those tests show the
  Zoom panel coming up under the crop slider. The two checks here are for the real controls.
- The check of a group collapsed over a crop collapses it through UI Automation. It did
  that with the focus on one of the group's sliders, where the check before it had left it,
  and nothing read where the focus went: to Play. The group now sends such a focus to its
  header whoever closes it, and the check puts the focus on a slider itself and reads it.
- One check is for a decision and not for a fault. The focus is sent to the rail whenever
  its panel or its control goes away, and on the rail Space was the list's, which did
  nothing with it. Space on the rail's chosen item now plays.

**The first runs, on 7 October 2026.** The `inspector` group ran for the first time with the
rail (`--only inspector`, 88 checks).

- The window could not be made: the crop groups' style was based on a key the framework
  does not have (`DefaultExpanderStyle`). That was the app's, and is mended (e40ad26).
- Of the rail's 31 checks 30 passed as they were written. The one that failed was *the
  keyboard focus alone chooses no panel*: the focus stayed on the item it was put on. The
  app was right and the check was not. The inspector sends such a focus to the chosen item
  when the framework asks the rail about it (`GettingFocus`), and the framework asks only in
  a window that has the keyboard, which a window of this tool never has: traced, the
  inspector's handler was not called once in the whole group. Told that it has the keyboard
  (see "The focus is moved inside the window only"), the same window called the handler,
  the focus landed on the chosen item, and the check passed. The check now tells the window
  so for its few steps, notes what an untold window does, and also reads the two cases the
  rule leaves alone: a focus that moves on inside the rail, and one that comes the way a
  press brings it. It counts how often the framework asks the rail, and passes only if it
  asked about each of the three: what the framework does about being told it does in its
  own time, and in two runs of three groups the first focus put after the telling was not
  asked about, which failed the check as it stood then. The focus is therefore put again
  until the framework has asked, ten times at most. With the rule taken out of the inspector
  the check fails (tried once).
  That the Tab key coming into the rail lands on the chosen item after a jump has changed
  the panel is still for a person to try: the tool presses no keys.

Then the whole tool ran, for the first time since 5 October. Three checks failed that had
nothing to do with what they are about, and each was the check's:

- *While exporting, every control is disabled* (`accessibility`). Reading the window with
  each of the nine panels on show in turn takes longer than the export of that recording
  lasts: the first reading was taken as the export ended, with its progress at 100 and half
  the window enabled again. And a list that is disabled tells UI Automation of no selected
  item, so the step that waits for the rail to say which panel is on show waited its two
  seconds out nine times. The export then ended where the check goes on to cancel it, the
  project counted as exported, and the question on closing, which the next check reads, was
  not asked: that one failed with it. The window is now read once while it exports, as it
  is, with the Camera panel on show: the rail is disabled with the rest, so a person cannot
  bring up another panel either. The check says so if the export has ended before the
  reading has, and which item the rail has selected is read from the list itself. What a
  screen reader is told of the rail while an export runs is therefore: a disabled list of
  nine items, none of them selected. That is the framework's doing for a disabled list.
- *Play: the picture, the playhead and the time move on together* (`transport`). The
  playhead was 18 frames ahead of the picture, where 6 are allowed. The tool takes the
  picture and reads the playhead right after, and in that run its three looks were 16 and
  43 frames apart where they are 8 or 9: the PC was busy with something else, and the
  playhead was read late. Looks that took longer than 450 ms are now made again, twice at
  most, and a note says when that happened.
- *The Project panel in the dark theme* (`themes`). Tiny Clips badge was reported as not
  whole in the picture, in a picture that shows it whole: UI Automation did not give the
  check box at that moment. It is waited for now, up to two seconds.

Four full runs followed on the mended checks, for whether the tool reads the same each
time. Each counted 559 checks. One passed them all, and three had one check fail:

- *The Start handle of the trim bar, set to 11.7 s after a cut was deleted* (`cut`), once:
  it was read 188.6 px from where that is. In every other run it is 0.4 px. The bar gives
  the handle its place in the same call in which it takes the value (read), and nothing
  else moves it. Not explained. When it is off, the check writes down what the number is
  made of and reads it again a second later; it has not failed since.
- *The Start and End buttons move the zoom's two times, and its block follows* (`zoom`),
  twice: the block's start had moved and its end with it. The lane gives a block its place
  at once and its width when the window is next laid out, and the check had read the
  block's rectangle on the screen between the two. What a person sees is drawn after the
  layout. The check reads where the lane itself has the block now, and separately waits
  for the rectangle a screen reader is given: see "The handles at the ends of a block",
  with which that was mended.

A picture of each panel of the inspector is saved since then, in both themes, for a person
to look at each of them once: see "What it leaves behind".

## Three checks on a smaller preview

With the speed lane under it, the preview is smaller than it was: the canvas of a window with
a camera went from 1157 × 651 to 1082 × 609 pixels, and the screen of a window without a
camera from 1071 × 603 to 1019 × 573. In every one of the four runs made on that code, in the
evening of 5 October 2026, the same three checks failed.

**What the runs of 7 October 2026 showed.** The three groups were run as the checks were
(`--only inspector,scene,cut`, 4 of 230 failed: these three and one of the rail's, which is
another matter) and the prepared way (`--as-prepared`, where all three passed). Each of the
three read what is worked out below, line for line, and none is the app's:

1. *The corner.* The two pictures without the camera differed by 0, 22 and 12 at the three
   points, and by more than 20 in 10,170 of the 236,520 pixels of the screen recording.
   Against the second picture the fully round camera read "000", and the other four shapes
   read the same against either. So it was the copies: the check compared a picture drawn
   from one copy of the frame with a picture drawn from another. Nothing is drawn at the
   middle point.
2. *Playing into a scene.* `failed-scene-rest.txt` had, of the camera at its frame 69: the
   red patch's right edge not found, with nothing on rows 456 and 472 and an edge 0.19 px
   from its place on row 488; the lime patch's left edge not found, with nothing on rows 456
   and 472 and an edge on row 488; the lime patch's top not found, with an edge on column
   560 and nothing on columns 576 and 592. On row 456 the reading stopped at a pixel of
   (245, 13, 106) for the one edge and of (243, 1, 79) for the other: the band, worked out
   as about (240, 0, 78). Paused at frame 81, the picture was in the first scene's layout.
3. *Playing over a cut.* `failed-cut-rest.txt` had the lime patch's top by 2 of 3 lines:
   0.12 px from its place on column 296, 1.50 px above it on column 304, and nothing on
   column 312, where the pixel was (3, 249, 86), the band's green; and the blue patch's top
   not found, with the band's colour before two of its lines. The edge was 0.81 px off,
   where 0.75 px is allowed.

**So what was prepared for each is how the tool judges now**, and `--as-before` judges as the
checks did when they failed, saying what the tool's way would have read. For the third that
means a picture a play-through starts from may be right but for one or two edges. A way that
lets nothing off would be to start that play-through at a frame the band is clear of, as
the second now does: that was not done, and is noted here for whoever takes it up.

**The rest of this section is as it was written before those runs,** when no check tool
could be run and nothing in it had been seen in a picture taken at the moment of a check. It
says what each of the three checks reads and where, what the test clips have at those places
by their definition, and what a run should therefore show. Where it says `--as-prepared`,
that is the tool's way now.

### How the pictures were worked out

The test clips are ffmpeg's `testsrc2` with the frame strip, the patches and a label drawn
over it (`..\StudioPreviewCheck\Media\TestMedia.cs`). So a frame can be drawn from ffmpeg's
source for that picture (`vsrc_testsrc.c`, `test2_fill_picture`): the bars; a slanted band,
16 rows high, that goes down through the picture in two seconds and up again in two; a
checker of noise at the bottom right, in cells of 16 pixels that are alternately plain and
noisy, the noise different in every frame and the same whenever that frame is shown; a small
square that bounces. From such a frame a copy was made of the size the preview makes
(`StudioPreviewCopyTargets`), the layer was drawn from the copy on whole pixels with linear
sampling (`StudioSceneRenderer`), and this tool's reader, written out again line for line,
read the result. Not in it: what the encoder did to the recording, and how the player scales
a frame into its copy, for which several ways were tried.

How far that can be trusted was measured against the runs of that day. They printed ten
readings in full. Nine of them the worked-out picture has edge for edge, which were found and
which were not; the tenth, frame 69 of the camera, but for one edge or two, depending on how
the copy is made. Of how far an edge is off where the band lies on it, it is right within
0.2 px at one frame (frame 90: 1.0 px for 0.85) and wrong by 0.65 px at another (frame 142:
0.3 px for 0.97). Of two pictures that passed, of which the runs printed nothing, it has one
passing and one failing. So it says which lines the band disturbs, and not by how much.
Pictures those runs saved were held against it as well: the band within two pixels of the
clip at 27 places in five frames, the square within two in three frames, and in
`window-light.png`, of the 289 cells of the checker that the camera is clear of, 287 plain or
noisy as the rule says.

### 1. The corner of a fully round camera, in `inspector`

**What the check reads.** Three points, 2 %, 5 % and 10 % of the camera's short side in from
the top left corner of its rectangle, along the diagonal. A point counts as covered when the
mean of 3 × 3 pixels there differs by more than 20, in one of its three colours, from the
picture without the camera. Fully round corners cover none of the three, and the check read
"010".

**Where, at the present size.** From the layout: the camera is 433.1 × 243.6 at (724.7, 513.1)
of the screenshot, and its corners have a radius of 121.8. The points are at (729.5, 518.0),
(736.8, 525.3) and (749.0, 537.5), which is 44, 33 and 16 pixels outside the arc. The screen
recording is drawn in 650 × 365 at (310, 288), about a third of its size, so the three blocks
of 3 × 3 pixels show columns 1235 to 1244 and rows 678 to 687 of the recording, 1255 to 1264
and 698 to 707, and 1294 to 1303 and 734 to 743.

**What is there.** From the code, the app draws nothing there but the screen recording: the
camera has no border, its shadow is at 0 by then, and the outline of its handle shows only
under a pointer and would cross the first point, not the second. From ffmpeg's source, the
recording has its checker of noise there, at frame 90. The first block is inside a plain
cell, blue. The second is in the bottom right corner of a plain cell and reaches a fifth of a
pixel of the recording into the noisy cells to its right and under it. The third lies across
the corner where two noisy and two plain cells meet. The band and the square are far from
all three in that frame.

**What can differ between two pictures of that.** From the code: the preview draws a recording
from a copy of its frame, and `StudioPreviewCopyTargets` keeps a copy while it is large
enough and less than twice too large. The picture without the camera was taken at the layout
Screen, from the copy the window opened with, 960 × 540. The check then goes through the
layout Camera, which hides the screen recording and makes its copy 86 × 48; back in the
bubble layout the copy is made for what is needed then, 680 × 382. So the pictures with the
camera were drawn from another copy of the same frame than the picture they are compared
with. In the morning the two copies were 1358 × 764 and 960 × 540.

**Worked out.** From one copy the three points do not differ at all. From the two copies of
the evening they differ by 0 or 1 at the first point, by 4 to 10 at the second and by 4 to 21
at the third, over six ways of scaling a frame into its copy; from the two of the morning by
0, by 3 to 5 and by 2 to 4. That reads "000", or "001", and not "010". It reads "010" in some
cases when the two copies are sampled a fifth of a copy pixel or more away from where this
arithmetic has them, which the edges the runs read do not rule out: in 4 of 625 combinations
within a fifth of a copy pixel, in 58 within three tenths. In the saved picture the plain
cells are as plain as worked out, so it is not the encoder.

**So this one is not explained.** The copies are the one thing found that differs between the
two pictures at those points, and not at the first; and in the arithmetic they do not differ
by enough. Either they differ by more than was worked out, or something is at the middle
point in the picture with the camera, which would be the app's.

**What a run should show.** Two notes in the `inspector` group. The first says how the picture
taken at the layout Screen differs from a second picture without the camera, taken right
before the camera's checks by going to the layout Screen and back, which keeps the copy:
"differs from it by *a*, *b*, *c* at the three points … and by more than 20 in *n* of the …
pixels of the screen recording". The second says what each corner check reads against either
picture: "… fully round 010 and …".

- *a* should be 0 or 1, and *n* some thousands of the 236,520. If *n* is next to nothing,
  both pictures came from one copy, and the above is wrong from the start.
- If *b* is over 20 and the fully round camera reads "000" against the second picture, it was
  the copies, and the check was wrong to compare across them.
- If *b* is under 20, as worked out, the fully round camera reads "010" against the second
  picture as well. Then something is drawn at the middle point. The failed check prints the
  three colours with and without the camera and which pixels along the diagonal differ, and
  keeps the pictures before and after the edit and the one without the camera.

**Prepared, with `--as-prepared`:** the corner checks go by the second picture. The points,
the 20 and the frame are as they were.

### 2. The picture that playing into a scene starts from, in `scene`

**What the check reads.** Paused at frame 75, both layers against the first scene's layout:
every edge of the test picture that the layer shows, on three lines or more across it, each
line 12 pixels long and three wide, with two flat pixels before and after it. An edge is
found when at least two of its lines, and more than half of them, agree. The check failed on
the camera: too few upright edges, which are asked to span a fifth of the layer.

**Where, at the present size.** The camera is 244 × 244 at (914, 513), mirrored, showing the
middle 720 columns of its clip at 0.339 pixels to one of the clip, from a copy of 454 × 256.
It was 260 × 260, from a copy of 640 × 360. It shows its frame 69. Its four patches are 64
pixels of the clip wide and high and 32 apart, which is 21.7 and 10.8 pixels of the picture.
A line across the side of a patch reaches 9 pixels to each side, so it has 1.8 pixels to
spare before the next patch, where colours are taken to mix up to 1.7 pixels from an edge;
at 260 × 260 it had 2.6. The sides of the patches are read on rows 456, 472 and 488 of the
clip, their tops on three columns each.

**What is there.** From ffmpeg's source, in frame 69 of the camera's clip the band covers rows
454 to 469 at the red patch's right side and 446 to 461 at the lime patch's left side. So it
runs through the gap between the two, on the line of row 456 and against the line of row
472, and leaves the line of row 488 clear. Above the lime patch it covers the four and the
eight rows just over the patch's top on the second and the third of the three columns read
there. Of the three lines of each, the band is therefore on two of the red patch's right
side, of the lime patch's left side and of the lime patch's top: the three edges that were
not found. The other two that were not found never are at these sizes: left of the red patch
the bar changes 20 pixels of the clip from the patch, and right of the gray patch the checker
of noise begins. The upright edges that are left span 128 pixels of the clip, which is 43 of
the layer's 244, and 49 are asked for.

**Worked out.** At 244 × 244 the lime patch's top is not found, and its left side is not found
with one way of making the copy and found with the other. The red patch's right side is
found, 0.7 px off, from two lines that are 1.2 px apart, where the run did not find it: two
lines agree while they are up to 1.5 px apart, so the run's two were further apart than
that, or one of them read nothing. At 260 × 260 both sides at the gap are found and the
picture passes, as it did in the morning.

**What a run should show.** In `failed-scene-rest.txt`: for the red patch's right edge, nothing
on row 456 (a pixel that is not on the way from the band's colour, about (240, 0, 78), to
red), on row 472 nothing or an edge more than a pixel from its place, and on row 488 an edge
within 0.3 px; for the lime patch's left edge nothing on rows 456 and 472 and an edge on row
488; for the lime patch's top an edge on column 560 and nothing on columns 576 and 592. And
a note: paused at frame 81, the picture is in the first scene's layout.

**Prepared, with `--as-prepared`:** playing into a scene starts at frame 81 and not at 75,
what is drawn is judged from frame 86 and not from 80, and 16 pictures of the frames 86 to
109 are asked for where it is 20 of the frames 80 to 109. By the band's rule it is on lines
of the camera's patches while the screen shows its frames 62 to 80 and on lines of the
screen's own patches from 82 to 92; at 81 it touches one, of the three across the top of the
camera's red patch. That moves a frame, which is why it is not done unasked. Nothing in the
reader was changed for it.

### 3. The picture that playing over a cut starts from, in `cut`

**What the check reads.** As above, paused at frame 90, the screen recording alone. An edge
was 0.85 px from its place, the lime patch's top, where 0.75 px is allowed.

**Where, at the present size.** The screen is 1019 × 573 at (126, 205), 0.531 pixels to one of
the clip, from a copy of 1358 × 764. The top of the lime patch, row 450 of the clip, is read
on columns 296, 304 and 312, at (283.1, 443.8), (287.3, 443.8) and (291.6, 443.8) of the
screenshot.

**What is there.** From ffmpeg's source, in frame 90 the band's top row is 450 at column 296,
446 at column 304 and 444 at column 312, and its colour there is (0, 255, 115), green with
some blue, over a lime patch. On the first column it begins where the patch does; on the
second it puts green four rows above the patch's top, which is two pixels of the picture;
on the third, six rows. It can be seen in `window-light.png` of those runs, which shows
frame 90. And it is known from the runs: playing into a scene reported, in each of the three
full runs of that morning, "frame 90: the lime patch's top of the screen -0.85 px while
playing and -0.85 px paused", with the screen in 1019 × 573, which was the size the window
with a camera had then.

**Worked out.** The same edges found and not found as in the run. The first column reads the
edge 0.3 to 0.45 px above its place, the second 1.7 px above it, and the third nothing; the
two that read are taken to agree, and the edge is put half way between them, 1.0 px above
its place, where the run had 0.85. The top of the blue patch is not found, as in the run:
the band's lower edge is where two of its three lines take their flat colour from. At
1071 × 603 the worked-out picture fails the same way, and the run of the morning passed: the
two lines are 1.3 to 1.4 px apart there, and at 1.5 they no longer agree.

**What a run should show.** The check fails as before, and adds that but for the lime patch's
top the picture is in its layout. In `failed-cut-rest.txt`: the lime patch's top by 2 of 3
lines, the one on column 296 within half a pixel of its place and the one on column 304 more
than a pixel above it, and nothing on column 312, with a pixel that is neither red nor
green; the blue patch's top not found, with two lines whose two pixels before the line are
not one flat colour.

**Prepared, with `--as-prepared`:** the picture a play-through starts from may be right but for
one or two edges, as a picture drawn during a play-through may be when the paused picture
reads the same, and a note says which edges and by how much. The 0.75 px and the frame are
as they were.

**What may be better, and was not written.** Two lines across an edge agree when each is
within 0.75 px of the middle between them, which lets them be 1.5 px apart; of three lines,
two have to be within 0.75 px of the middle one. If two lines had to be within 0.75 px of
each other, the top of the lime patch would not be found at frame 90, which a reading may
have of up to a third of its edges, and nothing would be let off. In the worked-out pictures
that makes frame 90 pass at both sizes, has frame 69 of the camera read exactly as the run
printed it with one of the two ways of making the copy, 20 edges and the same five not
found, and leaves no picture of either play-through right but for an edge. It changes what
is read in every picture of every group, so it is for after a run.
## Faults that were tried

**On 7 October 2026 the rail's twenty-five were tried** (R1 to R25 of `faults-evening.ps1`),
each put into the tree by itself, built, and run against `inspector`: twenty-three failed at
least one check, and two failed none. One of the two, the sliders of the selected zoom
handing back what they are shown, says of itself that it fails nothing where the arithmetic
gives the same number back, and the fault next to it, which is the whole of that mend put
back, was caught. The other is *a focus that is in the rail does not go with the choice*:
with the inspector's rule for it taken out, the focus still went from the rail's Zoom item
to its Cut item when what X runs showed the Cut panel. The framework's list moves the focus
with its selection, at least in a window that has not the keyboard, so no check can fail for
that half of the rule. Seven more were written and tried that day for the handles on the
lanes, against `zoom`, `cut` and `speed`: six failed between three and thirteen checks, and
the seventh, a block that gives a screen reader what is inside it, failed none, because the
handles are nothing to UI Automation in the first place. And two for checks that had been
mended that day, both caught: the editor left enabled while it exports (`accessibility`),
and a zoom's block that does not follow its start (`zoom`). The other eighty-nine of the
paragraph below are still as it says.

**For speed changes, the Background choice and the trim bar, for what was written on
5 October 2026, and for what was written on 6 October, none was tried.** Eighty-nine are
written down in `faults-evening.ps1`, which is with the report of that work and not in the
repository: the twenty-four for speed changes, the Background choice and the trim bar;
twenty-six for the evening of 5 October, six for what is dragged while the preview plays and
the keys that wait, seven for Keep this project, eight for a project that cannot be shown,
two for a save that fails as a window closes, and three for an export whose name is taken;
and thirty-nine for 6 October. One of those is a window that closes without waiting for a
recording that is being saved, and seven are for the button that is not disabled while it
works: the button not showing that it saves, not reading it, the window not told that a save
has started or ended, a press that says nothing to a screen reader, a press in the middle of
a copy that says nothing, and a copy that fails after its window has closed being told to
nobody, or told as a failed export. Five of the eight for a project that cannot be shown
were brought to where the code is now, and one of them was turned round: it had the button
stay enabled while the recording is saved, which is what the button does now, so the fault
is the button being disabled, as the app had it. Thirty-one are for Esc:

- eight in the rule for the key (an export that runs, a held key, a held key that stops an
  export, Shift, an open list, a drop-down that only has the focus, a drag, a window that
  cannot show its project);
- seven in the rule for what the window asks before it closes (a recording closed by Esc
  without its question, the same only while the setting is off, Esc's own question asked of
  a recording, the setting not asked either way, a window that opened nothing being asked,
  and the close button being asked Esc's question);
- sixteen in the window, where no unit test reaches: the key not mapped, text being edited
  and an open list not told of, a second question, a question on top of one that is open,
  the close button not waiting, the video playing on, the wrong words, Cancel closing the
  window, each of the four things the window tells the rule told wrongly, the focus put
  under a question, the focus that waited never placed, and the focus held back only if the
  question is open when it is asked for.

Each names the group to run and the check that is expected to catch it; the script says for
each whether its place in the code is still there, which it is for all eighty-nine, and
refuses to do more while the machine is not to be used for check tools. The fifteen in the
two rules for Esc were tried against the unit tests of the rules, which is not this tool's
doing but all that could be run: each failed exactly the tests that had been named for it,
between two and five of them. The same was done for the saving of a screen recording, with
twenty-one faults of its own in the editor's session and in the row of the drafts list
(`faults-r7-unit.ps1`, with the report): each failed exactly the unit tests that had been
named for it, between one and four of them. Three of the sixteen in the window say for
themselves when they would fail nothing: the three about the focus, if the framework does
not let the focus be taken from a question that is open, or puts it on Export itself when a
question goes. One of the
twenty-four, the transport row handing the focus past Speed when Split switches itself off,
would have failed nothing as the checks stood, so the `scene` group got a check for it: a
place where neither a zoom nor a cut fits and a speed change does. Whether that check, or any
of the others, catches its fault has not been seen.

For scenes and cuts, thirty-seven faults were put into a copy of the tree, one at a time, each
built and run against the group it belongs to:

- Thirty-two in the window's own code: the scene lane or the inspector not following the
  playhead into a scene, Delete on the scene lane not being about the scene, a key not mapped,
  Moving and A cut the wrong way round, Move takes setting the scene before, the line between
  two scenes only to be taken from one side, a block that looks like the others when it is the
  current or the selected one, a cut without hatching, the lanes in the wrong order, the trim
  bar without gaps, a drag of a cut's end moving the whole cut, a button that stays enabled
  where it has nothing to do, the focus left to itself when a button switches itself off, and
  what a screen reader is told left out or worded differently. Each failed at least one check.
  Six of them are about a press and the selection. A press on the empty part of the zoom lane
  that lets go of a selected zoom only failed the check of that and no other: before that check
  was written it would have failed nothing, since no other check presses the zoom lane while a
  cut is selected. The same on the cut lane failed two checks, and a press on a scene that lets
  go of the selection two. The playhead of the trim bar letting go of the selection failed 26
  checks of the three groups, because nearly every check moves the playhead that way. A handle
  of the trim bar letting go of it, tried for each handle, failed the one check.
- Two that keep a closed window in memory, as the two that were found did (see "A closed window
  that stays in memory"). Each failed the check for that, and no other. Looked for by
  `--memory` instead of by a group, the first was named by all eighteen Studio windows and the
  second by the two in which the range of a slider is read.
- Two in the layout itself, in a copy of `StudioLayout.cs`: the layers moving into a scene at an
  even pace, and the move starting a frame late. The preview draws from the same code that the
  pictures are read against, so each failed one check only: the one that works the rectangles
  of a move out by hand.
- One in the preview engine, in a copy of `StudioPreviewEngine.Render.cs`: the preview standing
  still for 170 ms when it draws the first frame of the second scene. It failed the count of
  the frames that are left out around a change of scene, with four of them, and the check of
  the pictures as well: the picture drawn after the stop did not read, which is the fault of
  "A frame that comes late".

The first time through, two faults failed nothing, and each showed something else than a check
that was merely missing. Without the transport row's own choice of where the focus goes when
Split switches itself off, the focus still went to the same button, because the framework
sends it on to the next stop by itself: the check now uses a place where neither a zoom nor a
cut fits (and, since the row has Speed, no speed change either), in which the two differ. And
without the building of a scene's controls ahead of time, the first change of scene took 17 ms
and not the 105 that the building had been written for: it was taken out (see "Playing into a
scene and over a cut").

## A closed window that stays in memory

The last check of a run, in the section "What followed from closing windows", is that no window
the run closed is still in memory, nor its inspector, nor its view model. The tool keeps a weak
reference to each when it opens a window, has the garbage collector run when all are closed,
and looks which of them are still there. A window that stays keeps its whole tree of controls
and the editor behind it, and every later run of the garbage collector takes longer for it,
which is time in which every thread of the process stands still. In the app those are the
threads that record.

Until this check there were two ways in which a closed Studio window stayed, and with them the
collector's pauses grew to two seconds over a full run of the tool:

- **Every window.** The code the XAML compiler writes for a window that uses `x:Bind` adds a
  handler to the window's `Activated` event and never takes it off. The handler holds the
  window and the window holds the handler, which the garbage collector cannot undo. The window
  now takes the handler off when it closes (`StudioWindow.LetGoOfBindings`). The handler is a
  method of a class the compiler writes, which the window's own code cannot name, so it is found
  by its name, `Activated`. If a later compiler calls it something else, this check fails.
- **A window in which a screen reader had read a slider of the inspector.** The slider hands out
  its range value as an object of its own, and UI Automation holds on to that object in a way
  the garbage collector cannot follow. The object held its slider, the slider its row, and the
  row's handlers the inspector and the view model. It knows its slider only weakly now
  (`StudioSliderRangeValue`).

Two more things are not a window that stays, and both are the framework's doing. What is left
is not the window, and with one exception not its inspector: it is the timeline and, through
the timeline, the view model. Neither grew: what was left of one Studio window was gone when
the same was left of the next.

**Until the next window is shown.** A window that is closed right after it played can be in
memory for as long as the process shows no other window. In 18 of 28 runs of `--only transport`,
whose one window is closed right after it played, the view model was there five seconds after
the window had been closed, and in one that was watched for longer, 36 seconds after. It was
gone each time as soon as the tool had opened and closed a window of nothing but a button and a
slider. A run of every group ends with windows that have not played and shows none of this.

A dump of the tool taken while the view model was there says what held it. Native code still
held the automation peers of two of the trim bar's three handles, and a peer is given to
nothing but UI Automation. A peer holds its handle; the handle leads to the trim bar through
the event the bar listens to, the trim bar to the timeline in the same way, and the timeline
to the view model. Nothing else held any of them. Why the two peers are kept until the next
window was not found. The handles tell UI Automation of every move of the playhead while the
preview plays, when something listens, and a window that was not closed right after it played
left nothing behind.

The same was seen of windows that were closed 0.3 s after the keyboard focus had been moved to
a button with a tooltip. Of 14 such windows, 8 had their view model in memory a second later,
and the one whose button was in the inspector had its inspector there as well. The eight were
buttons of the transport row and of the inspector; of the two controls of the header that were
tried, nothing was left. All eight were gone once the next window had been opened and closed,
which five times was a window of nothing but a button and a slider. What held them was not
looked at.

So when something is left at the end of a run, the tool opens and closes such a window and
looks again. What goes with it is said in a note, and what is still there fails the check: the
two faults above, put back into a copy, still do. The app is in the same place when its last
Studio window is closed right after it played while something listens to UI Automation: the
timeline and the editor of that window stay until the app next shows a window of any kind.
Nothing was changed in the app for it. To let go of them at once, the timeline and the lanes
would have to give up the view model when the window closes, since nothing tells a control of
a closing window that it has been unloaded.

**Until it happens again: a window that is closed while a tooltip waits.** The framework shows
the tooltip of a control that has the keyboard focus about 0.8 s after the focus came: in one
run the tooltip was not there after 822 ms and was there after 838 ms, and in the seven runs in
which the tool waited for it, it showed 818 to 850 ms after the focus. When a button that has
the focus already, not from the keyboard, is given the keyboard focus, and its window is closed
before the tooltip has come, the framework goes on holding the button, and with the button
what its Click handler belongs to. None of the editor's code is needed for that. A window of
nothing but a button with a tooltip and a slider does it: what the button's Click handler
belonged to was in memory a second after the window had been closed, 20 times of 20.

A Studio window opens with the focus on Play, put there by the window. The Click handler of
Play is the one `x:Bind` made; it belongs to the timeline's compiled bindings, and they know the
view model. So of a Studio window that is closed 0.3 to 0.8 s after the keyboard focus was put
on Play, the view model is left, and neither the window nor the inspector: in 65 of 66 such
windows. The one of which nothing was left came two seconds after a window in which the tooltip
had been shown. A dump says how it is held: `gcroot` finds nothing, no object of the app is
held by native code outright, and the Click handler of Play is pegged, which is how the
framework keeps what belongs to a control that it holds itself.

What was tried, one thing to a window, is in `exp-focus-*.txt` with the logs of this work:

- It stayed for as long as it was watched, which was 40 s at the end of six runs. It stayed
  through ten seconds without any window, through windows of nothing but a button and a slider
  that were open for 0.4 s and for 3 s, through a Studio window that was open for 3 s with
  nothing done to it, and through later windows in which the keyboard focus was moved to other
  controls.
- It went when the same was done in a later Studio window, whose view model was then the one
  that was left: one at a time, more than thirty times one after the other. It went when a
  later window kept the keyboard focus on Play until the tooltip had come (22 of 23; the one
  time it did not is with `--memory` below). And nothing was left of a window whose tooltip was
  taken off Play before it closed (`ToolTipService.SetToolTip(button, null)`, 3 of 3), nor then
  of the window before it.
- Nothing was left when the window stayed open until the tooltip had come (closed 0.84, 1, 2, 3
  and 8 s after the focus, with the tooltip showing each time, 6 of 6), when Play had no
  tooltip, and when the focus was put on Play only the way the window does it.
- It is not about Play: the same was left after Next frame had been given the focus first
  without the keyboard and then with it. It is not about the tool's use of UI Automation: the
  same was left of the first two windows of a run in which UI Automation had not been used.
- None of these, done before the window closed, let go of it: moving the focus on to another
  control, by the keyboard or not, putting the focus on Play once more the way the window does,
  switching the timeline off, taking the timeline out of the window, taking everything out of
  the window.
- What was left of the window of nothing but a button did not go with the Studio window after
  it to which the same was done (18 of 18), nor with the one after that, which kept the focus
  until its tooltip had come (15 of 15). It was gone at the end of the run, two windows later
  (12 of 12), and it did go with the next window of its own kind to which the same was done (1
  of 1). What the framework goes by there was not found.

The tool sends no keys. It puts the keyboard focus on a control by asking the framework for
it. The editor asks for the same in one kind of place: where a button that was pressed with
the keyboard focus on it has switched itself off, the focus is handed on the way the button
had it (`StudioFocus`), and the framework may have moved it to that control by then. Whether
that, or the Tab key, leaves a window in this state was not tried: it takes a key, and the
window would have to be closed within 0.8 s of it. Where the tool moved the keyboard focus,
what was left went with the next window, as above. Nothing was changed in the app for it. What
lets go of it at once is known from the above: taking the tooltip off the control that has the
focus when the window closes.

When the check does fail, the tool first says its process id and watches for half a minute
longer. A dump taken in that time holds what a later one cannot: `dotnet-dump collect -p <id>`,
then, in `dotnet-dump analyze`, `dumpheap -type StudioViewModel` and `gcroot` on what it
lists. `gcroot` finds no root for a thing that only native code holds. `gchandles` then lists,
as `RefCounted`, a `ManagedObjectWrapperHolder` for every object that has been handed to
native code. `dumpobj` on a holder gives the object (`_wrappedObject`) and an address
(`_wrapper`), and the second 64-bit number at that address (`dq`) is the object's reference
count: the low half counts what native code holds, the high half what the framework's own
tree keeps track of. An object whose low half is above zero is held from outside, and
`pathto` from it, or from the control it is the peer of, to the view model gives the way.
Where no object of the app has that, the framework itself can be holding a control: it pegs
what belongs to a control that it holds, and the third 64-bit number at the same address has
the highest bit of its low half set for an object that is pegged (`80000002` or `a0000002`
where the others have `00000002` or `20000002`). That is how the handler of Play was found
in the second of the two things above.

`--memory` is for when the check fails again. It runs no group. It opens and closes three
windows made of nothing but a button and a slider, three more with a title bar of their own, one
Studio window for each of fifteen things done to it (nothing, a screenshot, playing, one element
looked for, every element read, a button pressed, a slider set, a check box toggled, a layout
chosen, the focus moved, the tab stops gone through, and so on), and one whose recording is
missing, which has no preview. The check at the end then names the Studio windows that stayed by
what was done to them, and a note says whether the plain windows stayed, which would be the
framework's or the tool's doing and not the editor's. That is how the two above were told apart:
every Studio window stayed and no plain one, and once the first was mended, only the two windows
in which a slider's range had been read.

It also shows the tooltip's wait, apart from the fifteen, in three notes and with no check of
its own. A window of nothing but a button with a tooltip and a slider is closed 0.3 s after the
keyboard focus was put on its button, where the focus was already. The same is done to a Studio
window, with Play. Then a Studio window keeps the keyboard focus on Play until the tool sees
the tooltip. The notes say what was left of the first two a second after each, whether it was
gone after the next, how long the tooltip took to show, and whether anything of the first is
there at the end. What is left of a Studio window at the end fails the check as ever; when it
is the window of these notes, it is the framework's.

In each of the three the tool puts the focus on the button itself, the way a program does and
a tenth of a second later the way the keyboard does, and does not count on the window having
put the focus on Play by then. It did count on that at first, and waited a second and a half
instead of looking for the tooltip. Once in thirteen runs, right after a build, the third
window then let go of nothing, and the view model of the second was there at the end. The
likely reason, which was not shown, is that the window had not yet put the focus on Play when
the tool put the keyboard focus there, so that the focus was moved, which lets go of nothing.

One of the fifteen windows used to be closed 0.3 s after the focus had been put on Play, and
`--memory` named it as a window that stays, in both runs of the evening it was looked into, and
not in the one run of that afternoon. That window now keeps the focus until its tooltip shows.
The tooltip is a window of its own, and like the tool's other windows it is behind the windows
of whoever is at the machine: it belongs to the Studio window, is not a topmost window, and
takes no focus. Read from outside the tool while one showed, it was in place 268 of 414 from
the front, where the window in front was in place 20.

## A frame past the end of the video

In the `transport` group the preview plays into the end of the kept range, which the check has
set to 5.6 s, where frame 168 begins, and is then started again there. The screenshots taken on
the way may show no frame after 168, and after the second Play none but 168 and the first
frames of the range. In 3 of 64 runs of the group a screenshot showed frame 169: twice while
the preview played into the end, and once in the three screenshots after Play had been pressed
at the end, before the picture went to the start of the range. All three were in the evening of
4 October 2026, when other tools and builds were running on the machine; in the eighteen full
runs before that and the twelve after it, it was not seen. The editor stops the preview when
the preview reports a place at the end, and starts it again after sending it to the start; by
then the preview can have drawn the frame after the end. The window does nothing there but
pass Play on, so this is the preview's and the editor's, and the two checks are left as they
are.

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
