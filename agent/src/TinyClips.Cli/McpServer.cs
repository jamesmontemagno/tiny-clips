using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdkServer = ModelContextProtocol.Server.McpServer;

namespace TinyClips.Cli;

internal static class McpServer
{
    private const string Instructions =
        "Controls the Tiny Clips screen capture app. If a tool returns unauthorized, ask the user to run \"tinyclips pair\" " +
        "and approve it in Tiny Clips. Sensitive tools (delete, upload, discard) wait for the user to approve in the app.";

    public static async Task RunAsync(CliEnvironment env, CancellationToken cancellationToken)
    {
        using var logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        await using var transport = new StdioServerTransport("tinyclips", logging);
        await using var server = Create(transport, env, logging);
        await server.RunAsync(cancellationToken);
    }

    internal static SdkServer Create(ITransport transport, CliEnvironment env, ILoggerFactory? logging = null) =>
        SdkServer.Create(transport, new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "tinyclips",
                Version = typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                    .InformationalVersion.Split('+')[0],
            },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = ListTools(),
                    TimeToLive = TimeSpan.FromMinutes(5),
                    CacheScope = CacheScope.Public,
                }),
                CallToolHandler = (request, cancellationToken) => CallAsync(
                    request.Params ?? throw new McpProtocolException("Expected tool parameters.", McpErrorCode.InvalidParams),
                    env, cancellationToken),
            },
        }, logging);

    internal static IList<Tool> ListTools(string? platform = null)
    {
        var contract = Contract.Current;
        var tools = new List<Tool>();
        foreach (var command in contract.CommandsForPlatform(platform ?? Contract.CurrentPlatform))
        {
            var sensitive = contract.IsSensitive(command);
            tools.Add(new Tool
            {
                Name = Contract.ToolName(command.Name),
                Description = $"{command.Description} (scope: {command.Scope}{(sensitive ? "; asks the user to approve in Tiny Clips" : "")})",
                InputSchema = JsonSerializer.SerializeToElement(Contract.InputSchema(command)),
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = command.Scope == "read",
                    DestructiveHint = sensitive,
                },
            });
        }
        tools.Add(new Tool
        {
            Name = "tinyclips_status",
            Description = "Check whether Tiny Clips is running, its version and platform, and whether agent control is on.",
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false,
            }),
            Annotations = new ToolAnnotations { ReadOnlyHint = true },
        });
        return tools;
    }

    private static async ValueTask<CallToolResult> CallAsync(
        CallToolRequestParams request, CliEnvironment env, CancellationToken cancellationToken)
    {
        var arguments = request.Arguments is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(request.Arguments)!.AsObject();
        try
        {
            if (request.Name == "tinyclips_status")
            {
                if (arguments.Count != 0)
                    return Error("invalid_params", "Status does not accept arguments.");
                return Result(await AutomationClient.SendAsync(env, "session.hello", cancellationToken: cancellationToken));
            }

            var command = Contract.Current.FromToolName(request.Name)
                ?? throw new McpProtocolException($"Unknown tool: {request.Name}", McpErrorCode.InvalidParams);
            if (Contract.Validate(command, arguments) is { } problem)
                return Error("invalid_params", problem);
            return Result(await AutomationClient.SendAsync(env, command.Name, arguments, cancellationToken: cancellationToken));
        }
        catch (AutomationException ex)
        {
            return Error(ex.Code, ex.Message);
        }
    }

    private static CallToolResult Error(string code, string message) =>
        Result(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, isError: true);

    private static CallToolResult Result(JsonNode value, bool isError = false) => new()
    {
        Content = [new TextContentBlock { Text = value.ToJsonString(Cli.Indented) }],
        IsError = isError,
    };
}
