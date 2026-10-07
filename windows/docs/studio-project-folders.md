# Studio: save a project as a folder, open it again, delete it (Windows handoff)

**Status:** built on macOS on 7 October 2026. Not started on Windows. Nothing under
`windows/src`, `windows/tests`, or `windows/tools` was changed for it.

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

## How the Mac was checked

- 20 tests of the store pass.
- A project was saved as a folder and opened again in a test that loads the editor on the copy:
  it has the zoom it was saved with and its name.
- A `.tinyclips` file was opened with the running app from the command line, as the Finder does
  it, with the app not running and with it running: a new project in the store, one Studio
  window, and no Clips Manager window.
- Not done by anyone yet: pressing Save Project… and Delete Project…, which ask through a panel
  and an alert; double-clicking the file in the Finder; and everything about the App Store
  build's sandbox, which a build made without signing does not have.
