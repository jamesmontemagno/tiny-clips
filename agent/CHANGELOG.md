# Agent CLI Changelog

All notable changes to the Tiny Clips Agent CLI, MCP server, Agent Plugin, and shared automation contract are documented here.

## Unreleased

### Added
- On macOS, the CLI and MCP can inspect a Studio timeline, import a Library video or recording, duplicate, move, split, trim or remove a clip, and record another take at a project position. Timeline edits use the open editor's undo history and require the revision the agent read. Recording into a project needs both `record` and `edit`; Windows does not advertise these commands.
- `capture.screenshot`, `recording.startVideo`, and `recording.startGif` support explicit `mode=windowArea` for a fixed, display-clipped screen region matching a window's visible bounds. Results include the resolved display-local rectangle; OCR and scrolling targets remain unchanged. On Windows, a window-area recording start fails with `conflict` if the display changed after the target was resolved. macOS `mode=window` recordings continue to record the window's display region and can include overlays.
- Added a dedicated Agent CLI changelog and release-note workflow.

### Changed
- The interactive screenshot save preference remains UI-only; agent screenshot and scrolling-capture commands continue to save persistent files. Screenshot clipboard/editor setting changes that would leave no output return `conflict`.
- Contributor instructions now require Agent API coverage to be evaluated case by case, not added automatically to every app capability.

## 0.3.0 - 2026-11-22

### Added
- Added `studio.project.open`, `studio.project.openFolder`, `studio.project.delete`, and `studio.project.saveCopy` workflows for discovering, opening, organizing, deleting, and copying portable Studio projects.
- Studio project discovery now identifies readable and unreadable Library entries, including IDs for damaged projects that agents can safely target for deletion.
- Added `app.open window=guide guideTopic=...` for opening or switching the in-app Guide to Capture, Editor, Recording, Studio, Library, Agents, or Help.
- `editor.open` now supports opening MP4 and MOV files in Studio and a cross-platform standard result format.
- Video and Studio recording starts accept per-take `microphone` and `camera` choices that require the user's existing opt-in and OS permission.
- Recording status now reports effective microphone and camera capture, including macOS startup errors for asynchronous device setup.
- Studio project edits now cover project speed ranges and the Agent release workflow now supports an isolated version-bump pull request before tag publication.

### Changed
- Agent CLI and MCP requests use the longer two-hour media deadline for Studio portable project copies as well as exports and trims.
- Shortcut configuration remains UI-only; capture and recording agents continue to use direct commands without reserving or changing system-wide keys.

## 0.2.0 - 2026-11-15

### Added
- Added macOS-only `notifications.status`, `notifications.requestPermission`, and `notifications.openSettings` commands that preserve user notification and OS permission choices.
- Added macOS-only `editor.list` and `editor.closeAll` commands for reviewing open screenshot editors and closing only sessions with no unsaved work.
- Added Studio caption commands for checks, safe fixes, SRT/WebVTT import and export, and on-device speech model discovery and generation.
- Added `studio.export.options` and resolution/quality choices for unattended Studio exports, plus the `studioExportResolution` and `studioExportQuality` settings.
- Expanded Studio project edits to cover annotations and export-safe caption settings.
- The CLI and MCP server now advertise only commands supported on the current platform.

### Changed
- Pairing now presents the requested scopes as checkboxes and can be revised in Settings without re-pairing.
- Agent Plugin setup now recommends Copilot CLI and VS Code plugin installation and retains a manual MCP path for other agents.
- Scope changes take effect immediately, and removed scopes invalidate pending sensitive requests.
- Shared automation documentation and SDK tooling now treat Pro gating as a non-agent feature; supporter purchase remains user-controlled.

## 0.1.0 - 2026-11-03

### Added
- Added the cross-platform .NET 10 Agent CLI and stdio MCP server, packaged as the `TinyClips` NuGet tool for `dnx` and global-tool installation.
- Added opt-in pairing, scoped permissions, local audit logging, token storage, app-specific approval, and revoke support for Agent Control.
- Added CLI and MCP commands for status, pairing, capture targets and screenshots, OCR, targeted video/GIF recording, scrolling captures, screenshot and media editing, library access, settings, and Studio projects.
- Added the repository-root Agent Plugin with setup, capture, recording, library, and Studio Skills.
- Added shared contract-driven schemas, fake-app policy tests, package smoke tests, and a cross-platform Agent CLI CI matrix for Linux, Windows, macOS Apple silicon, and macOS Intel.
- Added the separate `agent-v*` release workflow with NuGet Trusted Publishing and portable plugin packaging.
