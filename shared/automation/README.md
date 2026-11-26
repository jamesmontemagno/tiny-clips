# Tiny Clips automation contract

`commands.json` is the one contract the macOS app, the Windows app, the `tinyclips` CLI, and its MCP server share. `fixtures/policy.json` holds the expected permission decisions; `mac/TinyClipsTests` and `windows/tests/TinyClips.Core.Tests` both load it.

See [docs/agent-integration.md](../../docs/agent-integration.md) for the protocol, security model, and setup.

Before adding or changing Agent coverage for an app capability, decide whether agent access is safe and meaningfully useful. Do not expose every UI action automatically. If the value or safety is ambiguous, ask the user before adding it. If coverage is clearly warranted or explicitly requested, add or update commands in `commands.json`, implement and advertise them on each platform where the feature exists (otherwise return `not_supported`), and add a policy case if it introduces a new scope. Preserve pairing, scopes, sensitive-action approvals, and platform permissions; update CLI/MCP coverage, relevant Skills, the integration guide, and [`../agent/CHANGELOG.md`](../agent/CHANGELOG.md) as needed. If safe agent access is not feasible, explain it and get user direction instead of silently omitting or bypassing it.

Keyboard shortcut configuration is an agreed exception on both platforms: bindings remain
UI-only and are not exposed through `settings.get`, `settings.set`, CLI, or MCP. Agents use
capture and recording commands directly without reserving or changing the user's keyboard
bindings. An unassigned Stop Recording shortcut does not disable `recording.stop`.
Studio Recording has a separate optional UI shortcut, unassigned by default. Agents continue
to start editable recordings with `recording.startVideo studio=true`; no shortcut is required.

The `recentCapturesLimit` automation setting controls how many saved captures and Studio drafts
the macOS menu bar and Windows tray Recent popover displays. It is readable and writable on both
platforms, defaults to 10, and accepts whole numbers from 5 through 30. The apps retain up to 30
captures so lowering the setting does not discard hidden recent history.

`app.open window=guide` opens the offline illustrated Guide on either platform. Optional
`guideTopic` selects `capture`, `editor`, `recording`, `studio`, `library`, `agents`, or `help`,
including in an already-open Guide. It is valid only with `window=guide`; invalid topics,
null, and use with another window return `invalid_params` before a window changes.
Omission preserves the current selection, or defaults to Capture for a new window.
Guide results retain `opened: "guide"` and add `guideTopic` with the selected topic.
This read-scoped navigation does not start captures or change settings.

Autonomous capture commands use ephemeral target IDs from `capture.targets`; refresh them after app restarts. Region rectangles are display-local physical pixels with a top-left origin. For screenshots and video/GIF recordings, `mode=windowArea` opts into a fixed crop of the selected window's visible bounds on the display with the largest overlap. The crop is clipped to that display and does not follow the window; unrelated windows and overlays inside the bounds may appear. `mode=window` captures window-only content for screenshots on both platforms and for Windows recordings; macOS `mode=window` recordings still use the window's display region and can include overlays. On Windows, if a window area's display changes before a recording starts, the start fails with `conflict` instead of recording a shifted region. `windowArea` is not accepted for OCR or scrolling capture. Window-area results report `windowArea: {displayId, region, fixed, clipped}`, with `region` in display-local physical pixels. Annotation bounds and point lists are source-image pixels from the top left. `editor.annotate`, `editor.trim`, and `editor.saveFrame` always save a new library item and must not overwrite or delete their source. Media times are seconds from the start; GIF frame indexes are zero-based. Optimistic Studio edits that lose a race return `conflict`, not `busy`.

`recording.startVideo` and `recording.startGif` use the same screen/region/window/windowArea target parameters, saved recording settings, and bypass interactive pickers and setup UI. Agent-started recordings save without opening a trimmer. Scrolling capture is a start/status/stop/cancel lifecycle; the agent performs the scrolling with its own UI tools, and Tiny Clips only captures and stitches frames.

The interactive screenshot save preference is UI-only. `capture.screenshot` and
`capture.scrolling.stop` always produce persistent files. Changes to the existing screenshot
clipboard or editor settings return `conflict` if they would disable the last screenshot output.

Video/Studio starts on macOS and Windows additionally accept optional `microphone` and `camera` booleans. Omitted values use saved preferences; `false` prevents device capture for this recording only. `true` requires both the saved device opt-in and existing OS permission, otherwise `permission_required`; no permission prompt or preference write occurs. Choices survive countdown, Studio fallback, and restart, with authorization rechecked before capture starts again. `recording.status` reports effective capture as `microphone` and `camera`; Windows also returns these fields in the start response. macOS schedules startup/countdown asynchronously, so poll status; its optional `error: {code, message}` reports startup/device failures and clears at the next recording setup or restart. Its device booleans reflect running sessions, so are false during countdown, startup, or pause until a session runs again. System audio is unchanged. GIFs use neither device.

On macOS, resume and restart recheck device opt-ins and OS access for agent-controlled video takes, whether requested from the panel or an agent. If this check fails, the take is preserved and a paused take stays paused.

`editor.open resultFormat=standard` returns `opened: true`, `path` (the absolute file path), and `editor` (the editor kind) on both platforms. Omission retains the original platform-specific result.

On macOS, `editor.open` reuses an already open screenshot editor for the same file. `editor.list` (`read`) lists screenshot sessions with `id`, `path`, `canCloseSafely`, and an optional `reason`. `editor.closeAll` (`edit`) closes only safe screenshot sessions, returning `closed` IDs and `remaining` session objects. Reasons are `unsavedChanges`, `unsavedCapture`, `saving`, `notReady`, and `busy`. No force-discard option exists; Studio and video/GIF trimmers are excluded. Both new commands are macOS-only, omitted from Windows capability/tool lists, and return `not_supported` on Windows.

Studio automation uses `studio.projects.list` and `studio.project.get` for discovery, `studio.project.open` to open a project by ID, `studio.project.edit` for validated atomic patches to editable fields, including `overlays.captions` to draw captions into the picture, `studio.zoom.suggest` for read-only proposals, `studio.captions.check`, `studio.captions.fix`, and `studio.captions.import` for captions, `studio.captions.export` for new SRT and WebVTT files, `studio.captions.models` and `studio.captions.generate` for making captions on the device, `studio.export.options` for the resolutions and qualities on offer, and `studio.export` for unattended MP4 rendering. On macOS and Windows, `studio.project.saveCopy` uses export scope to create a portable, unlinked project snapshot in a new folder directly inside the configured video folder, copying imported videos without changing their originals. Project edits use an `expectedModifiedAt` concurrency check, preserve source/identity/bookkeeping fields, and apply to an open editor through its undo history. Studio exports must target a new MP4 directly in the configured video folder. Their `resolution` and `quality` default to the choices last made in Studio's Export popup, which agents can read and set as the `studioExportResolution` and `studioExportQuality` settings. The read-only `studioProjectFolder` setting reports where Studio's Save Project and Save Project As start: a folder chosen in Settings, or `Studio` inside the video folder when default folders are enabled or no custom folder is chosen. Turning default folders off restores the saved custom choice. It does not move drafts, source recordings, exports, or `studio.project.saveCopy` snapshots.
