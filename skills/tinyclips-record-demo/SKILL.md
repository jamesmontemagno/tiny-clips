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
3. Perform or wait for the demo steps. Poll `tinyclips recording.status` instead of sleeping blindly.
4. Pause/resume with `tinyclips recording.togglePause`; finish with `tinyclips recording.stop`. Agent-started recordings save directly without opening a trimmer.
5. Find the file: `tinyclips library.list limit=1`.
6. Trim without a window: `tinyclips call editor.trim --params '{"path":"<file>","start":1.5,"end":8}'` (video or GIF; add `"removeAudio":true` for silent video). Save a still: `tinyclips call editor.saveFrame --params '{"path":"<file>","time":3.2}'` (GIFs also accept `"frame":<index>`). Both need scope `edit` and save new files; the original stays. To edit interactively or in Studio instead, use `tinyclips editor.open path="<file>"`.

Restarting (`recording.restart`) or discarding (`recording.discard`) deletes the take. The app asks the user to approve them; if they decline you get `approval_denied`. Don't retry.
