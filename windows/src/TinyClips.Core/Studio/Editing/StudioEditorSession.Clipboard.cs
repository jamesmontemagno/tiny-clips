using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Studio.Editing;

/// <summary>Opaque destination intent held while the app awaits an external clipboard provider.</summary>
public sealed class StudioAnnotationPasteIntent
{
    internal StudioAnnotationPasteIntent() { }
    internal StudioEditorSession Owner { get; init; } = null!;
    internal StudioEditorModel Model { get; init; } = null!;
    internal StudioEditableState State { get; init; } = null!;
    internal string ProjectId { get; init; } = string.Empty;
    internal string? ClipId { get; init; }
    internal long Generation { get; init; }
    internal double Playhead { get; init; }
    internal double DisplayedSourceTime { get; init; }
}

public sealed partial class StudioEditorSession
{
    private long _annotationPasteGeneration;

    public bool CanUseAnnotationClipboard => IsEditable && !IsInGesture && !IsPlaying && !IsGeneratingCaptions;

    public bool CanCopyAnnotation => CanUseAnnotationClipboard && SelectedAnnotation is { } annotation
        && StudioAnnotationClipboard.IsSupportedKind(annotation.Kind);

    public byte[] CopySelectedAnnotation()
    {
        if (!CanCopyAnnotation || SelectedAnnotationIndex is not { } index || Model is not { } model)
        {
            throw new InvalidOperationException("Select an annotation to copy when the editor is idle.");
        }
        return StudioAnnotationClipboard.Encode(model.CopyAnnotation(index));
    }

    public StudioAnnotationPasteIntent? CaptureAnnotationPasteIntent()
    {
        if (!CanUseAnnotationClipboard || Model is not { } model)
        {
            return null;
        }
        var rate = StudioPreviewTimeMath.NormalizeFrameRate(model.Project.Sources.Screen.FrameRate);
        var frame = StudioPreviewTimeMath.ClampFrame(StudioPreviewTimeMath.FrameAt(Playhead, rate),
            StudioPreviewTimeMath.FrameCount(model.SourceDuration, rate));
        return new()
        {
            Owner = this,
            Model = model,
            State = model.EditableState,
            ProjectId = ProjectId,
            ClipId = model.SelectedClipId,
            Generation = _annotationPasteGeneration,
            Playhead = Playhead,
            DisplayedSourceTime = Math.Min(Playhead, StudioPreviewTimeMath.FrameStart(frame, rate)),
        };
    }

    public StudioAnnotationEditResult PasteAnnotation(StudioAnnotationPasteIntent intent, StudioAnnotationTransfer transfer)
    {
        if (!CanUseAnnotationClipboard || !ReferenceEquals(intent.Owner, this) || !ReferenceEquals(intent.Model, Model)
            || intent.ProjectId != ProjectId || intent.ClipId != Model?.SelectedClipId
            || intent.Generation != _annotationPasteGeneration || intent.Playhead != Playhead
            || !intent.State.ContentEquals(Model!.EditableState))
        {
            throw new InvalidOperationException("The paste destination changed. Paste again when the editor is idle.");
        }
        var result = new StudioAnnotationEditResult(false, null);
        string? pastedId = null;
        EditAndSelect(model =>
        {
            result = model.PasteAnnotation(transfer, intent.DisplayedSourceTime);
            if (result.Changed && result.Index is { } index)
            {
                pastedId = model.Project.Overlays.Annotations[index].Id;
                return EditSelection.Annotation(index);
            }
            return null;
        });
        // Feedback must describe the committed editor, never just the mutation callback's result.
        if (!result.Changed)
        {
            return new(false, null);
        }
        return result.Index is { } place && pastedId is not null
            && Model?.Project.Overlays.Annotations.ElementAtOrDefault(place)?.Id == pastedId
            && SelectedAnnotation?.Id == pastedId
            ? result : throw new InvalidOperationException("The annotation could not be committed. Paste again when the editor is idle.");
    }
}
