using Microsoft.UI.Dispatching;
using TinyClips.Core.Automation;

namespace TinyClips.App;

/// <summary>
/// Shows pairing and sensitive-command approvals one at a time, on the UI thread. A prompt that is
/// cancelled (timeout, agent control turned off, client gone) closes and counts as denied.
/// </summary>
internal sealed class AutomationApprovalPrompt : IAutomationApprovalPrompt
{
    private readonly Func<DispatcherQueue?> _dispatcher;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public AutomationApprovalPrompt(Func<DispatcherQueue?> dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task<IReadOnlyList<string>?> RequestPairingApprovalAsync(AutomationPairingRequest request, CancellationToken cancellationToken)
    {
        var contract = AutomationContract.Default;
        var choices = contract.Scopes
            .Select(s => new AutomationScopeChoice(
                s.Key,
                s.Value,
                contract.IsSensitiveScope(s.Key),
                request.Scopes.Contains(s.Key, StringComparer.Ordinal)))
            .ToArray();
        var content = new AutomationApprovalContent(
            Title: "Allow an agent to control Tiny Clips?",
            Message: $"“{request.ClientName}” wants to pair with Tiny Clips. Only allow agents you started yourself.",
            ListHeader: "Permissions (the agent's request is checked):",
            Items: [],
            Footnote: "Deleting, uploading, and discarding always ask again. You can change permissions or revoke access in Settings → Agent control.",
            AllowText: "Allow",
            ScopeChoices: choices);
        return await ShowAsync(content, cancellationToken);
    }

    public async Task<bool> RequestCommandApprovalAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var path = context.GetString("path");
        var action = context.Command.Name switch
        {
            "library.delete" => ("Delete a capture?", "move this capture to the Recycle Bin", "Delete"),
            "library.upload" => ("Upload a capture?", "upload this capture to Uploadcare and get a public link", "Upload"),
            "recording.discard" => ("Discard the recording?", "stop and throw away the recording in progress", "Discard"),
            "recording.restart" => ("Restart the recording?", "throw away the current take and start recording again", "Restart"),
            _ => ("Allow this action?", context.Command.Description.TrimEnd('.').ToLowerInvariant(), "Allow"),
        };
        var content = new AutomationApprovalContent(
            Title: action.Item1,
            Message: $"“{context.Client.Name}” wants to {action.Item2}.",
            ListHeader: path is null ? null : "File:",
            Items: path is null ? [] : [path],
            Footnote: $"Command: {context.Command.Name}",
            AllowText: action.Item3);
        return await ShowAsync(content, cancellationToken) is not null;
    }

    /// <summary>Returns the chosen scopes (empty for a plain approval), or null if denied.</summary>
    private async Task<IReadOnlyList<string>?> ShowAsync(AutomationApprovalContent content, CancellationToken cancellationToken)
    {
        try
        {
            await _oneAtATime.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (_dispatcher() is not { } dispatcher || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var completion = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await AutomationApprovalWindow.ShowAsync(content, cancellationToken));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Agent approval prompt failed: {ex}");
                    completion.TrySetResult(null);
                }
            });

            return queued ? await completion.Task : null;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}

internal sealed record AutomationApprovalContent(
    string Title,
    string Message,
    string? ListHeader,
    IReadOnlyList<string> Items,
    string Footnote,
    string AllowText,
    IReadOnlyList<AutomationScopeChoice>? ScopeChoices = null);

internal sealed record AutomationScopeChoice(string Name, string Description, bool IsSensitive, bool IsRequested);
