# Tiny Clips automation contract

`commands.json` is the one contract the macOS app, the Windows app, the `tinyclips` CLI, and its MCP server share. `fixtures/policy.json` holds the expected permission decisions; `mac/TinyClipsTests` and `windows/tests/TinyClips.Core.Tests` both load it.

See [docs/agent-integration.md](../../docs/agent-integration.md) for the protocol, security model, and setup.

When adding a command: add it to `commands.json`, implement it in both apps (or return `not_supported`), and add a policy case if it introduces a new scope.

Autonomous capture commands use ephemeral target IDs from `capture.targets`; refresh them after app restarts. Region rectangles are display-local physical pixels with a top-left origin. Annotation bounds and point lists are source-image pixels from the top left. `editor.annotate` always saves a new library item and must not overwrite its source.

`recording.startVideo` and `recording.startGif` use the same screen/region/window target parameters, saved recording settings, and bypass interactive pickers and setup UI. Agent-started recordings save without opening a trimmer. Scrolling capture is a start/status/stop/cancel lifecycle; the agent performs the scrolling with its own UI tools, and Tiny Clips only captures and stitches frames.

Studio automation uses `studio.projects.list` and `studio.project.get` for discovery, `studio.project.edit` for validated atomic patches to editable fields, `studio.zoom.suggest` for read-only proposals, and `studio.export` for unattended MP4 rendering. Project edits use an `expectedModifiedAt` concurrency check, preserve source/identity/bookkeeping fields, and apply to an open editor through its undo history. Studio exports must target a new MP4 directly in the configured video folder.
