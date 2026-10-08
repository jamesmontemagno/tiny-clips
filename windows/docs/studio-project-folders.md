# Studio: save a project as a folder, open it again, delete it (Windows handoff)

**Status:** built on macOS on 7 October 2026. On Windows the store's half is built and tested
(item 1 under "What to do on Windows", and the rule for drafts in recent captures), and the
app's half is built: the commands in the editor, the file type, the drafts in the tray, and
Open project in Settings. The editor's part was run in the real window by the window check;
the app itself was not started, so nobody has opened a `.tinyclips` file from Explorer or
seen the tray. See "How Windows was checked" at the end. **The Mac's store does not have the replace rule of that evening yet**
(below, and section 14 of the format).

## Why

A project lives in the app's own storage, which no one is meant to look into. The owner wanted
to save a project, open it again, and get rid of one easily. He decided:

- A saved project is **an ordinary folder**: the recordings, and beside them a `.tinyclips`
  file with the metadata, which the system opens the app with. Not a macOS package and not an
  archive, so that both platforms can read the same folder.
- **Opening a `.tinyclips` file copies the project into the app's storage** as a new draft and
  opens the copy. The folder is only read. Editing it in place was the other choice and was
  turned down: cleanup, the drafts list, and reopening from an exported video would all change.
- **Nothing is copied unless he saves or opens.** A new recording stays where it is today.
- **Delete Project** in the editor, which asks first.
- **Open Recent** in the editor, for the other projects.

## The contract

Section 14 of [`docs/studio-project-format.md`](../../docs/studio-project-format.md), "A project
saved as a folder", is the contract. In short:

```
<any folder>/My Demo/
  My Demo.tinyclips   project.json under another name, with `exports` empty
  screen.mp4          required
  camera.mp4          when the project has a camera
  events.json, poster.jpg, the background image: when the project has them
```

Saving writes a copy and leaves the project in the store as it is. Opening reads the file by the
rules every project is read by, requires the screen file and the camera file beside it, follows
only plain file names, gives the new project an id of its own, and empties `exports`, so it is a
draft that cleanup leaves alone. Saving over a folder replaces it only when it is a saved
project: a folder with exactly one `.tinyclips` file in it.

## What the Mac has now

| What | Where |
|---|---|
| Saving, opening, finding the file in a folder, the folder's name, the order of Open Recent | `mac/TinyClips/Studio/StudioProjectStore.swift`: `exportProjectFolder`, `importProjectFolder`, `projectFile(at:)`, `isSavedProjectFolder`, `folderName(for:)`, `StudioProjectSummary.recent` |
| Their tests (20) | `mac/TinyClipsTests/StudioProjectFolderTests.swift` |
| Save Project, Delete Project, the list for Open Recent | `mac/TinyClips/Views/Studio/StudioViewModel.swift`: `saveProjectFolder`, `deleteProject`, `refreshRecentProjects` |
| Open Project, opening a file the system hands over | `mac/TinyClips/Views/Studio/StudioWindow.swift`: `chooseProjectFileToOpen`, `openProjectFile(at:)`; `TinyClipsAppDelegate` in `TinyClipsApp.swift` |
| The file type | `Info.plist` and `Info-MAS.plist`: `com.refractored.tinyclips.studio-project`, extension `tinyclips` |

In the editor:

- A **Project** menu button in the header, left of Undo: Open Recent (up to 8 other projects,
  the one opened last first, each with the date it was recorded, because two can have one
  name), Open Project…, Save Project…, Delete Project….
- The same four in the Studio menu: Open Project… is Command-O and Save Project… is
  Shift-Command-S.
- The **Project** panel of the inspector has Save Project… and, under a line at the bottom,
  Delete Project…, each with a sentence saying what it does.
- A project that cannot be shown has Delete Project… under its message.
- While the folder is being written the editor is dimmed with "Saving project…" and takes no
  edits, and the window does not close.
- After a save, the folder is shown in the Finder with the `.tinyclips` file selected.
- **Delete Project…** asks, with the project's name, and says that exported videos and saved
  folders are not deleted and that it cannot be undone. It stops an export that is running,
  closes the window, and deletes the project's folder in the store.

## What to do on Windows

1. **The store** (`TinyClips.Core/Studio/StudioProjectStore.cs`): the same five operations, by
   section 14. Port the 20 tests. The ones that matter most are the refusals: a file that names
   something outside its folder, a file without its recordings, a later `schemaVersion`, saving
   over something that is not a saved project, and that a failed save or open leaves nothing
   behind.
2. **The file type.** Register `.tinyclips` for the app in the package manifest (and for the
   unpackaged run, where that applies), and handle file activation: a `.tinyclips` file opens
   Studio on a copy of its project. Studio switched off: say so and import nothing, as the Mac
   does. The app is tray-first; opening a project file must not show another window with it.
3. **The editor:** Open Recent, Open project…, Save project…, and Delete project… in the
   header and in the Project panel of the inspector, in sentence case. Ctrl+O and Ctrl+Shift+S,
   if they are free in the window.
4. **Pickers.** Saving asks for a folder name in a place the user chooses. Opening accepts a
   `.tinyclips` file or the folder that holds one. The packaged app reaches the recordings
   beside a picked file only through the folder, so read up on what a file picker grants there
   before choosing between picking the file and picking the folder. The Mac's App Store build
   has the same limit: it asks for the folder when a file alone was handed to it.
5. **Copying takes time** on a drive that cannot clone files. Do it off the UI thread, with the
   editor taking no edits meanwhile.
6. **`StudioWindowCheck`**, the accessibility gate, `windows/CHANGELOG.md`, the README's list of
   keys.

### Drafts in Recent captures

Added on the Mac the same day, after the rest. The menu bar menu's Recent Captures lists the
last five captures that were saved as files. A video exported from Studio is one of them and
opens its project. A recording kept as a draft has no file, so it was not there, and with no
editor open the only way back to it was the list in Settings.

Now a draft is listed there too, mixed in by date, as "Name — Studio project, date", with its
poster as the picture. A draft here is what the Settings list calls one: nothing exported, or
what was exported is gone; not flat; and its recording still there to open. Its date is when it
was last open, or failing that recorded. Still five lines in all. With Studio switched off none
is listed. A folder a project was saved to is never listed: it is a copy.

Reference: `StudioProjectSummary.menuDrafts` and `lastUsedAt` in
`mac/TinyClips/Studio/StudioProjectStore.swift`; `RecentMenuEntry.merged` and
`StudioRecentDrafts` in `mac/TinyClips/Services/SaveService.swift`; three tests under "Recent
Captures" in `StudioProjectFolderTests.swift`. Do the same wherever the Windows tray menu lists
recent captures.

### To decide there

- **A folder saved on a Mac opened on a PC, and the other way.** This is what the folder was
  chosen for, and it has not been tried. Section 2 of the format lists what the two readers take
  differently; none of it is something either writer writes. Try both directions with a real
  recording before saying it works. One known difference: a Mac recording can have the computer's
  sound and the microphone as two sound tracks, and the Windows preview and exporter read the
  first sound track only (section 7).
- **Recordings that are not `screen.mp4` and `camera.mp4`.** The names come from the project
  file. Follow them; do not assume them.

### A folder saved on a Mac, exported on a PC (7 October)

The owner brought the folder of his recording of that afternoon to the PC, with the video the
Mac exported from it: 2880 × 1800, a camera that starts 1.4 s in, two scenes with a move
between them, a cut, a stretch at twice the speed, a zoom that follows the pointer and one on
a point. Windows could not open a folder yet that afternoon. What was tried is the half that existed: the project
file was read by the Windows reader and exported by the Windows exporter, without the app, by
`StudioRenderCheck --export-project` (written for this), and the result was held against the
Mac's export frame by frame. The folder was only read.

- **It reads and exports.** The Windows reader takes the Mac's `.tinyclips` file as it is. The
  export is 2880 × 1800 and 264 frames; the Mac's is 265.
- **The pictures agree where nothing moves.** Frame against frame the two are alike to 0.985 to
  0.991 (SSIM, 1 being the same picture) in the stretches at rest: the same canvas, card, corners,
  camera bubble, and side by side. Six pairs of frames were also looked at.
- **On Windows the Mac's pictures are about two frames late.** The camera in the Windows export
  is most like the Mac's two to three frames earlier, in both stretches measured. The Mac's
  encoder stores frames out of their order (the file says so). With the camera encoded again
  without that, the same pictures at the same times, the lag is none to
  one frame. So the Windows reader takes such a file's pictures about two frames (70 ms) later
  than the Mac does, against the sound and against every time in the project. Where in the
  reader, whether the preview does the same, and whether a Windows recording ever has such
  frames, was not looked for. Not mended.
- **Where something moves the two differ more** (0.80 to 0.89): through the zoom that follows the
  pointer, the move between the scenes, and where a zoom moves in or out. The two frames above
  are part of that; whether there is more to it was not separated.
- **Not tried:** opening the folder in the app, the preview, and a folder saved on Windows opened
  on a Mac.
- **Sound.** The export has one sound track, as section 7 now asks. Which of the Mac's two
  tracks it holds was not gone into, at the owner's word. One reading was taken before that:
  the computer's track of this recording is silent, and the Windows export is not, at about
  the level of the microphone's track.

## How the Mac was checked

- 20 tests of the store pass.
- A project was saved as a folder and opened again in a test that loads the editor on the copy:
  it has the zoom it was saved with and its name.
- A `.tinyclips` file was opened with the running app from the command line, as the Finder does
  it, with the app not running and with it running: a new project in the store, one Studio
  window, and no Clips Manager window.
- On the afternoon of 7 October the owner pressed Save Project… on a new recording that had
  exported once, double-clicked the `.tinyclips` file in the Finder, looked for the draft in
  Recent Captures, and pressed Delete Project…, and reported that each worked. What the files
  show: the folder has `screen.mp4` (with two sound tracks, the computer's and the
  microphone, in that order), `camera.mp4`, `events.json`, `poster.jpg` and the `.tinyclips`
  file; that file's `exports` is empty and it names nothing outside the folder; and opening it
  made a second project in the store, with an id of its own and no exports.
- That folder is 51 MB and shows a desktop, so it is not in the repository. The owner is
  taking it to the PC by hand to open there (#429).
- Not done by anyone yet: everything about the App Store build's sandbox, which a build made
  without signing does not have.

## What changed on the evening of 7 October, for both platforms

The owner was asked what saving over a folder may replace. By the first rule any folder with
exactly one `.tinyclips` file in it was a saved project and was replaced with everything in
it, so a project file copied by hand into Documents made Documents replaceable. His decision:
**replace only a folder that holds nothing a save does not write, and refuse otherwise.**
Section 14 of the format has the rule in full. Windows has it. **The Mac still replaces by the
first rule** (`saveProjectFolder` and `isSavedProjectFolder` in `StudioProjectStore.swift`,
read); that is the Mac session's to change and to run.

Decided with it, by the Windows lead and not asked: an entry whose name starts with a dot is
not counted when a folder is searched for its `.tinyclips` file, so that the `._Name.tinyclips`
a Mac writes on an exFAT stick does not make the folder unopenable on a PC. The Mac skips every
hidden file there, which comes to the same for those names.

## How Windows was checked

The store's half, 7 October, in `TinyClips.Core` (`StudioProjectStore.Folders.cs`,
`StudioProjectFolder.cs`, `Services/RecentMenuEntry.cs`):

- **Unit tests.** The Mac's 20 tests and its three for recent captures have Windows twins, and
  there are more for what Windows adds: names Windows takes for something else, links, two
  names for one file, a folder put in place file by file, and the replace rule. The Core tests
  count 2,199 with them, 4 skipped on purpose. Each rule of the new code was taken out once,
  and a test fails for every one but one: that the names of a folder are sorted before the
  first that does not belong is named, which NTFS does by itself.
- **The owner's folder from the Mac**, the one above. Opened by the Windows store from its
  file and from its folder: a project with an id of its own and no exports, and the four files
  copied byte for byte; the folder itself unchanged. Saved again by Windows, the only value in
  the project file that differs is the `id`, and `audioTracks` is kept. The writing differs
  (line ends, spacing, the order of keys). The test that does this is skipped unless it is
  given the folder, and its project file is the fixture
  `shared/studio/fixtures/folder/saved-on-macos-1.9.0.tinyclips`, which every run reads.
- **Found by the tests on the way:** a folder that was just filled cannot always be renamed
  into place while a virus scanner or the search index has a file in it open. The folder is
  now made empty under its name and the files moved in one by one, the project file last.
- **One limit, stated in a test:** when something else holds a file of the new copy open and
  lets it neither be renamed nor deleted, the save fails, what was to be replaced is as it
  was, and the folder the copy was filled in stays beside it with that one file.

Where Windows differs from the Mac's store, on purpose: the folder a project is saved into is
not made when it is not there; a suggested folder name is cut at 80 characters, since the name
is in the path twice; a recording that is a link is refused; and a recording whose name
Windows would take for something else (a colon, `CON`, a dot at the end) is refused as naming
something outside the folder.

### The app's half (the night of 7 October)

Items 2 to 6 above, and the drafts in recent captures. What a person sees, where the Mac's
description above is not the whole of it:

- **The editor** has one **Project** button with a menu in the header, left of Undo: Open
  recent, Open project… (Ctrl+O), Save project… (Ctrl+S), Delete project…. One button and not
  four: four more would take the preview's room at the window's smallest width, and it is
  what the Mac has. The inspector's Project panel ends with Save project… and, under a line,
  Delete project…, each with a sentence; a project that cannot be shown has Delete project…
  under its message. Open recent and Open project are in the header only, as on the Mac:
  they are not about this project.
- **Save project is Ctrl+S on Windows, not Ctrl+Shift+S.** Ctrl+Shift+S is the app's own
  global hotkey for Stop recording, registered at launch and not changeable, and Windows
  gives a registered hotkey to the app and not to the window with the keyboard. That is read
  in the code, not tried.
- **Saving asks in a dialog of its own**: a box for the folder's name, which starts as the
  project's name made fit for a folder, the place, which starts where a project was last
  saved and in Documents the first time, and **Choose folder…** over the folder picker. The
  system's save picker was the other way: it makes an empty file where it is pointed, which
  a folder of that name then cannot take, and its own "replace?" is about a file. What is at
  the place is looked at before the dialog closes, with the store's own function
  (`WhyASaveWouldNotReplace`), so nothing is copied before the answer is known:
  - nothing there: saved;
  - a saved project with nothing else in its folder: **Replace the saved project “Name”?**,
    naming the folder and where it is, Cancel by default; Cancel brings the dialog back;
  - a saved project that holds something else: no question; under the name, "This folder
    holds X, which is not part of the project, so it was not replaced. Choose another name.";
  - anything else: "There is already something with that name, and it is not a saved Tiny
    Clips project. Choose another name.";
  - a name a folder cannot have: "A folder cannot be called that. Try “…”."
- **While the recordings are copied** the editor is under "Saving project…" with a progress
  bar and Cancel, takes no edits, and is told to ask before its window closes. The edits the
  editor holds are written into `project.json` before the copy starts. **The save can be
  stopped**, by Cancel, by Esc, and by closing the window and answering Stop and close; the
  Mac's cannot, and its window beeps instead. Afterwards Explorer shows the folder with the
  `.tinyclips` file selected.
- **Opening** copies off the UI thread. Nothing of the app shows meanwhile, as on the Mac;
  on Windows a copy that is still running after two seconds is said to be running in a
  notification, and the same file asked for again meanwhile is not copied twice. Why a
  project was not opened is said in the editor's message bar when an editor asked, in a
  dialog in Settings, and in a notification when the file came from Explorer: "Studio could
  not open this project:" and the store's sentence. With Studio switched off: "Tiny Clips
  Studio is switched off. Switch it on in Settings, under Studio, to open this project.",
  and nothing is copied.
- **The open picker takes a `.tinyclips` file**, not a folder. The app is full trust, so the
  recordings beside the file are read by their path, and no folder has to be chosen for them.
  That is read from the manifest's `runFullTrust`, not tried on an installed app.
- **The tray's Recent** lists drafts among the saved captures, by `RecentMenuEntry.ForMenu`.
  The projects are read off the UI thread at launch, when an editor opens, has read its
  project, or closes, and when the popup is about to show; the popup shows what was read last
  and makes its Recent button again when the read finds another list.
- **Settings › Studio** has Projects with Open project…, and says under Project storage how
  a project outlives an uninstall.

The tray popup has no Open project: the Mac's menu bar menu has none, and the popup has no
menu to put it in. The brief for this work named the tray among the places for the open
picker; a draft's line and a file opened from Explorer are the tray's ways in.

**Run:**

- Unit tests. In `TinyClips.Core.Tests`: the session's save (the edits first, no edits
  meanwhile, stopped, refused, a close that waits), the keys, the question a close asks, and
  every sentence, with the cases of a place that is taken worked on real folders. In
  `TinyClips.App.Tests`: the editor's view model (what Open recent lists, when a command is
  passed on, what a save says and shows), the file type in both manifests and how a project
  file is told from a picture, the projects the tray lists, and the Settings page's markup.
- The editor's part in the real window, by `StudioWindowCheck --only project`: 20 checks
  through UI Automation, with stand-ins for the two pickers, the Explorer window and the
  notification, and the Project menu read without being opened. They save a real project
  into a temp folder, have the store open it again, try to edit while a save is held, replace,
  are refused, stop a save, open the saved folder as a second editor, and delete.

**Read, not run:**

- That Ctrl+Shift+S does not reach the window.
- That a full-trust package reads the recordings beside a file it was handed.
- What Windows does with the file type: the icon, the name in Explorer, "Open with".

**Not run by anyone: the app.** It was not started for this work. So nobody has opened a
`.tinyclips` file from Explorer, with the app running or not; seen the notification for
Studio switched off or for a file that cannot be opened; opened the tray popup with a draft
in it; pressed Open project… in Settings; or seen the folder picker, the open picker, the
Explorer window, or the open Project menu. A person's steps for all of it are step 22 of
the hands-on checklist in [`plans/video-studio-plan.md`](../../plans/video-studio-plan.md),
and rows A11Y-35 to A11Y-39 of the [accessibility gate](accessibility-release-gate.md).

Not tried by anyone, on either half: a folder saved on Windows opened on a Mac; the folder
the owner saved on his Mac opened in the Windows app (the store opened it); a path longer
than 260 characters; a FAT or exFAT volume; a recording of gigabytes, which is where the
progress bar, the notice after two seconds, and cancelling in the middle of a copy matter.
