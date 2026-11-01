namespace TinyClips.Core.Automation;

/// <summary>Error codes from the automation contract.</summary>
public static class AutomationErrorCodes
{
    public const string AutomationDisabled = "automation_disabled";
    public const string Unauthorized = "unauthorized";
    public const string MissingScope = "missing_scope";
    public const string ApprovalDenied = "approval_denied";
    public const string UnknownCommand = "unknown_command";
    public const string NotSupported = "not_supported";
    public const string InvalidParams = "invalid_params";
    public const string Busy = "busy";
    public const string Conflict = "conflict";
    public const string PermissionRequired = "permission_required";
    public const string ProRequired = "pro_required";
    public const string NotFound = "not_found";
    public const string Internal = "internal";
}

/// <summary>Session command names, which are not in the command list of the contract.</summary>
public static class AutomationSessionCommands
{
    public const string Hello = "session.hello";
    public const string Pair = "session.pair";
    public const string PairStatus = "session.pairStatus";
}

/// <summary>A failure a command reports with a contract error code.</summary>
public sealed class AutomationException : Exception
{
    public AutomationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
