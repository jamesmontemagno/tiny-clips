namespace TinyClips.Core.Studio.Editing;

/// <summary>What came of importing a caption file.</summary>
/// <param name="Count">How many cues the project has now.</param>
/// <param name="Skipped">How many blocks of the file could not be used.</param>
public readonly record struct StudioCaptionImportResult(int Count, int Skipped);

// Captions. One cue can be selected for its text to be edited. That is apart from the timeline's
// selection: captions have no lane. Selecting one lets go of a selected zoom, cut, or speed change,
// so that Delete does not take away something that is no longer on show. An index is a cue's place
// in Project.Captions.Cues, and it lasts for as long as that list does: undo, redo, and an edit
// from elsewhere each make another list, and let go of the selection.
public sealed partial class StudioEditorSession
{
    private int? _selectedCaptionIndex;
    private StudioCaptionCue[]? _selectedCaptionList;

    /// <summary>The cue whose text the inspector shows, or null.</summary>
    public int? SelectedCaptionIndex =>
        _selectedCaptionIndex is { } index
        && Model is { } model
        && ReferenceEquals(model.Project.Captions.Cues, _selectedCaptionList)
        && index >= 0
        && index < model.Project.Captions.Cues.Length
            ? index
            : null;

    public StudioCaptionCue? SelectedCaption => SelectedCaptionIndex is { } index ? Model?.Project.Captions.Cues[index] : null;

    /// <summary>The captions the video has, in output time, as the files and the drawn captions get them.</summary>
    public StudioResolvedCaption[] GetResolvedCaptions() =>
        Model is { } model ? StudioCaptionResolver.Resolve(model.Project) : [];

    /// <summary>What the checks find in the captions now.</summary>
    public StudioCaptionFinding[] CheckCaptions() =>
        Model is { } model ? StudioCaptionQuality.Check(model.Project.Captions.Cues, model.SourceDuration) : [];

    /// <summary>
    /// Selects a cue and moves the playhead to where it starts, or lets go with null. False when
    /// there is no such cue.
    /// </summary>
    public bool SelectCaption(int? index)
    {
        if (Model is not { } model)
        {
            return false;
        }

        var cues = model.Project.Captions.Cues;
        if (index is not { } place || place < 0 || place >= cues.Length)
        {
            _selectedCaptionIndex = null;
            _selectedCaptionList = null;
            RaiseChanged(StudioEditorChanges.Selection);
            return false;
        }

        _selectedCaptionIndex = place;
        _selectedCaptionList = cues;
        SelectNothing();
        RaiseChanged(StudioEditorChanges.Selection);
        Scrub(cues[place].Start);
        return true;
    }

    /// <summary>Changes what the selected cue says. It stays selected.</summary>
    public void SetSelectedCaptionText(string text)
    {
        if (SelectedCaptionIndex is not { } index)
        {
            return;
        }

        Edit(model =>
        {
            if (model.SetCaptionText(index, text))
            {
                _selectedCaptionList = model.Project.Captions.Cues;
            }
        });
    }

    /// <summary>Takes the selected cue out.</summary>
    public void RemoveSelectedCaption()
    {
        if (SelectedCaptionIndex is { } index)
        {
            _selectedCaptionIndex = null;
            _selectedCaptionList = null;
            Edit(model => model.RemoveCaption(index));
        }
    }

    /// <summary>
    /// Puts the cues of a caption file in place of the captions, as one edit. A file with no cues
    /// changes nothing.
    /// </summary>
    /// <returns>What came of it, or null when the project cannot be edited just now.</returns>
    public StudioCaptionImportResult? ImportCaptions(string text, StudioCaptionTiming timing)
    {
        if (!IsEditable || Model is not { } model)
        {
            return null;
        }

        var contents = StudioCaptionFiles.Read(text, timing, model.TimeMap);
        if (contents.Cues.Length > 0)
        {
            _selectedCaptionIndex = null;
            _selectedCaptionList = null;
            Edit(editing => editing.ReplaceCaptions(contents.Cues));
        }

        return new StudioCaptionImportResult(contents.Cues.Length, contents.SkippedBlocks);
    }

    /// <summary>Makes the safe fixes as one edit, and says how many things changed.</summary>
    public int FixCaptions()
    {
        var applied = 0;
        _selectedCaptionIndex = null;
        _selectedCaptionList = null;
        Edit(model => applied = model.FixCaptions());
        return applied;
    }
}
