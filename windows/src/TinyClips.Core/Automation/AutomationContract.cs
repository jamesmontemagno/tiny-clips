using System.Text.Json;

namespace TinyClips.Core.Automation;

/// <summary>One parameter of a contract command.</summary>
public sealed record AutomationParameterDefinition(
    string Name,
    string Type,
    bool Required,
    IReadOnlyList<string>? AllowedValues,
    JsonElement? DefaultValue);

/// <summary>One command of the automation contract and the scope it needs.</summary>
public sealed record AutomationCommandDefinition(
    string Name,
    string Scope,
    string Description,
    IReadOnlyDictionary<string, AutomationParameterDefinition> Parameters);

/// <summary>
/// The agent automation contract (<c>shared/automation/commands.json</c>) shared with the macOS
/// app and the <c>tinyclips</c> CLI. The file is embedded in this assembly.
/// </summary>
public sealed class AutomationContract
{
    private const string ResourceName = "TinyClips.Core.Automation.commands.json";
    private static readonly Lazy<AutomationContract> _default = new(LoadEmbedded);
    private readonly Dictionary<string, AutomationCommandDefinition> _commands;

    private AutomationContract(
        int protocolVersion,
        IReadOnlyDictionary<string, string> scopes,
        IReadOnlySet<string> sensitiveScopes,
        IReadOnlyList<string> sessionCommands,
        IReadOnlyList<AutomationCommandDefinition> commands,
        IReadOnlyDictionary<string, string> errors)
    {
        ProtocolVersion = protocolVersion;
        Scopes = scopes;
        SensitiveScopes = sensitiveScopes;
        SessionCommands = sessionCommands;
        Commands = commands;
        Errors = errors;
        _commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>The contract embedded in this build.</summary>
    public static AutomationContract Default => _default.Value;

    public int ProtocolVersion { get; }

    /// <summary>Scope name to description.</summary>
    public IReadOnlyDictionary<string, string> Scopes { get; }

    /// <summary>Scopes whose commands always need in-app approval.</summary>
    public IReadOnlySet<string> SensitiveScopes { get; }

    public IReadOnlyList<string> SessionCommands { get; }

    public IReadOnlyList<AutomationCommandDefinition> Commands { get; }

    /// <summary>Error code to description.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; }

    public bool IsSessionCommand(string command) => SessionCommands.Contains(command, StringComparer.Ordinal);

    public bool TryGetCommand(string command, out AutomationCommandDefinition definition) =>
        _commands.TryGetValue(command, out definition!);

    public bool IsSensitiveScope(string scope) => SensitiveScopes.Contains(scope);

    public static AutomationContract Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    public static AutomationContract Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    private static AutomationContract LoadEmbedded()
    {
        using var stream = typeof(AutomationContract).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The automation contract resource '{ResourceName}' is missing.");
        return Load(stream);
    }

    private static AutomationContract Parse(JsonElement root)
    {
        var protocolVersion = root.GetProperty("protocolVersion").GetInt32();

        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scope in root.GetProperty("scopes").EnumerateObject())
        {
            scopes[scope.Name] = scope.Value.GetString() ?? string.Empty;
        }

        var sensitive = new HashSet<string>(
            root.GetProperty("sensitiveScopes").EnumerateArray().Select(e => e.GetString()!),
            StringComparer.Ordinal);
        var sessionCommands = root.GetProperty("sessionCommands").EnumerateArray().Select(e => e.GetString()!).ToArray();

        var commands = new List<AutomationCommandDefinition>();
        foreach (var command in root.GetProperty("commands").EnumerateArray())
        {
            var name = command.GetProperty("name").GetString()!;
            var scope = command.GetProperty("scope").GetString()!;
            if (!scopes.ContainsKey(scope))
            {
                throw new InvalidDataException($"Command '{name}' uses unknown scope '{scope}'.");
            }

            var description = command.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var parameters = new Dictionary<string, AutomationParameterDefinition>(StringComparer.Ordinal);
            if (command.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var parameter in paramsElement.EnumerateObject())
                {
                    var spec = parameter.Value;
                    var type = spec.TryGetProperty("type", out var t) ? t.GetString() ?? "any" : "any";
                    var required = spec.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
                    IReadOnlyList<string>? allowed = spec.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array
                        ? e.EnumerateArray().Select(v => v.GetString()!).ToArray()
                        : null;
                    JsonElement? defaultValue = spec.TryGetProperty("default", out var dv) ? dv.Clone() : null;
                    parameters[parameter.Name] = new AutomationParameterDefinition(parameter.Name, type, required, allowed, defaultValue);
                }
            }

            commands.Add(new AutomationCommandDefinition(name, scope, description, parameters));
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var error in root.GetProperty("errors").EnumerateObject())
        {
            errors[error.Name] = error.Value.GetString() ?? string.Empty;
        }

        return new AutomationContract(protocolVersion, scopes, sensitive, sessionCommands, commands, errors);
    }
}
