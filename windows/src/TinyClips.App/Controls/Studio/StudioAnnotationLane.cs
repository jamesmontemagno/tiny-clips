using TinyClips.App.ViewModels.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The annotations as blocks along the recording. They may overlap, so a press takes the selected
/// block first and then the one drawn on top, and the panel's list reaches the ones under it.
/// Pressing a block selects its annotation, dragging it moves it, and dragging one of its ends
/// changes when it starts or stops. With the focus on the lane, Left and Right select the annotation
/// before and after the selected one, and Home and End the first and the last in the list.
/// </summary>
public sealed partial class StudioAnnotationLane : StudioRangeLane
{
    public StudioAnnotationLane(StudioViewModel viewModel)
        : base(viewModel, "StudioAnnotationLane", "Annotations", height: 24)
    {
    }

    protected override object Items => ViewModel.Annotations;

    protected override int ItemCount => ViewModel.Annotations.Count;

    protected override int? SelectedIndex => ViewModel.SelectedAnnotationIndex;

    protected override string SelectedIndexPropertyName => nameof(StudioViewModel.SelectedAnnotationIndex);

    internal override void Select(int? index) => ViewModel.SelectAnnotation(index);

    internal override bool SelectAndShow(int index) => ViewModel.SelectAndShowAnnotation(index);

    protected override (double Start, double End) GetRange(int index)
    {
        var annotation = ViewModel.Annotations[index];
        return (annotation.Start, annotation.End);
    }

    protected override StudioLaneBlock CreateBlock() => new StudioAnnotationBlock(this);

    protected override void ShowItem(StudioLaneBlock block, int index, double width, bool isSelected) =>
        ((StudioAnnotationBlock)block).Update(index, ViewModel.Annotations[index], width, isSelected);

    protected override int? Move(int index, double sourceTime) => ViewModel.MoveAnnotation(index, sourceTime).Index;

    // The playhead follows the end that is dragged: the editor does that for an annotation.
    protected override int? MoveStart(int index, double sourceTime) => ViewModel.SetAnnotationStart(index, sourceTime).Index;

    protected override int? MoveEnd(int index, double sourceTime) => ViewModel.SetAnnotationEnd(index, sourceTime).Index;

    protected override bool ShowPrevious() => ViewModel.SelectPreviousAnnotation();

    protected override bool ShowNext() => ViewModel.SelectNextAnnotation();
}
