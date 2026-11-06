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
4. For automatic zoom ideas, call `studio.zoom.suggest`. This is read-only. To apply the suggestions, retain existing manual zooms, replace the existing automatic zooms with the returned suggestions, and submit the combined list as `patch.zooms`.
5. Read `settings.get key=videoFolder` to find the configured destination. Call `studio.export.options` to see the sizes and estimated file sizes on offer, then export with `studio.export` to an unused `.mp4` path directly inside the folder, passing `resolution` (`source`, `2160p`, `1440p`, `1080p`, `720p`, `480p`) and `quality` (`best`, `high`, `standard`, `small`) when the user asked for them; `source` is the canvas's own size and can exceed what the machine can encode past 4K, so fall back to `2160p` if it fails. Omitted values use the choices last made in Studio's Export popup. Tiny Clips rejects existing files and destinations inside project/source folders; choose a different name rather than overwriting. CLI and MCP exports default to a two-hour request deadline; set `TINYCLIPS_REQUEST_TIMEOUT_SECONDS` in their environment to override it (`0` means no deadline).

Project times are seconds on the source recording timeline. Zoom focus coordinates are normalized from the top left. The project format is documented in `docs/studio-project-format.md`.
