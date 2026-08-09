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

Waivers or linked blocking issues:
```
