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
    public async Task GuideToolSelectsATopicWithReadScopeAndRejectsInvalidRequests()
    {
        await using var app = new FakeApp();
        app.Clients["t"] = ["read"];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var result = await client.CallToolAsync("tinyclips_app_open",
                new Dictionary<string, object?> { ["window"] = "guide", ["guideTopic"] = "studio" },
                cancellationToken: token);
            Assert.False(result.IsError);
            Assert.Equal("studio", app.Received[^1]["params"]!["guideTopic"]!.GetValue<string>());
            var count = app.Received.Count;
            foreach (var window in new[] { "settings", "library" })
            {
                var invalid = await client.CallToolAsync("tinyclips_app_open",
                    new Dictionary<string, object?> { ["window"] = window, ["guideTopic"] = "studio" },
                    cancellationToken: token);
                Assert.True(invalid.IsError);
                Assert.Equal("invalid_params", (string?)Body(invalid)["error"]!["code"]);
            }
            Assert.Equal(count, app.Received.Count);
        });
    }

    [Fact]
    public void OneToolPerCommandPlusStatus()
    {
        var tools = McpServer.ListTools();
        var commands = Contract.Current.CommandsForPlatform(Contract.CurrentPlatform).ToArray();
        Assert.Equal(commands.Length + 1, tools.Count);
        var delete = tools.Single(t => t.Name == "tinyclips_library_delete");
        Assert.True(delete.Annotations!.DestructiveHint);
        Assert.Contains("approve", delete.Description);
        Assert.True(tools.Single(t => t.Name == "tinyclips_studio_project_delete").Annotations!.DestructiveHint);
        foreach (var command in commands)
        {
            var tool = tools.Single(t => t.Name == Contract.ToolName(command.Name));
            Assert.True(JsonNode.DeepEquals(Contract.InputSchema(command), JsonNode.Parse(tool.InputSchema.GetRawText())));
        }
    }

    [Fact]
    public void WindowsToolListOmitsMacOnlyNotificationCommands()
    {
        var tools = McpServer.ListTools("windows");
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("tinyclips_notifications_", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, tool => tool.Name is "tinyclips_editor_list" or "tinyclips_editor_closeAll");
        Assert.Contains(tools, tool => tool.Name == "tinyclips_studio_project_saveCopy");
        Assert.Contains(tools, tool => tool.Name == "tinyclips_app_status");

        var macTools = McpServer.ListTools("macos");
        Assert.Contains(macTools, tool => tool.Name == "tinyclips_notifications_status");
        Assert.Contains(macTools, tool => tool.Name == "tinyclips_notifications_requestPermission");
        Assert.Contains(macTools, tool => tool.Name == "tinyclips_notifications_openSettings");
        Assert.Contains(macTools, tool => tool.Name == "tinyclips_editor_list");
        Assert.Contains(macTools, tool => tool.Name == "tinyclips_editor_closeAll");
        Assert.Contains(macTools, tool => tool.Name == "tinyclips_studio_project_saveCopy");
    }

    [Fact]
    public async Task TimelineRecordingRequiresRecordAndEditAndForwardsTarget()
    {
        var commands = new[] { "get", "import", "insert", "move", "record", "remove", "split", "transition", "trim" };
        foreach (var platform in new[] { "macos", "windows" })
        {
            const string prefix = "tinyclips_studio_timeline_";
            var tools = McpServer.ListTools(platform).Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            Assert.Equal(commands, tools.Select(t => t.Name[prefix.Length..]).Order(StringComparer.Ordinal));
            Assert.False(tools.Single(t => t.Name == prefix + "remove").Annotations!.DestructiveHint);
        }
        await using var app = new FakeApp();
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var args = new Dictionary<string, object?>
            {
                ["projectId"] = "p", ["expectedModifiedAt"] = "now", ["expectedRevision"] = "rev",
                ["projectTime"] = 2.5, ["microphone"] = false, ["camera"] = false, ["mode"] = "screen"
            };
            foreach (var scope in new[] { "edit", "record" })
            {
                app.Clients["t"] = [scope];
                var denied = await client.CallToolAsync("tinyclips_studio_timeline_record", args, cancellationToken: token);
                Assert.True(denied.IsError);
                Assert.Equal("missing_scope", (string?)Body(denied)["error"]!["code"]);
            }
            app.Clients["t"] = ["edit", "record"];
            var accepted = await client.CallToolAsync("tinyclips_studio_timeline_record", args, cancellationToken: token);
            Assert.False(accepted.IsError);
            Assert.Equal(2.5, (double?)Body(accepted)["params"]!["projectTime"]);
            Assert.False((bool?)Body(accepted)["params"]!["camera"]);
        });
    }

    [Fact]
    public async Task ProjectCopyToolForwardsArgumentsAndRequiresExportScope()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var app = new FakeApp();
        app.Clients["t"] = ["export"];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var args = new Dictionary<string, object?> { ["projectId"] = "project", ["folderName"] = "Copy" };
            var result = await client.CallToolAsync("tinyclips_studio_project_saveCopy", args, cancellationToken: token);
            Assert.False(result.IsError);
            Assert.Equal("studio.project.saveCopy", (string?)Body(result)["command"]);
            Assert.Equal("Copy", (string?)Body(result)["params"]!["folderName"]);
            app.Clients["t"] = ["edit"];
            var denied = await client.CallToolAsync("tinyclips_studio_project_saveCopy", args, cancellationToken: token);
            Assert.True(denied.IsError);
            Assert.Equal("missing_scope", (string?)Body(denied)["error"]!["code"]);
        });
    }

    [Fact]
    public async Task RecordingToolForwardsDeviceRestrictionsWithoutSettingsScope()
    {
        await using var app = new FakeApp();
        app.Clients["t"] = ["record"];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var result = await client.CallToolAsync("tinyclips_recording_startVideo",
                new Dictionary<string, object?> { ["microphone"] = false, ["camera"] = false, ["studio"] = true },
                cancellationToken: token);
            Assert.False(result.IsError);
            var parameters = app.Received[^1]["params"]!;
            Assert.False(parameters["microphone"]!.GetValue<bool>());
            Assert.False(parameters["camera"]!.GetValue<bool>());
            Assert.True(parameters["studio"]!.GetValue<bool>());
        });
    }

    [Fact]
    public async Task OpenStudioProjectToolForwardsIdAndRequiresEditScope()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var app = new FakeApp();
        app.Clients["t"] = ["edit"];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var args = new Dictionary<string, object?> { ["projectId"] = "project" };
            var result = await client.CallToolAsync("tinyclips_studio_project_open", args, cancellationToken: token);
            Assert.False(result.IsError);
            Assert.Equal("studio.project.open", (string?)Body(result)["command"]);
            Assert.Equal("project", (string?)Body(result)["params"]!["projectId"]);
            app.Clients["t"] = ["read"];
            var denied = await client.CallToolAsync("tinyclips_studio_project_open", args, cancellationToken: token);
            Assert.True(denied.IsError);
            Assert.Equal("missing_scope", (string?)Body(denied)["error"]!["code"]);
        });
    }

    [Fact]
    public async Task SafeEditorCleanupPreservesPartialResultsAndEnforcesScope()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var app = new FakeApp();
        app.Clients["t"] = ["edit"];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var result = await client.CallToolAsync("tinyclips_editor_closeAll", cancellationToken: token);
            Assert.False(result.IsError);
            Assert.Equal("clean", (string?)Body(result)["closed"]![0]);
            Assert.Equal("unsavedChanges", (string?)Body(result)["remaining"]![0]!["reason"]);
            app.Clients["t"] = ["read"];
            var denied = await client.CallToolAsync("tinyclips_editor_closeAll", cancellationToken: token);
            Assert.True(denied.IsError);
            Assert.Equal("missing_scope", (string?)Body(denied)["error"]!["code"]);
        });
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
            Assert.Equal(Contract.Current.CommandsForPlatform(Contract.CurrentPlatform).Count() + 1, listed.Tools.Count);
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
    [InlineData("notifications.status", "read")]
    [InlineData("notifications.requestPermission", "settings")]
    [InlineData("notifications.openSettings", "settings")]
    public async Task NotificationToolsForwardCommandsAndEnforceScopes(string command, string scope)
    {
        if (OperatingSystem.IsWindows()) return;

        await using var app = new FakeApp();
        app.Clients["t"] = [scope];
        await WithClientAsync(app.Environment(new StringWriter(), new StringWriter(), token: "t"), null, async (client, token) =>
        {
            var result = await client.CallToolAsync(Contract.ToolName(command), cancellationToken: token);
            Assert.False(result.IsError);
            Assert.Equal(command, (string?)Body(result)["command"]);

            app.Clients["t"] = ["capture"];
            var denied = await client.CallToolAsync(Contract.ToolName(command), cancellationToken: token);
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
            var project = await client.CallToolAsync("tinyclips_studio_project_delete",
                new Dictionary<string, object?> { ["projectId"] = "project" }, cancellationToken: token);
            Assert.True(project.IsError);
            Assert.Equal(expected, (string?)Body(project)["error"]!["code"]);
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
