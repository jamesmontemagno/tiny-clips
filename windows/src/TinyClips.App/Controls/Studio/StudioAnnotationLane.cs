using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.Foundation;
using Windows.System;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The annotations as blocks along the recording. They may overlap, so a press takes the selected
/// block first and then the one drawn on top. Blocks that overlap show one label between them, so
/// no words are drawn over others, and a count of how many are there; clicking where several
/// overlap, or Enter or the context menu key with the focus on the lane, lists them to choose from.
/// Pressing a block selects its annotation, dragging it moves it, and dragging one of its ends
/// changes when it starts or stops. With the focus on the lane, Left and Right select the annotation
/// before and after the selected one, and Home and End the first and the last in the list.
/// </summary>
public sealed partial class StudioAnnotationLane : StudioRangeLane
{
    public StudioAnnotationLane(StudioViewModel viewModel)
        : base(viewModel, "StudioAnnotationLane", "Annotations", height: 24)
    {
        ContextRequested += OnContextRequested;
    }

    /// <summary>The list of the annotations at a place where several overlap, while it is open. For the check tool.</summary>
    internal MenuFlyout? OverlapMenu { get; private set; }

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

    private protected override void ShowOverlaps(IReadOnlyList<StudioLaneBlock> blocks, int? selectedIndex)
    {
        var overlaps = StudioEditorModel.GetLaneOverlaps(PlacesOf(blocks), selectedIndex);
        for (var index = 0; index < blocks.Count; index++)
        {
            ((StudioAnnotationBlock)blocks[index]).ShowOverlap(overlaps[index]);
        }
    }

    private protected override void OnClicked(double x) => ShowOverlapMenu(x);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == VirtualKey.Enter && ShowOverlapMenuForSelected())
        {
            e.Handled = true;
        }
    }

    // Shift+F10, the menu key, and a right click.
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = e.TryGetPosition(this, out var point) ? ShowOverlapMenu(point.X) : ShowOverlapMenuForSelected();
    }

    private bool ShowOverlapMenuForSelected() =>
        SelectedBlock is { } block && ShowOverlapMenu(Canvas.GetLeft(block) + (block.Width / 2));

    /// <summary>
    /// Lists the annotations at a place along the lane, when more than one is there, to choose one
    /// from. Choosing one selects it and moves the playhead to where it starts.
    /// </summary>
    internal bool ShowOverlapMenu(double x)
    {
        var blocks = BlocksShown;
        var indices = StudioEditorModel.GetLaneBlocksAt(x, PlacesOf(blocks), SelectedIndex);
        if (indices.Length < 2 || !ViewModel.IsEditable)
        {
            return false;
        }

        var menu = new MenuFlyout();
        var selected = SelectedIndex;
        foreach (var index in indices.Order())
        {
            var annotation = ViewModel.Annotations[index];
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{StudioEditorText.GetAnnotationLabel(annotation)}  {StudioEditorText.GetAnnotationRangeText(annotation)}",
                GroupName = "StudioAnnotationOverlap",
                IsChecked = index == selected,
            };
            AutomationProperties.SetName(item, StudioEditorText.GetAnnotationDescription(annotation));
            AutomationProperties.SetAutomationId(item, $"StudioAnnotationOverlapItem_{index}");
            var chosen = index;
            item.Click += (_, _) => SelectAndShow(chosen);
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(OverlapMenu, menu))
            {
                OverlapMenu = null;
            }
        };
        OverlapMenu = menu;
        menu.ShowAt(this, new FlyoutShowOptions
        {
            Position = new Point(x, ActualHeight),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
        return true;
    }

    private static (double Left, double Width)[] PlacesOf(IReadOnlyList<StudioLaneBlock> blocks) =>
        [.. blocks.Select(block => (Canvas.GetLeft(block), block.Width))];
}
