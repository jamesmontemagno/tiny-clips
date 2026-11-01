using System.Text.Json.Nodes;

namespace TinyClips.Cli.Tests;

public class CliTests
{
    private static JsonNode Json(StringWriter writer) => JsonNode.Parse(writer.ToString())!;

    [Fact]
    public void KeyValueParamsAreTypedByTheContract()
    {
        var list = Cli.BuildParams(Contract.Current.Find("library.list"), ["limit=5", "kind=gif"], null);
        Assert.Equal(5, list["limit"]!.GetValue<int>());
        Assert.Equal("gif", list["kind"]!.GetValue<string>());

        var open = Cli.BuildParams(Contract.Current.Find("editor.open"), ["path=123"], null);
        Assert.Equal("123", open["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task PairingSavesATokenAndLaterCommandsUseIt()
    {
        await using var app = new FakeApp();
        var (output, error) = (new StringWriter(), new StringWriter());
        Assert.Equal(0, await Cli.RunAsync(["pair", "--name", "test agent", "--scopes", "read,capture"], app.Environment(output, error), TestContext.Current.CancellationToken));
        var saved = JsonNode.Parse(File.ReadAllText(app.ConfigPath))!;
        Assert.Equal("tok-p1", saved["token"]!.GetValue<string>());
        Assert.Equal(["read", "capture"], saved["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));

        output = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["capture.screenshot", "mode=screen"], app.Environment(output, error), TestContext.Current.CancellationToken));
        Assert.Equal("/tmp/shot-screen.png", Json(output)["path"]!.GetValue<string>());
        Assert.Equal("tok-p1", app.Received[^1]["token"]!.GetValue<string>());

        error = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["recording.startVideo"], app.Environment(new StringWriter(), error), TestContext.Current.CancellationToken));
        Assert.Equal("missing_scope", Json(error)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task PairAndUnpairSupportAConfigFileInTheCurrentDirectory()
    {
        await using var app = new FakeApp();
        var configPath = $"tinyclips-agent-test-{Guid.NewGuid():N}.json";
        var env = new CliEnvironment
        {
            GetVariable = name => name switch
            {
                "TINYCLIPS_AUTOMATION_ENDPOINT" => app.Endpoint,
                "TINYCLIPS_AGENT_CONFIG" => configPath,
                _ => null,
            },
            Out = new StringWriter(),
            Error = new StringWriter(),
            PollInterval = TimeSpan.FromMilliseconds(5),
            RequestTimeout = TimeSpan.FromSeconds(10),
        };
        try
        {
            Assert.Equal(0, await Cli.RunAsync(["pair"], env, TestContext.Current.CancellationToken));
            Assert.Equal("tok-p1", JsonNode.Parse(File.ReadAllText(configPath))!["token"]!.GetValue<string>());
            Assert.Equal(0, await Cli.RunAsync(["unpair"], env, TestContext.Current.CancellationToken));
            Assert.Null(JsonNode.Parse(File.ReadAllText(configPath))!["token"]);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task StaleDirectSocketFallsBackToStoreSocketButExplicitOverrideDoesNot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS socket discovery uses Unix domain sockets.");
        var home = Path.Combine("/tmp", $"tc-{Guid.NewGuid():N}"[..11]);
        var direct = Path.Combine(home, "Library", "Application Support", "TinyClips", "automation.sock");
        var store = Path.Combine(home, "Library", "Containers", "com.refractored.tinyclips", "Data", "tinyclips-automation.sock");
        Directory.CreateDirectory(Path.GetDirectoryName(direct)!);
        File.WriteAllText(direct, "stale socket");
        try
        {
            await using var app = new FakeApp(store);
            var output = new StringWriter();
            var env = new CliEnvironment
            {
                GetVariable = name => name == "HOME" ? home : null,
                Out = output,
                Error = new StringWriter(),
                IsWindows = false,
                RequestTimeout = TimeSpan.FromSeconds(10),
            };
            Assert.Equal(0, await Cli.RunAsync(["status"], env, TestContext.Current.CancellationToken));
            Assert.True(Json(output)["automationEnabled"]!.GetValue<bool>());

            var error = new StringWriter();
            var overridden = new CliEnvironment
            {
                GetVariable = name => name switch
                {
                    "HOME" => home,
                    "TINYCLIPS_AUTOMATION_ENDPOINT" => direct,
                    _ => null,
                },
                Out = new StringWriter(),
                Error = error,
                IsWindows = false,
                RequestTimeout = TimeSpan.FromSeconds(10),
            };
            Assert.Equal(2, await Cli.RunAsync(["status"], overridden, TestContext.Current.CancellationToken));
            Assert.Equal("not_running", Json(error)["error"]!["code"]!.GetValue<string>());
            Assert.Single(app.Received);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectedEndpointClosingAfterRequestDoesNotRetryOnAnotherEndpoint()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS socket discovery uses Unix domain sockets.");
        var home = Path.Combine("/tmp", $"tc-{Guid.NewGuid():N}"[..11]);
        var direct = Path.Combine(home, "Library", "Application Support", "TinyClips", "automation.sock");
        var store = Path.Combine(home, "Library", "Containers", "com.refractored.tinyclips", "Data", "tinyclips-automation.sock");
        try
        {
            await using var first = new FakeApp(direct) { CloseBeforeReply = true };
            await using var second = new FakeApp(store);
            var error = new StringWriter();
            var env = new CliEnvironment
            {
                GetVariable = name => name == "HOME" ? home : null,
                Out = new StringWriter(),
                Error = error,
                IsWindows = false,
                RequestTimeout = TimeSpan.FromSeconds(10),
            };

            Assert.Equal(2, await Cli.RunAsync(["status"], env, TestContext.Current.CancellationToken));
            Assert.Equal("connection", Json(error)["error"]!["code"]!.GetValue<string>());
            Assert.Single(first.Received);
            Assert.Empty(second.Received);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task DeclinedPairingIsAnError()
    {
        await using var app = new FakeApp { Approve = false };
        var error = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["pair"], app.Environment(new StringWriter(), error), TestContext.Current.CancellationToken));
        Assert.Contains("approval_denied", error.ToString());
        Assert.False(File.Exists(app.ConfigPath));
    }

    [Fact]
    public async Task CommandsWithoutATokenAreUnauthorized()
    {
        await using var app = new FakeApp();
        var error = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["app.status"], app.Environment(new StringWriter(), error), TestContext.Current.CancellationToken));
        Assert.Equal("unauthorized", Json(error)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task SensitiveCommandsReportApprovalDenied()
    {
        await using var app = new FakeApp { Approve = false };
        app.Clients["t"] = ["delete"];
        var error = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["library.delete", "path=/tmp/a.png"], app.Environment(new StringWriter(), error, token: "t"), TestContext.Current.CancellationToken));
        Assert.Equal("approval_denied", Json(error)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisabledAppStillAnswersStatus()
    {
        await using var app = new FakeApp { Enabled = false };
        var output = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["status"], app.Environment(output, new StringWriter()), TestContext.Current.CancellationToken));
        Assert.False(Json(output)["automationEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task InvalidParamsAndUnknownCommandsNeverReachTheApp()
    {
        await using var app = new FakeApp();
        Assert.Equal(1, await Cli.RunAsync(["editor.open"], app.Environment(new StringWriter(), new StringWriter()), TestContext.Current.CancellationToken));
        Assert.Equal(1, await Cli.RunAsync(["nope.command"], app.Environment(new StringWriter(), new StringWriter()), TestContext.Current.CancellationToken));
        Assert.Equal(1, await Cli.RunAsync(["call", "app.status", "--params", "{bad"], app.Environment(new StringWriter(), new StringWriter()), TestContext.Current.CancellationToken));
        Assert.Empty(app.Received);
    }

    [Fact]
    public async Task AppNotRunningIsAClearError()
    {
        var error = new StringWriter();
        var env = new CliEnvironment
        {
            GetVariable = name => name == "TINYCLIPS_AUTOMATION_ENDPOINT"
                ? (OperatingSystem.IsWindows() ? @"\\.\pipe\tc-missing" : Path.Combine(Path.GetTempPath(), "tc-missing.sock"))
                : null,
            Out = new StringWriter(),
            Error = error,
        };
        Assert.Equal(2, await Cli.RunAsync(["status"], env, TestContext.Current.CancellationToken));
        Assert.Equal("not_running", Json(error)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task HelpExitsZero()
    {
        var output = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["help"], new CliEnvironment { Out = output }, TestContext.Current.CancellationToken));
        Assert.Contains("tinyclips mcp", output.ToString());
        Assert.Contains("--params '{\"k\":\"v\"}'", output.ToString());
    }

    [Fact]
    public async Task CommandsListsSchemasForStructuredArguments()
    {
        var output = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["commands"], new CliEnvironment { Out = output }, TestContext.Current.CancellationToken));
        var commands = JsonNode.Parse(output.ToString())!.AsArray();
        var screenshot = commands.Single(c => c!["name"]!.GetValue<string>() == "capture.screenshot")!;
        Assert.Equal("object", screenshot["inputSchema"]!["properties"]!["region"]!["type"]!.GetValue<string>());
        Assert.Contains("width", screenshot["inputSchema"]!["properties"]!["region"]!["required"]!
            .AsArray().Select(value => value!.GetValue<string>()));
    }
}
