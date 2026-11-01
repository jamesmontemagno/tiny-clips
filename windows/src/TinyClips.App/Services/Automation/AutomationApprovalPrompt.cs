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

    public Task<bool> RequestPairingApprovalAsync(AutomationPairingRequest request, CancellationToken cancellationToken)
    {
        var scopes = AutomationContract.Default.Scopes;
        var lines = request.Scopes
            .Select(s => scopes.TryGetValue(s, out var description) ? $"{s} — {description}" : s)
            .ToArray();
        var content = new AutomationApprovalContent(
            Title: "Allow an agent to control Tiny Clips?",
            Message: $"“{request.ClientName}” wants to pair with Tiny Clips. Only allow agents you started yourself.",
            ListHeader: "It asks for:",
            Items: lines,
            Footnote: "Deleting, uploading, and discarding always ask again. You can revoke access in Settings → Agent control.",
            AllowText: "Allow");
        return ShowAsync(content, cancellationToken);
    }

    public Task<bool> RequestCommandApprovalAsync(AutomationCommandContext context, CancellationToken cancellationToken)
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
        return ShowAsync(content, cancellationToken);
    }

    private async Task<bool> ShowAsync(AutomationApprovalContent content, CancellationToken cancellationToken)
    {
        try
        {
            await _oneAtATime.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (_dispatcher() is not { } dispatcher || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await AutomationApprovalWindow.ShowAsync(content, cancellationToken));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Agent approval prompt failed: {ex}");
                    completion.TrySetResult(false);
                }
            });

            return queued && await completion.Task;
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
    string AllowText);
