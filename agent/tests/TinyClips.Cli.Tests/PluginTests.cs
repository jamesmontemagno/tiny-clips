using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TinyClips.Cli.Tests;

public class PluginTests
{
    static readonly string RepoRoot = FindRepoRoot();
    static readonly string PluginRoot = Path.Combine(RepoRoot, "plugins", "tinyclips");

    static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "plugins", "tinyclips", "plugin.json")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not find plugins/tinyclips/plugin.json above the test output.");
    }

    static JsonObject Read(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(PluginRoot, file)))!.AsObject();

    [Fact]
    public void ManifestTargetsAgentPlugins1AndMatchesTheToolVersion()
    {
        var manifest = Read("plugin.json");
        string[] allowed = ["$schema", "name", "version", "description", "author", "homepage", "repository", "license", "keywords", "extensions"];
        Assert.All(manifest.Select(p => p.Key), key => Assert.Contains(key, allowed));
        Assert.Equal("https://agent-plugins.org/schemas/1.0.0/plugin.schema.json", (string?)manifest["$schema"]);
        Assert.Equal("tinyclips", (string?)manifest["name"]);

        var csproj = XDocument.Load(Path.Combine(RepoRoot, "agent", "src", "TinyClips.Cli", "TinyClips.Cli.csproj"));
        var packageVersion = csproj.Descendants("PackageVersion").Single().Value;
        Assert.Equal(packageVersion, (string?)manifest["version"]);
        var registry = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepoRoot, "agent", "src", "TinyClips.Cli", ".mcp", "server.json")))!;
        Assert.Equal(packageVersion, (string?)registry["version"]);
        var package = Assert.Single(registry["packages"]!.AsArray());
        Assert.Equal("TinyClips", (string?)package!["identifier"]);
        Assert.Equal(packageVersion, (string?)package["version"]);
    }

    [Fact]
    public void McpConfigLaunchesThePublishedTool()
    {
        var mcp = Read("mcp.json");
        Assert.Equal(["$schema", "mcpServers"], mcp.Select(p => p.Key).Order());
        Assert.Equal("https://agent-plugins.org/schemas/1.0.0/mcp.schema.json", (string?)mcp["$schema"]);
        var server = mcp["mcpServers"]!["tinyclips"]!.AsObject();
        Assert.Equal("stdio", (string?)server["type"]);
        Assert.Equal("dotnet", (string?)server["command"]);
        Assert.Equal(["dnx", "TinyClips", "--verbosity", "quiet", "--yes", "--", "mcp"],
            server["args"]!.AsArray().Select(a => (string)a!));
    }

    [Fact]
    public void SkillsFollowTheAgentSkillsNamingRules()
    {
        var skills = Directory.GetDirectories(Path.Combine(PluginRoot, "skills"));
        Assert.NotEmpty(skills);
        foreach (var dir in skills)
        {
            var folder = Path.GetFileName(dir);
            var text = File.ReadAllText(Path.Combine(dir, "SKILL.md"));
            var frontmatter = Regex.Match(text, "^---\\r?\\n(.*?)\\r?\\n---", RegexOptions.Singleline);
            Assert.True(frontmatter.Success, $"{folder} is missing frontmatter");
            var name = Regex.Match(frontmatter.Groups[1].Value, "^name:\\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            var description = Regex.Match(frontmatter.Groups[1].Value, "^description:\\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            Assert.Equal(folder, name);
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", name);
            Assert.InRange(name.Length, 1, 64);
            Assert.InRange(description.Length, 1, 1024);
        }
    }
}
