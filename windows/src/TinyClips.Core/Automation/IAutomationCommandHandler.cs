using System.Text.Json.Nodes;

namespace TinyClips.Core.Automation;

/// <summary>A command that passed the policy, with its parameters validated and defaults filled in.</summary>
public sealed record AutomationCommandContext(
    AutomationCommandDefinition Command,
    JsonObject Params,
    AutomationClient Client)
{
    public string? GetString(string name) =>
        Params[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public bool GetBoolean(string name, bool fallback = false) =>
        Params[name] is JsonValue value && value.TryGetValue<bool>(out var b) ? b : fallback;

    public int GetInt32(string name, int fallback)
    {
        if (Params[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : fallback;
    }
}

/// <summary>A client asking to be paired.</summary>
public sealed record AutomationPairingRequest(string PairingId, string ClientName, IReadOnlyList<string> Scopes);

/// <summary>Runs contract commands in the app. Implemented by the WinUI app.</summary>
public interface IAutomationCommandHandler
{
    /// <summary>The app version reported by <c>session.hello</c>.</summary>
    string AppVersion { get; }

    /// <summary><c>direct</c> or <c>store</c>.</summary>
    string BuildFlavor { get; }

    /// <summary>The contract commands this build implements. Others return <c>not_supported</c>.</summary>
    IReadOnlyCollection<string> SupportedCommands { get; }

    /// <summary>
    /// Runs a command. Report contract failures by throwing <see cref="AutomationException"/>;
    /// any other exception becomes <c>internal</c>.
    /// </summary>
    Task<JsonNode?> ExecuteAsync(AutomationCommandContext context, CancellationToken cancellationToken);
}

/// <summary>Asks the user, in the app, to approve pairing and sensitive commands.</summary>
public interface IAutomationApprovalPrompt
{
    /// <summary>Returns true only if the user approved. Cancellation means no answer (denied).</summary>
    Task<bool> RequestPairingApprovalAsync(AutomationPairingRequest request, CancellationToken cancellationToken);

    /// <summary>Returns true only if the user approved. Cancellation means no answer (denied).</summary>
    Task<bool> RequestCommandApprovalAsync(AutomationCommandContext context, CancellationToken cancellationToken);
}
