using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyClips.Cli;

/// <summary>Saved pairing: the token the app issued, its scopes, and the client name.</summary>
internal static class AgentConfig
{
    public static JsonObject Read(CliEnvironment env)
    {
        var path = env.ConfigPath();
        if (!File.Exists(path)) return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Write(CliEnvironment env, JsonObject config)
    {
        var path = env.ConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, json);
            return;
        }

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var writer = new StreamWriter(stream);
        writer.Write(json);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static string? Token(CliEnvironment env) =>
        env.GetVariable("TINYCLIPS_TOKEN") is { Length: > 0 } token ? token : Read(env)["token"]?.GetValue<string>();
}
