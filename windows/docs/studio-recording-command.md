# Studio: a command of its own to start a Studio recording (Windows handoff)

**Status:** Studio is generally available on macOS and Windows. The preview switch was removed;
the explicit Studio recording command remains separate from ordinary video recording.

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
- **No global shortcut for Studio Recording**; Record Video's shortcut remains an ordinary recording.

## What the Mac has now

| What | Where |
|---|---|
| **Studio Recording...** under **Record Video...** in the menu bar menu | `mac/TinyClips/Views/MenuBarViews.swift` |
| The command's recording intent is carried through the capture picker, the Record panel, and a picker that comes back after the recording, until a recording is asked for again | `mac/TinyClips/CaptureManager.swift` |
| The Record panel has no Studio button. For a Studio recording it shows a label, "Studio", which is not a control, and the Record button's hint says the editor opens afterwards | `mac/TinyClips/Views/StartRecordingPanel.swift` |
| `VideoAfterRecording`, `videoAfterRecording`, and `isStudioVideoRecordingEnabled` are gone. The Video page is as it is on `main`: **Open trimmer after recording** and **Save immediately** | `mac/TinyClips/Models/CaptureSettings.swift`, `mac/TinyClips/Views/Settings/VideoSettingsSection.swift` |
| The Studio page of Settings says under Recording how a Studio recording is started | `mac/TinyClips/Views/Settings/StudioSettingsSection.swift` |

The former preview switch and the Open in Studio After recording choice are gone. A stored
`videoAfterRecording` value of `studio` is left in the defaults and read by nothing.

A Studio recording that cannot be saved as a project is still rescued as an ordinary video,
and that video then does what the trimmer switch says.

## Windows implementation

Windows has the same command separation: Studio is an explicit tray tile; Record video and its
hotkey always make ordinary recordings. The recording setup panel labels Studio recordings but
does not offer a toggle.

## What Windows has now

| What | Where |
|---|---|
| **Studio**, a fourth capture tile in the tray menu, between Video and GIF: Screenshot, Video, Studio, GIF. Its accessible name is "Studio recording", it keeps the automation id `TrayStudioRecordingButton`, and it has no hotkey. It only starts a recording: while a video or a GIF recording runs it is greyed, and the Video tile, which says Stop for a Studio recording as for any video, stops it | `App.xaml.cs`: `BuildTrayPopupContent`, `StartStudioRecordingAsync`, `UpdateRecordingState` |
| The popup's width. Three tiles have the 344 they always had. With four, each label has 65.5 where it had 92; "Screenshot" needs about 61 at the usual text size, so the width stays 344. The labels are measured when the menu opens, and with four tiles the popup is made wider when one would wrap, as with a larger text size in Windows | `Infrastructure/TrayPopupLayout.cs`, tested in `TrayPopupLayoutTests`; `App.xaml.cs`: `ShowTrayPopup`, `WidestCaptureTileLabel` |
| The whole decision of whether a recording is one for Studio. `Begin` takes the command the recording was asked for with: Studio recording sets the intent, Record video and its hotkey clear it, and a capture picker that comes back by itself leaves it | `TinyClips.Core/Capture/StudioRecordingIntent.cs`, tested in `StudioRecordingIntentTests` |
| Where the app tells it: `BeginCaptureAsync` calls `Begin` once the capture flow really begins, so a command that is ignored because a capture is being set up changes nothing | `App.xaml.cs`: `BeginCaptureAsync`, `ToggleVideoAsync`, `ReopenPickerAfterCaptureAsync`, `RestartActiveRecordingAsync` |
| The recording setup panel has no toggle and decides nothing. For a Studio recording it shows a label, "Studio", read as "Studio recording", which is not a control, and Record's help text says the editor opens afterwards | `Views/RecordingSetupWindow.xaml`, `.xaml.cs`: `StudioRecordingLabel` |
| `VideoAfterRecording`, `OpensTrimmerAfterVideoRecording`, and `IsStudioRecordingEnabled` are gone, and the Video page of Settings is the file `main` has: **Open trimmer after recording** alone | `TinyClips.Core/Services/CaptureSettings.cs`, `Controls/Settings/VideoSettingsSection.xaml`, `ViewModels/SettingsViewModel.Studio.cs` |

A stored `videoAfterRecording` stays in the app's local settings and is read by nothing. It is
not migrated: someone who chose Save or Open trimmer under After recording while Studio was on
has the trimmer switch as it was before that choice, because the choice left the switch alone
until Studio was switched off.

Where Windows differs from the Mac: the tray menu is a panel of buttons and not a list, so
"under Record Video" became a tile next to Video. It was first built as a wide button under
the row of Screenshot, Video, and GIF; the owner chose the fourth tile on the evening of
7 October ("Decided on 7 October" in the plan has his words).

What the four tiles show:

| | Screenshot | Video | Studio | GIF |
|---|---|---|---|---|
| Nothing is recording | Screenshot | Video | Studio | GIF |
| An ordinary video is recording | Screenshot | **Stop** | Studio, greyed | GIF, greyed |
| A Studio recording is recording | Screenshot | **Stop** | Studio, greyed | GIF, greyed |
| A GIF is recording | Screenshot | Video, greyed | Studio, greyed | **Stop** |

Not looked at by anyone, and worked out from the font's figures and the popup's numbers:
whether "Screenshot" really has room in a tile a quarter narrower, and what the popup looks
like when a larger text size makes it wider. At twice the text size the popup's fixed height
is too low for what is under the tiles, with three tiles as much as with four; that is as it
is on `main`, and was not changed.

## How the Mac was checked

- 354 tests pass and both builds succeed. None of them starts a recording: that needs screen
  recording permission and a display, which the unit tests do not have by the repository's rule.
- On the afternoon of 7 October the owner chose **Studio Recording…** and recorded the whole
  display with the camera, the microphone and the computer's sound. The editor opened on it,
  and its project has both sound tracks listed, the camera, and the clicks.
- Not done by anyone yet: **Record Video…** with Studio on making an ordinary recording, and
  the capture picker coming back after a Studio recording. Nobody was asked about the label in
  the Record panel.
