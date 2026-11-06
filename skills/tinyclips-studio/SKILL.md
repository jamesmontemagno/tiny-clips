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

   Object fields merge recursively; arrays such as `scenes`, `zooms`, `edits.cuts`, and `captions.cues` replace that whole array. To correct a caption, change its `text` and empty its `words`, which no longer match. To split, merge, or retime cues, replace `captions.cues` too: keep cues at least 0.1 seconds long and in time order, give a new cue a new `id`, and empty `words` that no longer fall inside their cue. Identity, project name, source recordings, cleanup pinning, and export bookkeeping are protected. A changed project returns `conflict`; read it again and reconcile before retrying. Edits to an open Studio window are undoable.
4. To point something out, add arrows, callouts, or text boxes to `overlays.annotations`. Send the whole list back, because it replaces the old one, and keep entries you did not change. Use `attach` `screen` for a place in the recording (it follows zooms) and `frame` for a place in the video. For example, to add an arrow and a title for ten seconds:

   ```json
   {"overlays":{"annotations":[
     {"id":"arrow-1","kind":"arrow","attach":"screen","start":12,"end":22,"x":0.71,"y":0.22,"offsetX":-0.25,"offsetY":0.2,"style":"red"},
     {"id":"title-1","kind":"text","attach":"frame","start":12,"end":22,"x":0.5,"y":0.9,"text":"Upgrading your plan","style":"white","size":"large"}
   ]}}
   ```

   `kind` is `arrow`, `callout` (with `text`), or `text`. `style` is `red`, `blue`, `yellow`, `white`, or `dark`, and `size` is `small`, `medium`, or `large`. For an exact color set `fill` and `textColor` to `#RRGGBB` (they stand in for the style's), and for a size in between set `scale`, 0.5 to 3, which multiplies `size`. `x`, `y` are normalized and are what is pointed at, or the center of a text box. `offsetX`, `offsetY` are in canvas short sides and place an arrow's tail or a callout's box. An annotation needs an `end` to show.
5. For automatic zoom ideas, call `studio.zoom.suggest`. This is read-only. To apply the suggestions, retain existing manual zooms, replace the existing automatic zooms with the returned suggestions, and submit the combined list as `patch.zooms`.
6. Read `settings.get key=videoFolder` to find the configured destination. Call `studio.export.options` to see the sizes and estimated file sizes on offer, then export with `studio.export` to an unused `.mp4` path directly inside the folder, passing `resolution` (`source`, `2160p`, `1440p`, `1080p`, `720p`, `480p`) and `quality` (`best`, `high`, `standard`, `small`) when the user asked for them; `source` is the canvas's own size and can exceed what the machine can encode past 4K, so fall back to `2160p` if it fails. Omitted values use the choices last made in Studio's Export popup. Tiny Clips rejects existing files and destinations inside project/source folders; choose a different name rather than overwriting. CLI and MCP exports default to a two-hour request deadline; set `TINYCLIPS_REQUEST_TIMEOUT_SECONDS` in their environment to override it (`0` means no deadline).
7. For captions, pass the text of an SRT or WebVTT file to `studio.captions.import`, with `timing` set to `editedVideo` when its times count from the edited video, such as a file made from an exported video. Call `studio.captions.check` to see readability problems and `studio.captions.fix` to make the safe fixes; both name a cue by its place in time order. Fixing can join a cue to its neighbor, so check again before naming a cue by its place after a fix. Export with `studio.captions.export` to an unused `.srt` or `.vtt` path directly inside the video folder. Captions in cut or sped-up parts of the video are left out.
   To draw captions into the picture, set `overlays.captions.enabled` to true with `studio.project.edit`, along with `size`, `position`, and `appearance` if the user cares, before `studio.export`. Viewers cannot turn them off, so only do it when asked.
   `studio.captions.generate` makes captions from the recording's sound, on the device, replacing the ones the project has. Call `studio.captions.models` first: a model that is not installed needs `allowModelDownload`, and the download can be gigabytes, so ask the user before setting it. It can take a long time. On a Mac the only model is `AppleSpeech`, which does not detect the language, so pass `language` if the recording is not in the system's. A Mac without macOS 26 and hardware that supports it answers `not_supported`, so import a file there instead. When it hears no speech it fails with `internal` and leaves the captions as they were; try another `language` on a Mac, or another `model` on Windows, such as one that is not English-only.

Project times are seconds on the source recording timeline. Zoom focus coordinates are normalized from the top left. The project format is documented in `docs/studio-project-format.md`.
