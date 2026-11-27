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
            Assert.All(command.AdditionalScopes, scope => Assert.True(contract.Scopes.ContainsKey(scope)));
            Assert.Matches("^[a-z]+(?:\\.[a-zA-Z]+)+$", command.Name);
            Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", Contract.ToolName(command.Name));
            Assert.Equal(command, contract.FromToolName(Contract.ToolName(command.Name)));
        }
    }

    [Fact]
    public void SensitiveScopesAreDeleteUploadAndDiscard() =>
        Assert.Equal(["delete", "discard", "upload"], Contract.Current.SensitiveScopes.Order());

    [Fact]
    public void PlatformSpecificCommandsAreMacOSOnly()
    {
        var contract = Contract.Current;
        var notificationCommands = new[]
        {
            "notifications.status",
            "notifications.requestPermission",
            "notifications.openSettings",
            "editor.list",
            "editor.closeAll",
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
    [InlineData("editor.list", "read")]
    [InlineData("editor.closeAll", "edit")]
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
        var required = Assert.Single(schema["required"]!.AsArray());
        Assert.Equal("path", required!.GetValue<string>());
        Assert.Equal("boolean", schema["properties"]!["studio"]!["type"]!.GetValue<string>());
        Assert.Contains("standard", schema["properties"]!["resultFormat"]!["enum"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void WindowAreaIsOptInOnlyForScreenshotAndRecordingCommands()
    {
        var contract = Contract.Current;
        foreach (var name in new[] { "capture.screenshot", "recording.startVideo", "recording.startGif" })
        {
            Assert.Contains("windowArea", contract.Find(name)!.Params["mode"].Enum!);
        }

        Assert.DoesNotContain("windowArea", contract.Find("capture.ocr")!.Params["mode"].Enum!);
        Assert.DoesNotContain("windowArea", contract.Find("capture.scrolling.start")!.Params["mode"].Enum!);
    }

    [Fact]
    public void GuideNavigationSchemaIsOptionalAndSharedAcrossPlatforms()
    {
        var command = Contract.Current.Find("app.open")!;
        var schema = Contract.InputSchema(command);
        Assert.Equal("read", command.Scope);
        Assert.Equal(["window"], schema["required"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(["capture", "editor", "recording", "studio", "library", "agents", "help"],
            schema["properties"]!["guideTopic"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Null(command.Params["guideTopic"].Default);
        Assert.Contains(command, Contract.Current.CommandsForPlatform("macos"));
        Assert.Contains(command, Contract.Current.CommandsForPlatform("windows"));
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
    public void RecordingDeviceChoicesAreOptionalWithoutDefaultsAndNeedOnlyRecordingScope()
    {
        var command = Contract.Current.Find("recording.startVideo")!;
        var schema = Contract.InputSchema(command);
        foreach (var device in new[] { "microphone", "camera" })
        {
            Assert.Equal("boolean", schema["properties"]![device]!["type"]!.GetValue<string>());
            Assert.False(command.Params[device].Required);
            Assert.Null(command.Params[device].Default);
        }
        Assert.Null(Contract.Validate(command, new JsonObject()));
        Assert.Equal("allow", FakeApp.Decide(Contract.Current, true, ["record"], command.Name));
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
        var open = contract.Find("studio.project.open")!;
        Assert.Equal("edit", open.Scope);
        Assert.Equal(["projectId"], Contract.InputSchema(open)["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()));
        Assert.Contains("Missing required", Contract.Validate(open, new JsonObject()));
        Assert.Null(Contract.Validate(open, new JsonObject { ["projectId"] = "project" }));
        Assert.Contains(open, contract.CommandsForPlatform("macos"));
        Assert.Contains(open, contract.CommandsForPlatform("windows"));
        foreach (var (name, scope) in new[] { ("studio.project.openFolder", "read"), ("studio.project.delete", "delete") })
        {
            var command = contract.Find(name)!;
            Assert.Equal(scope, command.Scope);
            Assert.Equal(["projectId"], Contract.InputSchema(command)["required"]!.AsArray()
                .Select(parameter => parameter!.GetValue<string>()));
        }
        Assert.Equal("read", contract.Find("studio.project.get")!.Scope);
        var copy = contract.Find("studio.project.saveCopy")!;
        Assert.Equal("export", copy.Scope);
        Assert.Contains(copy, contract.CommandsForPlatform("macos"));
        Assert.Contains(copy, contract.CommandsForPlatform("windows"));
        Assert.Equal(["folderName", "projectId"], Contract.InputSchema(copy)["required"]!.AsArray()
            .Select(parameter => parameter!.GetValue<string>()).Order());
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
    [InlineData("app.open", "{\"window\":\"guide\",\"guideTopic\":\"studio\"}", null)]
    [InlineData("app.open", "{\"window\":\"guide\"}", null)]
    [InlineData("app.open", "{\"window\":\"guide\",\"guideTopic\":\"unknown\"}", "one of")]
    [InlineData("app.open", "{\"window\":\"guide\",\"guideTopic\":1}", "string")]
    [InlineData("app.open", "{\"window\":\"guide\",\"guideTopic\":null}", "string")]
    [InlineData("app.open", "{\"window\":\"settings\",\"guideTopic\":\"studio\"}", "only when")]
    [InlineData("app.open", "{\"window\":\"library\",\"guideTopic\":\"studio\"}", "only when")]
    [InlineData("recording.startVideo", "{\"studio\":\"yes\"}", "true or false")]
    [InlineData("recording.startVideo", "{\"microphone\":false,\"camera\":false,\"studio\":true}", null)]
    [InlineData("recording.startVideo", "{\"microphone\":true,\"camera\":true}", null)]
    [InlineData("recording.startVideo", "{\"microphone\":\"false\"}", "true or false")]
    [InlineData("recording.startVideo", "{\"camera\":1}", "true or false")]
    [InlineData("studio.project.edit", "{\"projectId\":\"p\",\"expectedModifiedAt\":\"now\",\"expectedRevision\":\"rev\",\"patch\":{\"sources\":{}}}", "Unknown parameter")]
    public void ValidateChecksTypesEnumsAndUnknownKeys(string command, string json, string? expected)
    {
        var problem = Contract.Validate(Contract.Current.Find(command)!, JsonNode.Parse(json)!.AsObject());
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }
}
