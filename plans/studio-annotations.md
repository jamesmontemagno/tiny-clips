# Tiny Clips Studio: annotations

**Status:** planned with the owner on 5 November 2026. The code of phases 1 and 2 is written, and
compiled and unit tested in CI. No annotation has been checked by eye yet on a Mac or a Windows PC,
so the "done when" of phase 1 and the walkthrough of phase 2 are still open. Phases 3 and 4 have not
started. Both platforms ship each phase together, because
projects move between them and a build without annotations keeps them in the file but does not
draw them.

The rules a renderer follows are in [`docs/studio-project-format.md`](../docs/studio-project-format.md)
(`overlays.annotations` in section 3, section 6.10). This document says what is being built, in
what order, and why.

## What the owner asked for

- Start small: **arrows, callout bubbles, and text**.
- A clear answer to how they layer.
- **Do not crowd the UI at the bottom of the editor.**
- **Hide the timeline's lanes until something is added to them, the existing lanes included.**

## How it looks

| Part | Where | Why |
|---|---|---|
| Arrow, Callout, Text tools | A small group in the editor's header, with keys `A`, `C`, `T` | The transport row already holds six edit buttons. Adding three more there is what the owner wants to avoid |
| One **Annotate** lane | Under Speed, over the trim bar, hidden until the first annotation exists | One lane however many annotations there are |
| **Annotate** panel | A new rail item in the inspector's timeline group, after Speed | Holds the layer list, style, size, attach, timing, text, and delete, so none of it needs room at the bottom |
| Handles | On the preview, on the selected annotation | Direct manipulation, in the same place the camera bubble is already dragged |

Overlapping annotations share the one lane. Their blocks can overlap there, so the preview and the
panel's layer list are how one of several is chosen, and the selected block draws over its
neighbors, as selected lane blocks do today.

## Hiding empty lanes

A lane appears when it has something on it and goes when the last thing is removed. This applies
to the existing lanes as well as the new one.

| Lane | Shown when | Note |
|---|---|---|
| Scene | The recording has a camera **and** more than one scene | One scene has nothing to choose between. The Scene panel still edits it |
| Zoom | `zooms` is not empty | Suggested zooms count |
| Cut | `edits.cuts` is not empty | |
| Speed | `edits.speed` is not empty | |
| Annotate | `overlays.annotations` is not empty | |
| Trim bar | Always | It is how the video is scrubbed and trimmed, not a list of things added |

The rule is one pure function on each platform, next to `StudioInspectorPanel`, so it is unit
tested and the two cannot drift apart: given a project, which lanes show, in order.

What follows from it, and has to be handled in the same change:

- **Adding stays possible with nothing to press on a lane.** The transport buttons, the Studio
  menu, the keys, and the panels all add without a lane. An empty panel still says how to add the
  first one and with which key, which replaces what an empty lane says today.
- **Scrubbing.** Pressing an empty lane moves the playhead today. The trim bar and the lanes that
  remain still do.
- **Undo and redo** can make a lane come and go. That is the rule working, and it must not change
  the inspector panel on show.
- **Keyboard focus.** On Windows a lane takes the focus. When a lane goes because a key took its last block
  away, focus moves to Play, so the keyboard stays with the timeline.
- **The preview resizes** when a lane comes or goes, by the height of one lane (22 to 26). No
  animation. On Windows this resizes the swap chain, which is already handled and measured for
  window resizes, but it should be looked at while playing.
- **Tighter lane spacing on the Mac** (10 pt to Windows' 4) so that a project using every lane is
  barely taller than it is today.
- **Check tools and the gate.** `StudioWindowCheck` (Zooming, ZoomSection, Cuts, Speed, Scenes,
  Themes) looks lanes up by automation id, and a collapsed lane is not in the UI Automation tree,
  so each check must add something first. `windows/docs/accessibility-release-gate.md` needs rows
  for a lane appearing and going, and the Mac needs the same walk with VoiceOver.
- **Lane text goes away.** The "press Z" text inside an empty lane is unreachable once the lane is
  hidden: `emptyText` in the Mac lanes, and `EmptyText` in `StudioLane` on Windows.

This work needs no format change and does not depend on phase 1. It is the first step of phase 2
because the Annotate lane should be born following the rule.

## Layering

From the top of the picture down:

```text
Tiny Clips badge
Annotations, last in the list on top       <- new
Camera, border
Screen, click rings
Screen shadow, background
```

Annotations sit over the camera so that the bubble can never hide one by moving. The badge stays
on top because it is the app's own mark and does not move. The order among annotations is the order
of the list, so **Bring forward** and **Send back** in the panel move an item in the list.

Each annotation is attached to one of two things:

| `attach` | Editor default for | Behavior |
|---|---|---|
| `screen` | Arrow, callout | Its point is a place in the recording, like a click. It follows zooms, crops, and scene moves, keeps its size, fades with the screen during a morph, and is hidden while that place is out of view |
| `frame` | Text | A place on the canvas. It ignores zooms and scene changes |

An arrow's tail and a callout's bubble stay a fixed distance from the point, in canvas units, so
zooming in moves the annotation with what it points at without making it larger.

## Decisions, and why

| Decision | Why |
|---|---|
| Stored in `overlays.annotations`, schema version stays 1 | Additive. Readers keep unknown properties, so an older build opening a project and saving it loses nothing. It also makes `studio.project.edit` work at once |
| Times in source time | Like zooms and click rings. Trim, cuts, and speed apply by themselves and an edit to them moves nothing else |
| `kind` is kept as text, and a kind the reader does not know is kept and not drawn | The one exception to "an unrecognized enum reads as its default". Phase 4 adds kinds, and a build that drew a highlight as an arrow, then saved it as an arrow, would damage the project |
| Named styles and sizes, not free colors and pixel sizes | Five styles and three sizes can never be unreadable, look the same on both platforms, and take little panel space. Free colors can be added later as another property |
| Sizes relative to the canvas's short side | The preview and the export have different pixel sizes and must look the same |
| Box text centered on a stored point | Fonts differ between platforms, so box sizes do too. A center drifts least |
| A box is pushed back inside the canvas | Text that runs off the frame is unreadable. Applies to the moved position only: it is not stored |
| No fade or animation in the first version | Each adds format and preview work, and appearing and going at once is correct until seen otherwise |
| White space is a list in the format, not the platform's own idea | .NET and Foundation disagree about U+200B, and the same project would draw an empty box on one platform only |
| An arrow no longer than its head has no shaft | Stated in the format and worked out with the geometry, so that the two renderers cannot draw it differently |
| Each platform's own system font | Bundling one would add weight and licensing for no visible gain at this size. Text therefore does not match pixel for pixel across platforms |

## How it is drawn

The geometry is pure and shared by contract: section 6.10, the same fixtures on both platforms.
Measuring and drawing text is the platform's.

| | macOS | Windows |
|---|---|---|
| Geometry | `Studio/StudioAnnotations.swift` | `Studio/StudioAnnotations.cs` |
| Drawing | `StudioCompositor`: Core Graphics and Core Text into a bitmap, cached, laid on with Core Image | `StudioSceneRenderer`: Direct2D, DirectWrite (already in `Vortice.Direct2D1`), text layouts cached |
| Preview | The same compositor, with `containsTweening` already on | The same renderer. `StudioPreviewStillness.SameLayout` must report a change when an annotation starts or ends, or a frame of unknown number could be drawn with the wrong annotations |

## Agents

`studio.project.edit` merges objects and replaces arrays, so `patch.overlays.annotations` works
on both platforms from phase 1, undoable in an open editor. `studio.project.get` returns them.
No command is added, the same permissions apply, and `not_supported` does not arise because both
platforms have the feature. Phase 1 documents the shape in `docs/agent-integration.md`,
`skills/tinyclips-studio/SKILL.md`, and the command's description.

## Phases

### Phase 1: format and rendering

No UI. An annotation exists because an agent or a project file put it there.

- [x] This plan.
- [x] `overlays.annotations` and section 6.10 in the format document.
- [x] Shared fixtures in `shared/studio/fixtures/annotations/`, run by both test suites.
- [x] Models, JSON, and geometry on both platforms, with round-trip and default tests.
- [x] Drawing in preview and export on both platforms.
- [x] `StudioPreviewStillness` on Windows, with tests.
- [x] A `StudioRenderCheck` group on Windows that reads the result back from pixels.
- [x] Agent docs, the Studio skill, and the changelogs.

**Done when** an agent can add an arrow, a callout, and text to a project on either platform and
see them in the preview and in the exported MP4, in the right place through a zoom, and the project
opens on the other platform looking the same apart from the font.

### Phase 2: hidden lanes and editing

Built in this order. Where the build differs from what was first written here, it says so below.

1. Hidden lanes, as above, with the rule and its tests on both platforms.
2. Editor model: add at playhead, select, change, reorder, delete, one undo step each, with the
   same words on both platforms (`StudioEditorModel`, `StudioEditorText`).
3. The Annotate rail item and panel, with an inline text field.
4. The Annotate lane, built on the existing range lane: drag to move, handles to resize, and
   previous/next, as Zoom, Cut, and Speed have.
5. Header tools and `A`, `C`, `T`. `Delete` removes the selected annotation.
6. Accessibility: every control named, lane and rail reachable by keyboard, and every action
   available without dragging. New rows in the Windows gate; VoiceOver and Full Keyboard Access on
   the Mac.
7. Agent coverage reviewed again: anything new the editor can do must be reachable through
   `studio.project.edit`.

Typing in the panel's text box is one undo step until the box loses the focus or something else
is edited. The model keeps a small marker for this instead of an open edit group, so nothing is left
open if the window closes mid-word. Changing **Attach to** keeps the annotation where it is in the
picture at the playhead wherever that can be worked out.

Adding from a tool drops the annotation at the center of the visible picture, or for a `screen`
annotation at the center of the zoom window, lasting three seconds from the playhead.

### Phase 3: handles on the preview

Drag an arrow by its tip or tail, a callout by its point or bubble, and text by its box. On Windows
this is `StudioPreviewOverlay`, which already does this for the bubble; on the Mac, the preview's
existing drag handling. A drag is one undo step, and it snaps to the canvas's center lines. The
panel keeps exact values for keyboard and screen reader users. Pressing an annotation in the
preview selects it and shows the panel.

### Phase 4: later, each decided when it comes up

Drag-to-draw on the preview, inline text editing on the canvas, fade or pop animations, highlight
boxes and blur for sensitive areas, free colors, curved arrows, reusing the screenshot editor's
presets, and copying and pasting annotations.

## Risks and limits

- **Fonts differ.** The same text can wrap differently on each platform, so a box can differ in
  size. Centers and pointers follow the same rules.
- **A sparse recording on Windows.** The preview draws when a player hands a frame over. An
  annotation that starts while nothing on the screen changes can appear at the next frame handed
  over. Click rings and zoom eases have the same limit, and the export is exact either way.
  Compare with a real recording in phase 2.
- **No shadow or outline** in the first version, so a red arrow on red content is hard to see. The
  five styles are the answer for now.
- **Editing is reached through agents only** until phase 2. This is why phase 1 changes no UI and
  shows nothing to a user who has not asked for it.
- **Very long text** is not limited: a box is pushed inside the canvas and what does not fit is cut
  off by the frame. The panel in phase 2 is the place to discourage it.
- **Rendering must be seen on real machines.** The geometry and project reading run in unit tests
  and fixtures on any machine with the tools, but drawing needs Windows and macOS.
