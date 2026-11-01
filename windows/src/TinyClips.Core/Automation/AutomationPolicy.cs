namespace TinyClips.Core.Automation;

public enum AutomationDecisionKind
{
    /// <summary>Run the command.</summary>
    Allow,

    /// <summary>Run the command only after the user approves it in the app.</summary>
    Approve,

    /// <summary>Refuse the command with <see cref="AutomationDecision.ErrorCode"/>.</summary>
    Deny,
}

public readonly record struct AutomationDecision(AutomationDecisionKind Kind, string? ErrorCode)
{
    public static AutomationDecision Allow { get; } = new(AutomationDecisionKind.Allow, null);

    public static AutomationDecision Approve { get; } = new(AutomationDecisionKind.Approve, null);

    public static AutomationDecision Deny(string errorCode) => new(AutomationDecisionKind.Deny, errorCode);

    /// <summary>The decision as <c>shared/automation/fixtures/policy.json</c> writes it.</summary>
    public override string ToString() => Kind switch
    {
        AutomationDecisionKind.Allow => "allow",
        AutomationDecisionKind.Approve => "approve",
        _ => ErrorCode ?? AutomationErrorCodes.Internal,
    };
}

/// <summary>
/// Decides whether a command may run, following the policy order in
/// <c>docs/agent-integration.md</c>. Pure: no I/O and no UI.
/// </summary>
public sealed class AutomationPolicy
{
    private readonly AutomationContract _contract;

    public AutomationPolicy(AutomationContract contract)
    {
        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
    }

    /// <param name="enabled">Whether agent control is on.</param>
    /// <param name="grantedScopes">The scopes of the client whose token was sent, or null when the token is missing or unknown.</param>
    /// <param name="command">The command name.</param>
    public AutomationDecision Decide(bool enabled, IReadOnlyCollection<string>? grantedScopes, string command)
    {
        if (command == AutomationSessionCommands.Hello)
        {
            return AutomationDecision.Allow;
        }

        if (!enabled)
        {
            return AutomationDecision.Deny(AutomationErrorCodes.AutomationDisabled);
        }

        if (command is AutomationSessionCommands.Pair or AutomationSessionCommands.PairStatus)
        {
            return AutomationDecision.Allow;
        }

        if (grantedScopes is null)
        {
            return AutomationDecision.Deny(AutomationErrorCodes.Unauthorized);
        }

        if (!_contract.TryGetCommand(command, out var definition))
        {
            return AutomationDecision.Deny(AutomationErrorCodes.UnknownCommand);
        }

        if (!grantedScopes.Contains(definition.Scope, StringComparer.Ordinal))
        {
            return AutomationDecision.Deny(AutomationErrorCodes.MissingScope);
        }

        return _contract.IsSensitiveScope(definition.Scope)
            ? AutomationDecision.Approve
            : AutomationDecision.Allow;
    }
}
