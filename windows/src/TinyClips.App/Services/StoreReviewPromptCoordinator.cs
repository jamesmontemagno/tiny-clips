#if TINYCLIPS_STORE_BUILD
using System.Diagnostics;
using Microsoft.UI.Xaml;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Shows the one-time Store review prompt only after every capture editor has finished. It also
/// holds picker continuations so neither a custom prompt nor the native Store surface is covered.
/// </summary>
internal sealed class StoreReviewPromptCoordinator
{
    private readonly StoreReviewPromptState _state;
    private readonly Func<bool> _hasOpenCaptureEditor;
    private readonly Func<bool> _isCaptureInProgress;
    private readonly Func<bool> _isExiting;
    private readonly Action<Window> _activateWindow;
    private readonly Action<string> _reportFailure;
    private readonly List<Action> _pendingContinuations = [];
    private StoreReviewPromptWindow? _window;

    public StoreReviewPromptCoordinator(
        StoreReviewPromptState state,
        Func<bool> hasOpenCaptureEditor,
        Func<bool> isCaptureInProgress,
        Func<bool> isExiting,
        Action<Window> activateWindow,
        Action<string> reportFailure)
    {
        _state = state;
        _hasOpenCaptureEditor = hasOpenCaptureEditor;
        _isCaptureInProgress = isCaptureInProgress;
        _isExiting = isExiting;
        _activateWindow = activateWindow;
        _reportFailure = reportFailure;
    }

    /// <summary>
    /// Evaluates the count only for an editor-window close. Capture completion paths never call
    /// this method, so reaching 30 while no editor is open cannot present the prompt.
    /// </summary>
    public void EditorWindowClosed(Action? continuation = null)
    {
        if (_isExiting())
        {
            _pendingContinuations.Clear();
            return;
        }

        if (continuation is not null)
        {
            _pendingContinuations.Add(continuation);
        }

        if (_window is not null)
        {
            return;
        }

        if (_isCaptureInProgress())
        {
            FlushContinuations();
            return;
        }

        if (_state.HasClaimedPrompt)
        {
            FlushContinuations();
            return;
        }

        if (!_state.IsEligible)
        {
            FlushContinuations();
            return;
        }

        if (_hasOpenCaptureEditor())
        {
            return;
        }

        // The state persists the claim before the window is created or activated.
        if (!_state.TryClaimPromptAfterEditorWindowClosed(
                isCaptureInProgress: _isCaptureInProgress(),
                hasOpenCaptureEditor: _hasOpenCaptureEditor()))
        {
            FlushContinuations();
            return;
        }

        try
        {
            var window = new StoreReviewPromptWindow(OnPromptClosed);
            _window = window;
            _activateWindow(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Store review prompt could not be shown: {ex}");
            _window = null;
            _reportFailure("Tiny Clips couldn't open the review prompt. It won't ask again.");
            FlushContinuations();
        }
    }

    private void OnPromptClosed()
    {
        _window = null;
        FlushContinuations();
    }

    private void FlushContinuations()
    {
        var continuations = _pendingContinuations.ToArray();
        _pendingContinuations.Clear();
        foreach (var continuation in continuations)
        {
            try
            {
                continuation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Store review prompt continuation failed: {ex}");
            }
        }
    }
}
#endif
