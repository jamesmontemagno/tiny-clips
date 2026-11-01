namespace TinyClips.Cli;

/// <summary>Everything the CLI reads from the outside world, so tests can replace it.</summary>
internal sealed class CliEnvironment
{
    public Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PairTimeout { get; init; } = TimeSpan.FromSeconds(130);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(150);

    public string Home =>
        GetVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public IReadOnlyList<string> EndpointCandidates()
    {
        if (GetVariable("TINYCLIPS_AUTOMATION_ENDPOINT") is { Length: > 0 } custom) return [custom];
        if (IsWindows) return [$@"\\.\pipe\TinyClips.Automation.{GetVariable("USERNAME") ?? Environment.UserName}"];
        return
        [
            Path.Combine(Home, "Library", "Application Support", "TinyClips", "automation.sock"),
            Path.Combine(Home, "Library", "Containers", "com.refractored.tinyclips", "Data", "tinyclips-automation.sock"),
        ];
    }

    public string ResolveEndpoint()
    {
        var candidates = EndpointCandidates();
        if (candidates.Count == 1) return candidates[0];
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public string ConfigPath()
    {
        if (GetVariable("TINYCLIPS_AGENT_CONFIG") is { Length: > 0 } custom) return custom;
        if (IsWindows)
        {
            var appData = GetVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "TinyClips", "agent.json");
        }
        var configHome = GetVariable("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");
        return Path.Combine(configHome, "tinyclips", "agent.json");
    }
}
