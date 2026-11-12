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

### VS Code

1. Run **Chat: Install Plugin from Source** from the Command Palette.
2. Enter the repository URL:
   ```text
   https://github.com/jamesmontemagno/tiny-clips.git
   ```
3. Confirm `tinyclips` is enabled in **Chat: Open Customizations → Plugins**, then start a fresh session and check its Skills and MCP tools.

VS Code downloads the plugin; you do not need to clone the repository yourself or register a marketplace. The portable manifest ([`plugin.json`](../plugin.json)), MCP configuration ([`mcp.json`](../mcp.json)), and [`skills/`](../skills) are at the repository root so direct URL installation can discover the complete plugin.

For local development, you can enter the absolute path to the repository root instead. See [VS Code's plugin installation guide](https://code.visualstudio.com/docs/agent-customization/agent-plugins).

### GitHub Copilot CLI

Register the Tiny Clips marketplace, then install the plugin:

```sh
copilot plugin marketplace add jamesmontemagno/tiny-clips
copilot plugin install tinyclips@tinyclips
```

Direct repository installs are deprecated and will stop being supported in a future Copilot CLI release. Use the marketplace commands above. To update or remove the marketplace install later:

```sh
copilot plugin update tinyclips
copilot plugin uninstall tinyclips
```

For another Agent Plugins-compatible client, use that client's plugin install flow. If your agent doesn't support plugins yet, follow the manual MCP and Skills setup in the [agent integration guide](agent-integration.md).

The plugin uses the same permissions as the CLI. Tiny Clips enforces scopes and asks before deleting, uploading, or discarding anything.

The `dotnet dnx` form works without launching a Windows `.cmd` wrapper. Quiet restore is required to keep NuGet installation messages off MCP's JSON-RPC stdout stream.
