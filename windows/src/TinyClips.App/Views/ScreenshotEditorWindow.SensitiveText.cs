using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using TinyClips.Core.Capture;
using TinyClips.Core.Editing;
using Windows.Foundation;

namespace TinyClips.App;

public sealed partial class ScreenshotEditorWindow
{
    private enum SensitiveTextReviewDecision { Unreviewed, Approved, Dismissed }

    private IReadOnlyList<SensitiveTextSuggestion> _sensitiveTextSuggestions = Array.Empty<SensitiveTextSuggestion>();
    private SensitiveTextReviewDecision[] _sensitiveTextDecisions = [];
    private CancellationTokenSource? _sensitiveTextCancellation;
    private DocumentRevision _sensitiveTextRevision;
    private int _sensitiveTextIndex;
    private bool _hasSensitiveTextScan;
    private bool _isSensitiveTextReviewOpen;

    private void OnSensitiveTextReviewOpened(object? sender, object e)
    {
        _isSensitiveTextReviewOpen = true;
        if (!_hasSensitiveTextScan) _ = FindSensitiveTextAsync();
        else UpdateSensitiveTextReview();
    }

    private void OnSensitiveTextReviewClosed(object? sender, object e)
    {
        _isSensitiveTextReviewOpen = false;
        // Closing the review never changes the document. Hide highlights until it is reopened.
        Canvas.SetSensitiveTextHighlights(Array.Empty<Rect>());
    }

    private void ClearSensitiveTextReview()
    {
        // The worker owns/disposes its CTS after native OCR has drained.
        _sensitiveTextCancellation?.Cancel();
        _sensitiveTextCancellation = null;
        _sensitiveTextSuggestions = Array.Empty<SensitiveTextSuggestion>();
        _sensitiveTextDecisions = [];
        _sensitiveTextIndex = 0;
        Canvas.SetSensitiveTextHighlights(Array.Empty<Rect>());
        SensitiveTextApply.IsEnabled = false;
        SensitiveTextApply.Content = "Apply approved redactions (0)";
        SensitiveTextReviewPanel.Visibility = Visibility.Collapsed;
        SensitiveTextProgress.Visibility = Visibility.Collapsed;
        SensitiveTextFindAgain.IsEnabled = true;
        SensitiveTextValue.Text = string.Empty;
    }

    private void OnSensitiveTextDocumentChanged(object? sender, EventArgs e)
    {
        ClearSensitiveTextReview();
        if (_hasSensitiveTextScan)
            SetSensitiveTextStatus("The image changed. Find again to review fresh suggestions.");
    }

    private async Task FindSensitiveTextAsync()
    {
        if (_isClosed || _sensitiveTextCancellation is not null) return;
        Canvas.CancelActiveInteraction();
        ClearSensitiveTextReview();
        _hasSensitiveTextScan = true;
        if (!_controller.CanExport)
        {
            SetSensitiveTextStatus("The image isn't ready yet. Try Find again after it has loaded.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _sensitiveTextCancellation = cancellation;
        var token = cancellation.Token;
        SetSensitiveTextStatus("Finding likely email addresses, phone numbers and IP addresses…");
        SensitiveTextProgress.Visibility = Visibility.Visible;
        SensitiveTextFindAgain.IsEnabled = false;
        try
        {
            // Native-size, no export frame/padding/scale. Existing annotations and redactions are
            // included, so OCR never inspects text already covered by an opaque redaction.
            using var snapshot = _controller.CaptureRenderSnapshot(includeBackground: false);
            _sensitiveTextRevision = snapshot.Revision;
            using var documentRegistration = snapshot.DocumentCancellation.Register(cancellation.Cancel);
            using var bitmap = await _controller.RenderToBitmapAsync(snapshot, token);
            var suggestions = await Task.Run(() => OcrService.FindSensitiveTextAsync(bitmap, token), token);
            if (_isClosed || !ReferenceEquals(_sensitiveTextCancellation, cancellation) ||
                !_controller.IsCurrent(snapshot.Revision, token)) return;
            _sensitiveTextSuggestions = suggestions;
            _sensitiveTextDecisions = new SensitiveTextReviewDecision[suggestions.Count];
            SetSensitiveTextStatus(suggestions.Count == 0
                ? "No likely sensitive text found. OCR and patterns can miss text; check the image yourself."
                : $"{suggestions.Count} suggestions. Approve or dismiss each, then apply approved redactions.");
            UpdateSensitiveTextReview();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Edits, Reset, crop or closure invalidated this scan.
        }
        catch (Exception)
        {
            // Never log OCR values or native exception details that may contain image content.
            if (!_isClosed && ReferenceEquals(_sensitiveTextCancellation, cancellation))
                SetSensitiveTextStatus("Couldn't find sensitive text. Check that an OCR language is installed in Windows Settings → Time & language → Language & region, then try again. You can still redact manually.");
        }
        finally
        {
            if (ReferenceEquals(_sensitiveTextCancellation, cancellation))
            {
                _sensitiveTextCancellation = null;
                if (!_isClosed)
                {
                    SensitiveTextProgress.Visibility = Visibility.Collapsed;
                    SensitiveTextFindAgain.IsEnabled = true;
                }
            }
        }
    }

    private void UpdateSensitiveTextReview()
    {
        var hasSuggestions = _sensitiveTextSuggestions.Count > 0;
        SensitiveTextReviewPanel.Visibility = hasSuggestions ? Visibility.Visible : Visibility.Collapsed;
        var approved = _sensitiveTextDecisions.Count(d => d == SensitiveTextReviewDecision.Approved);
        SensitiveTextApply.IsEnabled = approved > 0 && _controller.IsCurrent(_sensitiveTextRevision);
        SensitiveTextApply.Content = $"Apply approved redactions ({approved})";
        if (!hasSuggestions) return;
        var suggestion = _sensitiveTextSuggestions[_sensitiveTextIndex];
        SensitiveTextPosition.Text = $"{_sensitiveTextIndex + 1} of {_sensitiveTextSuggestions.Count} · {suggestion.Kind}";
        SensitiveTextValue.Text = suggestion.Text;
        SensitiveTextDecision.Text = _sensitiveTextDecisions[_sensitiveTextIndex] switch
        {
            SensitiveTextReviewDecision.Approved => "Approved — will be covered in black when applied",
            SensitiveTextReviewDecision.Dismissed => "Dismissed — will not be changed",
            _ => "Not reviewed — will not be changed",
        };
        AutomationProperties.SetName(SensitiveTextDecision,
            $"{SensitiveTextPosition.Text}. {suggestion.Text}. {SensitiveTextDecision.Text}");
        AnnounceSensitiveTextChange(SensitiveTextDecision);
        SensitiveTextPrevious.IsEnabled = _sensitiveTextIndex > 0;
        SensitiveTextNext.IsEnabled = _sensitiveTextIndex < _sensitiveTextSuggestions.Count - 1;
        if (_isSensitiveTextReviewOpen)
        {
            var bounds = _sensitiveTextSuggestions
                .Where((_, index) => _sensitiveTextDecisions[index] != SensitiveTextReviewDecision.Dismissed)
                .Select(s => s.Bounds).ToArray();
            Canvas.SetSensitiveTextHighlights(bounds, suggestion.Bounds);
        }
    }

    private void OnSensitiveTextPrevious(object sender, RoutedEventArgs e)
    {
        if (_sensitiveTextIndex > 0) _sensitiveTextIndex--;
        UpdateSensitiveTextReview();
    }

    private void OnSensitiveTextNext(object sender, RoutedEventArgs e)
    {
        if (_sensitiveTextIndex + 1 < _sensitiveTextSuggestions.Count) _sensitiveTextIndex++;
        UpdateSensitiveTextReview();
    }

    private void OnSensitiveTextApprove(object sender, RoutedEventArgs e) => DecideSensitiveText(SensitiveTextReviewDecision.Approved);
    private void OnSensitiveTextDismiss(object sender, RoutedEventArgs e) => DecideSensitiveText(SensitiveTextReviewDecision.Dismissed);

    private void DecideSensitiveText(SensitiveTextReviewDecision decision)
    {
        if (_sensitiveTextSuggestions.Count == 0) return;
        _sensitiveTextDecisions[_sensitiveTextIndex] = decision;
        UpdateSensitiveTextReview();
    }

    private void OnSensitiveTextFindAgain(object sender, RoutedEventArgs e) => _ = FindSensitiveTextAsync();

    private void OnSensitiveTextApply(object sender, RoutedEventArgs e)
    {
        var approved = _sensitiveTextSuggestions
            .Where((_, index) => _sensitiveTextDecisions[index] == SensitiveTextReviewDecision.Approved).ToArray();
        Canvas.CancelActiveInteraction();
        // Controller validates the exact revision again before committing any annotation.
        if (_controller.ApplySensitiveTextRedactions(_sensitiveTextRevision, approved))
        {
            SetSensitiveTextStatus($"Applied {approved.Length} opaque black redactions. Use Undo to remove one at a time.");
            // Return keyboard ownership to the editor so Ctrl+Z works immediately. Native
            // flyout dismissal restores focus to its invoking command.
            SensitiveTextFlyout.Hide();
        }
    }

    private void SetSensitiveTextStatus(string message)
    {
        SensitiveTextStatus.Text = message;
        AnnounceSensitiveTextChange(SensitiveTextStatus);
    }

    private void AnnounceSensitiveTextChange(FrameworkElement element)
    {
        if (!_isSensitiveTextReviewOpen || _isClosed) return;
        var peer = FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
