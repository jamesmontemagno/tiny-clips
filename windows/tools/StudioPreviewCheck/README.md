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
- For the clips with a recording's frame times (`--investigate recordings`): an H.264 encoder
  that Media Foundation can use, which is what the app records with. Those clips are written by
  the app's own writers and need no ffmpeg; ffprobe is used to read them a third way when it
  is there.

## Build and run

From the repository root. The tool is not in `TinyClips.Windows.slnx`.

```powershell
dotnet build windows\tools\StudioPreviewCheck\StudioPreviewCheck.csproj -c Debug -p:Platform=x64
windows\tools\StudioPreviewCheck\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\StudioPreviewCheck.exe
```

A full run takes about fifteen minutes. It exits with 0 when every check passed and with 1 when
one did not, after printing one `FAILED:` line for each; 2 is a usage error.

| Option | |
| --- | --- |
| `--quick` | fewer repetitions; a smoke run, not the source of the numbers in the docs |
| `--only a,b` | only these groups |
| `--skip a,b` | leave these groups out |
| `--no-window` | leave out the checks that open a window |
| `--no-headless` | leave out the checks that need no window |
| `--out <folder>` | where clips, reports and pictures go (default `out` next to the project) |
| `--trust-first-frames` | open every preview without a window the way the engine opened before it stopped believing what the players hand over first. The checks of the first picture are then expected to fail: it shows that they notice |
| `--believe-positions` | take every frame of playback for the one its player's position names, as the engine did before it told the frames apart. The checks made while the process is held up (`stalls`, `zoom`, `scenes`) are then expected to fail: it shows that they notice |
| `--fetch-every-rest` | fetch the frame anew that the clock stops on, every time, as the engine does by itself when the frame's number rests on an inference. With `--believe-positions` the pause checks then fail for the moment `Pause()` returns and hold at rest: it shows what the fetch puts right |
| `--no-quiet-rule` | do not hold the picture during the first position change after playing: how often the pause checks then see a frame nobody asked for |
| `--stop-noted-late` | note when the clock stopped only once it has stopped and the thread has got on, as the engine did before. A frame a player hands over in between then passes for a frame of playback. With `--stalls busy+afterstop` the pause checks are then expected to fail: it shows that they notice |
| `--judge-limits` | judge every frame that was given a number its pixels do not show: also the two kinds the engine says it cannot rule out, which the checks otherwise count apart (see *Holding the process up*). With this switch the checks made while the process is held up fail now and then |
| `--guard-rule-3` | let the rule that takes a hand-over the garbage collector kept waiting for the frame next in line (rule 3) speak only when the player had its thread back for a look before the collector struck (`StudioPreviewNamingSettings.CollectorRuleNeedsIdleGap`). The engine does not do this by itself. It is here to measure what it would cost, in frames that then have no number: `--only stalls,zoom,scenes --stalls collector`, with and without it |
| `--frame-times file` | read each clip's frame times from its file's index when a preview opens, and count in frames of the file (`StudioPreviewOptions.FrameTimesFromFile`): a frame handed over is the one whose time the position has reached, and the frame of the timeline it is shown under is the one the export shows it in. The engine does not do this by itself yet. On the usual clips, whose frames are on the grid, every check has to come out as without it; on the clips with a recording's frame times it is what `--only recordings` and `--investigate recordings` look at. `grid` is the default |
| `--people N[,M]` | remove the camera's background in every project of the run (`camera.cutout`), and give every preview without a window a stand-in for the model that finds the people: it calls the whole frame a person, takes N ms for every camera picture it looks at, with the processor busy, and M ms to be made, inside the draw that first shows the camera, as a model that is loaded there would. The frames the renderer is given say which picture they are for the run (`StudioPreviewOptions.StampPictures`), which the engine does not have them do by itself yet. It is here to see what a finder's time does to everything else: `--only open,seek,step,play,pause,stalls,zoom --no-window --people 5,500`. Checks that time a draw or an open are expected to notice; see *A camera without its background* |
| `--stalls a,b` | what holds the process up in the groups `stalls`, `zoom`, `cuts` and `end`: `none`, `collector`, `draw`, `stopped`, `busy`, `loaded`, `gpu`, `afterstop`, `all`, or several joined with `+`; see *Holding the process up* |
| `--stall-ms N`, `--stall-gap N` | about how long one hold-up lasts (60); in `zoom`, the shortest time between two collections (150), the longest being three times that |
| `--count N` | in `stalls`, pauses for each kind of hold-up; in `zoom`, plays; in `cuts`, crossings; in `end`, plays into the end for each kind of hold-up |
| `--camera none`, `--from N` | in `zoom`: a project without a camera; the frame the plays start from |
| `--keep-traces`, `--trace-lines N` | in `zoom`: keep the engine's trace of every play and the list of the frames the players handed over, with the frame each one really was, in `out\traces`; how many lines of trace are kept for a failure |
| `--investigate <name>` | an experiment instead of the checks; see below |

Groups without a window: `open seek step seekplay position editor play pause stalls zoom cuts
scenes end update camera mute surface dispose device software`. Groups with one: `window-exact
window-playback window-resize window-scale window-blocked window-reload window-engines
window-devicelost`. Two more groups run only when they are named with `--only`: `recordings`
(see *The app's own recordings* below) and `people` (see *A camera without its background*).

`stalls` and `zoom` at the size the numbers in the docs come from take far longer than a full
run gives them: `--only stalls --stalls collector --count 550` is about eight minutes. The kinds
with `busy` in them keep every processor of the PC at work for as long as they run, and are
left out of a full run for that reason.

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

Six more came with the question which frame a player hands over while it plays:

| | |
| --- | --- |
| `--investigate names` | Two bare `MediaPlayer`s on one clock, played over and over while the process is held up, with every copy read back: when the hand-over began, what the player's position named, how long the copy took, what the garbage collector did meanwhile, and which frame the copy really holds. `--stalls none`, `gc`, `gate`, `freeze`, `both` or `all`; `--fps 30` or `60`; `--count N` plays; `--seconds N` each. It writes a CSV file, which is what the rules in `StudioPreviewFrameNamer` were worked out from. |
| `--investigate starts` | The whole process is stopped once, for 0.7 to 1.3 times `--stall-ms` (100), at a moment between 2 ms before `Play()` and `--until N` ms (60) after it, `--count N` times (240), and every frame handed over afterwards is read back. A play in which a frame shows a later frame than its position named, or was given a wrong number, is printed with its hand-overs and the engine's trace. |
| `--investigate waits` | A hand-over is kept waiting for the graphics device for `--stall-ms` (100) and a little more, by the other clip's hand-over: `--count N` times (60) with the process running, and as often with it stopped from outside meanwhile. It says how many of the waiting hand-overs held the frame their position had named when they set out. |
| `--investigate tails` | A full garbage collection of about `--stall-ms` (60) is begun up to `--lead N` spins (600, some 40 millionths of a second) after the copy of a hand-over, `--count N` times (300), and the hand-over after it is read back: whether rule 3 took it for the frame next in line, and whether it was. It asks whether a collection that catches a hand-over's thread on its way out is what makes rule 3 wrong; in 300 it was not wrong once. |
| `--investigate exits` | The other half of `tails`. The engine tells a clip's namer that a hand-over is over while its thread is still inside the player's callback. Here the player's own thread begins a full collection of about `--stall-ms` (60) right there, after the namer was told (`StudioPreviewEngine.AfterExit`), `--count N` times (300), so that every collection keeps the player's thread while the namer takes it for something that kept the next hand-over at the door. As often, for comparison, another thread begins the collection `--lead N` ms (15) after the hand-over ended, when the player has had its thread back for a look: the case rule 3 was made for. It says which frame the hand-over after each collection held, what the engine took it for, and how many of the ones rule 3 numbered came with less than a look's time that was not the collector's, which is what `--guard-rule-3` would say nothing about. **Written on 5 October while no check tool could be run: it has never run.** |
| `--investigate reopens` | Previews opened in the circumstances in which two editors in a row once did not open: `--scenario after-close` (each 0 to 20 ms after the one before was closed, which was at rest, playing, in the middle of a seek or just paused), `while-closing`, `two-alive`, `two-playing`, `pile` (more and more open at once, up to `--count`), and `undecodable` (files that cannot be decoded: how long until `OpenAsync` says so, what it says, the error code underneath and how many attempts it made; what a file that opens with part of its pictures missing does when it is sent there; and a player that says it cannot decode what it opened, which no file brought about and is simulated, at both attempts and at the first only). Every preview has a surface attached. `--count N` opens. |

One is for the piece of work after this one, playing a project's speed changes at their speed:

| | |
| --- | --- |
| `--investigate rates` | What the players do when the clock they follow runs at another speed, before the engine is taught to play at one (`IStudioPreview.SetPlaybackRate`, which it does not have yet). The clock's rate is set from outside, through a door that is there for this experiment only (`StudioPreviewEngine.SetClockRateForExperiment`); nothing in the engine knows of it, so what the engine makes of the frames is what its rules for a clock at 1 make of them. At 1, 0.25, 0.5, 1.5, 2, 4 and 8, played `--count N` times (2) from frame 30 for `--seconds N` (3) at most: how fast the recording really went, how many frames a player handed over a second and how many it left out, how far past a frame's own time the position was that came with it, what the engine took each frame for and how often that was right, where the camera was beside the screen, and what `Pause()` left. Then the rate changed three times in one play, with how long after each call the frames came at the new pace, and playing into the end at 8. **Written on 5 October while no check tool could be run: it has never run.** |

### The app's own recordings

One is about the app's own recordings. Every clip the checks play is made by ffmpeg, with one
frame exactly at the start of every thirtieth of a second. A recording is not like that: the
recorder stamps a screen frame with the clock a moment after its pacer's tick, a tick it misses
leaves its slot without a frame, and a camera's frames carry the camera's own times.
`Media\RecordedMedia.cs` writes clips with such frame times through the app's own writers, each
frame with its place in the file in its strip, into `out\media\recorded`:

| Clip | Written by | Its frames |
| --- | --- | --- |
| `screen-into04`, `-into12`, `-into20`, `-into28` | the screen recorder's encoder (`MfSinkWriterEncoder` created and fed as `VideoRecordingService` does on its usual path: textures from the encoder's own allocator, each with its time, and sound from zero in 20 ms pieces) | one in every slot, that many milliseconds into it and up to 2 ms more at random |
| the same with `-single` | the same | a slot left empty about once a second |
| the same with `-runs` | the same | two slots in a row left empty, then three, in turn, about every two seconds |
| `screen-shift` | the same | 4 ms into the slot, 22 ms after a third of the clip, 4 ms again after two thirds: as a recording that was paused twice |
| `screen-middle` | the same | 15.5 to 17.5 ms into the slot: around its middle, which is where the export looks. A frame that begins after one middle, followed by one that begins before the next, is in no frame of the export at all, and the frame before it is in two |
| `screen-as-recorded` | the same | no frame in the first slot, then one in every slot 2 to 3.5 ms into it: what the recorder's pacer makes of a recording that was not paused, as read from its code (`FramePacer` ticks first one interval after it starts, which is a moment after the timeline's zero) |
| `screen-gaps` | the same | the frame times of StudioRenderCheck's clip with dropped frames |
| `rendercheck-gaps` | StudioRenderCheck's own clip writer, whose source files are compiled into this tool | that clip itself (`TestClips.GappyScreen`), 1280x720, with that tool's picture |
| `screen60-into02`, `-into08`, each also with `-single` | the screen recorder's encoder | 60 frames a second |
| `camera-stalls` | `StudioCameraRecorder` itself, given frames as the webcam service delivers them | a thirtieth of a second apart give or take 2 ms, with three stalls of a third of a second |
| `camera-15` | the same | 15 frames a second on a track that is written as 30 |
| `screen-reordered` | ffmpeg, from the usual screen clip, its sound copied | a frame at the start of every slot, stored out of the order they are shown in (two B-frames between the others: 0 3 1 2 6 4 5 ...), and an edit list that starts the track at the first frame shown, which is how ffmpeg writes such a file |
| `screen-reordered-noedits` | the same, with `-use_editlist 0` | the same without an edit list: by the file's own times the first frame is shown two frames into the track |
| `screen-reordered-sink` | Media Foundation's sink writer with the H.264 encoder that comes with Windows (no encoder of the graphics card), asked for two B-frames between the others; 1280x720, StudioRenderCheck's picture, no sound | a frame at the start of every slot. Whether they are stored out of order after all is what the look at the file says |

The last three are not recordings: the app's recorder never stores frames out of order. They are
there because the engine's reading of an index takes such a file by the book (a frame is shown
at its decode time plus its offset, moved by the edit list), and nobody here has seen what
Media Foundation does with one: whether its reader's times, and the frame a player shows, begin
at the first frame shown or two frames after it, and whether an edit list is honoured. If a
player counts from the first frame shown where the file says two frames in, every number the
engine has for such a file is two low. `--scenario files` looks at them with the others;
`players` and `table` only when they are named with `--clip`, and the `recordings` group not at
all. **Written on 6 October while no check tool could be run: the three clips have never been
made, and whether the two encoders take these settings is not known.**

| | |
| --- | --- |
| `--investigate recordings --scenario files` | Makes the clips that are missing and says of each what the file is: what its index says (`Media\Mp4Index.cs` reads the boxes: time units, edit list, the table of sample lengths; nothing is decoded), what Media Foundation's reader hands out as each frame's time, which is what a player and the exporter go by, what the engine's own reading of the index gives (`StudioPreviewFrameTimes`, which is what it counts by with `--frame-times file`) and whether that is what Media Foundation hands out to the last 100 ns (the engine allows nothing at a frame's edge there, so it says how many frames are a unit off, and which way: a reader that cuts a time off where the engine rounds it is one unit earlier for two frames in three at 30 a second), what the app's probe reads as its length and frame rate, and, when ffprobe is on `PATH`, what ffprobe reads. Each is held against the times the frames were written with: whether the first frame's time survives, and whether every frame has the time it was given. Without `--clip`, the usual clips are asked the same at the end: with `--frame-times file` the engine reads their index too. `--clip a,b` for some of the recorded ones. |
| `--investigate recordings --scenario players` | Opens each clip in the engine and says what a player makes of it: which frame the preview rests on in the first slots, around the first slot without a frame and in the last ones, beside the frame the export has there (the last that began at or before the middle of the slot); what one frame forward at a time does across a slot without a frame; and, playing from the first slot, the position the player reports with each frame it hands over, how far after the frame's own time that is (and, in units of 100 ns, whether any came before it and how near the nearest came after), what the engine takes the frame for, and which slot it shows it under. A camera clip is opened with the usual screen clip. |
| `--investigate recordings --scenario table` | The same clips in numbers, one line a clip and a kind of hold-up (`--stalls`, by default `none`, `collector` and `draw`), and the lines as a file `out\recordings-table-<stamp>-<file or grid>-<rules or positions>.csv`. While playing and pausing `--count N` times (10): how many frames had their number at once, from the frame after them, never, the longest stretch without, how many were called unsure, how many were shown under a slot the export does not have them in, how many frames were fetched anew at rest; what `Position` and the picture were when `Pause()` returned and at rest; paused seeks to the first slots, the last one and the slots around those that show what the slot before shows; one slot at a time across such a slot and back; and a zoom that moves while the clip plays, with rests inside the move. Run it four ways for the four tables: with and without `--frame-times file`, with and without `--believe-positions`. |

What is right in all three is the export's rule: a frame of the timeline (a slot) shows the last
frame of the file that began at or before its middle, and the first one while none has. A frame's
number in these clips' pixels is its place in the file, so the truth of a picture is read from
the strip and then looked up in the file's frame times (`Media\SlotMath.cs`). A frame the export
has in no slot is counted apart, and taken to be right under the slot the export has the frame
after it in.

`--only recordings` is the same measurement with nothing holding the process up, judged, for
twelve of the clips or the ones named with `--clip`: every frame numbered at once, none shown
under a slot the export does not have it in, `Position` and the picture as the export has them
when `Pause()` returns and at rest, the paused seeks, the moves of one slot, and the scenes of the
zoom. It goes by `--frame-times` like every group. Counting on the grid, which is what the engine
does by itself, the checks on clips whose frames sit late in their slot or leave a slot empty are
expected to fail: that run is the group's control. The group is left out of a full run until it
has been run and what it holds is known.

**All of this was written on 5 October while no check tool could be run: no scenario and no
check of the group has run, and no clip has been written.** What the engine does with such clips
is therefore not known yet. The frame namer's unit tests have a model of a player on such a file
(`StudioPreviewFrameNamerRecordingTests`), and the engine's arithmetic for a file's own frame
times is unit tested (`StudioPreviewFrameTimesTests`, `StudioPreviewRecordingTimesTests`).

### A camera without its background

A camera's background can be blurred or removed (`camera.cutout`), and the app ships the model
that finds the people. The renderer looks for them in every camera picture it is given, which
takes the model some milliseconds, and keeps what it made of a picture for as long as it is
given the same picture again. Which picture it is, a frame says with a stamp
(`StudioGpuVideoFrame.Stamp`); a stamp of 0 says nothing, and the people are then looked for at
every draw: also at every redraw of a paused preview, which is every move of a slider.

**The engine gives every frame with stamp 0, as it always has.** It can give each frame the
count of pictures the clip has had put into its textures, which changes exactly when the picture
does and is never 0, and it does so only when it is asked to (`StudioPreviewOptions.StampPictures`,
off by default). That is off because a stamp that stayed while the picture changed would show an
old camera frame in the app, and nothing has run that would show it.

`--only people` switches the stamps on and holds the engine to them, with a stand-in for the
model (`StudioPreviewOptions.PersonFinderFactory`, `Checks\StandInFinders.cs`) that counts how
often it is asked and calls the whole frame a person, so that the camera is drawn whole and its
frame number can be read as ever:

- first with the stamps off, as the engine runs by itself: redrawn ten times while paused, the
  people are looked for at every one of the draws. That is the control for what follows, and
  what the app does today;
- where the camera is no part of the picture, nobody is looked for;
- paused where it shows, the people were looked for and the scene shows the right camera frame;
- redrawn ten times while paused, they are looked for no more;
- after a seek and after a step of one frame, the new picture is looked at and is the one drawn;
- playing (`--count N` plays of 2.5 s, 4 by default), with nothing in the way and with the
  garbage collector (`--stalls`): every scene shows the camera frame that the texture it was
  drawn from holds (both are read, for every scene, in the same draw), the people are looked for
  no less often than the camera's picture changed and no more often than scenes were drawn, and
  the camera stays with the screen;
- paused after playing, the scene shows the frame `Position` names;
- after a lost device the new renderer gets a finder of its own and the old one's is disposed;
- every finder is disposed when the preview is closed.

**The group and `--people` were written on 5 October while no check tool could be run: neither
has run.** The group is left out of a full run until it has, and the stamps stay off in the
engine until the group and the faults made against it (a stamp of 0, one stamp for every
picture, the stamp of the wrong texture) have run.

## What it leaves behind

- `out\report-<time>.txt`: what was printed. `pass` and `FAIL` lines are checks; `note` lines are
  measurements that are reported and not judged, because they depend on what else the machine is
  doing (latencies, dropped frames, how long two clips were a frame apart). The last note of a
  run says how many of its previews opened only at the second attempt, the first having failed
  in a way that may pass (a lost graphics device, a player that failed after every player had
  handed over a frame, or a player that stopped decoding before that); the traces of those are
  kept with the failures.
- `out\window.png`: the first screenshot of the window.
- `out\failures\<time>\`, named like the report of the run it belongs to and only there when a
  check of that run failed or a frame was given a wrong number, judged or counted apart (then
  the engine's trace of that playing, and for a play of the `zoom` group every frame handed
  over, as a CSV file): for a check that found the wrong picture or the wrong position, the
  surface and both players' textures as PNG files, and the engine's trace of what the players
  reported and what was asked of them. For a preview that did not open, the trace alone: there
  is no engine left to take pictures from. Every preview of the tool is opened with a trace, so
  this holds for all groups. The first twelve of a run are kept, and the `FAIL` line names the
  files.
- Project folders are made under `%TEMP%\TinyClipsStudioPreviewCheck-<pid>` and deleted at the end;
  the last check of a run is that they are gone.

## Holding the process up

What `--stalls` names (`Checks\Stalls.cs`, `Checks\Freezer.cs`). A list with commas is gone
through one after the other; kinds joined with `+` hold the process up together
(`--stalls busy+afterstop`):

| | |
| --- | --- |
| `collector` | A thread forces a full garbage collection every 150 to 450 ms, with enough kept alive for one to stop every managed thread for about `--stall-ms`. A player's own threads run on; a frame they hand over waits at the door. |
| `draw` | The engine's render thread keeps the graphics device for two thirds of `--stall-ms` to two and a half times it before a scene, every 300 to 900 ms (`StudioPreviewOptions.RenderDelay`). The players wait for the device with their frames. |
| `stopped` | A second copy of the tool, which does nothing else, suspends every thread of this process for a third of `--stall-ms` to twice it, every 200 to 700 ms: the players' threads too. It ends with the run. |
| `busy` | Half as many threads again as the PC has processors do nothing but run. |
| `loaded` | `busy` and `collector` together: a PC that is busy. |
| `gpu` | A thread with a graphics device of its own copies two 4096x4096 textures into each other for about `--stall-ms`, then leaves the adapter alone for one to three times as long. Nothing of the preview is locked. It was meant to make the players' copies slow, and on the PC it was written on it does not: of 4,834 copies made meanwhile the slowest took 16 ms. |
| `afterstop` | The thread that stops the engine's clock, in `Pause` or to go somewhere, is kept from going on for a tenth to half of `--stall-ms` each time it has stopped it (`StudioPreviewOptions.StopDelay`). By itself it changes little: a player hands nothing over in that time unless it is starved. Together with `busy` it is the moment in which a player goes on by itself and hands over the frame after the one the stopped clock names. |
| `all` | `collector`, `draw` and `stopped` at once. No PC does that; it shows where the rules that number the frames end. |

Two kinds of wrong number are counted apart by every check that reads the frames' numbers
(`TruthProbe.cs`), because the engine itself says it cannot rule them out. `--judge-limits`
judges both like any other.

- **A number from rule 3.** `StudioPreviewFrameNamer` takes a hand-over that the garbage
  collector kept waiting for the frame that was next in line. That is so nearly every time:
  one frame in about 130,000 handed over with the collector at work showed the frame after
  it, and with `all` 4 in 5,400. Why is not known. The engine calls such a number inferred:
  it draws by it, and fetches the frame anew when the clock stops on it. A check allows one
  such number, or one in 20,000 numbers when it has that many; a number from any other rule
  has to be right every time.
- **A number given while the whole process was stopped.** When a stop begins in the instant a
  player announces a frame, the player puts the frame two after it in its place when it runs
  again, and the hand-over that was under way gives that frame the number of its position. It
  happened once in about 60,000 frames with the stops of `stopped` at random. The checks know
  when the process was stopped, which the engine does not: a wrong number whose hand-over a
  stop began in is counted and said, and not judged. `--investigate starts` makes it happen
  about once in a hundred plays; `--investigate waits` shows that a hand-over which merely
  waits through a stop keeps its frame.

A scene drawn with such a frame has the layout of the frame it was taken for; it is counted
with them and said in the check of the scenes.

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
  a player fail while a preview opens, after every player has handed over a frame, and makes
  one say that it cannot decode what it opened, before any frame: each once, which the engine
  answers by opening once more after a wait, and twice, which fails the open. A file that is
  not a video has to fail at the first attempt, with the error code of what failed underneath
  as the exception's `InnerException.HResult`.
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
- The `pause` group ends with `Pause()` and `Play()` called one after the other while the render
  thread is kept in one draw for 1.1 s, which is longer than `Pause()` waits for it. The render
  thread then finds playing wanted, as when it last looked, and a clock that `Pause()` has
  stopped; playback has to go on. Written on 5 October for a fault found by reading the engine.
  It was first run that evening, with the fix in, and passed (`--only device,pause`: 23 of 23).
  **It has not been run without the fix, which is the only thing that would show that it
  catches the fault.**
- After that, `Pause()` is called on one thread and kept inside it for 0.9 s once it has
  stopped the clock (`StudioPreviewOptions.PauseDelay`), and `Play()` comes from another thread
  meanwhile. The render thread starts the clock again; when the pausing thread gets on it must
  not shut the picture to the playback that has begun. A second later the position has to have
  moved on by twenty frames and scenes have to be drawn. **Written on 5 October for another
  fault found by the same reading (`Pause()` shut the picture outside the lock under which
  playback is started again), while no check tool could be run: this check has never run.**
- The `stalls` group (`HeadlessChecks.HeldUp.cs`) pauses a playing preview again and again
  while the process is held up, and reads at the moment `Pause()` has returned what a caller
  reads: `Position`, and the frame each clip's picture holds. It reads them again when the
  engine has nothing left to do, looks at every scene drawn in between, and then asks for the
  next frame. The moment of the return and the rest that follows are two checks, because a
  frame that the engine fetches anew is put right in between. A full run goes through `none`,
  `collector`, `draw`, `stopped` and `afterstop`. A note says in how many pauses a player still
  handed a frame over once the clock had been stopped, how long after the call, and how many of
  those frames were a later one than the stopped clock's position named: those are the frames
  the engine has to leave out, and `--stop-noted-late` is the control that lets them in.
- The `zoom` group plays through a zoom that moves in over three seconds and reads every scene
  the engine draws (`MovingScenes.cs`): which frame the screen's picture is, from its strip, and
  where the edges of the strip's cells are, to a tenth of a pixel. A scene is right when the
  edges are where the layout of that same frame puts them. One frame further on they are up to
  five pixels away. It does that directly and through the editor session.
- `TruthProbe.cs` reads the number of every frame a player hands over, from the copy itself and
  through a hook the engine has for it, and holds it against the number the engine gave the
  frame, if it gave one. That is the check "no frame was given a number that its pixels do not
  show", and where the counts of frames without a number come from. Two kinds of wrong number
  it counts apart and does not judge unless asked to: see *Holding the process up*.
- A pause can take the picture back: to the last frame the engine could tell, when the scene
  showed a later one of which it could not (`windows\docs\studio-preview.md` says why). The
  checks that judge the order in which frames were drawn therefore judge it up to the moment
  the pause was asked for, and report what the pause did. While it plays, and where the engine
  stops the clock itself to go somewhere (a `Seek` while playing, the end of the recording), no
  frame may be drawn after a later one.
- The `end` group also plays into the end of the recording while the process is held up, by the
  collector and by slow draws in turn. When the clock runs out, a clip can be showing a frame
  without a number; the picture then has to stay until the last frame is there.
- The `cuts` and `scenes` groups (`HeadlessChecks.Editing.cs`) drive `StudioEditorSession` on
  the engine as the `editor` group does. `cuts` makes a cut in the middle and one that ends the
  video, plays over the first and into the second, and steps and scrubs into the cut while
  paused; its notes say what is seen at a cut. `scenes` makes two scenes with different layouts
  and a move between them, and reads the rectangles of the screen and the camera back from the
  scenes drawn while it plays through the move and while it rests inside it.
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
- The `device` group ends with a camera that is shown for the middle of the recording only, and
  loses the device where the camera is no part of the picture: paused before its first frame,
  and playing after its last. The camera's player is parked there and hands nothing over, so
  the scene has to be drawn without waiting for its picture; and the camera has to show its
  last frame when it is next asked for on it. Written on 5 October for a fault found by reading
  the engine. They were first run that evening, with the fix in, and passed
  (`--only device,pause`: 23 of 23). **They have not been run without the fix, which is the
  only thing that would show that they catch the fault.**
- The `dispose` group also closes a preview while this tool holds one of its recordings open, as
  another program that plays the video does. A recording in the project folder is waited for
  (5 s, after which the engine says that it gave up): that is the control. A screen recording
  outside the folder, the user's own video, is not waited for.
- `Windowed\` holds the window checks. The tool writes four corner markers with 1 px stripes and
  the scene's number into every scene before it is presented; a screenshot then says whether the
  picture is on screen pixel for pixel, where it is, and which scene it shows. Screenshots are
  delivered a little after the system composed them, so one that shows an older scene than the
  last one drawn is passed over.
- `Checks\HeadlessChecks.Investigate.cs`, `HeadlessChecks.Names.cs`, `HeadlessChecks.Reopens.cs`
  and `HeadlessChecks.Stops.cs` hold the experiments described under *Experiments* above.
- Windows leaves kernel handles behind for every `MediaPlayer` and `MediaTimelineController` ever
  created (about 12 per open and close of a preview). The handle check therefore measures two
  bare players on a controller first, used the way the engine uses them, and holds the engine to
  that figure plus a margin of 3. Handles of the thread pool and of I/O in progress are left out
  of the comparison, because they come and go by a dozen whatever the process does.
