# Agent integration (CLI, MCP, Skills)

Tiny Clips can be driven by AI agents and scripts on macOS and Windows. Every interface goes through one command layer inside the running app:

```
agent ── tinyclips CLI ───────────┐
agent ── tinyclips mcp (stdio) ───┼── local socket / named pipe ──> app automation host ──> policy ──> app services
skills/ (workflow guidance) ──────┘
```

- **Contract:** [`shared/automation/commands.json`](../shared/automation/commands.json) lists every command, its scope, and its parameters. Both apps, the CLI, and the MCP server read the same file.
- **CLI + MCP:** [`agent/`](../agent/README.md) — the `TinyClips` .NET 10 tool (`dnx TinyClips`, or `dotnet tool install -g TinyClips`); `tinyclips mcp` starts the MCP server.
- **Skills:** [`skills/`](../skills) teach agents common workflows. Skills are guidance only; the app enforces permissions.
- **Agent Plugin:** The [repository-root plugin](agent-plugin.md) packages the MCP server and Skills as a portable [Agent Plugin](https://agent-plugins.org/).

## Set up your agent

Use a Tiny Clips release that includes **Agent Control**, running on the same Mac or Windows PC as your agent. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for the commands below. Your agent needs either terminal access or support for a local stdio MCP server. Tiny Clips provides capture tools, not an AI model; bring your own agent.

1. Turn on Agent Control from the macOS menu bar or Windows tray, or open **Settings → Agent Control** and turn on **Allow agents to control Tiny Clips**. If it has never been enabled, turning it on from the menu or tray opens Settings to the setup instructions. Leave the app running.
2. **Recommended: install the Agent Plugin.** It bundles the MCP server and workflow Skills, so no separate MCP configuration or global CLI installation is needed. In VS Code, run **Chat: Install Plugin from Source** and enter:
   ```text
   https://github.com/jamesmontemagno/tiny-clips.git
   ```
   See [plugin installation](#install-the-agent-plugin) for Copilot CLI and other clients. If your agent does not support plugins, use the [manual MCP setup](#connect-an-mcp-capable-agent) instead.
3. Pair from the same OS account your agent uses:
   ```sh
   dnx TinyClips --yes -- pair --name "My agent"
   ```
   Tiny Clips shows the requested permissions as checkboxes. By default the CLI asks for `read`, `capture`, `record`, `edit`, and `export`. Uncheck anything you don't want to grant, or check more, then approve. You can change them later in Settings without pairing again.
4. Verify the connection and discover a capture target in Terminal or PowerShell:
   ```sh
   dnx TinyClips --yes -- status
   dnx TinyClips --yes -- capture.targets kind=windows
   ```
5. Start a fresh agent session and ask it to take and inspect its first screenshot using a returned window ID. The equivalent terminal command is:
   ```sh
   dnx TinyClips --yes -- capture.screenshot mode=window windowId=<returned-window-id>
   ```
   The result includes the saved path and image dimensions. Do not paste the angle-bracket placeholder literally. Saved screenshot settings determine PNG, JPEG, or WebP output.

For scripts and terminal use, optionally install the standalone CLI with `dotnet tool install -g TinyClips`, then use `tinyclips` in place of `dnx TinyClips --yes --`. Update an existing global tool with `dotnet tool update -g TinyClips`. CLI and MCP use the same saved pairing token. Keep it private; never include it in prompts, screenshots, issues, or source control.

### Connect an MCP-capable agent

**Manual alternative for agents without plugin support.** Skip this if you installed the plugin, which already configures the MCP server. Install the .NET 10 SDK and pair as above, then add a **local stdio** server to your agent's MCP settings:

```json
{
  "mcpServers": {
    "tinyclips": {
      "command": "dotnet",
      "args": ["dnx", "TinyClips", "--verbosity", "quiet", "--yes", "--", "mcp"]
    }
  }
}
```

This is a generic configuration example, not a universal configuration file: the settings location and surrounding JSON vary by agent. If you installed the global tool, use `"command": "tinyclips"` and `"args": ["mcp"]` instead. Restart or reload the agent's MCP connection after adding it.

Use `dotnet dnx` rather than the Windows `dnx.cmd` wrapper, and keep `--verbosity quiet` so initial NuGet restore messages do not corrupt MCP's JSON-RPC stdout stream.

The MCP process must run as the same OS account and environment used for pairing. A remote/container agent cannot automatically reach the app on your desktop. `tinyclips mcp` speaks JSON-RPC over stdin/stdout; let the MCP client launch it rather than leaving it running in a terminal.

The server uses the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) for protocol handling. Tiny Clips still enforces pairing, scopes, and sensitive-action approvals in the app; using the SDK does not grant additional access.

### Install the Agent Plugin

For **VS Code**, run **Chat: Install Plugin from Source** and enter:

```text
https://github.com/jamesmontemagno/tiny-clips.git
```

Confirm the plugin is enabled in **Chat: Open Customizations → Plugins**, then start a fresh session. VS Code downloads the plugin; you do not need to clone the repository yourself or register a marketplace. The manifest, MCP configuration, and Skills live at the repository root. See the [VS Code installation steps](agent-plugin.md#vs-code).

For **GitHub Copilot CLI**, install directly from this repository:

```sh
copilot plugin install jamesmontemagno/tiny-clips
```

Update or uninstall CLI installs with `copilot plugin update tinyclips` or `copilot plugin uninstall tinyclips`. VS Code also discovers CLI-installed plugins. Other Agent Plugins-compatible clients may have their own install flow.

The plugin adds the `tinyclips` MCP server (launched with `dotnet dnx`) and all Skills below, so you can skip the manual MCP and Skills setup. You still need the .NET 10 SDK, Agent Control turned on, and a one-time pairing (`tinyclips pair` if installed, or `dnx TinyClips --yes -- pair`) before the tools work. See the [plugin installation guide](agent-plugin.md) for its prerequisites and install details.

### Add workflow Skills

The [`skills/`](../skills) folder contains `tinyclips-setup`, `tinyclips-capture`, `tinyclips-record-demo`, `tinyclips-library`, and `tinyclips-studio`. Copy the relevant Skill folders (including their `SKILL.md`) into the directory supported by your agent, such as a repository's `.github/skills/` or `~/.claude/skills/` for Claude Code. Follow your client's Skill discovery rules. Skills supply workflow guidance; they do not install the CLI, configure MCP, or approve access.

## Prompts to try

Give these to an agent with Tiny Clips CLI or MCP access. Tell it which app or project you mean and review the result before sharing it.

**Capture and annotate**
> Use Tiny Clips to discover the Copilot window, capture it, inspect the image, draw an arrow toward Create PR, and add an eyes emoji. Save a new annotated copy without changing the original, then show me the result.

**Read text from a region**
> Use Tiny Clips to discover my displays, capture the relevant screen to locate the error message, and run OCR on that region. Return the text without changing my clipboard. Use display-local physical-pixel coordinates, not guessed desktop coordinates.

**Record a demo**
> Use Tiny Clips to record a 10-second demo of my browser window with my saved audio and camera settings. Wait for any configured countdown, stop and save it, then show me the result. Do not discard, delete, or upload anything.

**Capture a long page**
> Use Tiny Clips to start a scrolling screenshot of my browser window. Use your browser or desktop tools to scroll the page, check the captured-frame count, then stop and save the stitched image. Inspect the result for missing or duplicated content.

**Polish a Studio project**
> Inspect my latest Tiny Clips Studio project, suggest zooms from click events, preserve manual zooms, adjust the canvas padding, and export a new MP4 into my configured video folder. Use the current revision for edits; re-read and reconcile if the project changes. Do not overwrite any file.

**Find recent clips**
> List my ten most recent Tiny Clips screenshots and reveal the one I choose. Do not delete or upload anything.

An agent needs its own browser/desktop tools to click, type, or scroll in other apps; Tiny Clips does not provide those actions. Studio must be enabled for Studio commands. On macOS, Agent Control does not require a supporter tip. OS permissions still apply, and sensitive commands still ask for approval.

## Troubleshooting setup

| Problem | Recovery |
|---|---|
| `dotnet`, `dnx`, or `tinyclips` is not found | Install the .NET 10 SDK/tool, reopen the terminal or agent so it picks up PATH, and confirm the global .NET tools directory is on PATH. |
| Cannot connect | Start Tiny Clips and enable Agent Control. Check the listening status and endpoint in Settings. Local access does not bridge to a remote machine or container. |
| `unauthorized` or revoked access | Run `tinyclips pair` again as the same OS account/environment used by the agent and approve it in Tiny Clips. |
| `missing_scope` | Grant the scope to that agent in **Settings → Agent Control → Paired Agents → Permissions** (it applies immediately), or pair again requesting it with `--scopes`. |
| `permission_required` | Grant the relevant OS screen/audio/camera permission. On macOS, follow System Settings → Privacy & Security and restart Tiny Clips if required. |
| `not_supported` | Check your app's version and feature settings; enable Studio for Studio commands. |
| MCP connects but calls fail | Verify the server runs locally under the same account, can find the CLI/SDK, and shares the pairing configuration. Restart or reload the MCP connection. |
| Studio edit returns `conflict` | Read the project again and reconcile changes with its latest `modifiedAt` and `revision`; do not blindly resend the old patch. |

Change what an individual agent may do with **Permissions**, or revoke it, in **Settings → Agent Control → Paired Agents**. Turn Agent Control off to stop all connections. **Activity → Open Audit Log** shows local request history.

## Security model

1. **Off by default.** Turn on **Settings → Agent control → Allow agents to control Tiny Clips**. The local listener exists only while enabled; when off, clients cannot connect.
2. **Pairing.** A client calls `session.pair` with a name and the scopes it wants. The app shows an approval prompt listing every scope, with the requested ones pre-checked; you choose the scopes actually granted (at least one). If you approve, the client receives a token once (`session.pairStatus`), and `scopes` reports what you granted, which can differ from the request. The app stores only a SHA-256 hash of the token. Change a client's scopes or revoke it any time in Settings; scope changes keep the token and take effect on the next request, and a sensitive action approved after its scope was removed still fails with `missing_scope`.
3. **Scopes.** `read`, `capture`, `record`, `edit`, `export`, `settings` run unattended once granted. Capture targets, regions, OCR, annotations, video/GIF recording, and scrolling captures use structured commands; they do not depend on a human-operated picker or simulated mouse input.
4. **Sensitive scopes.** `delete`, `upload`, and `discard` (discarding or restarting a recording, discarding unsaved work) always show an in-app approval prompt, even when granted. No answer within 120 seconds returns `approval_denied`. A pending sensitive-action approval is dismissed when its client disconnects.
5. **No bypasses.** OS permissions (Screen Recording, Microphone, Camera) and platform-specific feature availability still apply. Commands return `permission_required` when an OS permission is missing.
6. **Visibility.** The menu bar / tray menu shows when agent control is on, and **Turn off agent control** stops it immediately. Every command is written to an audit log (`automation-audit.jsonl` next to the app's settings).

## Transport

| Platform | Endpoint |
|---|---|
| macOS (direct) | Unix socket `~/Library/Application Support/TinyClips/automation.sock` |
| macOS (App Store) | Unix socket `~/Library/Containers/com.refractored.tinyclips/Data/tinyclips-automation.sock` |
| Windows | Named pipe `\\.\pipe\TinyClips.Automation.<UserName>` (current-user ACL) |

`TINYCLIPS_AUTOMATION_ENDPOINT` selects a single endpoint for the CLI. Without an override, the CLI tries the standard endpoints before sending a request. `TINYCLIPS_AGENT_CONFIG` overrides the pairing configuration file; relative paths are resolved from the CLI's working directory.

The CLI and MCP server wait up to 150 seconds for ordinary requests and two hours for `studio.export` or `editor.trim`. Set `TINYCLIPS_REQUEST_TIMEOUT_SECONDS` in the CLI or MCP server's environment to override the deadline, or set it to `0` for no deadline. Explicit caller cancellation still applies.

Framing is one UTF-8 JSON object per line (`\n`). A connection may send several requests; responses come back in order. On Windows, at most 16 requests may be queued per connection; exceeding that limit closes the connection.

```json
{"id":"1","token":"<token>","command":"capture.screenshot","params":{"mode":"screen"}}
{"id":"1","ok":true,"result":{"path":"/Users/me/Desktop/TinyClips 2026-11-01.png"}}
{"id":"2","ok":false,"error":{"code":"missing_scope","message":"This client was not granted the record scope."}}
```

### Session commands

| Command | Params | Result |
|---|---|---|
| `session.hello` | – | `app`, `version`, `platform` (`macos`/`windows`), `build` (`direct`/`store`), `protocolVersion`, `automationEnabled`, `commands` (supported names) |
| `session.pair` | `clientName`, `scopes[]` | `pairingId` |
| `session.pairStatus` | `pairingId` | `status` (`pending`/`approved`/`denied`), and when approved `token`, `scopes` |

### macOS notification permissions

`notifications.status` (`read` scope) returns `status` (`notDetermined`, `denied`, `authorized`, `provisional`, `ephemeral`, or `unknown`), `alertsEnabled` (whether macOS currently allows alert presentation), and `canRequest`. `alertsEnabled` does not guarantee delivery; macOS may still adjust delivery based on alert style, Focus, or other system choices. This is system permission, separate from the `showSaveNotifications` preference available through `settings.get` / `settings.set`.

With the `settings` scope, `notifications.requestPermission` requests permission only if macOS has not yet asked. The user must answer the macOS prompt; agents cannot grant permission. It returns the same status fields on success or `permission_required` otherwise. `notifications.openSettings` opens macOS notification settings so the user can restore denied permission or adjust alerts. These commands do not change the after-save preference or override Focus, quiet delivery, or other system choices.

```sh
tinyclips notifications.status
tinyclips notifications.requestPermission
tinyclips notifications.openSettings
```

The corresponding MCP tools are `tinyclips_notifications_status`, `tinyclips_notifications_requestPermission`, and `tinyclips_notifications_openSettings`. Windows does not advertise these macOS-only commands and returns `not_supported`.

### Autonomous capture and annotation

`capture.targets` lists display and visible-window targets. IDs are opaque and valid only for the current app session; refresh the list before using an ID after a restart. Display-relative regions use physical pixels with `(0,0)` at that display's top-left. `capture.screenshot` and `capture.ocr` accept a `windowId`, or `displayId` plus `region: {x,y,width,height}`; no picker is shown, and OCR returns recognized text without changing the clipboard.

`editor.annotate` accepts a screenshot path and typed annotation objects for rectangles, ellipses, arrows, lines, pen strokes, text, numbered callouts, emoji, and redactions. Bounds and points use source-image pixels from the top-left. It renders to a new library capture and never overwrites the source. It does not open the interactive editor. Redactions accept `redactionLevel` (`light`, `medium`, or `heavy`) and `redactionStyle` (`blur`, `pixelate`, or `solid`). Blur and pixelate are built from the image, so use `solid` for passwords, keys, and other secrets; `light` can stay readable.

### Trimming and frames

`editor.trim` takes a video or GIF `path` plus `start` and `end` in seconds and saves a trimmed copy; an `end` past the media length is clamped. Pass `removeAudio: true` to drop a video's audio. GIFs keep each frame overlapping the range, and the result reports the actual `start`, `end`, and `frameCount`. `editor.saveFrame` saves a single still image from a video at `time` seconds, or from a GIF at `time` or a zero-based `frame` index. Windows saves frames as PNG; macOS uses the configured screenshot format. Both commands need the `edit` scope, add a new library item, never overwrite or delete the source, and do not open a trimmer window.

### Targeted recording and scrolling capture

`recording.startVideo` and `recording.startGif` accept the same target parameters as `capture.screenshot`. They start against the chosen screen, display region, or window without opening a picker or recording setup window, and use saved recording settings, including any configured countdown. `recording.status` reports progress; `recording.stop` saves the result without opening a trimmer. `studio: true` on `recording.startVideo` creates a Studio recording.

For long pages, call `capture.scrolling.start` with a target, scroll the page with the agent's own browser or UI tools, and call `capture.scrolling.stop` to save the stitched screenshot directly. Tiny Clips only captures and stitches frames; it does not send scroll input. `capture.scrolling.status` reports the active state and frame count. `capture.scrolling.cancel` discards the in-progress image and requires the sensitive `discard` approval.

### Studio projects

Studio commands work on both platforms when Studio is enabled. `studio.projects.list` discovers stored projects; `studio.project.get` returns the project JSON, including `modifiedAt`, its timeline, look, and audio settings. Project IDs come from that list and are not filesystem paths.

`studio.project.edit` accepts a partial `patch` with any of `canvas`, `screen`, `camera`, `scenes`, `zooms`, `edits`, `audio`, `overlays`, and `captions`. Object values merge recursively; arrays replace their whole field. Send both the current project's `modifiedAt` as `expectedModifiedAt` and the opaque `revision` returned by `studio.project.get` as `expectedRevision`. Identity, name, source recordings, cleanup pinning, and export bookkeeping are protected. Edits to an open Studio window use its undo history; edits to a closed project are saved directly. A stale revision is rejected, so fetch the project again before retrying.

Arrows, callouts, text boxes, emoji, rectangles, ellipses, and redactions are in `overlays.annotations`, bottom first, on both platforms. They are drawn in the preview and the export, over the camera and under the badge. The array replaces the whole list, so read the current one and send it back with your change:

```json
{
  "overlays": {
    "annotations": [
      { "id": "upgrade-arrow", "kind": "arrow", "attach": "screen", "start": 55.2, "end": 58.2, "x": 0.71, "y": 0.22, "offsetX": -0.25, "offsetY": 0.2, "style": "red", "size": "medium" },
      { "id": "billing-note", "kind": "callout", "attach": "screen", "start": 55.2, "end": 58.2, "x": 0.1, "y": 0.3, "offsetX": 0.2, "offsetY": 0.15, "text": "Billing lives here now", "style": "dark" },
      { "id": "title", "kind": "text", "attach": "frame", "start": 50, "end": 60, "x": 0.5, "y": 0.9, "text": "Upgrading your plan", "style": "white", "size": "large" },
      { "id": "plan-box", "kind": "rectangle", "attach": "screen", "start": 55.2, "end": 58.2, "x": 0.71, "y": 0.22, "width": 0.18, "height": 0.08, "cornerRadius": 0.01, "shapeFill": "#FFD60A", "shapeFillOpacity": 0.3 },
      { "id": "hide-email", "kind": "redact", "attach": "screen", "start": 40, "end": 60, "x": 0.85, "y": 0.06, "width": 0.2, "height": 0.04, "redactStyle": "solid", "style": "dark" },
      { "id": "done", "kind": "emoji", "attach": "screen", "start": 58.2, "end": 60, "x": 0.8, "y": 0.3, "text": "🎉", "size": "large", "shadow": true }
    ]
  }
}
```

- `start` and `end` are seconds on the source recording. Without an `end` an annotation is never shown.
- With `attach` `screen`, `x` and `y` are normalized in the screen recording, like a zoom's focus: the annotation follows zooms and crops, keeps its size, and is hidden while that point is out of view. With `frame` they are normalized in the canvas, and it stays where it is.
- `x`, `y` are what an arrow or a callout points at, or the center of a text box, an emoji, a rectangle, an ellipse, or a redaction. `offsetX`, `offsetY` are in canvas short sides and place an arrow's tail or a callout's box. Text, emoji, and the area kinds ignore them.
- An `emoji` draws its `text` in color, with no box, at `size` and `scale`; `shadow` `true` adds a soft drop shadow. `style` and the colors do not change it, and the emoji art is each platform's own.
- Every annotation accepts `rotation`, clockwise degrees about its resolved `x`, `y` anchor; omitted rotation is zero. For example, `"rotation":45` turns an emoji, shape, or redaction diagonally. Resize areas with `width`/`height`; resize arrows, callouts, text, or emoji with `scale` (multiply arrow/callout offsets too to reproduce the uniform preview resize). The same properties are available through the shared CLI and MCP `studio.project.edit` tool.
- `rectangle`, `ellipse`, and `redact` cover an area `width` by `height`, normalized like `x` and `y`. With `attach` `screen` the area grows with a zoom, so it keeps covering the same part of the recording, and it shows while any of it is in view. A rectangle or an ellipse is an outline in the style's color; `cornerRadius` (canvas short sides, 0 to 0.5) rounds a rectangle, and `shapeFill` (`#RRGGBB`) with `shapeFillOpacity` (0 to 1, default 0.35) fills the inside.
- A `redact` hides what is under it: `redactStyle` is `blur`, `pixelate`, or `solid` (filled with the style's color), and `redactLevel` is `light`, `medium`, or `heavy`. Use `solid` for passwords, keys, and other secrets: blur and pixelate can leave large text readable. Put a redaction on `screen` so it stays on what it hides through zooms, and give it a margin around the secret.
- `style` is `red`, `blue`, `yellow`, `white`, or `dark`. `size` is `small`, `medium`, or `large`.
- Keep any entry you do not understand as it is: a `kind` this version does not draw is stored and left out of the picture.
- `fill` and `textColor` are `#RRGGBB` and stand in for the colors of `style`, and `scale`, 0.5 to 3, multiplies `size`. Leave them out for the style's look.
- The Annotate panel and the preview edit this same list, so anything a person can do there an agent can do through the patch: text, emoji, shadow, style, colors, size, area, corners, fill, redaction style and strength, what it is attached to, position, timing, drawing order (the order of the list), and deleting (leaving it out). Give an annotation a stable `id`: an open editor keeps its selection on an annotation by its `id` through an edit.

The full rules are in section 6.10 of [`studio-project-format.md`](studio-project-format.md).

`studio.zoom.suggest` returns proposed automatic zooms without changing the project. To apply them, combine the suggestions with the project's manual zooms and send the resulting `zooms` array through `studio.project.edit`; suggested automatic zooms are replaced, not accumulated.

`studio.captions.check` reports readability and timing problems without changing anything, naming each cue by its place in time order. `studio.captions.fix` makes the safe fixes as one edit and reports what is left. To give a caption that is too short or too fast more time, it uses free time after or before it, borrows spare time from its neighbors, or joins it to a neighbor, as section 15.6 of the [project format](studio-project-format.md) describes; joining removes a cue, so get the project again before naming a cue by its place. `studio.captions.import` replaces the cues with those of SRT or WebVTT `text`, which you pass in rather than a path; set `timing` to `editedVideo` when its times count from the edited video, such as a file made from an exported video. Both edits take `expectedModifiedAt` and `expectedRevision`, like `studio.project.edit`, and return the new `revision`. To correct a caption, use `studio.project.edit` to replace `captions.cues`, emptying the `words` of what you change. Splitting, merging, and retiming cues, which the Captions panel offers on both platforms, have no commands of their own: replace `captions.cues` the same way, following section 15.5 of the [project format](studio-project-format.md). `studio.captions.export` writes the captions the video has, timed to the edited video, to a new `.srt` or `.vtt` directly inside the configured video-save folder, and the extension chooses the format. It never overwrites a file. Captions in cut or sped-up parts of the video are left out, because those parts have no sound.

To draw captions into the picture, set `overlays.captions` through `studio.project.edit`: `enabled`, `size` (`small`, `medium`, `large`), `position` (`bottom`, `top`), and `appearance` (`classicOutline`, `contrastPanel`, `boldImpact`, `cleanShadow`). The next `studio.export` then has them in its picture, on either platform. Viewers cannot turn them off, so export a file with `studio.captions.export` as well when that matters.

`studio.captions.generate` makes a project's captions from its recording's sound, on this computer, replacing the ones it has as one edit, and returns how many it made and the language it heard. It takes `expectedModifiedAt` and `expectedRevision`, and it can take a long time. `studio.captions.models` lists the speech models with their sizes, which are installed, and which is selected. `model` chooses one; without it, the selected model is used. On a Mac the only one is `AppleSpeech`, the speech recognition built into macOS, listed with its `languages`. It does not detect the language, so pass `language` as a BCP 47 tag, or it listens for the system's. Macs need macOS 26 or later and hardware that supports it. Any other Mac does not advertise either command and answers `not_supported`, though it can still import captions, and Windows answers `invalid_params` for `AppleSpeech`. When it hears no speech, it answers `internal` and leaves the captions as they were: try another `language` on a Mac, or another `model` on Windows. A model that is not on the computer is an `invalid_params` error unless `allowModelDownload` is true, because the download can be gigabytes, so ask before setting it.

`studio.export` requires an explicit MP4 path directly inside the configured video-save folder. It never overwrites an existing file or writes inside the project/source folders. A successful export is added to the library and linked back to its Studio project.

`studio.export` takes an optional `resolution` (`source`, `2160p`, `1440p`, `1080p`, `720p`, or `480p`) and `quality` (`best`, `high`, `standard`, or `small`). A video keeps the shape of its canvas, fits inside the named frame turned to match the canvas, and is never scaled up, so check the `width` and `height` it reports. It also reports the video `codec` it wrote (`h264` or `hevc`): the `videoCodec` setting, except that a Mac without a usable HEVC encoder for that size writes H.264 instead. `source` is the canvas at its own size, which can be larger than 4K; past 4K, the largest size the PC or Mac can encode varies, and a size it cannot encode fails with an error suggesting 4K or smaller. Omitted or null values use the choices last made in Studio's Export popup, which start at `1080p` and `high`; agents can read and set them as the `studioExportResolution` and `studioExportQuality` settings. `studio.export.options` is the read-only view of the same popup: the resolutions offered for a project with their sizes, and each quality's video bitrate and estimated file size at the chosen resolution.

### Policy order

1. `session.hello` is allowed without a token while the listener is available.
2. Agent control off → no local listener; clients cannot connect.
3. `session.pair` / `session.pairStatus` → allowed without a token.
4. Missing or unknown token → `unauthorized`.
5. Command not in the contract → `unknown_command`.
6. Command scope not granted → `missing_scope`.
7. Scope is sensitive → ask the user in the app (`approval_denied` if declined); otherwise run.

[`shared/automation/fixtures/policy.json`](../shared/automation/fixtures/policy.json) holds the expected decisions, and both platforms test against it.
