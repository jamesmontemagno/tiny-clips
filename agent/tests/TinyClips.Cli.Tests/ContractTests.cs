using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace TinyClips.Cli.Tests;

public class ContractTests
{
    [Fact]
    public void EveryCommandUsesADeclaredScopeAndAUniqueName()
    {
        var contract = Contract.Current;
        Assert.NotEmpty(contract.Commands);
        Assert.Equal(contract.Commands.Count, contract.Commands.Select(c => c.Name).Distinct().Count());
        foreach (var command in contract.Commands)
        {
            Assert.True(contract.Scopes.ContainsKey(command.Scope), $"{command.Name} uses unknown scope {command.Scope}");
            Assert.Matches("^[a-z]+(?:\\.[a-zA-Z]+)+$", command.Name);
            Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", Contract.ToolName(command.Name));
            Assert.Equal(command, contract.FromToolName(Contract.ToolName(command.Name)));
        }
    }

    [Fact]
    public void SensitiveScopesAreDeleteUploadAndDiscard() =>
        Assert.Equal(["delete", "discard", "upload"], Contract.Current.SensitiveScopes.Order());

    [Fact]
    public void PolicyOrderInTheDocsMatchesTheSharedFixtures()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AutomationFixtures", "policy.json")))!;
        foreach (var testCase in fixture["cases"]!.AsArray())
        {
            var scopes = testCase!["scopes"]?.AsArray().Select(s => s!.GetValue<string>()).ToArray();
            var decision = FakeApp.Decide(Contract.Current, testCase["enabled"]!.GetValue<bool>(), scopes, testCase["command"]!.GetValue<string>());
            Assert.True(testCase["decision"]!.GetValue<string>() == decision, $"{testCase["name"]}: expected {testCase["decision"]}, got {decision}");
        }
    }

    [Fact]
    public void InputSchemasMarkRequiredParams()
    {
        var schema = Contract.InputSchema(Contract.Current.Find("editor.open")!);
        Assert.Equal("path", schema["required"]![0]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void InputSchemaIncludesNestedAnnotationShape()
    {
        var schema = Contract.InputSchema(Contract.Current.Find("editor.annotate")!);
        Assert.Equal("array", schema["properties"]!["annotations"]!["type"]!.GetValue<string>());
        Assert.Contains("type", schema["properties"]!["annotations"]!["items"]!["required"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task CropUsesTheSameSchemaAndArgumentsOverCliAndMcp()
    {
        var command = Contract.Current.Find("editor.crop")!;
        Assert.Equal("edit", command.Scope);
        var schema = Contract.InputSchema(command);
        Assert.Equal(["path", "region"], schema["required"]!.AsArray().Select(v => (string)v!).Order());
        var region = schema["properties"]!["region"]!;
        Assert.Equal(4, (int)region["minProperties"]!);
        Assert.False((bool)region["additionalProperties"]!);
        foreach (var name in new[] { "x", "y", "width", "height" })
        {
            Assert.Equal("integer", (string?)region["properties"]![name]!["type"]);
            Assert.Equal(name is "x" or "y" ? 0 : 1, (int)region["properties"]![name]!["minimum"]!);
        }
        var tool = Assert.Single(McpServer.ListTools(), t => t.Name == "tinyclips_editor_crop");
        Assert.True(JsonNode.DeepEquals(schema, JsonNode.Parse(tool.InputSchema.GetRawText())));

        const string args = """{"path":"shot.png","region":{"x":2,"y":3,"width":7,"height":8}}""";
        await using var app = new FakeApp();
        app.Clients["t"] = ["edit"];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var env = app.Environment(new StringWriter(), new StringWriter(), token: "t");
        Assert.Equal(0, await Cli.RunAsync(["editor.crop", "--params", args], env, deadline.Token));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "crop-test", Command = "dotnet", Arguments = [typeof(Contract).Assembly.Location, "mcp"],
            EnvironmentVariables = new Dictionary<string, string?> { ["TINYCLIPS_AUTOMATION_ENDPOINT"] = app.Endpoint, ["TINYCLIPS_TOKEN"] = "t" },
        }), cancellationToken: deadline.Token);
        var result = await client.CallToolAsync(tool.Name, new Dictionary<string, object?>
        {
            ["path"] = "shot.png", ["region"] = JsonNode.Parse(args)!["region"],
        }, cancellationToken: deadline.Token);
        Assert.False(result.IsError);
        Assert.Collection(app.Received, CheckCrop, CheckCrop);
        static void CheckCrop(JsonObject request)
        {
            Assert.Equal("editor.crop", (string?)request["command"]);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(args), request["params"]));
        }
    }

    [Fact]
    public void MediaEditCommandsRequirePathAndRange()
    {
        var contract = Contract.Current;
        Assert.Equal("edit", contract.Find("editor.trim")!.Scope);
        Assert.Equal("edit", contract.Find("editor.saveFrame")!.Scope);

        var trim = Contract.InputSchema(contract.Find("editor.trim")!);
        var required = trim["required"]!.AsArray().Select(parameter => parameter!.GetValue<string>()).ToArray();
        Assert.Equal(["end", "path", "start"], required.Order());
        Assert.Equal("boolean", trim["properties"]!["removeAudio"]!["type"]!.GetValue<string>());

        var frame = Contract.InputSchema(contract.Find("editor.saveFrame")!);
        Assert.Equal("integer", frame["properties"]!["frame"]!["type"]!.GetValue<string>());
        Assert.Equal("number", frame["properties"]!["time"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void StudioCommandsExposeReadEditSuggestAndExportOperations()
    {
        var contract = Contract.Current;
        Assert.Equal("read", contract.Find("studio.projects.list")!.Scope);
        Assert.Equal("read", contract.Find("studio.project.get")!.Scope);
        Assert.Equal("edit", contract.Find("studio.project.edit")!.Scope);
        Assert.Equal("read", contract.Find("studio.zoom.suggest")!.Scope);
        Assert.Equal("export", contract.Find("studio.export")!.Scope);

        var edit = Contract.InputSchema(contract.Find("studio.project.edit")!);
        Assert.Contains("expectedModifiedAt", edit["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()));
        Assert.Contains("expectedRevision", edit["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()));
        Assert.Contains("patch", edit["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()));
        Assert.False(edit["properties"]!["patch"]!["additionalProperties"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("""{"path":"shot.png","annotations":[]}""", "at least 1")]
    [InlineData("""{"path":"shot.png","annotations":[{"type":"unknown","bounds":{"x":0,"y":0,"width":5,"height":5}}]}""", "one of")]
    [InlineData("""{"path":"shot.png","annotations":[{"type":"rectangle","bounds":{"x":0,"y":0,"width":0,"height":5}}]}""", null)]
    [InlineData("""{"path":"shot.png","annotations":[{"type":"rectangle","bounds":{"x":0,"width":5,"height":5}}]}""", "Missing required")]
    public void ValidateChecksNestedSchemas(string json, string? expected)
    {
        var problem = Contract.Validate(
            Contract.Current.Find("editor.annotate")!,
            JsonNode.Parse(json)!.AsObject());
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("library.list", "{\"limit\":5,\"kind\":\"gif\"}", null)]
    [InlineData("library.list", "{\"limit\":1.5}", "integer")]
    [InlineData("library.list", "{\"kind\":\"pdf\"}", "one of")]
    [InlineData("editor.open", "{}", "Missing required")]
    [InlineData("editor.crop", """{"path":"shot.png"}""", "Missing required")]
    [InlineData("editor.crop", """{"path":"shot.png","region":{"x":0,"y":0,"width":0,"height":3}}""", "at least 1")]
    [InlineData("editor.crop", """{"path":"shot.png","region":{"x":0,"y":0.5,"width":1,"height":1}}""", "integer")]
    [InlineData("app.status", "{\"extra\":1}", "Unknown parameter")]
    [InlineData("recording.startVideo", "{\"studio\":\"yes\"}", "true or false")]
    [InlineData("studio.project.edit", "{\"projectId\":\"p\",\"expectedModifiedAt\":\"now\",\"expectedRevision\":\"rev\",\"patch\":{\"sources\":{}}}", "Unknown parameter")]
    public void ValidateChecksTypesEnumsAndUnknownKeys(string command, string json, string? expected)
    {
        var problem = Contract.Validate(Contract.Current.Find(command)!, JsonNode.Parse(json)!.AsObject());
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }
}
