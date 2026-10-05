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

**Not run yet.** The `speed` group, the checks of the camera's Background choice in `inspector`,
and what changed with them in `scene`, `cut` and `themes` were written while no check tool
could be run on the machine. They compile, and that is all that is known of them: see "Speed
changes" for what that leaves open.

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

A full run took about three minutes before the `speed` group was added, and has not been timed
since. It exits with 0 when every check passed and with 1 when one did not, after printing one
`FAILED:` line for each; 2 is a usage error.

| Option | |
| --- | --- |
| `--only a,b` | only these groups |
| `--skip a,b` | leave these groups out |
| `--out <folder>` | where reports, trees and pictures go (default `out` next to the project) |
| `--media <folder>` | where the test clips are, or are generated (default `..\StudioPreviewCheck\out\media` when it has them, otherwise `media` in the out folder) |
| `--held-up` | in the `zoom` group, play through a zoom that moves in a second time while the tool holds its own process up: see "A frame that comes late" |
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
  scrolled to its Zoom section with a zoom selected. The section is higher than the inspector, so
  there are three: from its heading, from the focus pad, and from its end.
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
- `out\tree-*.txt`: the UI Automation tree of the window in each state the `accessibility` group
  reads: opening, the editor in three layouts, exporting, the question on closing, a recording
  without a camera, and a project that cannot be opened; from the `zoom` group, with a zoom
  selected and with suggested zooms; from the `scene` group, with three scenes; from the `cut`
  group, with a cut selected; and from the `speed` group, with a speed change selected.
- `out\tab-order.txt`, `out\tab-order-scenes.txt`, `out\tab-order-cuts.txt`,
  `out\tab-order-speed.txt`: the tab stops of the window with a zoom selected, with three
  scenes, with a cut selected, and with a speed change selected, in the order the focus moves
  through them.
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
  window maps the R, S, X, Z and Delete keys is checked on `StudioWindow.MapKey`. For the arrow
  keys, Home and End on a lane, the lane's own `HandleKey` is called, which is what its key
  handler calls. Delete on the scene lane is checked with the focus put on the lane inside the
  window, which is what the window's key handler asks about. That a key reaches a handler, and
  what a focused control does with it first, is not checked.
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
- **The focus is moved inside the window only.** The window never has the keyboard. Where the
  focus goes is read from XAML's own focus manager: the order of the tab stops by asking it to
  move the focus to the next stop over and over, and where the focus lands after a button
  switches itself off or goes away by putting it on the button first. Each of these asks
  Windows for the keyboard, and each request is refused and counted like every other. A group
  of radio buttons is one stop and is listed by the group: which of its buttons the Tab key
  lands on is the group's doing for a focus that comes from the keyboard, and a focus moved
  this way stays on its first button.
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
  Then the camera's Background choice (`Checks\CameraBackground.cs`): that it is not there
  where people cannot be found, nor in a recording without a camera; that Keep, Blur and Remove
  are radio buttons by those names between Mirror and Border, one tab stop together; that Blur
  and Remove set the project and each is one undo step; the note while the background is
  removed; and that the choice goes and comes with the camera's other styling, is saved with
  the project, and is part of a saved look. No picture is read for it: what a blurred or a
  removed background looks like is the renderer's, and `StudioRenderCheck` checks it.
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
- `themes`: the pictures, and that each is what its name says. For the window with scenes,
  zooms, cuts and speed changes, at both sizes: the ten items of the transport row are on one
  row in their order, the four lanes and the trim bar are one above the other and equally wide,
  all of it whole inside the window, and the canvas is at least 400 × 225.
- `zoom`: the lane above the trim bar, Add zoom, what Z and Delete run, the lane's keys, its
  blocks as list items, Previous and Next, the Zoom section with every control of the selected
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
  inside a move; a scene that is cut to; Move takes; the three Start buttons; Previous and Next;
  the lane's keys, its blocks as list items, presses and drags on it; what the scene the
  playhead is in looks like, in both themes; Delete with the focus on the lane and Delete scene;
  a scene that is come into while the preview plays, with what that costs the UI thread; the
  handle on the camera, which is the current scene's; names, tab order, and undo and redo.
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
  in the wrong place has all its edges off, and differs from the paused picture.
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
  shows the current scene is refreshed: the lane's selected item, the Scene section, Layout, the
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

**None of this has been run.** The group was written, and the checks around it changed, while
the machine was not to be used for anything but compiling. So of every sentence in this section
that says what a check does, read: what it is written to do. What is known is that the tool
compiles without a warning. What is not known:

- whether each check passes on the window as it is. Every number a check expects was worked out
  by hand, from the editor's rules and the test clips, and is written out in a comment at the
  check; none has been seen on a screen or in a report. A check that fails on its first run is
  as likely to be wrong itself as to have found something;
- whether the limits of the checks that read pixels fit what the window draws: how round an end
  has to be, how flat a fill, how many pixels make a mark, how far a tinted pixel of the trim
  bar is from the bar's own colour. They were set from the brushes and the sizes in the XAML,
  not from a picture;
- how long the group takes, and so how long a full run takes;
- whether a check can fail. Twenty-four faults are ready in a script (see "Faults that were
  tried") and none has been tried.

What the group is written to check, in `Checks\Speed.cs`, `SpeedLane.cs` and `SpeedPlaying.cs`:

- **Adding.** The empty lane; R, Speed in the transport row and Change speed at playhead in the
  section; where a speed change already is, and where the shortest does not fit; what is read
  out; the block's name; the section for the selected one; and the time, which counts the video
  and so gets shorter for a faster stretch.
- **The lane.** That it is on the trim bar's time scale; its keys; its blocks as list items;
  presses and drags, by what the lane's pointer handlers call: a press on a block and on the
  empty lane, a drag of the body, of the first and of the last 6 pixels, against a neighbour
  and against the ends of the recording, a block too narrow for ends of its own, and each drag
  as one undo step.
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

## Faults that were tried

**For speed changes, the Background choice and the trim bar, none was tried.** Twenty-four are
written down in `faults-speed.ps1`, which is with the report of that work and not in the
repository, each with the group to run and the check that is expected to catch it; the script
says for each whether its place in the code is still there, and refuses to do more while the
machine is not to be used for check tools. One of them, the transport row handing the focus
past Speed when Split switches itself off, would have failed nothing as the checks stood, so
the `scene` group got a check for it: a place where neither a zoom nor a cut fits and a speed
change does. Whether that check, or any of the others, catches its fault has not been seen.

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
