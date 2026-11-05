---
name: tinyclips-setup
description: Connect an agent to the Tiny Clips screen capture app on macOS or Windows. Use when a tinyclips command or MCP tool returns not_running, automation_disabled, or unauthorized, or before first use.
---

# Connect to Tiny Clips

1. Check the app: `tinyclips status`.
   - `not_running`: ask the user to open Tiny Clips. Do not try to start it with elevated rights.
   - `automationEnabled: false` or `automation_disabled`: ask the user to turn on **Settings → Agent control → Allow agents to control Tiny Clips**. You cannot turn it on yourself.
2. Pair once: `tinyclips pair --name "<your agent name>" --scopes read,capture,record,edit,export`.
   The user approves the prompt in Tiny Clips and may grant different scopes than requested; use the `scopes` returned by pairing. Request only the scopes the task needs.
   Add `delete`, `upload`, or `discard` only when the task needs them; those commands still ask the user every time.
3. Confirm: `tinyclips app.status`.

`unauthorized` after pairing means the client was revoked; pair again. `missing_scope` means the user hasn't granted that scope: tell the user why it's needed and ask them to enable it under Settings → Agent Control → Paired Agents → Permissions (or re-pair with the extra scope).

Install the .NET 10 SDK for `dotnet tool install -g TinyClips`, or prefix commands with `dnx TinyClips --yes --`. Keep the app running; pair from the same OS account/environment as the agent. See `docs/agent-integration.md` for setup and troubleshooting.

MCP: run `tinyclips mcp` (or `dotnet dnx TinyClips --verbosity quiet --yes -- mcp`) as a stdio server. Quiet restore keeps NuGet messages off JSON-RPC stdout; `dotnet dnx` also avoids the Windows `.cmd` wrapper. The `tinyclips_*` tools match the CLI commands one to one.
