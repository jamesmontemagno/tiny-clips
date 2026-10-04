# Windows accessibility release gate

Every Windows release must complete this matrix on the packaged build that will ship. It is a
manual release gate: source review and automated builds cannot establish Narrator speech, focus
order, or the usability of a keyboard-only flow.

## Running the gate

1. Install the signed release candidate MSIX on a supported Windows 11 machine and record the
   app version, package architecture, Windows build, display scale, and Narrator voice.
2. Run each applicable row with a keyboard only, then repeat it with Narrator running.
3. Record **Pass**, **Fail**, or **Not applicable** for both columns in the release evidence. A
   failure is release-blocking until it is resolved or an explicit waiver is approved.
4. Re-run every affected row after changing the corresponding flow. Run the tray and capture
   completion rows for every release because Tiny Clips starts tray-first.

Do not mark a row as passed based solely on automation metadata or a successful build. The
operator must verify that focus is visible, follows a sensible order, and returns to a usable
surface after dismissing a popup or completing a flow. All icon-only and custom controls must
have an accessible name, current state/value, and a keyboard alternative.

## Release matrix

| ID | Surface | Keyboard acceptance | Narrator acceptance | Initial status |
| --- | --- | --- | --- | --- |
| A11Y-01 | Tray popup | Open from the notification area; traverse every command; activate Screenshot, Video, GIF, Settings, Guide, recent captures, folders, and Exit; dismiss without trapping focus. | Announces each command, its shortcut/state where present, and the popup context. | Pending hands-on validation |
| A11Y-02 | Capture completion notifications | Complete screenshot, video, and GIF captures with the picker and tray popup closed. | Confirms recording start/stop and successful or failed save once through the tray-lifetime notification anchor introduced by #198. | Pending hands-on validation |
| A11Y-03 | Settings | Navigate every navigation item and each settings section with Tab, arrow keys, Space, Enter, and Escape; edit a hotkey and confirm focus returns sensibly. | Announces navigation selection, labels, values, toggle states, validation messages, and dialog buttons. | Pending hands-on validation |
| A11Y-04 | Capture picker | Use Tab/Shift+Tab, Enter, Escape, and R/S/W; open and change Countdown and the video time-limit flyouts. | Announces Region, Screen, Window, countdown, time-limit current values, and Cancel. | Pending hands-on validation |
| A11Y-05 | Screen picker | Select a display with keyboard navigation and cancel without starting a capture. | Announces the picker, each display name, primary-display state, resolution, and Cancel. | Pending hands-on validation |
| A11Y-06 | Window picker | Select a listed window with keyboard navigation and cancel without starting a capture. | Announces the picker, each available window title, and Cancel. | Pending hands-on validation |
| A11Y-07 | Region selector and outline | Confirm Esc always cancels and focus does not become trapped; verify the pointer-only selection path remains understandable with the instruction overlay. | Announces the region-selection instruction and cancellation path; document any keyboard-only limitation as a blocker or approved exception. | Pending hands-on validation |
| A11Y-08 | Countdown and region indicator | Start then cancel a countdown, including a region capture. | Announces each countdown value without duplicate or stale speech and does not expose the decorative region outline as actionable content. | Pending hands-on validation |
| A11Y-09 | Recording and processing indicators | Tab through pause/resume, audio mute, restart, discard, and stop; verify disabled and hidden controls are skipped; complete video and GIF processing. | Announces elapsed/paused state, audio mute state, button names, and processing context. | Pending hands-on validation |
| A11Y-10 | Screenshot editor | Reach every output action, tool, inspector control, color picker, canvas action, and close path by keyboard. | Announces tool selection, editable values, color names, selected annotation guidance, and output actions. | Pending hands-on validation |
| A11Y-11 | Video trimmer | Reach preview controls, trim range, speed, remove-audio, export, save, and cancel. On the trim range, use Left/Right to seek, Ctrl+Left/Right to adjust the start, Shift+Left/Right to adjust the end, Page Up/Down to move the range, and Home/End to seek. | Announces video preview, trim range help, controls, checked state, labels, and busy state. | Pending hands-on validation |
| A11Y-12 | GIF trimmer | Reach frame stepper, trim range, playback, speed, export, save, and cancel. Exercise the same trim-range keyboard commands as A11Y-11. | Announces current frame, trim range help, playback state, controls, labels, and busy state. | Pending hands-on validation |
| A11Y-13 | Onboarding | Complete, go back, and skip each step with keyboard only. | Announces the current step content, controls, and the changing Next/Get started action. | Pending hands-on validation |
| A11Y-14 | Guide | Read every section and shortcut with keyboard scrolling; close the window without trapping focus. | Announces guide headings, rows, shortcut labels, and scrollable content in a useful order. | Pending hands-on validation |
| A11Y-15 | Tiny Clips Studio (only while the Studio preview is switched on) | Open a draft from Settings › General. Reach Undo, Redo, Canvas, Export, every inspector control, Play, the frame steps, Start here, End here, and the trim bar's Start, End, and Playhead with Tab. Move the three trim bar parts with the arrow keys, Page Up/Down, Home, and End. Use Space, Left/Right, I, O, 1–4, Ctrl+Z, Ctrl+Y, and Ctrl+E from the window, and confirm a focused slider, drop-down, or button keeps its own keys. Start an export and stop it with Esc. Close a draft and choose each answer of the close question; confirm Delete is not the default. Delete a draft from Settings and confirm focus moves to a neighbouring row. | Announces the preview with its layout, every slider with its name and percentage, the trim handles and playhead as sliders with a time, background swatches by name with their selected state, "Export started", "Export finished" or "Export cancelled", the layout chosen with a number key, and the message bar when something fails. A project that cannot be opened reads its reason when the window opens. | Pending hands-on validation |
| A11Y-16 | Tiny Clips Studio zoom lane (only while the Studio preview is switched on) | Reach **Add zoom** in the transport row and the zoom lane with Tab; the lane is one stop, after **End here** and before the trim bar. Press Z to add a zoom at the playhead, Z again in the same place, and Z in the last third of a second of the recording. On the lane, select the previous and the next zoom with Left and Right and the first and the last with Home and End, and confirm the playhead moves into the selected zoom. Remove the selected zoom with Delete and bring it back with Ctrl+Z. Confirm the focus rectangle shows on the lane, and that the selected block differs from the others by more than its colour, also in a contrast theme. | Announces the lane as the list "Zooms", each zoom as a list item with its scale and times ("Zoom 2×, 12.0 to 16.5 seconds", with "follows the pointer" and "suggested" where they apply), the newly selected zoom as the selection moves, and "Zoom added.", "There is already a zoom here.", "There is no room for a zoom here.", and "Zoom deleted.", each once. An empty lane reads "No zooms. Press Z to add one at the playhead." | Pending hands-on validation |
| A11Y-17 | Tiny Clips Studio Zoom section of the inspector | With no zooms, reach Add zoom and Suggest zooms. With zooms, step through them with Previous zoom and Next zoom, and confirm focus moves to the other button at the first and the last zoom. With a zoom selected, reach Scale, Looks at, Horizontal, Vertical, the three buttons each of Start and End, Ease in, Ease out, and Delete zoom, in that order, and change each. Confirm focus is on Next zoom, Previous zoom, or Add zoom after Delete zoom, and on Suggest zooms after Remove suggestions. | Reads "Zoom 2 of 5" and the zoom's times between Previous zoom and Next zoom, every slider with its name and value ("Scale, 2×", "Ease in, 0.5 seconds"), the choice "Looks at" with "A point" and "The pointer", and each Start and End button by what it does ("Start 0.1 seconds earlier", "End at playhead"). Announces the zoom that Previous zoom or Next zoom lands on ("Zoom 2 of 5, 2×, 12.0 to 16.5 seconds"), the new time after a Start or End button ("Start 2.1 seconds"), "3 zooms suggested." or "No zooms to suggest for this recording.", "Suggested zooms removed.", and "Zoom deleted.". Where Suggest zooms or The pointer is unavailable, reads the reason as its description and as the text next to it. | Pending hands-on validation |
| A11Y-18 | Tiny Clips Studio focus pad and its sliders | The focus pad is for a pointer and is not a tab stop. Confirm everything it does can be done with the Horizontal and Vertical sliders under it: move the point a zoom looks at to each corner and to the middle, and see the dot on the pad follow. Confirm the pad and both sliders go while the zoom follows the pointer, and come back with "A point". | Does not land on the pad. Announces "Horizontal" and "Vertical" as sliders with a percentage ("Horizontal, 30%"). | Pending hands-on validation |
| A11Y-19 | Tiny Clips Studio crop sliders | In the Screen section, and in the Camera section in each layout that shows the camera, reach Crop left, Crop top, Crop right, Crop bottom, and Reset crop with Tab. Move each slider with the arrow keys, Home, and End, and confirm an edge stops where the opposite edge leaves a twentieth of the picture and the slider shows where it stopped. Confirm Reset crop is skipped while nothing is cut off, that focus is on Crop left after it is pressed, and that one Ctrl+Z brings the whole crop back. | Announces each slider by its name with a percentage ("Crop left, 8%"), and Reset crop as unavailable while nothing is cut off. | Pending hands-on validation |

## Evidence template

Copy this block into the release issue or pull request and replace every pending value:

```text
Windows version/build:
Tiny Clips version/package architecture:
Narrator voice:
Display scale(s):

A11Y-01 keyboard:     Narrator:
A11Y-02 keyboard:     Narrator:
A11Y-03 keyboard:     Narrator:
A11Y-04 keyboard:     Narrator:
A11Y-05 keyboard:     Narrator:
A11Y-06 keyboard:     Narrator:
A11Y-07 keyboard:     Narrator:
A11Y-08 keyboard:     Narrator:
A11Y-09 keyboard:     Narrator:
A11Y-10 keyboard:     Narrator:
A11Y-11 keyboard:     Narrator:
A11Y-12 keyboard:     Narrator:
A11Y-13 keyboard:     Narrator:
A11Y-14 keyboard:     Narrator:
A11Y-15 keyboard:     Narrator:
A11Y-16 keyboard:     Narrator:
A11Y-17 keyboard:     Narrator:
A11Y-18 keyboard:     Narrator:
A11Y-19 keyboard:     Narrator:

Waivers or linked blocking issues:
```
