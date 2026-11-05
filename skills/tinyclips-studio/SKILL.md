---
name: tinyclips-studio
description: Inspect, edit, suggest zooms for, and export Tiny Clips Studio projects without driving timeline UI. Use for agent-assisted video polish.
---

# Edit a Tiny Clips Studio project

Needs `read` to inspect, `edit` to change a project, and `export` to render a video. If Studio is disabled, tell the user to enable it in Tiny Clips Settings. If access is missing, use the tinyclips-setup skill.

1. Discover projects with `tinyclips studio.projects.list`; use the returned `projectId`, never a guessed ID or project-file path.
2. Read the current project with `tinyclips studio.project.get projectId=<id>`. Keep both its `project.modifiedAt` and the opaque `revision` value for the next edit.
3. Apply only the fields needed with `studio.project.edit`, passing a partial `patch` and the fetched `expectedModifiedAt`. For example:

   ```bash
   tinyclips call studio.project.edit --params '{"projectId":"<id>","expectedModifiedAt":"<modifiedAt>","expectedRevision":"<revision>","patch":{"canvas":{"aspect":"landscape16x9","padding":0.08},"audio":{"volume":0.8}}}'
   ```

   Object fields merge recursively; arrays such as `scenes`, `zooms`, and `edits.cuts` replace that whole array. Identity, project name, source recordings, cleanup pinning, and export bookkeeping are protected. A changed project returns `conflict`; read it again and reconcile before retrying. Edits to an open Studio window are undoable.
4. To point something out, add arrows, callouts, or text boxes to `overlays.annotations`. Send the whole list back, because it replaces the old one, and keep entries you did not change. Use `attach` `screen` for a place in the recording (it follows zooms) and `frame` for a place in the video. For example, to add an arrow and a title for ten seconds:

   ```json
   {"overlays":{"annotations":[
     {"id":"arrow-1","kind":"arrow","attach":"screen","start":12,"end":22,"x":0.71,"y":0.22,"offsetX":-0.25,"offsetY":0.2,"style":"red"},
     {"id":"title-1","kind":"text","attach":"frame","start":12,"end":22,"x":0.5,"y":0.9,"text":"Upgrading your plan","style":"white","size":"large"}
   ]}}
   ```

   `kind` is `arrow`, `callout` (with `text`), or `text`. `style` is `red`, `blue`, `yellow`, `white`, or `dark`, and `size` is `small`, `medium`, or `large`. `x`, `y` are normalized and are what is pointed at, or the center of a text box. `offsetX`, `offsetY` are in canvas short sides and place an arrow's tail or a callout's box. An annotation needs an `end` to show.
5. For automatic zoom ideas, call `studio.zoom.suggest`. This is read-only. To apply the suggestions, retain existing manual zooms, replace the existing automatic zooms with the returned suggestions, and submit the combined list as `patch.zooms`.
6. Read `settings.get key=videoFolder` to find the configured destination, then export with `studio.export` to an unused `.mp4` path directly inside it. Tiny Clips rejects existing files and destinations inside project/source folders; choose a different name rather than overwriting. CLI and MCP exports default to a two-hour request deadline; set `TINYCLIPS_REQUEST_TIMEOUT_SECONDS` in their environment to override it (`0` means no deadline).

Project times are seconds on the source recording timeline. Zoom focus coordinates are normalized from the top left. The project format is documented in `docs/studio-project-format.md`.
