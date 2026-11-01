# Tiny Clips Agent Plugin

A portable [Agent Plugin](https://agent-plugins.org/) (spec 1.0.0) that connects AI agents to the Tiny Clips app on macOS and Windows.

| Component | What it adds |
|---|---|
| `mcp.json` | The `tinyclips` stdio MCP server, launched with `dotnet dnx TinyClips --verbosity quiet --yes -- mcp` |
| `skills/` | `tinyclips-setup`, `tinyclips-capture`, `tinyclips-record-demo`, `tinyclips-library`, `tinyclips-studio` |

## Before you install

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) so `dnx` is available.
2. In Tiny Clips, open **Settings → Agent Control** and turn on **Allow agents to control Tiny Clips**.
3. Pair once with `dnx TinyClips --yes -- pair` and approve the request in the app.

## Install

Install this folder (`plugins/tinyclips` in `jamesmontemagno/tiny-clips`) through your agent's Agent Plugins install flow. Installation steps vary by client. If your agent doesn't support plugins yet, follow the manual MCP and Skills setup in [docs/agent-integration.md](../../docs/agent-integration.md).

The plugin uses the same permissions as the CLI. Tiny Clips enforces scopes and asks before deleting, uploading, or discarding anything.

The `dotnet dnx` form works without launching a Windows `.cmd` wrapper. Quiet restore is required to keep NuGet installation messages off MCP's JSON-RPC stdout stream.
