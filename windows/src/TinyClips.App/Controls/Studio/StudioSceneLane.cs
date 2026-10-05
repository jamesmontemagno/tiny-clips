using TinyClips.App.ViewModels.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The scenes as blocks along the recording, each from its start to the start of the next one.
/// The scene the playhead is in is the marked one, so there is nothing to select: pressing a
/// block moves the playhead there, and dragging the line between two blocks, from either side of
/// it, changes when the later scene begins. With the focus on the lane, Left and Right go to the
/// scene before and after, and Home and End to the first and the last, each shown where it has
/// been entered. Delete, which the window handles, then deletes the scene the playhead is in.
/// </summary>
public sealed partial class StudioSceneLane : StudioLane
{
    // Neighbors stop this far short of each other, so that the line between them shows.
    private const double Gap = 2;

    // This much of a block next to the line between two scenes takes hold of the line, or half of
    // a block that is narrower than twice that, so that both of its lines can be taken.
    private const double GripWidth = 6;

    public StudioSceneLane(StudioViewModel viewModel)
        : base(viewModel, "StudioSceneLane", "Scenes", string.Empty, height: 24)
    {
    }

    internal override bool IsSelectionRequired => true;

    protected override object Items => ViewModel.Scenes;

    protected override int ItemCount => ViewModel.Scenes.Count;

    protected override int? SelectedIndex => ItemCount > 0 ? ViewModel.CurrentSceneIndex : null;

    protected override string SelectedIndexPropertyName => nameof(StudioViewModel.CurrentSceneIndex);

    protected override double MinimumBlockWidth => 4;

    // A scene is not selected. Going to it makes it the one the playhead is in.
    internal override void Select(int? index)
    {
        if (index is { } scene)
        {
            ViewModel.ShowScene(scene);
        }
    }

    internal override bool SelectAndShow(int index) => ViewModel.ShowScene(index);

    protected override (double Start, double End) GetRange(int index) => ViewModel.GetSceneRange(index) ?? default;

    protected override StudioLaneBlock CreateBlock() => new StudioSceneBlock(this);

    protected override void ShowItem(StudioLaneBlock block, int index, double width, bool isSelected) =>
        ((StudioSceneBlock)block).Update(index, ViewModel.GetSceneLayoutName(index), ViewModel.GetSceneDescription(index), width, isSelected);

    /// <summary>A scene's block runs from just after its start to just before its end.</summary>
    protected override (double Left, double Width) PlaceBlock(double startX, double endX, double laneWidth)
    {
        var left = Math.Min(Math.Max(startX + (Gap / 2), 0), Math.Max(0, laneWidth - MinimumBlockWidth));
        var drawn = Math.Min(Math.Max(MinimumBlockWidth, endX - startX - Gap), Math.Max(MinimumBlockWidth, laneWidth - left));
        return (left, drawn);
    }

    /// <summary>
    /// A press near the line between two scenes takes hold of where the later one starts.
    /// Anywhere else it takes hold of nothing, and moves the playhead as every press does.
    /// </summary>
    private protected override StudioLanePress TakeHold(double x)
    {
        var count = ItemCount;
        for (var index = 1; index < count; index++)
        {
            var (earlierStart, _) = GetRange(index - 1);
            var (start, end) = GetRange(index);
            var line = GetX(start);
            if (x >= line - Reach(line - GetX(earlierStart)) && x <= line + Reach(GetX(end) - line))
            {
                return new StudioLanePress(index, StudioLanePart.Start, start, end, x);
            }
        }

        return StudioLanePress.OnTheLane(x);
    }

    // Its neighbors keep a scene in its place in the list while its start is dragged, and the
    // playhead follows the start: the editor does both.
    private protected override int? Drag(StudioLanePress press, double seconds) =>
        press.Index is { } index ? ViewModel.SetSceneStart(index, press.Start + seconds).Index : null;

    protected override bool ShowPrevious() => ViewModel.ShowPreviousScene();

    protected override bool ShowNext() => ViewModel.ShowNextScene();

    // How far from the line a press still takes hold of it, on the side of a scene that is this wide.
    private static double Reach(double sceneWidth) => (Gap / 2) + Math.Min(GripWidth, Math.Max(0, sceneWidth - Gap) / 2);
}
