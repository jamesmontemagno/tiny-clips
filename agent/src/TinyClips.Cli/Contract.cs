using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyClips.Cli;

internal sealed record ParamSpec(string? Type, IReadOnlyList<string>? Enum, JsonNode? Default, bool Required, JsonObject Schema);

internal sealed record CommandSpec(string Name, string Scope, string Description, IReadOnlyDictionary<string, ParamSpec> Params);

internal sealed class Contract
{
    private static readonly Lazy<Contract> Embedded = new(LoadEmbedded);

    public static Contract Current => Embedded.Value;

    public required int ProtocolVersion { get; init; }
    public required IReadOnlyDictionary<string, string> Scopes { get; init; }
    public required IReadOnlyList<string> SensitiveScopes { get; init; }
    public required IReadOnlyList<CommandSpec> Commands { get; init; }

    public CommandSpec? Find(string name) => Commands.FirstOrDefault(c => c.Name == name);

    public bool IsSensitive(CommandSpec command) => SensitiveScopes.Contains(command.Scope);

    public static string ToolName(string commandName) => "tinyclips_" + commandName.Replace('.', '_');

    public CommandSpec? FromToolName(string toolName) =>
        toolName.StartsWith("tinyclips_", StringComparison.Ordinal)
            ? Commands.FirstOrDefault(c => c.Name.Replace('.', '_') == toolName["tinyclips_".Length..])
            : null;

    public static Contract Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var commands = root["commands"]!.AsArray().Select(node =>
        {
            var command = node!.AsObject();
            var parameters = new Dictionary<string, ParamSpec>();
            foreach (var (key, value) in command["params"]?.AsObject() ?? [])
            {
                var spec = value!.AsObject();
                var required = spec["required"] is JsonValue requiredValue &&
                    requiredValue.TryGetValue<bool>(out var requiredFlag) && requiredFlag;
                var schema = (JsonObject)spec.DeepClone();
                if (spec["required"] is JsonValue) schema.Remove("required");
                parameters[key] = new ParamSpec(
                    spec["type"]?.GetValue<string>(),
                    spec["enum"]?.AsArray().Select(e => e!.GetValue<string>()).ToArray(),
                    spec["default"]?.DeepClone(),
                    required,
                    schema);
            }
            return new CommandSpec(
                command["name"]!.GetValue<string>(),
                command["scope"]!.GetValue<string>(),
                command["description"]!.GetValue<string>(),
                parameters);
        }).ToArray();

        return new Contract
        {
            ProtocolVersion = root["protocolVersion"]!.GetValue<int>(),
            Scopes = root["scopes"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()),
            SensitiveScopes = root["sensitiveScopes"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray(),
            Commands = commands,
        };
    }

    public static JsonObject InputSchema(CommandSpec command)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (key, spec) in command.Params)
        {
            var property = (JsonObject)spec.Schema.DeepClone();
            if (spec.Type == "any") property.Remove("type");
            properties[key] = property;
            if (spec.Required) required.Add(key);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    public static string? Validate(CommandSpec command, JsonObject parameters)
    {
        foreach (var (key, spec) in command.Params)
        {
            if (!parameters.TryGetPropertyValue(key, out var value) || value is null)
            {
                if (spec.Required) return $"Missing required parameter \"{key}\".";
                continue;
            }

            if (ValidateValue(value, spec.Schema, key) is { } error) return error;
        }

        var unknown = parameters.Select(p => p.Key).Where(k => !command.Params.ContainsKey(k)).ToArray();
        return unknown.Length > 0 ? $"Unknown parameter(s): {string.Join(", ", unknown)}." : null;
    }

    private static string? ValidateValue(JsonNode value, JsonObject schema, string path)
    {
        var kind = value.GetValueKind();
        var type = schema["type"]?.GetValue<string>();
        var matchesType = type switch
        {
            "string" => kind == JsonValueKind.String,
            "integer" => kind == JsonValueKind.Number && value.AsValue().TryGetValue<long>(out _),
            "number" => kind == JsonValueKind.Number && value.AsValue().TryGetValue<double>(out var number) && double.IsFinite(number),
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            _ => true,
        };
        if (!matchesType)
            return $"\"{path}\" must be {(type == "boolean" ? "true or false" : type)}.";

        if (schema["enum"] is JsonArray values &&
            !values.Any(candidate => JsonNode.DeepEquals(candidate, value)))
        {
            return $"\"{path}\" must be one of: {string.Join(", ", values.Select(v => v?.ToJsonString()))}.";
        }

        if (kind == JsonValueKind.Number &&
            value.AsValue().TryGetValue<double>(out var numericValue))
        {
            if (schema["minimum"]?.GetValue<double>() is { } minimum && numericValue < minimum)
                return $"\"{path}\" must be at least {minimum}.";
            if (schema["maximum"]?.GetValue<double>() is { } maximum && numericValue > maximum)
                return $"\"{path}\" must be at most {maximum}.";
        }

        if (value is JsonArray array)
        {
            if (schema["minItems"]?.GetValue<int>() is { } minimum && array.Count < minimum)
                return $"\"{path}\" must contain at least {minimum} item(s).";
            if (schema["items"] is JsonObject itemSchema)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is not { } item) return $"\"{path}[{index}]\" cannot be null.";
                    if (ValidateValue(item, itemSchema, $"{path}[{index}]") is { } error) return error;
                }
            }
        }

        if (value is JsonObject obj && schema["properties"] is JsonObject properties)
        {
            if (schema["required"] is JsonArray requiredProperties)
            {
                foreach (var required in requiredProperties)
                {
                    if (required?.GetValue<string>() is { } requiredName &&
                        (!obj.TryGetPropertyValue(requiredName, out var requiredValue) || requiredValue is null))
                    {
                        return $"Missing required parameter \"{path}.{requiredName}\".";
                    }
                }
            }

            foreach (var (key, child) in obj)
            {
                if (!properties.TryGetPropertyValue(key, out var propertySchema))
                {
                    if (schema["additionalProperties"]?.GetValue<bool>() == false)
                        return $"Unknown parameter \"{path}.{key}\".";
                    continue;
                }
                if (child is null) return $"\"{path}.{key}\" cannot be null.";
                if (propertySchema is JsonObject childSchema &&
                    ValidateValue(child, childSchema, $"{path}.{key}") is { } error)
                    return error;
            }
        }

        return null;
    }

    private static Contract LoadEmbedded()
    {
        using var stream = typeof(Contract).Assembly.GetManifestResourceStream("TinyClips.Cli.commands.json")
            ?? throw new InvalidOperationException("commands.json is missing from the tinyclips tool.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
