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

   Object fields merge recursively; arrays such as `scenes`, `zooms`, `edits.cuts`, and `captions.cues` replace that whole array. To correct a caption, change its `text` and empty its `words`, which no longer match. Identity, project name, source recordings, cleanup pinning, and export bookkeeping are protected. A changed project returns `conflict`; read it again and reconcile before retrying. Edits to an open Studio window are undoable.
4. For automatic zoom ideas, call `studio.zoom.suggest`. This is read-only. To apply the suggestions, retain existing manual zooms, replace the existing automatic zooms with the returned suggestions, and submit the combined list as `patch.zooms`.
5. Read `settings.get key=videoFolder` to find the configured destination, then export with `studio.export` to an unused `.mp4` path directly inside it. Tiny Clips rejects existing files and destinations inside project/source folders; choose a different name rather than overwriting. CLI and MCP exports default to a two-hour request deadline; set `TINYCLIPS_REQUEST_TIMEOUT_SECONDS` in their environment to override it (`0` means no deadline).
6. For captions, pass the text of an SRT or WebVTT file to `studio.captions.import`, with `timing` set to `editedVideo` when its times count from the edited video, such as a file made from an exported video. Call `studio.captions.check` to see readability problems and `studio.captions.fix` to make the safe fixes; both name a cue by its place in time order. Export with `studio.captions.export` to an unused `.srt` or `.vtt` path directly inside the video folder. Captions in cut or sped-up parts of the video are left out.
   To draw captions into the picture, set `overlays.captions.enabled` to true with `studio.project.edit`, along with `size`, `position`, and `appearance` if the user cares, before `studio.export`. Viewers cannot turn them off, so only do it when asked.
   `studio.captions.generate` makes captions from the recording's sound, on the device, replacing the ones the project has. Call `studio.captions.models` first: a model that is not installed needs `allowModelDownload`, and the download can be gigabytes, so ask the user before setting it. It can take a long time.

Project times are seconds on the source recording timeline. Zoom focus coordinates are normalized from the top left. The project format is documented in `docs/studio-project-format.md`.
