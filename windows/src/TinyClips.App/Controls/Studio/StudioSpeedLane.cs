using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The speed changes as blocks along the recording: the stretches the video plays faster or
/// slower. Pressing a block selects its speed change, dragging it moves the speed change, and
/// dragging one of its ends changes where it starts or stops. With the focus on the lane, Left
/// and Right select the speed change before and after the selected one, and Home and End the
/// first and the last, each shown where it starts.
/// </summary>
public sealed partial class StudioSpeedLane : StudioRangeLane
{
    public StudioSpeedLane(StudioViewModel viewModel)
        : base(viewModel, "StudioSpeedLane", "Speed changes", StudioEditorText.NoSpeedHint, height: 24)
    {
    }

    protected override object Items => ViewModel.SpeedChanges;

    protected override int ItemCount => ViewModel.SpeedChanges.Count;

    protected override int? SelectedIndex => ViewModel.SelectedSpeedIndex;

    protected override string SelectedIndexPropertyName => nameof(StudioViewModel.SelectedSpeedIndex);

    internal override void Select(int? index) => ViewModel.SelectSpeed(index);

    internal override bool SelectAndShow(int index) => ViewModel.SelectAndShowSpeed(index);

    protected override (double Start, double End) GetRange(int index)
    {
        var speed = ViewModel.SpeedChanges[index];
        return (speed.Start, speed.End);
    }

    protected override StudioLaneBlock CreateBlock() => new StudioSpeedBlock(this);

    protected override void ShowItem(StudioLaneBlock block, int index, double width, bool isSelected) =>
        ((StudioSpeedBlock)block).Update(index, ViewModel.SpeedChanges[index], width, isSelected);

    protected override int? Move(int index, double sourceTime) => ViewModel.MoveSpeed(index, sourceTime).Index;

    // The playhead follows the end that is dragged: the editor does that for a speed change.
    protected override int? MoveStart(int index, double sourceTime) => ViewModel.SetSpeedStart(index, sourceTime).Index;

    protected override int? MoveEnd(int index, double sourceTime) => ViewModel.SetSpeedEnd(index, sourceTime).Index;

    protected override bool ShowPrevious() => ViewModel.SelectPreviousSpeed();

    protected override bool ShowNext() => ViewModel.SelectNextSpeed();
}
