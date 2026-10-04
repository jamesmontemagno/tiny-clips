namespace TinyClips.Core.Studio.Editing;

// Speed changes. An index is a speed change's place in Project.Edits.Speed, which is kept in time
// order. Every edit answers with the place the speed change has afterwards, or none when it is
// gone. An edit that is refused, because the project cannot be edited just now, leaves it where
// it was.
//
// One speed change can be selected, or one zoom, or one cut, never two of them: selecting a speed
// change lets go of the others. An edit to the selected speed change takes the selection with it,
// and so does adding one. Through every other edit, and undo and redo, the selection follows its
// speed change as StudioEditorModel.FindSpeedFollowing finds it, or lets go when it is gone.
public sealed partial class StudioEditorSession
{
    private int? _selectedSpeedIndex;

    /// <summary>
    /// The speed change the inspector shows, as its place in <see cref="StudioEdits.Speed"/>, or
    /// null. It stays on its speed change while edits, undo and redo change the list around it.
    /// </summary>
    public int? SelectedSpeedIndex =>
        _selectedSpeedIndex is { } index && Model is { } model && index >= 0 && index < model.Project.Edits.Speed.Length
            ? index
            : null;

    public StudioSpeedRange? SelectedSpeed => SelectedSpeedIndex is { } index ? Model?.Project.Edits.Speed[index] : null;

    /// <summary>How many speed changes the project has. 0 until the project has been read.</summary>
    public int SpeedCount => Model?.Project.Edits.Speed.Length ?? 0;

    /// <summary>
    /// Whether Speed has a speed change to answer with at the playhead: one fits there, or one is
    /// already there to select.
    /// </summary>
    public bool CanAddSpeedAtPlayhead => IsEditable && Model is { } model && model.CanAddSpeed(Playhead);

    /// <summary>The speed change that contains a source time, or null.</summary>
    public int? GetSpeedIndexAt(double sourceTime) => Model?.GetSpeedIndexAt(sourceTime);

    /// <summary>
    /// Selects a speed change, or none with null or a place that has no speed change. The playhead
    /// stays. A selected zoom or cut is let go when a speed change is selected.
    /// </summary>
    public void SelectSpeed(int? index)
    {
        var before = Selection;
        _selectedSpeedIndex = index;
        _selectedSpeedIndex = SelectedSpeedIndex;
        if (_selectedSpeedIndex is not null)
        {
            _selectedZoomIndex = null;
            _selectedCutIndex = null;
        }

        if (Selection != before)
        {
            RaiseChanged(StudioEditorChanges.Selection);
        }
    }

    /// <summary>
    /// Selects the speed change after the selected one and moves the playhead to where it starts.
    /// With nothing selected, the one at the playhead or the first one after it. False when there
    /// is none.
    /// </summary>
    public bool SelectNextSpeed() => SelectAndShowSpeed(Model?.GetSpeedIndexAfter(SelectedSpeedIndex, Playhead));

    /// <summary>
    /// Selects the speed change before the selected one and moves the playhead to where it starts.
    /// With nothing selected, the one at the playhead or the last one before it. False when there
    /// is none.
    /// </summary>
    public bool SelectPreviousSpeed() => SelectAndShowSpeed(Model?.GetSpeedIndexBefore(SelectedSpeedIndex, Playhead));

    /// <summary>
    /// Adds a speed change that starts at a source time, and selects it. Where one already is,
    /// that one is selected instead.
    /// </summary>
    public StudioSpeedEditResult AddSpeed(double sourceTime)
    {
        var result = new StudioSpeedEditResult(false, null);
        EditAndSelect(model =>
        {
            result = model.AddSpeed(sourceTime);
            return result.Index is { } index ? EditSelection.Speed(index) : null;
        });
        return result;
    }

    /// <summary>
    /// Adds a speed change at the playhead and selects it. The playhead stays where it starts, on
    /// the first picture that plays at the other speed.
    /// </summary>
    public StudioSpeedEditResult AddSpeedAtPlayhead() => AddSpeed(Playhead);

    /// <summary>Deletes a speed change, so its stretch plays at the recording's own speed again.</summary>
    public StudioSpeedEditResult RemoveSpeed(int index) => EditSpeed(index, model => model.RemoveSpeed(index));

    /// <summary>Deletes the selected speed change. Unchanged, with no index, when none is selected.</summary>
    public StudioSpeedEditResult RemoveSelectedSpeed() =>
        SelectedSpeedIndex is { } index ? RemoveSpeed(index) : new StudioSpeedEditResult(false, null);

    /// <summary>
    /// Moves a speed change's start, from the lane or a step button, and shows the picture there,
    /// which is the first one that plays at the other speed.
    /// </summary>
    public StudioSpeedEditResult SetSpeedStart(int index, double sourceTime)
    {
        var result = EditSpeed(index, model => model.SetSpeedStart(index, sourceTime));
        if (IsEditable && result.Index is { } place && Model is { } model)
        {
            Scrub(model.Project.Edits.Speed[place].Start);
        }

        return result;
    }

    /// <summary>
    /// Moves a speed change's end, from the lane or a step button, and shows the picture there,
    /// which is the first one at the recording's own speed again.
    /// </summary>
    public StudioSpeedEditResult SetSpeedEnd(int index, double sourceTime)
    {
        var result = EditSpeed(index, model => model.SetSpeedEnd(index, sourceTime));
        if (IsEditable && result.Index is { } place && Model is { } model)
        {
            Scrub(model.Project.Edits.Speed[place].End);
        }

        return result;
    }

    /// <summary>Starts a speed change at the playhead. The playhead stays where it is.</summary>
    public StudioSpeedEditResult SetSpeedStartAtPlayhead(int index)
    {
        var time = Playhead;
        return EditSpeed(index, model => model.SetSpeedStart(index, time));
    }

    /// <summary>Ends a speed change at the playhead. The playhead stays where it is.</summary>
    public StudioSpeedEditResult SetSpeedEndAtPlayhead(int index)
    {
        var time = Playhead;
        return EditSpeed(index, model => model.SetSpeedEnd(index, time));
    }

    /// <summary>
    /// Moves a whole speed change so it starts at a source time, keeping its length and its rate.
    /// The playhead stays.
    /// </summary>
    public StudioSpeedEditResult MoveSpeed(int index, double sourceTime) =>
        EditSpeed(index, model => model.MoveSpeed(index, sourceTime));

    /// <summary>
    /// Sets how fast a speed change plays: how many seconds of the recording pass in one second
    /// of video. The playhead stays.
    /// </summary>
    public StudioSpeedEditResult SetSpeedRate(int index, double rate) =>
        EditSpeed(index, model => model.SetSpeedRate(index, rate));

    // An edit to one speed change. The selection goes with it when it is the selected one.
    private StudioSpeedEditResult EditSpeed(int index, Func<StudioEditorModel, StudioSpeedEditResult> change)
    {
        var result = new StudioSpeedEditResult(false, index);
        var isSelected = SelectedSpeedIndex == index;
        EditAndSelect(model =>
        {
            result = change(model);
            return isSelected ? EditSelection.Speed(result.Index) : null;
        });
        return result;
    }

    /// <summary>
    /// Selects a speed change and moves the playhead to where it starts, as
    /// <see cref="SelectNextSpeed"/> does. False, with nothing changed, when there is no such
    /// speed change or the project cannot be edited just now.
    /// </summary>
    public bool SelectAndShowSpeed(int? index)
    {
        if (index is not { } speed || !IsEditable || Model is not { } model || speed < 0 || speed >= model.Project.Edits.Speed.Length)
        {
            return false;
        }

        SelectSpeed(speed);
        Scrub(model.Project.Edits.Speed[speed].Start);
        return true;
    }
}
