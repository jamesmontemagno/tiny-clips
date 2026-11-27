using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.Controls.Studio;
using TinyClips.App.Services.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Views.Studio;

// Videos that are added to the timeline: chosen as files, picked from the Clips Library, recorded
// as another segment, or dropped on the clip row. The strip says which and where; the questions
// and the system's pickers are here, and the view model does the adding as one edit.
public sealed partial class StudioWindow
{
    // Ends a question, a take or the copying of a video when the window closes for good.
    private readonly CancellationTokenSource _clipsLifetime = new();

    private void ConnectClipStrip()
    {
        var strip = _timeline.ClipStrip;
        strip.AddRequested += OnClipAddRequested;
        strip.CanRecordSegment = () => !_isClosed && ViewModel.CanAddClips && _projects.CanRecordSegment;
    }

    private void OnClipAddRequested(object? sender, StudioClipAddRequest request) => _ = AddClipsAsync(request);

    // Never fails: a video that cannot be added is said so in the message bar, and the timeline
    // is as it was. A question that was not answered is not an error.
    private async Task AddClipsAsync(StudioClipAddRequest request)
    {
        if (_isClosed || _isPromptOpen || !ViewModel.CanAddClips)
        {
            return;
        }

        try
        {
            await ViewModel.AddClipsAsync(token => AcquireClipsAsync(request, token), request.Placement, _clipsLifetime.Token);
        }
        catch (ObjectDisposedException)
        {
            // The window closed under the question.
        }
    }

    private async Task<IReadOnlyList<StudioTimelineImport>> AcquireClipsAsync(
        StudioClipAddRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Source == StudioClipAddSource.Dropped)
        {
            return await StudioWindowService.CaptureFileImportsAsync(request.Paths ?? [], cancellationToken);
        }

        // The window is not asked anything else while the question is open, and the focus
        // returns to where it was when it is answered.
        _isPromptOpen = true;
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as Control;
        try
        {
            switch (request.Source)
            {
                case StudioClipAddSource.Files:
                    var paths = await _projects.ChooseVideoFilesAsync(this);
                    return await StudioWindowService.CaptureFileImportsAsync(paths, cancellationToken);

                case StudioClipAddSource.Library:
                    return Single(await _projects.ChooseLibraryVideoAsync(this, ViewModel, cancellationToken));

                default:
                    return Single(await _projects.RecordSegmentAsync(this, ViewModel, cancellationToken));
            }
        }
        finally
        {
            _isPromptOpen = false;
            if (!_isClosed && _isActive)
            {
                focused?.Focus(FocusState.Programmatic);
            }
        }
    }

    private static IReadOnlyList<StudioTimelineImport> Single(StudioTimelineImport? import) =>
        import is null ? Array.Empty<StudioTimelineImport>() : new[] { import };
}
