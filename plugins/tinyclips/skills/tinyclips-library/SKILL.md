---
name: tinyclips-library
description: Find, open, copy, delete, or upload Tiny Clips captures. Use when the user asks about recent screenshots, recordings, GIFs, or sharing them.
---

# Manage captures

- List: `tinyclips library.list limit=20 kind=all|screenshot|video|gif` (newest first, needs `read`).
- Show in Finder/Explorer: `tinyclips library.reveal path="<file>"`.
- Open the clips library window: `tinyclips app.open window=library`.
- Copy to clipboard: `tinyclips library.copy path="<file>"` (needs `export`).
- Delete: `tinyclips library.delete path="<file>"` (needs `delete`; moves to Trash/Recycle Bin after the user approves).
- Upload: `tinyclips library.upload path="<file>"` (needs `upload`; uses the upload service configured in the app, after the user approves). Returns the URL.

Only delete or upload files the user explicitly asked about. Settings: `tinyclips settings.get` lists the settings agents can read and change; change them with `tinyclips settings.set key=<key> value=<value>` (needs `settings`).
