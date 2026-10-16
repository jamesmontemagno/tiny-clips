using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The cuts as blocks along the recording: the stretches the video leaves out. Pressing a block
/// selects its cut, dragging it moves the cut, and dragging one of its ends changes where the cut
/// starts or stops. With the focus on the lane, Left and Right select the cut before and after
/// the selected one, and Home and End the first and the last, each shown where it starts.
/// </summary>
public sealed partial class StudioCutLane : StudioRangeLane
{
    public StudioCutLane(StudioViewModel viewModel)
        : base(viewModel, "StudioCutLane", "Cuts", StudioEditorText.NoCutsHint, height: 24)
    {
    }

    protected override object Items => ViewModel.Cuts;

    protected override int ItemCount => ViewModel.Cuts.Count;

    protected override int? SelectedIndex => ViewModel.SelectedCutIndex;

    protected override string SelectedIndexPropertyName => nameof(StudioViewModel.SelectedCutIndex);

    internal override void Select(int? index) => ViewModel.SelectCut(index);

    internal override bool SelectAndShow(int index) => ViewModel.SelectAndShowCut(index);

    protected override (double Start, double End) GetRange(int index)
    {
        var cut = ViewModel.Cuts[index];
        return (cut.Start, cut.End);
    }

    protected override StudioLaneBlock CreateBlock() => new StudioCutBlock(this);

    protected override void ShowItem(StudioLaneBlock block, int index, double width, bool isSelected) =>
        ((StudioCutBlock)block).Update(index, ViewModel.Cuts[index], width, isSelected);

    protected override int? Move(int index, double sourceTime) => ViewModel.MoveCut(index, sourceTime).Index;

    // The playhead follows the end that is dragged: the editor does that for a cut.
    protected override int? MoveStart(int index, double sourceTime) => ViewModel.SetCutStart(index, sourceTime).Index;

    protected override int? MoveEnd(int index, double sourceTime) => ViewModel.SetCutEnd(index, sourceTime).Index;

    protected override bool ShowPrevious() => ViewModel.SelectPreviousCut();

    protected override bool ShowNext() => ViewModel.SelectNextCut();
}
