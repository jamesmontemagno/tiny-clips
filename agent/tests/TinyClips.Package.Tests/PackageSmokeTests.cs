using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TinyClips.Cli;
using TinyClips.Cli.Tests;

namespace TinyClips.Package.Tests;

public class PackageSmokeTests
{
    static string PackageDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("TINYCLIPS_PACKAGE_DIRECTORY")
        ?? throw new InvalidOperationException("Pack the tool and set TINYCLIPS_PACKAGE_DIRECTORY before running package smoke tests."));

    static string PackagePath => Assert.Single(Directory.GetFiles(PackageDirectory, "TinyClips.*.nupkg"));

    static string RepoRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "plugin.json")))
                    return directory.FullName;
            throw new InvalidOperationException("Cannot find the Tiny Clips plugin above the test output.");
        }
    }

    static string ReadEntry(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(
            (archive.GetEntry(path) ?? throw new InvalidOperationException($"Package is missing {path}.")).Open());
        return reader.ReadToEnd();
    }

    static string PackageVersion
    {
        get
        {
            using var archive = ZipFile.OpenRead(PackagePath);
            return XDocument.Parse(ReadEntry(archive, "TinyClips.nuspec"))
                .Descendants().Single(e => e.Name.LocalName == "version").Value;
        }
    }

    [Fact]
    public void PackageContainsTheToolAndMatchingRegistryMetadata()
    {
        using var archive = ZipFile.OpenRead(PackagePath);
        var nuspec = XDocument.Parse(ReadEntry(archive, "TinyClips.nuspec"));
        Assert.Equal("TinyClips", nuspec.Descendants().Single(e => e.Name.LocalName == "id").Value);
        Assert.Contains(nuspec.Descendants(), e => e.Name.LocalName == "packageType" && (string?)e.Attribute("name") == "DotnetTool");
        Assert.Contains(nuspec.Descendants(), e => e.Name.LocalName == "packageType" && (string?)e.Attribute("name") == "McpServer");

        const string toolDirectory = "tools/net10.0/any/";
        var settings = XDocument.Parse(ReadEntry(archive, toolDirectory + "DotnetToolSettings.xml"));
        var command = Assert.Single(settings.Descendants("Command"));
        Assert.Equal("tinyclips", (string?)command.Attribute("Name"));
        Assert.Equal("tinyclips.dll", (string?)command.Attribute("EntryPoint"));
        Assert.Equal("dotnet", (string?)command.Attribute("Runner"));
        foreach (var file in new[] { "tinyclips.dll", "tinyclips.deps.json", "tinyclips.runtimeconfig.json" })
            Assert.NotNull(archive.GetEntry(toolDirectory + file));
        Assert.NotEmpty(ReadEntry(archive, "README.md"));

        var registry = JsonNode.Parse(ReadEntry(archive, ".mcp/server.json"))!;
        var sourceRegistry = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "agent", "src", "TinyClips.Cli", ".mcp", "server.json")));
        Assert.True(JsonNode.DeepEquals(sourceRegistry, registry), "Packaged MCP registry metadata differs from the source.");
        Assert.Equal(PackageVersion, (string?)registry["version"]);
        Assert.Equal(PackageVersion, (string?)registry["packages"]![0]!["version"]);
        var plugin = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "plugin.json")))!;
        Assert.Equal(PackageVersion, (string?)plugin["version"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedCliAndMcpWorkWithInstalledToolAndDnx(bool useDnx)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("tc-package-").FullName;
        try
        {
            await using var app = new FakeApp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var cancellationToken = timeout.Token;
            var config = Path.Combine(temporaryDirectory, "NuGet.Config");
            new XDocument(new XElement("configuration", new XElement("packageSources",
                new XElement("clear"), new XElement("add", new XAttribute("key", "smoke"),
                    new XAttribute("value", PackageDirectory))))).Save(config);
            var environment = new Dictionary<string, string?>
            {
                ["DOTNET_CLI_HOME"] = Path.Combine(temporaryDirectory, "dotnet"),
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["NUGET_PACKAGES"] = Path.Combine(temporaryDirectory, "packages"),
                ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(temporaryDirectory, "http-cache"),
                ["TINYCLIPS_AUTOMATION_ENDPOINT"] = app.Endpoint,
                ["TINYCLIPS_AGENT_CONFIG"] = app.ConfigPath,
                ["TINYCLIPS_TOKEN"] = null,
            };
            var executable = "dotnet";
            string[] prefix;
            if (useDnx)
            {
                var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "mcp.json")))!;
                var server = mcp["mcpServers"]!["tinyclips"]!;
                executable = server["command"]!.GetValue<string>();
                var pluginArgs = server["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
                Assert.Equal("dnx", pluginArgs[0]);
                Assert.Equal("mcp", pluginArgs[^1]);
                prefix = [.. pluginArgs[..2], "--version", PackageVersion, "--source", PackageDirectory,
                    "--configfile", config, "--no-http-cache", .. pluginArgs[2..^1]];
            }
            else
            {
                var toolPath = Path.Combine(temporaryDirectory, "tool");
                var install = await RunAsync("dotnet",
                    ["tool", "install", "TinyClips", "--version", PackageVersion, "--tool-path", toolPath,
                        "--configfile", config, "--no-http-cache"], environment, cancellationToken);
                Assert.True(install.ExitCode == 0, install.Error + install.Output);
                executable = Path.Combine(toolPath, OperatingSystem.IsWindows() ? "tinyclips.exe" : "tinyclips");
                prefix = [];
            }

            var commands = await RunAsync(executable, [.. prefix, "commands"], environment, cancellationToken);
            Assert.True(commands.ExitCode == 0, commands.Error + commands.Output);
            var commandList = JsonNode.Parse(commands.Output)!.AsArray();
            var expectedCommands = Contract.Current.CommandsForPlatform(Contract.CurrentPlatform).ToArray();
            Assert.Equal(expectedCommands.Select(c => c.Name).Order(),
                commandList.Select(c => c!["name"]!.GetValue<string>()).Order());
            foreach (var command in expectedCommands)
            {
                var actual = commandList.Single(c => (string?)c!["name"] == command.Name)!;
                Assert.True(JsonNode.DeepEquals(Contract.InputSchema(command), actual["inputSchema"]),
                    $"Packaged input schema differs for {command.Name}.");
            }

            var unauthorized = await RunAsync(executable, [.. prefix, "app.status"], environment, cancellationToken);
            Assert.Equal(2, unauthorized.ExitCode);
            Assert.Equal("unauthorized", (string?)JsonNode.Parse(unauthorized.Error)!["error"]!["code"]);
            var pair = await RunAsync(executable,
                [.. prefix, "pair", "--name", "package smoke", "--scopes", "read,capture"], environment, cancellationToken);
            Assert.True(pair.ExitCode == 0, pair.Error + pair.Output);
            Assert.True(JsonNode.Parse(pair.Output)!["paired"]!.GetValue<bool>());
            var screenshot = await RunAsync(executable,
                [.. prefix, "capture.screenshot", "mode=screen"], environment, cancellationToken);
            Assert.True(screenshot.ExitCode == 0, screenshot.Error + screenshot.Output);
            Assert.Equal("/tmp/shot-screen.png", (string?)JsonNode.Parse(screenshot.Output)!["path"]);

            foreach (var protocolVersion in new string?[] { "2025-06-18", null })
            {
                await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = executable,
                    Arguments = [.. prefix, "mcp"],
                    EnvironmentVariables = environment,
                }), new McpClientOptions
                {
                    ProtocolVersion = protocolVersion,
                    ClientInfo = new Implementation { Name = "package-smoke", Version = "1" },
                }, cancellationToken: cancellationToken);
                Assert.Equal(PackageVersion, client.ServerInfo!.Version);
                if (protocolVersion is not null) Assert.Equal(protocolVersion, client.NegotiatedProtocolVersion);
                var listed = await client.ListToolsAsync(new ListToolsRequestParams(), cancellationToken);
                Assert.Equal(expectedCommands.Length + 1, listed.Tools.Count);
                Assert.Contains(listed.Tools, t => t.Name == "tinyclips_status");
                var captured = await client.CallToolAsync("tinyclips_capture_screenshot",
                    new Dictionary<string, object?> { ["mode"] = "screen" }, cancellationToken: cancellationToken);
                Assert.False(captured.IsError);
                var text = Assert.IsType<TextContentBlock>(Assert.Single(captured.Content)).Text;
                Assert.Equal("/tmp/shot-screen.png", (string?)JsonNode.Parse(text)!["path"]);
                var denied = await client.CallToolAsync("tinyclips_recording_startVideo", cancellationToken: cancellationToken);
                Assert.True(denied.IsError);
                Assert.Contains("missing_scope", Assert.IsType<TextContentBlock>(Assert.Single(denied.Content)).Text);
            }
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    static Process Start(string executable, IEnumerable<string> arguments, Dictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
    }

    static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable, string[] arguments, Dictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        using var process = Start(executable, arguments, environment);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
