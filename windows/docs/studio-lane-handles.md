# Studio lanes: handles to resize a zoom, a cut, or a speed change (Windows handoff)

**Status:** built on macOS on 7 October 2026. Not started on Windows. Nothing under
`windows/src`, `windows/tests`, or `windows/tools` was changed for it.

## Why

The owner wanted to make a zoom, a cut, or a speed change longer or shorter by dragging, "with
little handles instead of manually adjusting times". Both platforms could already do it: a drag
that starts within 6 px of a block's end moves that end. Nothing showed that it could be done,
the pointer did not change, and a block under 24 px wide could not be resized at all. A new
cut is one second long, and in a recording of a minute that is under 20 pt of the Mac window at
its default size, so the commonest cut was one of those.

On Windows the same rule is in `Controls/Studio/StudioRangeLane.cs` (`EndGripWidth` 6,
`EndGripMinimumBlockWidth` 24), and the blocks draw no grips either.

## What the Mac has now

Reference: `StudioEditorModel.laneBlockPart`, `laneHandleOutset`, and
`laneBlockHasInsideHandles` in `mac/TinyClips/Studio/StudioEditorModel.swift`, their five tests
under "Lane Block Handles" in `mac/TinyClipsTests/StudioEditorModelTests.swift`, and
`StudioLaneBlockHandles` in `mac/TinyClips/Views/Studio/StudioViews.swift`.

- **A handle is 8 pt wide,** at each end of a block. A press in it takes the end; a press
  between the handles takes the block.
- **A block of 28 pt or more has its handles inside its ends.** Each is a short upright grip
  mark. It is faint on a block at rest, and stronger, on a tinted end, while the pointer is over
  the block or the block is selected.
- **A narrower block has no room, so it is only moved, until it is selected.** Selected, it gets
  a handle outside each end, drawn as a tab in the block's own color, and is that much wider to
  press. The selected block is drawn over its neighbors, so its tabs are not hidden.
- **The pointer** is the left-right resize pointer over a handle.
- **Help text** on a handle says which end it changes. On a narrow block that is not selected,
  the block's help says to select it to show the handles.
- The handles are for a pointer. The Start and End rows of the inspector stay the way to do the
  same with a keyboard or a screen reader, and VoiceOver passes the handles by.

What a drag does once it has hold of an end did not change: the same view-model calls, the same
limits, one undo step.

## What to do on Windows

1. Port the three functions and their five tests to `TinyClips.Core`, and have
   `StudioRangeLane` ask them what a press takes hold of, in place of its own two constants.
2. Draw the handles on `StudioZoomBlock`, `StudioCutBlock`, and `StudioSpeedBlock`, inside or
   outside by the same rule, and show the east-west resize pointer over them.
3. A lane takes the keyboard focus on Windows and its arrow keys step from block to block
   (`StudioLane.HandleKey`); leave that as it is. Keep the handles out of the UI Automation tree.
4. `StudioWindowCheck` sends no pointer input. It checks a drag on a lane by calling what the
   lane's own handlers call, with places along the lane (its README, "What stands in for a
   person"). A place 7 or 8 px in from an end is an end now and was the body before, and a
   narrow selected block is 8 px wider on each side. Read those checks against the new numbers,
   and add checks for a narrow block: not resizable until selected, resizable by its outside
   handles after.
5. `windows/CHANGELOG.md`, the accessibility gate rows for the three lanes, and the Windows
   build and tests.

## How the Mac was checked

- The five tests of what a press takes hold of pass.
- The timeline was drawn off screen, in light and dark, with wide and narrow blocks selected and
  not, and the pictures read.
- Not checked by a person at the controls: dragging a handle with a pointer, the pointer
  changing over a handle, and the look of a block with the pointer over it.
