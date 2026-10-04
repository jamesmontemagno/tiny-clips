namespace TinyClips.Core.Studio.Editing;

// Cuts. An index is a cut's place in Project.Edits.Cuts, which is kept in time order. Every edit
// answers with the place the cut has afterwards, or none when it is gone. An edit that is refused,
// because the project cannot be edited just now, leaves the cut where it was.
//
// One cut can be selected, or one zoom, never both: selecting a cut lets go of the zoom. An edit
// to the selected cut takes the selection with it, and so does adding a cut. Through every other
// edit, and undo and redo, the selection follows its cut as StudioEditorModel.FindCutFollowing
// finds it, or lets go when the cut is gone.
public sealed partial class StudioEditorSession
{
    private int? _selectedCutIndex;

    // Where playback was last sent to get over a cut. Playback only goes forward, so it comes to
    // the same cut again only after Play has been pressed, which forgets this.
    private double? _cutSkipTarget;

    /// <summary>
    /// The cut the inspector shows, as its place in <see cref="StudioEdits.Cuts"/>, or null. It
    /// stays on its cut while edits, undo and redo change the list around it.
    /// </summary>
    public int? SelectedCutIndex =>
        _selectedCutIndex is { } index && Model is { } model && index >= 0 && index < model.Project.Edits.Cuts.Length
            ? index
            : null;

    public StudioTimeRange? SelectedCut => SelectedCutIndex is { } index ? Model?.Project.Edits.Cuts[index] : null;

    /// <summary>How many cuts the project has. 0 until the project has been read.</summary>
    public int CutCount => Model?.Project.Edits.Cuts.Length ?? 0;

    /// <summary>
    /// Whether Cut has a cut to answer with at the playhead: one fits there, or one is already
    /// there to select.
    /// </summary>
    public bool CanAddCutAtPlayhead => IsEditable && Model is { } model && model.CanAddCut(Playhead);

    /// <summary>The cut that contains a source time, or null.</summary>
    public int? GetCutIndexAt(double sourceTime) => Model?.GetCutIndexAt(sourceTime);

    /// <summary>
    /// Selects a cut, or none with null or a place that has no cut. The playhead stays. A selected
    /// zoom is let go when a cut is selected.
    /// </summary>
    public void SelectCut(int? index)
    {
        var zoomBefore = SelectedZoomIndex;
        var cutBefore = SelectedCutIndex;
        _selectedCutIndex = index;
        _selectedCutIndex = SelectedCutIndex;
        if (_selectedCutIndex is not null)
        {
            _selectedZoomIndex = null;
        }

        if (SelectedZoomIndex != zoomBefore || SelectedCutIndex != cutBefore)
        {
            RaiseChanged(StudioEditorChanges.Selection);
        }
    }

    /// <summary>
    /// Selects the cut after the selected one and moves the playhead to where it starts. With
    /// nothing selected, the cut at the playhead or the first one after it. False when there is none.
    /// </summary>
    public bool SelectNextCut() => SelectAndShowCut(Model?.GetCutIndexAfter(SelectedCutIndex, Playhead));

    /// <summary>
    /// Selects the cut before the selected one and moves the playhead to where it starts. With
    /// nothing selected, the cut at the playhead or the last one before it. False when there is none.
    /// </summary>
    public bool SelectPreviousCut() => SelectAndShowCut(Model?.GetCutIndexBefore(SelectedCutIndex, Playhead));

    /// <summary>
    /// Adds a cut that starts at a source time, and selects it. Where a cut already is, that one
    /// is selected instead.
    /// </summary>
    public StudioCutEditResult AddCut(double sourceTime)
    {
        var result = new StudioCutEditResult(false, null);
        EditAndSelect(model =>
        {
            result = model.AddCut(sourceTime);
            return result.Index is { } index ? EditSelection.Cut(index) : null;
        });
        return result;
    }

    /// <summary>
    /// Adds a cut at the playhead and selects it. The playhead stays where the cut starts, on the
    /// first picture the video leaves out.
    /// </summary>
    public StudioCutEditResult AddCutAtPlayhead() => AddCut(Playhead);

    /// <summary>Deletes a cut, which puts its stretch back into the video.</summary>
    public StudioCutEditResult RemoveCut(int index) => EditCut(index, model => model.RemoveCut(index));

    /// <summary>Deletes the selected cut. Unchanged, with no index, when none is selected.</summary>
    public StudioCutEditResult RemoveSelectedCut() =>
        SelectedCutIndex is { } index ? RemoveCut(index) : new StudioCutEditResult(false, null);

    /// <summary>
    /// Moves a cut's start, from the lane or a step button, and shows the picture there, which is
    /// the first one the video leaves out.
    /// </summary>
    public StudioCutEditResult SetCutStart(int index, double sourceTime)
    {
        var result = EditCut(index, model => model.SetCutStart(index, sourceTime));
        if (IsEditable && result.Index is { } place && Model is { } model)
        {
            Scrub(model.Project.Edits.Cuts[place].Start);
        }

        return result;
    }

    /// <summary>
    /// Moves a cut's end, from the lane or a step button, and shows the picture there, which is
    /// the one the video picks up again with.
    /// </summary>
    public StudioCutEditResult SetCutEnd(int index, double sourceTime)
    {
        var result = EditCut(index, model => model.SetCutEnd(index, sourceTime));
        if (IsEditable && result.Index is { } place && Model is { } model)
        {
            Scrub(model.Project.Edits.Cuts[place].End);
        }

        return result;
    }

    /// <summary>Starts a cut at the playhead. The playhead stays where it is.</summary>
    public StudioCutEditResult SetCutStartAtPlayhead(int index)
    {
        var time = Playhead;
        return EditCut(index, model => model.SetCutStart(index, time));
    }

    /// <summary>Ends a cut at the playhead. The playhead stays where it is.</summary>
    public StudioCutEditResult SetCutEndAtPlayhead(int index)
    {
        var time = Playhead;
        return EditCut(index, model => model.SetCutEnd(index, time));
    }

    /// <summary>Moves a whole cut so it starts at a source time, keeping its length. The playhead stays.</summary>
    public StudioCutEditResult MoveCut(int index, double sourceTime) =>
        EditCut(index, model => model.MoveCut(index, sourceTime));

    // An edit to one cut. The selection goes with the cut when it is the selected one.
    private StudioCutEditResult EditCut(int index, Func<StudioEditorModel, StudioCutEditResult> change)
    {
        var result = new StudioCutEditResult(false, index);
        var isSelected = SelectedCutIndex == index;
        EditAndSelect(model =>
        {
            result = change(model);
            return isSelected ? EditSelection.Cut(result.Index) : null;
        });
        return result;
    }

    private bool SelectAndShowCut(int? index)
    {
        if (index is not { } cut || !IsEditable || Model is not { } model || cut < 0 || cut >= model.Project.Edits.Cuts.Length)
        {
            return false;
        }

        SelectCut(cut);
        Scrub(model.Project.Edits.Cuts[cut].Start);
        return true;
    }
}
