namespace TinyClips.Core.Studio;

// Captions. An index is a cue's place in Project.Captions.Cues. What this app writes there is in
// time order, and what an agent or another app wrote need not be. Each edit is one undo step.
public sealed partial class StudioEditorModel
{
    /// <summary>Whether the captions are drawn into the picture (section 15.4 of the project format).</summary>
    public void SetCaptionsDrawn(bool isDrawn) => SetCaptionOverlay(overlay => overlay with { Enabled = isDrawn });

    public void SetCaptionSize(StudioCaptionSize size) => SetCaptionOverlay(overlay => overlay with { Size = size });

    public void SetCaptionPosition(StudioCaptionPosition position) => SetCaptionOverlay(overlay => overlay with { Position = position });

    public void SetCaptionAppearance(StudioCaptionAppearance appearance) => SetCaptionOverlay(overlay => overlay with { Appearance = appearance });

    private void SetCaptionOverlay(Func<StudioCaptionOverlay, StudioCaptionOverlay> change) =>
        Mutate(project => project with { Overlays = project.Overlays with { Captions = change(project.Overlays.Captions) } });

    /// <summary>What a list of the captions shows: the places of the cues in time order.</summary>
    public int[] GetCaptionIndexesInOrder()
    {
        var cues = Project.Captions.Cues;
        return Enumerable.Range(0, cues.Length)
            .OrderBy(index => cues[index].Start)
            .ThenBy(index => cues[index].End)
            .ToArray();
    }

    /// <summary>Puts <paramref name="cues"/> in place of the captions: section 15.5 of the project format.</summary>
    public void ReplaceCaptions(IEnumerable<StudioCaptionCue> cues)
    {
        var ordered = StudioCaptionQuality.InOrder(cues);
        Mutate(project => project with { Captions = project.Captions with { Language = null, Cues = ordered } });
    }

    /// <summary>Puts what was heard in a recording in place of the captions, with its language.</summary>
    public void SetGeneratedCaptions(string? language, IEnumerable<StudioCaptionCue> cues)
    {
        var ordered = StudioCaptionQuality.InOrder(cues);
        Mutate(project => project with { Captions = project.Captions with { Language = language, Cues = ordered } });
    }

    /// <summary>
    /// Changes what a cue says. Its words go with the text they were of. Text that cleans to what
    /// the cue already says changes nothing.
    /// </summary>
    /// <returns>False when there is no such cue or nothing changed.</returns>
    public bool SetCaptionText(int index, string text)
    {
        var cues = Project.Captions.Cues;
        if (index < 0 || index >= cues.Length)
        {
            return false;
        }

        var cleaned = StudioCaptionResolver.CleanText(text);
        if (string.Equals(cleaned, StudioCaptionResolver.CleanText(cues[index].Text), StringComparison.Ordinal))
        {
            return false;
        }

        var updated = (StudioCaptionCue[])cues.Clone();
        updated[index] = updated[index] with { Text = cleaned, Words = [] };
        Mutate(project => project with { Captions = project.Captions with { Cues = updated } });
        return true;
    }

    /// <summary>Takes a cue out. False when there is no such cue.</summary>
    public bool RemoveCaption(int index)
    {
        var cues = Project.Captions.Cues;
        if (index < 0 || index >= cues.Length)
        {
            return false;
        }

        var updated = cues.Where((_, place) => place != index).ToArray();
        Mutate(project => project with { Captions = project.Captions with { Cues = updated } });
        return true;
    }

    /// <summary>Makes the safe fixes of section 15.6, and says how many things changed.</summary>
    public int FixCaptions()
    {
        var result = StudioCaptionQuality.Fix(Project.Captions.Cues, SourceDuration);
        if (result.Applied > 0)
        {
            Mutate(project => project with { Captions = project.Captions with { Cues = result.Cues } });
        }

        return result.Applied;
    }
}
