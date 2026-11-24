using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using TinyClips.Core.Services;

namespace TinyClips.App;

public partial class App
{
#if TINYCLIPS_STORE_BUILD
    private StoreReviewPromptCoordinator? _storeReviewPromptCoordinator;
#endif

    /// <summary>
    /// Runs review eligibility only after an actual capture-editor window has closed. Capture
    /// completion paths never call this method; direct builds continue exactly as before.
    /// </summary>
    private void EditorWindowClosed(Action? continuation = null)
    {
#if TINYCLIPS_STORE_BUILD
        if (!_isExiting)
        {
            _storeReviewPromptCoordinator ??= new StoreReviewPromptCoordinator(
                Services.GetRequiredService<StoreReviewPromptState>(),
                HasOpenCaptureEditor,
                HasActiveCaptureWork,
                () => _isExiting,
                ActivateWindowToForeground,
                OpenQuickBugReportWindow,
                ShowMessageNotification);
            _storeReviewPromptCoordinator.EditorWindowClosed(continuation);
            return;
        }
#endif

        continuation?.Invoke();
    }

#if TINYCLIPS_STORE_BUILD
    private bool HasOpenCaptureEditor() =>
        _editorWindows.Count > 0
        || _trimmerWindow is not null
        || (_studioWindows?.HasOpenEditors ?? false);

    private bool HasActiveCaptureWork() =>
        _captureFlowCts is not null
        || _scrollingPanel is not null
        || _processingIndicator is not null
        || IsAnyRecordingActive();

    private void OnStudioEditorClosed(object? sender, EventArgs e)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            if (!_isExiting)
            {
                EditorWindowClosed();
            }
        });
    }
#endif
}
