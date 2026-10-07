# Studio: icons on the editor's buttons (Windows handoff)

**Status:** built on macOS on 7 October 2026. Not started on Windows. Nothing under
`windows/src`, `windows/tests`, or `windows/tools` was changed for it.

## Why

The owner asked for more icons on the editor's key buttons. Play, the frame steps, Undo, Redo,
Export, and the rail had them; the buttons that do the editing were words alone. On Windows it
is the same: in `Controls/Studio/StudioTimeline.xaml` only Play and the two frame steps have a
`FontIcon`, and Split, Add zoom, Cut, Speed, Start here, and End here do not.

## What the Mac has now

Every button keeps its words and gains a symbol in front of them. The names are in one place,
`StudioSymbol` in `mac/TinyClips/Studio/StudioInspectorPanel.swift`.

| Button | Where | SF Symbol |
|---|---|---|
| Split, Split Scene | timeline row, Scene panel | `square.split.2x1` |
| Add Zoom | timeline row, Zoom panel | the Zoom panel's, `plus.magnifyingglass` |
| Cut, Add Cut | timeline row, Cut panel | the Cut panel's, `scissors` |
| Speed, Add Speed Change | timeline row, Speed panel | the Speed panel's, `gauge.with.needle` |
| Start Here, End Here | timeline row | `arrow.left.to.line`, `arrow.right.to.line` |
| Suggest Zooms | Zoom panel | `sparkles`, the mark a suggested zoom has on its block |
| Remove Suggestions | Zoom panel | `xmark.circle` |
| Delete Scene, Zoom, Cut, Speed Change | their panels | `trash` |
| Reset Crop | Screen and Camera panels | `arrow.counterclockwise` |
| Show Scene | Camera panel, when the camera is hidden | the Scene panel's |
| Save as Default Look | Project panel | `paintpalette` |
| Save Screen Recording | a project that cannot be shown | `square.and.arrow.down` |

**The one rule:** a button that adds what a panel edits has that panel's symbol, so the rail,
the timeline row, and the panel's own button show one picture for one thing. A test holds it.

Left as words: the three **At Playhead** buttons, which sit in a row with a stepper and have no
room, and Cancel on the export overlay.

Nothing else changed: the names, the help text, the accessible names, and what each button does.

## What to do on Windows

1. The same buttons, each with a `FontIcon` from Segoe Fluent Icons before its text, chosen to
   mean the same as the Mac's symbol. Use the glyph the rail already has for Zoom, Cut, Speed,
   and Scene (`StudioInspectorPanels.GetGlyph` in `TinyClips.Core`) on the buttons that add or
   show them, and port the test that holds that.
2. The icon is decoration: keep it out of the accessible name, which stays the button's words.
3. Look at the timeline row at the window's smallest width, 980, and at 200% text size. Six
   buttons each grow by an icon. On the Mac they fit at 980 with room to spare.
4. `StudioWindowCheck` finds these buttons by `AutomationId` and should not need to change.
   `windows/CHANGELOG.md`.

## How the Mac was checked

- Every symbol name resolves to an image on this Mac, and each has been in SF Symbols since
  macOS 14 or earlier (the app's minimum is 15).
- The editor was drawn off screen at its default size and at its smallest, light and dark, with
  each panel shown, and the pictures read: nothing is clipped or wraps.
- The owner has had them in the running app since they were built, through one recording
  edited on the afternoon of 7 October, and said nothing against them. Nobody was asked to
  look at them one by one.
