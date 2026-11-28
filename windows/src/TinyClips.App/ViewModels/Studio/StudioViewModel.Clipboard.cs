using TinyClips.App.Services.Studio;
using TinyClips.Core.Studio;

namespace TinyClips.App.ViewModels.Studio;

public sealed partial class StudioViewModel
{
    private IStudioAnnotationClipboardService? _annotationClipboard;
    private bool _hasAnnotationClipboard;
    private bool _isAnnotationClipboardBusy;
    private string _annotationClipboardStatus = string.Empty;

    internal Func<bool>? CanUseWindowClipboard { get; set; }

    public bool CanCopyAnnotation => _annotationClipboard is not null && !_isAnnotationClipboardBusy
        && _session.CanCopyAnnotation && (CanUseWindowClipboard?.Invoke() ?? true);

    public bool CanPasteAnnotation => _hasAnnotationClipboard && !_isAnnotationClipboardBusy
        && _session.CanUseAnnotationClipboard && (CanUseWindowClipboard?.Invoke() ?? true);

    public string AnnotationClipboardStatus => _annotationClipboardStatus;

    internal void AttachAnnotationClipboard(IStudioAnnotationClipboardService clipboard)
    {
        DetachAnnotationClipboard();
        _annotationClipboard = clipboard;
        clipboard.ContentChanged += OnAnnotationClipboardChanged;
        RefreshAnnotationClipboard();
    }

    internal void DetachAnnotationClipboard()
    {
        if (_annotationClipboard is { } clipboard)
        {
            clipboard.ContentChanged -= OnAnnotationClipboardChanged;
            clipboard.Dispose();
            _annotationClipboard = null;
        }
        _hasAnnotationClipboard = false;
        RaiseAnnotationClipboardAvailability();
    }

    private void OnAnnotationClipboardChanged(object? sender, EventArgs args) =>
        _post(() =>
        {
            if (!_session.IsClosed && ReferenceEquals(sender, _annotationClipboard))
            {
                RefreshAnnotationClipboard();
            }
        });

    internal void RefreshAnnotationClipboard()
    {
        try
        {
            _hasAnnotationClipboard = _annotationClipboard?.HasAnnotation ?? false;
        }
        catch (Exception exception)
        {
            _hasAnnotationClipboard = false;
            SetAnnotationClipboardStatus($"Could not inspect the clipboard: {exception.Message}", isError: true);
        }
        RaiseAnnotationClipboardAvailability();
    }

    private void RaiseAnnotationClipboardAvailability()
    {
        OnPropertyChanged(nameof(CanCopyAnnotation));
        OnPropertyChanged(nameof(CanPasteAnnotation));
    }

    public async Task CopyAnnotationAsync()
    {
        if (!CanCopyAnnotation || _annotationClipboard is not { } clipboard)
        {
            return;
        }
        _isAnnotationClipboardBusy = true;
        RaiseAnnotationClipboardAvailability();
        try
        {
            var bytes = _session.CopySelectedAnnotation();
            await clipboard.WriteAsync(bytes);
            if (!_session.IsClosed && ReferenceEquals(clipboard, _annotationClipboard))
            {
                SetAnnotationClipboardStatus("Annotation copied.");
            }
        }
        catch (Exception exception)
        {
            if (!_session.IsClosed && ReferenceEquals(clipboard, _annotationClipboard))
            {
                SetAnnotationClipboardStatus($"Could not copy the annotation: {exception.Message}", isError: true);
            }
        }
        finally
        {
            _isAnnotationClipboardBusy = false;
            RaiseAnnotationClipboardAvailability();
        }
    }

    public async Task PasteAnnotationAsync()
    {
        if (!CanPasteAnnotation || _annotationClipboard is not { } clipboard
            || _session.CaptureAnnotationPasteIntent() is not { } intent)
        {
            return;
        }
        _isAnnotationClipboardBusy = true;
        RaiseAnnotationClipboardAvailability();
        try
        {
            var bytes = await clipboard.ReadAsync();
            if (_session.IsClosed || !ReferenceEquals(clipboard, _annotationClipboard))
            {
                return;
            }
            if (!(CanUseWindowClipboard?.Invoke() ?? true))
            {
                SetAnnotationClipboardStatus("The paste destination changed. Paste again when the editor is idle.", isError: true);
                return;
            }
            if (bytes is null)
            {
                _hasAnnotationClipboard = false;
                SetAnnotationClipboardStatus("The clipboard does not contain a Tiny Clips annotation.");
                return;
            }
            var transfer = StudioAnnotationClipboard.Decode(bytes);
            var result = _session.PasteAnnotation(intent, transfer);
            if (result.Changed)
            {
                SetAnnotationClipboardStatus(IsAnnotationNotDrawn
                    ? "Annotation pasted. It is not drawn at this playhead; check its attachment and visibility."
                    : "Annotation pasted.");
            }
            else
            {
                SetAnnotationClipboardStatus("There is no room for an annotation here.", isError: true);
            }
        }
        catch (Exception exception)
        {
            if (!_session.IsClosed && ReferenceEquals(clipboard, _annotationClipboard))
            {
                SetAnnotationClipboardStatus($"Could not paste the annotation: {exception.Message}", isError: true);
            }
        }
        finally
        {
            _isAnnotationClipboardBusy = false;
            RaiseAnnotationClipboardAvailability();
        }
    }

    private void SetAnnotationClipboardStatus(string message, bool isError = false)
    {
        _annotationClipboardStatus = message;
        OnPropertyChanged(nameof(AnnotationClipboardStatus));
        if (isError)
        {
            ShowError(message);
        }
        else
        {
            Announce(message, AnnotationActivityId, StudioAnnouncementKind.Completed);
        }
    }
}
