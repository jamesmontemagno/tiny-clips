using TinyClips.App.ViewModels.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The zooms as blocks along the recording. Pressing a block selects its zoom, dragging it moves
/// the zoom, and dragging one of its ends changes when the zoom starts or stops. With the focus
/// on the lane, Left and Right select the zoom before and after the selected one, and Home and
/// End the first and the last, each shown where it has moved in.
/// </summary>
public sealed partial class StudioZoomLane : StudioRangeLane
{
    public StudioZoomLane(StudioViewModel viewModel)
        : base(viewModel, "StudioZoomLane", "Zooms", "No zooms. Press Z to add one at the playhead.", height: 26)
    {
    }

    protected override object Items => ViewModel.Zooms;

    protected override int ItemCount => ViewModel.Zooms.Count;

    protected override int? SelectedIndex => ViewModel.SelectedZoomIndex;

    protected override string SelectedIndexPropertyName => nameof(StudioViewModel.SelectedZoomIndex);

    internal override void Select(int? index) => ViewModel.SelectZoom(index);

    internal override bool SelectAndShow(int index) => ViewModel.SelectAndShowZoom(index);

    protected override (double Start, double End) GetRange(int index)
    {
        var zoom = ViewModel.Zooms[index];
        return (zoom.Start, zoom.End);
    }

    protected override StudioLaneBlock CreateBlock() => new StudioZoomBlock(this);

    protected override void ShowItem(StudioLaneBlock block, int index, double width, bool isSelected) =>
        ((StudioZoomBlock)block).Update(index, ViewModel.Zooms[index], width, isSelected);

    protected override int? Move(int index, double sourceTime) => ViewModel.MoveZoom(index, sourceTime).Index;

    // The playhead follows the end that is dragged, as it follows a trim handle.
    protected override int? MoveStart(int index, double sourceTime) => ViewModel.DragZoomStart(index, sourceTime).Index;

    protected override int? MoveEnd(int index, double sourceTime) => ViewModel.DragZoomEnd(index, sourceTime).Index;

    protected override bool ShowPrevious() => ViewModel.SelectPreviousZoom();

    protected override bool ShowNext() => ViewModel.SelectNextZoom();
}
