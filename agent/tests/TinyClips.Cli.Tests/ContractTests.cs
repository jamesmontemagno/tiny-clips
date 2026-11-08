using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
    public void NotificationCommandsAreMacOSOnly()
    {
        var contract = Contract.Current;
        var notificationCommands = new[]
        {
            "notifications.status",
            "notifications.requestPermission",
            "notifications.openSettings",
        };

        foreach (var name in notificationCommands)
        {
            Assert.Equal(["macos"], contract.Find(name)!.Platforms);
            Assert.DoesNotContain(
                name,
                contract.CommandsForPlatform("windows").Select(command => command.Name));
            Assert.Contains(
                name,
                contract.CommandsForPlatform("macos").Select(command => command.Name));
        }
    }

    [Theory]
    [InlineData("notifications.status", "read")]
    [InlineData("notifications.requestPermission", "settings")]
    [InlineData("notifications.openSettings", "settings")]
    public void NotificationCommandsUseLeastPrivilegeAndExposeParameterlessTools(string name, string scope)
    {
        var command = Contract.Current.Find(name)!;
        Assert.Equal(scope, command.Scope);
        Assert.Empty(command.Params);
        Assert.Null(Contract.Validate(command, new JsonObject()));
        Assert.Equal(command, Contract.Current.FromToolName(Contract.ToolName(name)));
        Assert.Equal("missing_scope", FakeApp.Decide(Contract.Current, true, ["capture"], name));
        Assert.Equal("allow", FakeApp.Decide(Contract.Current, true, [scope], name));
    }

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
        Assert.Equal("read", contract.Find("studio.export.options")!.Scope);
        Assert.Equal("export", contract.Find("studio.export")!.Scope);
        Assert.Equal("read", contract.Find("studio.captions.check")!.Scope);
        Assert.Equal("edit", contract.Find("studio.captions.import")!.Scope);
        Assert.Equal("edit", contract.Find("studio.captions.fix")!.Scope);
        Assert.Equal("export", contract.Find("studio.captions.export")!.Scope);
        Assert.Equal("read", contract.Find("studio.captions.models")!.Scope);
        Assert.Equal("edit", contract.Find("studio.captions.generate")!.Scope);
        var generate = Contract.InputSchema(contract.Find("studio.captions.generate")!);
        Assert.Equal("string", generate["properties"]!["language"]!["type"]!.GetValue<string>());
        Assert.Contains("AppleSpeech", generate["properties"]!["model"]!["enum"]!.AsArray().Select(static value => value!.GetValue<string>()));

        var edit = Contract.InputSchema(contract.Find("studio.project.edit")!);
        Assert.Contains("expectedModifiedAt", edit["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()));
        Assert.Contains("expectedRevision", edit["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()));
        Assert.Contains("patch", edit["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()));
        Assert.False(edit["properties"]!["patch"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("object", edit["properties"]!["patch"]!["properties"]!["captions"]!["type"]!.GetValue<string>());
        var annotations = edit["properties"]!["patch"]!["properties"]!["overlays"]!["properties"]!["annotations"]!;
        Assert.Equal("number", annotations["items"]!["properties"]!["rotation"]!["type"]!.GetValue<string>());
        Contract.Validate(contract.Find("studio.project.edit")!, JsonNode.Parse("""
            {"projectId":"p","expectedModifiedAt":"now","expectedRevision":"rev","patch":{"overlays":{"annotations":[{"kind":"emoji","text":"Hi","rotation":45,"scale":1.5}]}}}
            """)!.AsObject());
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
    [InlineData("studio.export", "{\"projectId\":\"p\",\"outputPath\":\"a.mp4\",\"resolution\":\"8k\"}", "one of")]
    [InlineData("studio.export", "{\"projectId\":\"p\",\"outputPath\":\"a.mp4\",\"quality\":\"nice\"}", "one of")]
    [InlineData("studio.export", "{\"projectId\":\"p\",\"outputPath\":\"a.mp4\",\"resolution\":\"1080p\",\"quality\":\"high\"}", null)]
    [InlineData("studio.export", "{\"projectId\":\"p\",\"outputPath\":\"a.mp4\",\"resolution\":\"source\"}", null)]
    [InlineData("studio.export", "{\"projectId\":\"p\",\"outputPath\":\"a.mp4\",\"resolution\":\"maximum\"}", "one of")]
    [InlineData("studio.export.options", "{\"projectId\":\"p\",\"resolution\":\"720p\"}", null)]
    [InlineData("editor.open", "{}", "Missing required")]
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
