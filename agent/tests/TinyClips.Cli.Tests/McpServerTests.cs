using System.IO.Pipelines;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TinyClips.Cli.Tests;

public class McpServerTests
{
    private static JsonNode Body(CallToolResult result) =>
        JsonNode.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text)!;

    [Fact]
    public void OneToolPerCommandPlusStatus()
    {
        var tools = McpServer.ListTools();
        Assert.Equal(Contract.Current.Commands.Count + 1, tools.Count);
        var delete = tools.Single(t => t.Name == "tinyclips_library_delete");
        Assert.True(delete.Annotations!.DestructiveHint);
        Assert.Contains("approve", delete.Description);
        foreach (var command in Contract.Current.Commands)
        {
            var tool = tools.Single(t => t.Name == Contract.ToolName(command.Name));
            Assert.True(JsonNode.DeepEquals(Contract.InputSchema(command), JsonNode.Parse(tool.InputSchema.GetRawText())));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public async Task OfficialClientConnectsAndCallsTools(string? protocolVersion)
    {
        await using var app = new FakeApp();
        app.Clients["t"] = ["capture"];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), protocolVersion, async (client, token) =>
        {
            if (protocolVersion is not null) Assert.Equal(protocolVersion, client.NegotiatedProtocolVersion);
            Assert.Equal("tinyclips", client.ServerInfo!.Name);
            Assert.Contains("approve", client.ServerInstructions);
            var listed = await client.ListToolsAsync(new ListToolsRequestParams(), token);
            Assert.Equal(Contract.Current.Commands.Count + 1, listed.Tools.Count);
            var screenshot = await client.CallToolAsync("tinyclips_capture_screenshot",
                new Dictionary<string, object?> { ["mode"] = "screen" }, cancellationToken: token);
            Assert.False(screenshot.IsError);
            Assert.Equal("/tmp/shot-screen.png", (string?)Body(screenshot)["path"]);
            var denied = await client.CallToolAsync("tinyclips_recording_stop", cancellationToken: token);
            Assert.True(denied.IsError);
            Assert.Equal("missing_scope", (string?)Body(denied)["error"]!["code"]);
        });
    }

    [Theory]
    [InlineData("tinyclips_editor_open")]
    [InlineData("tinyclips_status")]
    public async Task InvalidArgumentsDoNotReachTheApp(string tool)
    {
        await using var app = new FakeApp();
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter()), null, async (client, token) =>
        {
            var result = await client.CallToolAsync(tool,
                tool == "tinyclips_status" ? new Dictionary<string, object?> { ["unexpected"] = true } : null,
                cancellationToken: token);
            Assert.True(result.IsError);
            Assert.Equal("invalid_params", (string?)Body(result)["error"]!["code"]);
            Assert.Empty(app.Received);
            Assert.NotEmpty((await client.ListToolsAsync(new ListToolsRequestParams(), token)).Tools);
        });
    }

    [Fact]
    public async Task UnknownToolsReturnProtocolErrorsWithoutStoppingTheSession()
    {
        await using var app = new FakeApp();
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter()), null, async (client, token) =>
        {
            var error = await Assert.ThrowsAsync<McpProtocolException>(
                async () => await client.CallToolAsync("tinyclips_unknown", cancellationToken: token));
            Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
            Assert.Empty(app.Received);
            Assert.NotEmpty((await client.ListToolsAsync(new ListToolsRequestParams(), token)).Tools);
        });
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("automation_disabled")]
    [InlineData("approval_denied")]
    public async Task AppPolicyErrorsRemainToolErrors(string expected)
    {
        await using var app = new FakeApp { Enabled = expected != "automation_disabled", Approve = expected != "approval_denied" };
        if (expected != "unauthorized") app.Clients["t"] = ["delete"];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var result = await client.CallToolAsync("tinyclips_library_delete",
                new Dictionary<string, object?> { ["path"] = "/tmp/smoke.png" }, cancellationToken: token);
            Assert.True(result.IsError);
            Assert.Equal(expected, (string?)Body(result)["error"]!["code"]);
        });
    }

    [Fact]
    public async Task RequestDeadlineOverrideAppliesToMcpCalls()
    {
        await using var app = new FakeApp
        {
            BeforeReply = token => Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        var env = new CliEnvironment
        {
            GetVariable = name => name switch
            {
                "TINYCLIPS_AUTOMATION_ENDPOINT" => app.Endpoint,
                "TINYCLIPS_REQUEST_TIMEOUT_SECONDS" => "0.05",
                _ => null,
            },
        };
        await WithClientAsync(env, null, async (client, token) =>
        {
            var result = await client.CallToolAsync("tinyclips_status", cancellationToken: token);
            Assert.True(result.IsError);
            Assert.Equal("timeout", (string?)Body(result)["error"]!["code"]);
        });
    }

    private static async Task WithClientAsync(
        CliEnvironment environment, string? protocolVersion, Func<McpClient, CancellationToken, Task> action)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var input = new Pipe();
        var output = new Pipe();
        await using var transport = new StreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream(), "tinyclips-test");
        var server = McpServer.Create(transport, environment);
        var run = server.RunAsync(timeout.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()),
                new McpClientOptions
                {
                    ProtocolVersion = protocolVersion,
                    ClientInfo = new Implementation { Name = "tinyclips-test", Version = "1" },
                }, cancellationToken: timeout.Token);
            await action(client, timeout.Token);
        }
        finally
        {
            await server.DisposeAsync();
            await run;
        }
    }
}
