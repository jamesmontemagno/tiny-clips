using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TinyClips.Core.Automation;

/// <summary>A request line: <c>{"id","token","command","params"}</c>.</summary>
public sealed class AutomationRequest
{
    /// <summary>Echoed back as sent (string or number).</summary>
    public JsonNode? Id { get; set; }

    public string? Token { get; set; }

    public string? Command { get; set; }

    public JsonObject? Params { get; set; }
}

public sealed class AutomationError
{
    public required string Code { get; set; }

    public required string Message { get; set; }
}

/// <summary>A response line: <c>{"id","ok":true,"result"}</c> or <c>{"id","ok":false,"error"}</c>.</summary>
public sealed class AutomationResponse
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonNode? Id { get; set; }

    public bool Ok { get; set; }

    public JsonNode? Result { get; set; }

    public AutomationError? Error { get; set; }

    public static AutomationResponse Success(JsonNode? id, JsonNode? result) =>
        new() { Id = id, Ok = true, Result = result ?? new JsonObject() };

    public static AutomationResponse Failure(JsonNode? id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new AutomationError { Code = code, Message = message } };
}

/// <summary>Line framing and JSON options for the automation protocol.</summary>
public static class AutomationProtocol
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Serializes a response as one line, without the trailing newline.</summary>
    public static string SerializeResponse(AutomationResponse response) =>
        JsonSerializer.Serialize(response, JsonOptions);

    public static string SerializeRequest(AutomationRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    /// <summary>Parses a request line. Throws <see cref="JsonException"/> when it is not a JSON object.</summary>
    public static AutomationRequest ParseRequest(string line)
    {
        var node = JsonNode.Parse(line) as JsonObject
            ?? throw new JsonException("A request must be a JSON object.");
        var request = new AutomationRequest
        {
            Id = node["id"]?.DeepClone(),
            Token = node["token"] is JsonValue token && token.TryGetValue<string>(out var t) ? t : null,
            Command = node["command"] is JsonValue command && command.TryGetValue<string>(out var c) ? c : null,
        };

        switch (node["params"])
        {
            case null:
                break;
            case JsonObject parameters:
                request.Params = (JsonObject)parameters.DeepClone();
                break;
            default:
                throw new JsonException("params must be a JSON object.");
        }

        return request;
    }

    public static AutomationResponse ParseResponse(string line) =>
        JsonSerializer.Deserialize<AutomationResponse>(line, JsonOptions)
            ?? throw new JsonException("Empty response.");
}
