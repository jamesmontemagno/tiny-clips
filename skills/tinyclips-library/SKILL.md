---
name: tinyclips-library
description: Find, open, copy, delete, or upload Tiny Clips captures. Use when the user asks about recent screenshots, recordings, GIFs, or sharing them.
---

# Manage captures

- List: `tinyclips library.list limit=20 kind=all|screenshot|video|gif` (newest first, needs `read`).
- To add a Library video to an existing Studio project on macOS, use `studio.timeline.import` with its returned path and the destination's revision tokens. Keep the default `library` mode to retain a linked recording's editable layers. See the tinyclips-studio Skill.
- Show in Finder/Explorer: `tinyclips library.reveal path="<file>"`.
- Open the clips library window: `tinyclips app.open window=library`.
- macOS screenshot editors: `tinyclips editor.list` (needs `read`) lists open sessions and close-safety reasons. `tinyclips editor.closeAll` (needs `edit`) closes only saved, unchanged screenshots and reports remaining sessions for user review; it never discards work. It excludes Studio/trimmers and is unavailable on Windows.
- Copy to clipboard: `tinyclips library.copy path="<file>"` (needs `export`).
- Delete: `tinyclips library.delete path="<file>"` (needs `delete`; moves to Trash/Recycle Bin after the user approves).
- Upload: `tinyclips library.upload path="<file>"` (needs `upload`; uses the upload service configured in the app, after the user approves). Returns the URL.

Only delete or upload files the user explicitly asked about. Settings: `tinyclips settings.get` lists the settings agents can read and change; change them with `tinyclips settings.set key=<key> value=<value>` (needs `settings`). `recentCapturesLimit` is an integer from 5 through 30 and controls the menu/tray Recent popover count without discarding retained history.
