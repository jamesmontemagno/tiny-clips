# Studio: icons on the editor's buttons (Windows handoff)

**Status:** built on macOS on 7 October 2026, and on Windows the same evening (#432): see
"What Windows has now" and "How Windows was checked" at the end. Nobody has seen the Windows
buttons in the running app.

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

## What Windows has now

Built on 7 October 2026 (#432). The same buttons as on the Mac, each with a `FontIcon` of Segoe
Fluent Icons before its words. Every glyph but the mark of a suggestion is in Segoe MDL2 Assets
as well, as the rail's are.

| Button | Where | Glyph | The Mac's symbol | Name in `StudioGlyphs` |
|---|---|---|---|---|
| Split, Split scene | timeline row, Scene panel | U+E90C DockLeft, a frame in two parts | `square.split.2x1` | `SplitScene` |
| Add zoom | timeline row, Zoom panel | the Zoom panel's, U+E8A3 ZoomIn | the Zoom panel's | `AddZoom` |
| Cut, Add cut | timeline row, Cut panel | the Cut panel's, U+E8C6 Cut | the Cut panel's | `AddCut` |
| Speed, Add speed change | timeline row, Speed panel | the Speed panel's, U+EC4A SpeedHigh | the Speed panel's | `AddSpeed` |
| Start here | timeline row | U+EA52 ImportMirrored, an arrow up to a line on its left | `arrow.left.to.line` | `TrimStart` |
| End here | timeline row | U+E8B5 Import, an arrow up to a line on its right | `arrow.right.to.line` | `TrimEnd` |
| Suggest zooms | Zoom panel | U+E794 Effects, the mark a suggested zoom has on its block | `sparkles` | `SuggestZooms` |
| Remove suggestions | Zoom panel | U+EA39 ErrorBadge, a cross in a ring | `xmark.circle` | `RemoveSuggestions` |
| Delete scene, zoom, cut, speed change | their panels | U+E74D Delete, a bin | `trash` | `Delete` |
| Reset crop | Screen and Camera panels | U+E777 UpdateRestore, an arrow going round against the clock | `arrow.counterclockwise` | `ResetCrop` |
| Show scene | Camera panel, when the camera is hidden | the Scene panel's, U+E8B2 Movies | the Scene panel's | `ShowScene` |
| Save as default look | Project panel | U+E790 Color, a palette | `paintpalette` | `SaveDefaultLook` |
| Save the screen recording | a project that cannot be shown | U+E74E Save, a disk | `square.and.arrow.down` | `SaveRecording` |

Four more names are for the project commands of #429: `OpenProject` (U+E838 FolderOpen),
`OpenRecentProject` (U+E823 Recent, a clock), `SaveProject` (the disk of `SaveRecording`), and
`DeleteProject` (the bin of `Delete`). Since the night of 7 October they are on:

| Command | Where | Held as |
|---|---|---|
| Open recent, Open project…, Save project…, Delete project… | the menu of **Project** in the editor's header | the `Icon` of each menu item, a `FontIcon` |
| Save project…, Delete project… | the end of the Project panel | a `StudioButtonLabel`, as the other buttons of the panels |
| Delete project… | under the message of a project that cannot be shown | a `StudioButtonLabel` |
| Open project… | Settings › Studio › Projects, on the button and before the card's header | a `StudioButtonLabel`, and a `FontIcon` |

The **Project** button itself shows the Project panel's glyph, the rail's folder, before its
word. It is set in code (`StudioWindow`'s constructor), as the marks of the blocks are,
because it is a button with a menu and holds a row like Export's, not a `StudioButtonLabel`.
**Choose folder…** in the Save project dialog has no glyph: it is none of the commands, and
the answers of a dialog have none.

**Where the glyphs are.** In one place, `StudioGlyphs` in
`windows/src/TinyClips.Core/Studio/Editing/StudioGlyphs.cs`, beside the rail's
(`StudioInspectorPanels.GetGlyph`). In Core and not in the app, because four of them are the
rail's, which are in Core, and because from there the app's markup, both test projects and the
window check can name them. The markup binds each button's glyph to it
(`{x:Bind editing:StudioGlyphs.AddZoom}`), so a name that does not exist does not compile. The
marks on a zoom's and a cut's block and the bin of Delete on the question on closing, which had
their glyphs written out, take them from there now too.

**The one rule** is held by `StudioInspectorPanelTests.AButtonThatAddsWhatAPanelEditsHasThatPanelsGlyph`
in the Core tests, the Mac's test ported. Two more tests there hold the code point of each name
and that buttons that do different things show different pictures. In the app tests,
`StudioButtonIconTests` reads the markup: each of the twenty-three buttons (the twenty of #432
and the three of #429 in the editor) shows its glyph from `StudioGlyphs` before its words and
has its name written on it, each of the four items of the Project menu has its glyph as its
icon, and no glyph that has a name is written out in the markup. The Settings button is held
by `SettingsStudioPageTests`.

**What a button holds.** A `StudioButtonLabel` (`Controls/Studio`): the glyph, 14 high, and 6
further on the words, as Export has them. The glyph and the words are both kept out of what a
screen reader walks, and the button's name is written on it in the markup: the words it had.
No name, help text, tooltip or `AutomationId` changed. Save the screen recording keeps its two
labels and has its glyph before them.

**Where there is no room.** The words come first. A label that is given less room than its
glyph and its words need leaves the glyph out, which covers a button of a panel. Buttons side
by side are each given all they ask for, so their row adds up what its items take and tells
the labels (`StudioGlyphRow`, and the rule in `StudioGlyphRowLayout`): the timeline's row, and
Add zoom beside Suggest zooms. Without its glyphs a row is what it was before it had them. This
was added because of the sum under "Text size at 200%" below.

Left as words, as on the Mac: the **At playhead** buttons and the 0.1 s steps beside them, and
Cancel on the export overlay. Play, the frame steps, Undo, Redo, Export and the steps between
scenes, zooms, cuts and speed changes had glyphs before, which are written out where they were.

## How Windows was checked

On the evening of 7 October 2026, on one PC, at a display scale of 150% and a text size of
100%, by the agent that built it. No person has looked at the buttons, and the app itself was
not started: the window is the real one, opened by `StudioWindowCheck` behind every other window.

- **Tests.** The Core tests: 2,218 in all, where there were 2,199; 2,214 passed and 4 were skipped,
  the same four as before. The app's tests: 130, where there were 100; all passed.
- **Builds.** The app in both flavours (x64 Debug, and the same with
  `-p:TinyClipsStoreBuild=true`) and the window check: no warnings.
- **The window check.** A full run: 579 checks, where there were 573, all passed, in 352 s. It
  finds the buttons by `AutomationId` and reads their names, and those checks pass as they
  were. One check read the words of Speed from the
  button's content as text, which a label is not; it reads them from the label now
  (`Checks/Speed.cs`). Six checks are new, three in each theme (`Checks/ButtonGlyphs.cs`): the
  timeline's row, given less room than its six glyphs need, gives them up, keeps its words and
  its names, holds nothing a screen reader walks, and gets them back; Add zoom beside Suggest
  zooms, and Add speed change alone, do the same; and five buttons that no picture of a panel
  shows are pictured.
- **Pictures, read by the agent.** At the window's smallest size, 980 × 640, in light and in
  dark (`window-timeline-light-smallest.png`, `-dark-`): the ten items of the timeline's row are
  on one row, nothing is cut or wraps, and there is room left between the time and Split (120
  effective pixels by the sizes). Each of the nine panels from its top in each theme
  (`panel-<panel>-light.png`, `-dark.png`), the end of the Zoom panel (`zoom-section-*-3.png`),
  a selected cut (`cut-looks-*-selected.png`), the project that cannot be shown
  (`cannot-be-shown-*.png`), the question on closing (`close-dialog-*.png`), and the five
  buttons the others do not show (`button-<button>-light.png`, `-dark.png`): every one of the
  twenty buttons, Save the screen recording and Delete on the question was seen in both themes
  with its glyph before its words, whole, and grey when the button is unavailable. The row
  without its glyphs (`timeline-row-without-glyphs-*.png`) looks as it did before it had them.
- **Text size at 200%: a sum, not something seen.** No tool may change that setting. The check
  measures the row's real items and measures every text and glyph of them again at twice its
  font size (its note "the timeline's row by its sizes"). In effective pixels: the window has
  46.7 around the row, so the row has 933.3 in the smallest window (980) and 1133.3 in the
  window as it opens (1180). At 100% the ten items are
  38.7 + 38.7 + 38.7 + 86 + 72 + 109.3 + 66.7 + 83.3 + 104.7 + 99.3 with 76 between them: 813.3,
  of which the six glyphs and the gap after each are 120. At 200% they would be
  52.7 + 52.7 + 52.7 + 171 + 113 + 186.3 + 102.7 + 136.3 + 178.7 + 167.3 with the same 76:
  1289.3, of which the glyphs are 204. So with its glyphs the row would not fit at 200% in a
  window narrower than 1336, and in the window as it opens End here would be cut. Without them
  it is 1085.3, which is what it was before: it fits the window as it opens, with 48 to spare,
  and not a window narrower than 1132, where the time (175) is cut first and the buttons with
  their gaps (910.3) are whole down to the smallest window. That is why the glyphs give way
  where the row has no room for them. What the row then does was run at 100% by taking the room
  away, and never at 200%.

**For a person to look at** (also in A11Y-15 of `accessibility-release-gate.md`):

- The buttons in the running app, in light, in dark and in a contrast theme, and whether each
  glyph says what its button does.
- At a text size of 200%: that the timeline's row is whole in the window as it opens, with
  words on every button and without glyphs; that making the window wider than about 1340 brings
  the glyphs back; that the buttons of each panel show their words; and that 100% brings every
  glyph back.
- With Narrator: that a button reads as its name and that nothing is read for its glyph.

**What nobody has tried.** The app itself; a contrast theme; any text size but 100%; a display
scale other than 150%; Narrator or any screen reader (the names were read through UI
Automation); a recording without a camera, whose row has no Split; Windows 10, which the app
does not run on; and a version of Segoe Fluent Icons older than the one on this PC (1.54).
