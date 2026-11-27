namespace TinyClips.Core.Studio;

// Edits to the order, joins and extent of the clips. Each is one undo step. A single recording
// becomes a one-clip timeline the first time it is changed this way.
public sealed partial class StudioEditorModel
{
    /// <summary>
    /// Whether a clip can be split at <paramref name="sourceTime"/> of the selected clip: the time
    /// is inside what plays, so both halves have something to show.
    /// </summary>
    public bool CanSplitClipAt(double sourceTime) =>
        double.IsFinite(sourceTime)
        && StudioTimelineEditing.ToTimelineProject(Manifest).Timeline is { } timeline
        && SplitTimeline(timeline, sourceTime) is not null;

    /// <summary>Splits the selected clip at <paramref name="sourceTime"/> and selects the second half.</summary>
    public bool SplitClip(double sourceTime) =>
        double.IsFinite(sourceTime) && EditTimeline(timeline => SplitTimeline(timeline, sourceTime));

    private (StudioTimeline Timeline, string SelectClipId)? SplitTimeline(StudioTimeline timeline, double sourceTime)
    {
        var clipId = SelectedClipId ?? timeline.Clips[0].Id;
        var index = Array.FindIndex(timeline.Clips, clip => clip.Id == clipId);
        return index >= 0 && StudioTimelineEditing.Split(timeline, clipId, sourceTime) is { } split
            ? (split, split.Clips[index + 1].Id)
            : null;
    }

    /// <summary>Moves a clip to <paramref name="index"/> in the order that results, and selects it.</summary>
    public bool MoveClip(string clipId, int index) =>
        EditTimeline(timeline =>
        {
            var from = Array.FindIndex(timeline.Clips, clip => clip.Id == clipId);
            if (from < 0 || index < 0 || index >= timeline.Clips.Length || index == from)
            {
                return null;
            }

            return StudioTimelineEditing.Move(timeline, clipId, index) is { } moved ? (moved, clipId) : null;
        });

    /// <summary>Takes a clip out of the timeline. Its recording stays in the project. The last clip stays.</summary>
    public bool RemoveClip(string clipId) =>
        EditTimeline(timeline =>
        {
            var index = Array.FindIndex(timeline.Clips, clip => clip.Id == clipId);
            if (index < 0 || StudioTimelineEditing.Remove(timeline, clipId) is not { } removed)
            {
                return null;
            }

            var keeping = SelectedClipId;
            var next = keeping is not null && keeping != clipId && removed.Clips.Any(clip => clip.Id == keeping)
                ? keeping
                : removed.Clips[Math.Min(index, removed.Clips.Length - 1)].Id;
            return (removed, next);
        });

    /// <summary>Sets how a clip begins after the one before it. The first clip has no join.</summary>
    public bool SetClipTransition(string clipId, StudioClipTransition transition) =>
        EditTimeline(timeline =>
        {
            var clip = timeline.Clips.FirstOrDefault(item => item.Id == clipId);
            if (clip is null || clip.TransitionIn == transition
                || StudioTimelineEditing.SetTransition(timeline, clipId, transition) is not { } updated)
            {
                return null;
            }

            return (updated, clipId);
        });

    /// <summary>The join before a clip, or null for the first clip.</summary>
    public StudioClipTransition? GetClipTransition(string clipId)
    {
        var clips = Manifest.Timeline?.Clips;
        var index = clips is null ? -1 : Array.FindIndex(clips, clip => clip.Id == clipId);
        return index > 0 ? clips![index].TransitionIn : null;
    }

    private bool EditTimeline(Func<StudioTimeline, (StudioTimeline Timeline, string SelectClipId)?> change)
    {
        var project = StudioTimelineEditing.ToTimelineProject(Manifest);
        if (project.Timeline is not { } timeline || change(timeline) is not { } result)
        {
            return false;
        }

        try
        {
            return AdoptManifest(project with { Timeline = result.Timeline }, result.SelectClipId);
        }
        catch (StudioProjectInvalidException)
        {
            return false;
        }
    }
}
