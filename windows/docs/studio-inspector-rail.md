# Studio inspector: rail and panels (Windows handoff)

**Status:** built on macOS on 6 October 2026. Built on Windows the same day, from this brief
(`8c3f4e1`, with its checks in `2c5ac42`): the rules are unit tested, and no window has been opened
with it yet. What follows is the brief as it was written, before that. What was decided where it
says "to decide there", and where the code is not as it expected, is in
`plans/video-studio-plan.md` under "The Windows inspector as a rail" and "The inspector: a rail
and one panel".

## Why

The owner ran Studio on a Mac for the first time and found the editing right and the inspector
hard to use: eleven sections (Scene, Layout, Background, Screen, Camera, Zoom, Cut, Speed, Audio,
Extras, Project) in one scroll, all open at once, and some labels that did not say what they did.
The Windows inspector (`windows/src/TinyClips.App/Controls/Studio/StudioInspector.xaml`) has the
same shape and the same labels, so it has the same problem.

The owner chose, from three options: **a rail of buttons down the inspector's edge, one panel on
show at a time, and taking hold of something on the timeline shows the panel that edits it.**

## What the Mac has now

Reference implementation, all under `mac/TinyClips`:

| What | Where |
|---|---|
| The panels, their order, groups, and what a recording without a camera has | `Studio/StudioInspectorPanel.swift` (Foundation only) |
| Tests of the above | `mac/TinyClipsTests/StudioInspectorPanelTests.swift` |
| The panel on show and what changes it | `Views/Studio/StudioViewModel.swift`: `inspectorPanel`, `showInspectorPanel(_:)` |
| The rail, the panel container, and the Background, Screen, Camera, Audio, Project panels | `Views/Studio/StudioViews.swift`: `StudioInspectorView`, `StudioInspectorRail` |
| The Scene, Zoom, Cut, Speed panels | `Views/Studio/Studio{Scene,Zoom,Cut,Speed}Views.swift` |

### Layout

- The inspector is a panel (328 pt) with a rail (72 pt) on its outer edge, a divider between them.
- The panel has its name as a heading that stays put, and its controls in a scroll view under it.
  A panel starts at its top each time it is shown.
- A rail button is a symbol over the panel's name. The panel on show is marked with a tinted
  fill, a tinted symbol, and a heavier name. The name is always visible: symbols alone were judged
  too hard to tell apart for Scene, Cut, and Speed.
- The rail draws a line between three groups.

### Panels

In rail order. A recording without a camera has neither Scene nor Camera.

| Group | Panel | Holds |
|---|---|---|
| Look | **Scene** | Previous/next scene and "Scene 2 of 3"; **Layout** (the four layouts); **Split Scene** and its notes; **Transition** (Instant or Animated, and Duration); **Timing** (Start); **Delete Scene** |
| Look | **Background** | Show background, the Solid and Gradient swatches, Padding |
| Look | **Screen** | Corner radius, Shadow, **Click highlights**, and a **Crop** group |
| Look | **Camera** | **Placement** (bubble: Size, Position, offsets; side by side: Camera side, Camera size); **Appearance** (Shape, Corner radius, Mirror, Camera background, Border, Shadow); a **Crop** group |
| Timeline | **Zoom** | Previous/next, Add Zoom, Suggest Zooms; for the selected zoom: Zoom level, **Focus** (Fixed Point or Follow Pointer, the pad, Horizontal, Vertical), **Timing** (Start, End, Zoom-in time, Zoom-out time), Delete Zoom |
| Timeline | **Cut** | Previous/next, Add Cut; for the selected cut: **Timing** (Start, End, its length), Delete Cut |
| Timeline | **Speed** | Previous/next, Add Speed Change; for the selected one: the rate, **Timing** (Start, End, its length), the note about sound, Delete Speed Change |
| Rest | **Audio** | Mute, and the volume sliders a recording has. On Windows: Mute and one **Volume**, of the whole video (8 October 2026) |
| Rest | **Project** | **Export** (Tiny Clips badge); **Storage** (Keep this project, with its reason written under it); **New Recordings** (Save as Default Look, with what it saves written under it) |

What moved, so nothing is lost:

- **Layout** is in the Scene panel, because a layout belongs to a scene.
- **Extras** is gone. Click rings went to Screen as **Click highlights**; the badge went to Project.
- **Mute** is in Audio (on Windows it sits under Extras today).
- The four **Crop** sliders are behind a header that opens and closes them. It starts open when
  a crop is set and closed otherwise, and when closed over a crop it says "Cropped".
- In the Camera panel, what belongs to the scene (Placement) is apart from what belongs to the
  whole video (Appearance, Crop). With more than one scene a note says so.
- With the Screen layout, the Camera panel says the camera is hidden and has a **Show Scene**
  button that goes to the Scene panel.
- An empty Zoom, Cut, or Speed panel says how to add the first one, with its key.

### What shows a panel

| The user does this | Panel shown |
|---|---|
| Presses a rail button | That panel |
| Presses or drags a zoom, cut, or speed block on its lane | Zoom, Cut, Speed |
| Adds a zoom, cut, or speed change: timeline button, menu, or Z, X, R | Zoom, Cut, Speed |
| Steps with Previous/Next zoom, cut, or speed change (button or menu) | Zoom, Cut, Speed |
| Suggest Zooms | Zoom |
| Presses a block on the scene lane | Scene |
| Splits a scene (button, menu, or S), also when the split is refused, so the reason is on show | Scene |
| Steps with Previous/Next scene | Scene |
| Starts dragging the camera in the preview | Camera |
| Opens a project | Scene, or Background without a camera |

What does **not** change the panel: undo and redo, changing the layout with 1 to 4, playing,
scrubbing on the trim bar, pressing an empty part of a lane, deleting the selected item. The panel
is not part of the project and not an undo step. Keyboard focus is never moved by a jump.

A request for Scene or Camera on a recording without a camera goes to Background or Screen.

### Words that changed

Mac strings, in title case as the Mac writes buttons. Windows keeps its own sentence case.

| Was | Now | Why |
|---|---|---|
| Entered by: A Cut / Moving | Transition: Instant / Animated | "Cut" also names the Cut feature two panels away |
| Move takes | Duration | |
| Split at Playhead | Split Scene | Matches the menu |
| Cut at Playhead | Add Cut | Matches the menu, and Add Zoom |
| Change Speed at Playhead | Add Speed Change | Matches the menu |
| Scale | Zoom level | |
| Looks at: A Point / The Pointer | Focus: Fixed Point / Follow Pointer | |
| Ease in / Ease out | Zoom-in time / Zoom-out time | The value is a time |
| Background (in Camera) | Camera background | The canvas has a Background too |
| Camera share | Camera size | |
| Crop left, top, right, bottom | Left, Top, Right, Bottom under a Crop header | The accessible names stay "Screen crop left" and so on |
| Show a background | Show background | |
| Mute audio | Mute | It is in the Audio panel |
| Click rings | Click highlights | |
| Extras | (gone) | |
| "…puts its stretch back…", "How fast this stretch plays" (help text) | "…the part it removed…", "this part of the video" | |

### What did not change

- Every editor-model string (`StudioEditorModel` on the Mac, `StudioEditorText` on Windows), so
  the two stay word for word the same: the VoiceOver and Narrator descriptions, "suggested",
  the scene notes, `SpeedSilentNote`, and so on.
- The project format, the fixtures, the lanes, the transport row and its button names, the
  Studio menu, every key.
- **Keep this project**, **Save as Default Look**, **Suggest Zooms**, **Remove Suggestions**: the
  owner's own terms or good as they were.

## What to do on Windows

1. **The panel model** in `TinyClips.Core` (next to `Studio/Editing/StudioEditorText.cs`), a
   port of `StudioInspectorPanel.swift`: the nine panels, `Groups(hasCamera)`,
   `Available(hasCamera)`, `Initial(hasCamera)`, `Resolved(panel, hasCamera)`, a title and a
   one-line summary each. Port the six tests to `TinyClips.Core.Tests`.
2. **The view model** (`ViewModels/Studio/StudioViewModel.Inspector.cs`): an `InspectorPanel`
   property and `ShowInspectorPanel`, called from the places in the table above. On the Mac the
   calls sit in the select, add, step, split, and suggest methods, not in the views, so the
   menu and the keys get them too.
3. **The inspector** (`Controls/Studio/StudioInspector.xaml`, 320 px wide in
   `Views/Studio/StudioWindow.xaml`): the rail, one panel on show, and the sections regrouped as
   in the Panels table. Use what WinUI gives for a vertical single-choice list, so UI Automation
   reports the rail as a list with one selected item; do not build it from plain buttons. Icons
   from Segoe Fluent Icons. Follow `.github/instructions/windows-winui.instructions.md`.
4. **The words.** Apply the table in sentence case ("Add cut", "Zoom level", "Follow pointer").
5. **Keep every `AutomationProperties.AutomationId` as it is.** `StudioWindowCheck` finds
   controls by them.
6. **`StudioWindowCheck`** (`windows/tools/StudioWindowCheck/Checks/Inspector.cs` and the lane
   and section checks): a collapsed panel's controls are not in the UI Automation tree, so a
   check has to show the panel before it looks for a control in it. Checks that assert a
   control's `Name` or text need the new words. Add checks for the jump table.
7. **`windows/docs/accessibility-release-gate.md`:** rows A11Y-19, A11Y-21, A11Y-23, A11Y-25,
   A11Y-26, and A11Y-30 name sections, tab order, and labels that change. Add a row for the rail:
   reach it with Tab, move in it with the arrow keys, and hear which panel is selected.
8. **`windows/CHANGELOG.md`**, and the Windows build and tests in the root agent instructions.

### Where Windows differs, to decide there

- **Audio.** Windows has Mute and no volume sliders (one mixed track). The Audio panel then has
  one check box. Keep the panel so both platforms have the same nine, or put Mute in Project
  until volumes exist: ask the owner.
  *Since 8 October 2026 the panel has a second control:* a **Volume** slider under Mute, for
  the whole video (`audio.volume`), which the owner asked for. It goes from 0% to 100% in steps
  of 5%, is switched off while Mute is on, and the rail describes the panel as "Audio: mute
  and volume". A volume for a part of the sound still needs two sound tracks.
- **The time rows** on Windows have −0.1 s and +0.1 s buttons where the Mac has a stepper. Keep
  them.
- **Lane blocks take the keyboard focus on Windows.** Moving focus onto a block with the
  keyboard selects it today; decide whether that alone should change the panel, or only
  activating it. The Mac has no such case.
- **`StudioWindowCheck` has three failing checks of its own** (see "Known problems on Windows"
  in `plans/video-studio-plan.md`). Do not count them against this work.

## How the Mac was checked

- `StudioInspectorPanelTests`: 6 tests, passing.
- The editor was drawn off screen from a real project with a camera, in light and dark, with each
  panel shown in turn, and the pictures read. In the same run, adding a zoom, a cut, and a speed
  change, splitting a scene, and selecting a zoom each put the view model on the right panel.
- The owner has edited three recordings with it by hand since 6 October, the last on the
  afternoon of 7 October, and had nothing to say against it.
- Still not checked by anyone: the rail with VoiceOver and with Full Keyboard Access, and a
  recording without a camera.
