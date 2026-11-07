using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyClips.Cli;

internal sealed class UsageException(string message) : Exception(message);

internal static class Cli
{
    public const int Success = 0;
    public const int UsageError = 1;
    public const int CommandError = 2;

    private static readonly string[] DefaultScopes = ["read", "capture", "record", "edit", "export"];
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Help => $"""
        tinyclips — control Tiny Clips from scripts and AI agents

        Usage:
          tinyclips status                       App, version, and whether agent control is on
          tinyclips pair [--name N] [--scopes a,b]
                                                 Ask the app for access (approve it in Tiny Clips)
          tinyclips unpair                       Forget this machine's saved token
          tinyclips commands                     List every command with its scope
          tinyclips <command> [key=value ...]    Run a command, e.g. tinyclips capture.screenshot mode=screen
          tinyclips call <command> [--params '{"{"}"k":"v"{"}"}'] [key=value ...]
          tinyclips mcp                          Run the MCP server on stdio

        Default pairing scopes: {string.Join(", ", DefaultScopes)}.
        Sensitive scopes (delete, upload, discard) always ask for approval in the app.
        Results print as JSON. Exit codes: 0 success, 1 usage error, 2 command error.

        Environment: TINYCLIPS_TOKEN, TINYCLIPS_AUTOMATION_ENDPOINT, TINYCLIPS_AGENT_CONFIG,
                     TINYCLIPS_REQUEST_TIMEOUT_SECONDS (0 disables the request deadline).
        """;

    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken cancellationToken = default)
    {
        var (flags, positional) = ParseArgs(args);
        try
        {
            if (positional.Count == 0 || positional[0] == "help" || flags.ContainsKey("help"))
            {
                await env.Out.WriteLineAsync(Help);
                return positional.Count == 0 && !flags.ContainsKey("help") ? UsageError : Success;
            }

            var verb = positional[0];
            var rest = positional.Skip(1).ToList();
            switch (verb)
            {
                case "status":
                    await PrintAsync(env, await AutomationClient.SendAsync(env, "session.hello", cancellationToken: cancellationToken));
                    return Success;
                case "pair":
                    return await PairAsync(flags, env, cancellationToken);
                case "unpair":
                {
                    var config = AgentConfig.Read(env);
                    var hadToken = config.Remove("token");
                    AgentConfig.Write(env, config);
                    await PrintAsync(env, new JsonObject
                    {
                        ["unpaired"] = hadToken,
                        ["note"] = "Revoke the client in Tiny Clips Settings → Agent control to invalidate the token.",
                    });
                    return Success;
                }
                case "commands":
                    await PrintAsync(env, new JsonArray(Contract.Current.CommandsForPlatform(Contract.CurrentPlatform).Select(c => (JsonNode)new JsonObject
                    {
                        ["name"] = c.Name,
                        ["scope"] = c.Scope,
                        ["description"] = c.Description,
                        ["inputSchema"] = Contract.InputSchema(c),
                    }).ToArray()));
                    return Success;
                case "mcp":
                    await McpServer.RunAsync(env, cancellationToken);
                    return Success;
                case "call":
                    if (rest.Count == 0) throw new UsageException("Usage: tinyclips call <command> [key=value ...]");
                    return await RunCommandAsync(rest[0], rest.Skip(1), flags, env, cancellationToken);
                default:
                    if (verb.Contains('.')) return await RunCommandAsync(verb, rest, flags, env, cancellationToken);
                    throw new UsageException($"Unknown command \"{verb}\". Run \"tinyclips help\".");
            }
        }
        catch (UsageException ex)
        {
            await PrintErrorAsync(env, "usage", ex.Message);
            return UsageError;
        }
        catch (JsonException ex)
        {
            await PrintErrorAsync(env, "usage", $"Invalid JSON: {ex.Message}");
            return UsageError;
        }
        catch (AutomationException ex)
        {
            await PrintErrorAsync(env, ex.Code, ex.Message);
            return CommandError;
        }
    }

    internal static (Dictionary<string, string?> Flags, List<string> Positional) ParseArgs(IReadOnlyList<string> args)
    {
        var flags = new Dictionary<string, string?>();
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            var equals = name.IndexOf('=');
            if (equals >= 0) flags[name[..equals]] = name[(equals + 1)..];
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) flags[name] = args[++i];
            else flags[name] = null;
        }
        return (flags, positional);
    }

    internal static JsonObject BuildParams(CommandSpec? command, IEnumerable<string> pairs, string? paramsJson)
    {
        var parameters = paramsJson is null
            ? new JsonObject()
            : JsonNode.Parse(paramsJson) as JsonObject ?? throw new UsageException("--params must be a JSON object.");

        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) throw new UsageException($"Expected key=value, got \"{pair}\".");
            var key = pair[..equals];
            var raw = pair[(equals + 1)..];
            if (command?.Params.TryGetValue(key, out var spec) == true && spec.Type == "string")
            {
                parameters[key] = raw;
                continue;
            }

            try
            {
                parameters[key] = JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                parameters[key] = raw;
            }
        }
        return parameters;
    }

    private static async Task<int> RunCommandAsync(string name, IEnumerable<string> pairs, Dictionary<string, string?> flags, CliEnvironment env, CancellationToken cancellationToken)
    {
        var command = Contract.Current.Find(name) ?? throw new UsageException($"Unknown command \"{name}\". Run \"tinyclips commands\".");
        var parameters = BuildParams(command, pairs, flags.GetValueOrDefault("params"));
        if (Contract.Validate(command, parameters) is { } problem) throw new UsageException(problem);
        await PrintAsync(env, await AutomationClient.SendAsync(env, name, parameters, cancellationToken: cancellationToken));
        return Success;
    }

    private static async Task<int> PairAsync(Dictionary<string, string?> flags, CliEnvironment env, CancellationToken cancellationToken)
    {
        var scopes = flags.GetValueOrDefault("scopes") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : DefaultScopes;
        var unknown = scopes.Where(s => !Contract.Current.Scopes.ContainsKey(s)).ToArray();
        if (unknown.Length > 0)
            throw new UsageException($"Unknown scope(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", Contract.Current.Scopes.Keys)}.");

        var clientName = flags.GetValueOrDefault("name") ?? $"tinyclips CLI on {Environment.MachineName}";
        var pairing = await AutomationClient.SendAsync(env, "session.pair", new JsonObject
        {
            ["clientName"] = clientName,
            ["scopes"] = new JsonArray(scopes.Select(s => (JsonNode)s).ToArray()),
        }, useToken: false, cancellationToken);
        var pairingId = pairing["pairingId"]?.GetValue<string>() ?? throw new AutomationException("protocol", "Tiny Clips did not return a pairing id.");

        await env.Error.WriteLineAsync("Approve the request in Tiny Clips…");
        var deadline = DateTime.UtcNow + env.PairTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = await AutomationClient.SendAsync(env, "session.pairStatus", new JsonObject { ["pairingId"] = pairingId }, useToken: false, cancellationToken);
            switch (status["status"]?.GetValue<string>())
            {
                case "approved":
                {
                    var granted = status["scopes"]?.DeepClone() ?? new JsonArray(scopes.Select(s => (JsonNode)s).ToArray());
                    var config = AgentConfig.Read(env);
                    config["token"] = status["token"]?.GetValue<string>() ?? throw new AutomationException("protocol", "Tiny Clips did not return a token.");
                    config["scopes"] = granted.DeepClone();
                    config["clientName"] = clientName;
                    AgentConfig.Write(env, config);
                    await PrintAsync(env, new JsonObject
                    {
                        ["paired"] = true,
                        ["clientName"] = clientName,
                        ["scopes"] = granted,
                        ["config"] = env.ConfigPath(),
                    });
                    return Success;
                }
                case "denied":
                    throw new AutomationException("approval_denied", "Pairing was declined in Tiny Clips.");
            }
            await Task.Delay(env.PollInterval, cancellationToken);
        }
        throw new AutomationException("approval_denied", "Pairing was not approved in time.");
    }

    private static Task PrintAsync(CliEnvironment env, JsonNode value) =>
        env.Out.WriteLineAsync(value.ToJsonString(Indented));

    private static Task PrintErrorAsync(CliEnvironment env, string code, string message) =>
        env.Error.WriteLineAsync(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(Indented));
}
