---
name: tinyclips-record-demo
description: Record a screen demo video, GIF, or Studio project with Tiny Clips, then trim or edit it. Use when the user wants a screen recording, demo, or GIF of a workflow.
---

# Record a demo

Needs scope `record` (`edit` to open editors, `discard` to throw a take away).

1. `tinyclips recording.status` must be `idle`; otherwise ask before stopping the current recording.
2. Start:
   - Video: `tinyclips recording.startVideo`
   - Studio project (zooms, backgrounds, cursor effects): `tinyclips recording.startVideo studio=true`
   - GIF: `tinyclips recording.startGif`
   To target autonomously, pass the same `mode`, `displayId`, `windowId`, and `region` parameters used by `capture.screenshot` (use `tinyclips call ... --params '<JSON>'`). Targeted recording skips the picker and setup window, and uses saved recording settings, including any configured countdown. Refresh IDs from `capture.targets` first.
   On macOS and Windows, add `microphone=false camera=false` to video or Studio starts for a screen-only take; this prevents source capture without changing saved preferences or needing `settings` scope. Omitted values use saved choices. `true` requires the user's existing Tiny Clips device opt-in AND already-granted OS device permission; do not enable saved settings or request consent as a workaround. Check effective `microphone`/`camera` in recording status (also in Windows start results). macOS schedules startup/countdown asynchronously; `started: true` alone does not confirm device startup. Poll status and inspect its optional `error` for startup/device failures. System audio is separate and unchanged. GIFs capture neither device.
3. Perform or wait for the demo steps. Poll `tinyclips recording.status` instead of sleeping blindly.
4. Pause/resume with `tinyclips recording.togglePause`; finish with `tinyclips recording.stop`. On macOS, a `permission_required` response to resume or restart leaves the current take intact. Agent-started recordings save directly without opening a trimmer.
5. Find the file: `tinyclips library.list limit=1`.
6. Trim without a window: `tinyclips call editor.trim --params '{"path":"<file>","start":1.5,"end":8}'` (video or GIF; add `"removeAudio":true` for silent video). Save a still: `tinyclips call editor.saveFrame --params '{"path":"<file>","time":3.2}'` (GIFs also accept `"frame":<index>`). Both need scope `edit` and save new files; the original stays. To edit interactively, use `tinyclips editor.open path="<file>"`; add `studio=true` to open a library MP4 or MOV directly in Studio when Studio is enabled.

For a machine-readable `editor.open` result, pass `resultFormat=standard`: `opened` is a boolean and `path` is the opened file on either OS.

Restarting (`recording.restart`) or discarding (`recording.discard`) deletes the take. The app asks the user to approve them; if they decline you get `approval_denied`. Don't retry.
