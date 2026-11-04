# Tiny Clips CLI, MCP server, and Skills

Lets scripts and AI agents control Tiny Clips on macOS, Windows, and the [GNOME development preview](../linux/README.md). Check the running app's advertised commands; package-level tools are not a promise of platform support. Ships as the `TinyClips` .NET 10 tool on NuGet. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for the `dnx` and `dotnet tool` commands below, and keep Tiny Clips running with **Settings → Agent control** turned on. See the [setup guide, example prompts, and security model](../docs/agent-integration.md).

## Run

```bash
dnx TinyClips status                  # no install needed
dotnet tool install -g TinyClips      # or install the `tinyclips` command

tinyclips pair --name "My agent"      # approve the prompt in Tiny Clips
tinyclips capture.targets kind=all    # enumerate displays and visible windows
tinyclips capture.screenshot mode=screen
```

`tinyclips commands` lists each command, its scope, description, and JSON `inputSchema` (including nested region, annotation, and Studio edit-patch shapes). Results print as JSON; exit code 1 is a usage error, 2 means the app returned an error (`error.code` comes from the contract).

For unattended targeting, refresh `capture.targets` and pass a returned `windowId`, or pass a `displayId` with a region object:

```bash
tinyclips call capture.screenshot --params '{"mode":"region","displayId":"...","region":{"x":0,"y":0,"width":640,"height":480}}'
```

Region coordinates are display-local physical pixels from the top left. `capture.ocr` accepts the same target parameters and returns text without opening a picker or changing the clipboard. `editor.annotate` accepts typed annotations in source-image pixel coordinates and saves a new capture without opening the editor or overwriting its input. `editor.trim` (video or GIF, `start`/`end` seconds, optional `removeAudio`) and `editor.saveFrame` (video `time`, or GIF `time`/`frame`) also save new library items and leave the source untouched.

Studio workflows can list/read projects, atomically patch editable settings and timeline data with the observed `modifiedAt` and opaque revision token, request non-mutating zoom suggestions, and export a new MP4 into the configured video folder. See the [Studio skill](../plugins/tinyclips/skills/tinyclips-studio/SKILL.md) and [integration guide](../docs/agent-integration.md#studio-projects) for safe patch and export usage.

CLI and MCP requests normally time out after 150 seconds; `studio.export` and `editor.trim` default to two hours. Set `TINYCLIPS_REQUEST_TIMEOUT_SECONDS` for a different deadline, or `0` for no request deadline. Caller cancellation still stops the request.

## MCP

The package is also an MCP server (`tinyclips mcp`, stdio):

```json
{ "mcpServers": { "tinyclips": { "command": "dotnet", "args": ["dnx", "TinyClips", "--verbosity", "quiet", "--yes", "--", "mcp"] } } }
```

Each contract command becomes a `tinyclips_<domain>_<verb>` tool, plus `tinyclips_status`. Pair with the CLI first; the MCP server uses the same saved token.

`dotnet dnx` avoids the Windows `dnx.cmd` wrapper, and quiet restore keeps NuGet text out of MCP's stdout. MCP uses the official [`modelcontextprotocol/csharp-sdk`](https://github.com/modelcontextprotocol/csharp-sdk), through its `ModelContextProtocol.Core` package. The SDK owns stdio framing, lifecycle, version negotiation, request cancellation, and protocol errors. Tiny Clips supplies contract-driven tools and forwards calls to the app; SDK logs go to stderr, never protocol stdout.

## Skills

The [`plugins/tinyclips`](https://github.com/jamesmontemagno/tiny-clips/tree/main/plugins/tinyclips) [Agent Plugin](https://agent-plugins.org/) bundles this MCP server with portable SKILL.md workflows (setup, capture, Studio editing, record a demo, library). Install the plugin in a client that supports Agent Plugins, or copy folders from [`plugins/tinyclips/skills/`](https://github.com/jamesmontemagno/tiny-clips/tree/main/plugins/tinyclips/skills) into your agent's skills folder (for example `.github/skills/` or `~/.claude/skills/`).

## Develop

```bash
dotnet test agent/TinyClips.Agent.slnx
dotnet pack agent/src/TinyClips.Cli/TinyClips.Cli.csproj -c Release -o out
dnx TinyClips --yes --source out -- status
```

The command contract is embedded from `shared/automation/commands.json`. Tests run the CLI and MCP server against a fake app that follows the shared policy fixtures.

### Package smoke tests

After packing into a fresh `agent/out` directory, run these separate tests (they are intentionally not part of the ordinary unit-test solution):

```bash
# macOS / Linux
TINYCLIPS_PACKAGE_DIRECTORY="$PWD/agent/out" dotnet test agent/tests/TinyClips.Package.Tests/TinyClips.Package.Tests.csproj -c Release
```

```powershell
# Windows
$env:TINYCLIPS_PACKAGE_DIRECTORY = (Resolve-Path agent/out).Path
dotnet test agent/tests/TinyClips.Package.Tests/TinyClips.Package.Tests.csproj -c Release
```

They inspect the actual `.nupkg`, install it into an isolated tool directory, and launch both that tool and the plugin's `dotnet dnx` command. They verify the embedded command schemas, pairing, scoped commands, and real stdin/stdout sessions using the official SDK client, with both legacy initialization and the SDK's current protocol. A fake app uses Unix sockets or Windows named pipes. Restore uses only the local package feed and isolated caches, not an already-published package or your saved pairing. These are transport/package checks, not permission-dependent screen-capture or UI tests.

### CI and NuGet releases

[`agent-build.yml`](../.github/workflows/agent-build.yml) packs the candidate once on Linux, then builds, tests, and runs smoke tests against that same package on Linux, Windows, Apple silicon macOS, and Intel macOS. Relevant PRs and pushes to `main`/`develop` run these jobs; manual dispatch always runs them. The stable **Agent CLI result** check succeeds on an intentional skip but fails on failed detection or validation. Agent/plugin Markdown is included because it ships in the package or supplies workflow Skills.

[`agent-release.yml`](../.github/workflows/agent-release.yml) publishes only on **`agent-v<version>` tags**, independently of the macOS and Windows app tags. Before tagging, update `PackageVersion` in the CLI project, `version` in `plugins/tinyclips/plugin.json`, and both versions in `agent/src/TinyClips.Cli/.mcp/server.json` to the same value. The tag must match those committed versions. The release reuses the full CI matrix, publishes the tested Linux `.nupkg`, and attaches that package and a portable plugin ZIP to a separate GitHub release. Prerelease tags such as `agent-v0.2.0-preview.1` create prereleases. Merging a PR alone does not publish.

Configure [NuGet.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) with:

| Setting | Value |
|---|---|
| Repository owner | `jamesmontemagno` |
| Repository | `tiny-clips` |
| Workflow file | **`agent-release.yml`** (filename only) |
| Environment | Leave empty; the publishing job does not use one |
| NuGet user / policy owner | `jamesmontemagno` |
| Package pattern | `TinyClips` |
| Allowed publishing scopes | New packages and new versions (the first release creates the package) |

The workflow uses `NuGet/login@v1` to obtain a short-lived key immediately before pushing; no stored NuGet API key is needed. Protect agent release tags and require reviewed workflow changes. After the workflow is merged and the NuGet policy is configured, push a tag such as `agent-v0.1.0` on the reviewed commit to publish. This documentation does not itself create or push a release tag.
