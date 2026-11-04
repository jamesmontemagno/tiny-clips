using System.Globalization;

namespace TinyClips.Cli;

/// <summary>Everything the CLI reads from the outside world, so tests can replace it.</summary>
internal sealed class CliEnvironment
{
    public Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    public bool IsLinux { get; init; } = OperatingSystem.IsLinux();
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PairTimeout { get; init; } = TimeSpan.FromSeconds(130);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(150);
    public TimeSpan MediaRequestTimeout { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan RequestTimeoutFor(string command)
    {
        if (GetVariable("TINYCLIPS_REQUEST_TIMEOUT_SECONDS") is { Length: > 0 } value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds)
                || (seconds != 0 && (seconds < 0.001 || seconds > 4_294_967)))
            {
                throw new AutomationException("invalid_config",
                    "TINYCLIPS_REQUEST_TIMEOUT_SECONDS must be 0 (unlimited) or a number between 0.001 and 4294967 seconds.");
            }

            return seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
        }

        return command is "studio.export" or "editor.trim" ? MediaRequestTimeout : RequestTimeout;
    }

    public string Home =>
        GetVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public IReadOnlyList<string> EndpointCandidates() => EndpointCandidates(out _);

    public IReadOnlyList<string> EndpointCandidates(out bool linuxDiscovery)
    {
        linuxDiscovery = false;
        if (GetVariable("TINYCLIPS_AUTOMATION_ENDPOINT") is { Length: > 0 } custom) return [custom];
        if (IsWindows) return [$@"\\.\pipe\TinyClips.Automation.{GetVariable("USERNAME") ?? Environment.UserName}"];
        if (IsLinux)
        {
            // Never fall back to a shared directory, a relative location, or a macOS endpoint.
            var runtime = GetVariable("XDG_RUNTIME_DIR")?.TrimEnd('/');
            if (runtime is not { Length: > 1 } || runtime[0] != '/' || runtime.Contains('\0')
                || runtime.Split('/').Skip(1).Any(part => part is "" or "." or "..")) return [];
            linuxDiscovery = true;
            return
            [
                $"{runtime}/app/com.refractored.tinyclips/automation.sock",
                $"{runtime}/app/com.refractored.tinyclips.Devel/automation.sock",
            ];
        }
        return
        [
            Path.Combine(Home, "Library", "Application Support", "TinyClips", "automation.sock"),
            Path.Combine(Home, "Library", "Containers", "com.refractored.tinyclips", "Data", "tinyclips-automation.sock"),
        ];
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
