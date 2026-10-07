# Studio: a command of its own to start a Studio recording (Windows handoff)

**Status:** built on macOS on 7 October 2026, and on Windows the same day from this brief
(#431). On Windows it is compiled and unit tested and has never been run: nobody has opened the
tray menu with the command in it, pressed it, or seen the label in the recording setup panel.
What to try by hand is step 20 of the Windows hands-on checklist in
[`plans/video-studio-plan.md`](../../plans/video-studio-plan.md). "What Windows has now" below
says where each part is.

## Why

There were three things that could make a recording a Studio recording, and two of them could
disagree: the **After recording** setting with its Open in Studio choice, and the **Record for
Studio** button of the recording setup panel, which started from the setting and could be
switched for one recording. The owner found that more than it needed to be and decided:

- **Two commands in the menu: Record Video… and Studio Recording….** The command carries the
  flag to the recorder.
- **Record Video… is always an ordinary recording**, and so is its shortcut.
- **The Studio choice of After recording goes, and the button goes.** The menu command is the
  one way in.
- **No global shortcut for Studio Recording yet**, while Studio is a preview.

## What the Mac has now

| What | Where |
|---|---|
| **Studio Recording...** under **Record Video...** in the menu bar menu, shown only while Studio is switched on | `mac/TinyClips/Views/MenuBarViews.swift` |
| The flag: `startVideoRecording(forStudio:)` keeps it through the capture picker, the Record panel, and a picker that comes back after the recording, until a recording is asked for again. It counts only while Studio is switched on, asked when the recording is set up and again when it starts | `mac/TinyClips/CaptureManager.swift`: `pendingVideoIsForStudio` |
| The Record panel has no Studio button. For a Studio recording it shows a label, "Studio", which is not a control, and the Record button's hint says the editor opens afterwards | `mac/TinyClips/Views/StartRecordingPanel.swift` |
| `VideoAfterRecording`, `videoAfterRecording`, and `isStudioVideoRecordingEnabled` are gone. The Video page is as it is on `main`: **Open trimmer after recording** and **Save immediately** | `mac/TinyClips/Models/CaptureSettings.swift`, `mac/TinyClips/Views/Settings/VideoSettingsSection.swift` |
| The Studio page of Settings says under Recording how a Studio recording is started | `mac/TinyClips/Views/Settings/StudioSettingsSection.swift` |

What this takes away with it, on purpose: the rules for what the trimmer switch says while
Studio is the After recording choice, and what comes back when Studio is switched off ("Open in
Studio is stored apart from the trimmer switch" in `plans/video-studio-plan.md`). With no
Studio choice there is nothing to keep apart. A stored `videoAfterRecording` of `studio` is
left in the defaults and read by nothing.

A Studio recording that cannot be saved as a project is still rescued as an ordinary video,
and that video then does what the trimmer switch says.

## What to do on Windows

1. **The tray menu** (`App.xaml.cs`, where "Record video" and "Record GIF" are built): a
   **Studio recording** item under Record video, shown only while Studio is switched on, that
   starts a video recording with the flag set.
2. **Carry the flag** to where `setup.RecordForStudio` is read today, without the setup window
   being the one that decides it. Ask `StudioPreviewEnabled` again where the recording starts.
3. **`Views/RecordingSetupWindow.xaml`:** take out `RecordForStudioToggle`
   (`SetupRecordForStudioButton`) and show that the recording is a Studio recording in a way
   that is not a control.
4. **Settings › Video:** take the Open in Studio choice out of **After recording**
   (`VideoAfterRecordingCombo`). Windows shows that combo box while Studio is switched on and
   the **Open trimmer after recording** switch while it is off; with two choices left, the
   switch alone can do. The Mac went back to exactly what `main` has.
5. **The global hotkey for Record video** always makes an ordinary recording. No hotkey for
   Studio recording yet.
6. **Tests and checks that set After recording to Studio** to get a Studio recording
   (`SettingsViewModelStudioTests`, `CaptureSettingsStudioTests`, the hands-on checklist, the
   accessibility gate): they need the command instead, and the tests of what the trimmer switch
   keeps while Studio is chosen have nothing left to test.
7. `windows/README.md` ("While the switch is on, **After recording** decides…"),
   `windows/CHANGELOG.md`, and `studio-settings-page.md`, whose Recording section depends on
   this.

## What Windows has now

| What | Where |
|---|---|
| **Studio recording**, a button under Screenshot, Video, and GIF in the tray menu, built only while Studio is switched on. The menu is built anew each time it opens, so the button follows the switch while the app runs. It is disabled while a recording runs | `App.xaml.cs`: `BuildTrayPopupContent`, `StartStudioRecordingAsync`, `UpdateRecordingState` |
| The flag, and the whole decision of whether a recording is one for Studio. `Begin` takes the command the recording was asked for with: Studio recording sets the flag if Studio is on, Record video and its hotkey clear it, and a capture picker that comes back by itself leaves it. `IsForStudio` and `CreateOptions` ask `StudioPreviewEnabled` when the recording is set up, and `OptionsAtStart` asks again where it starts, after the countdown and for a restart | `TinyClips.Core/Capture/StudioRecordingIntent.cs`, tested in `StudioRecordingIntentTests` |
| Where the app tells it: `BeginCaptureAsync` calls `Begin` once the capture flow really begins, so a command that is ignored because a capture is being set up changes nothing | `App.xaml.cs`: `BeginCaptureAsync`, `ToggleVideoAsync`, `ReopenPickerAfterCaptureAsync`, `RestartActiveRecordingAsync` |
| The recording setup panel has no toggle and decides nothing. For a Studio recording it shows a label, "Studio", read as "Studio recording", which is not a control, and Record's help text says the editor opens afterwards | `Views/RecordingSetupWindow.xaml`, `.xaml.cs`: `StudioRecordingLabel` |
| `VideoAfterRecording`, `OpensTrimmerAfterVideoRecording`, and `IsStudioRecordingEnabled` are gone, and the Video page of Settings is the file `main` has: **Open trimmer after recording** alone | `TinyClips.Core/Services/CaptureSettings.cs`, `Controls/Settings/VideoSettingsSection.xaml`, `ViewModels/SettingsViewModel.Studio.cs` |

A stored `videoAfterRecording` stays in the app's local settings and is read by nothing. It is
not migrated: someone who chose Save or Open trimmer under After recording while Studio was on
has the trimmer switch as it was before that choice, because the choice left the switch alone
until Studio was switched off.

Where Windows differs from the Mac: the tray menu is a panel of buttons and not a list, so
"under Record Video" became a wide button under the row of Screenshot, Video, and GIF.

## How the Mac was checked

- 354 tests pass and both builds succeed. None of them starts a recording: that needs screen
  recording permission and a display, which the unit tests do not have by the repository's rule.
- On the afternoon of 7 October the owner chose **Studio Recording…** and recorded the whole
  display with the camera, the microphone and the computer's sound. The editor opened on it,
  and its project has both sound tracks listed, the camera, and the clicks.
- Not done by anyone yet: **Record Video…** with Studio on making an ordinary recording, and
  the capture picker coming back after a Studio recording. Nobody was asked about the label in
  the Record panel.
