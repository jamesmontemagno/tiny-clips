using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// A lane whose blocks are stretches of the recording that are selected, moved, and made longer
/// and shorter: the zoom lane, the cut lane and the speed lane. Pressing a block selects it,
/// dragging it moves it, and dragging the handle at one of its ends changes when it starts or
/// stops. Pressing or dragging on the empty lane moves the playhead and selects nothing.
/// </summary>
/// <remarks>
/// Where a block's handles are is the editor model's rule, the same on the Mac
/// (<see cref="StudioEditorModel.GetLaneBlockPart"/>): inside the ends of a block with room for
/// them, and outside the ends of a narrower one once it is selected, which is then that much
/// wider to press. <see cref="StudioLaneBlockHandles"/> draws them in the same places.
/// </remarks>
public abstract partial class StudioRangeLane : StudioLane
{
    protected StudioRangeLane(StudioViewModel viewModel, string automationId, string name, string emptyText, double height)
        : base(viewModel, automationId, name, emptyText, height)
    {
    }

    /// <summary>Moves a whole item so it starts at a source time. Returns its place in the list afterwards.</summary>
    protected abstract int? Move(int index, double sourceTime);

    /// <summary>Moves an item's start. Returns its place in the list afterwards.</summary>
    protected abstract int? MoveStart(int index, double sourceTime);

    /// <summary>Moves an item's end. Returns its place in the list afterwards.</summary>
    protected abstract int? MoveEnd(int index, double sourceTime);

    private protected sealed override StudioLanePress TakeHold(double x)
    {
        if (FindBlockToPress(x) is { } block && block.Index >= 0 && block.Index < ItemCount)
        {
            var (start, end) = GetRange(block.Index);

            // Read before the press selects the block: a narrow block has handles only once it is selected.
            var part = GetPart(x - Canvas.GetLeft(block), block.Width, block.IsSelected);
            Select(block.Index);
            return new StudioLanePress(block.Index, part, start, end, x);
        }

        // Not only this lane's selection: a zoom, a cut or a speed change is selected, never two of them.
        ViewModel.SelectNothing();
        return StudioLanePress.OnTheLane(x);
    }

    private protected sealed override int? Drag(StudioLanePress press, double seconds)
    {
        if (press.Index is not { } index)
        {
            return null;
        }

        return press.Part switch
        {
            StudioLanePart.Start => MoveStart(index, press.Start + seconds),
            StudioLanePart.End => MoveEnd(index, press.End + seconds),
            _ => Move(index, press.Start + seconds),
        };
    }

    /// <summary>
    /// The block a press at a place along the lane is on. The selected block comes first: it is
    /// drawn over its neighbors, and a narrow one reaches as far as the handles outside its ends.
    /// </summary>
    private StudioLaneBlock? FindBlockToPress(double x)
    {
        if (SelectedBlock is { } selected)
        {
            var left = Canvas.GetLeft(selected);
            var outset = StudioEditorModel.GetLaneHandleOutset(selected.Width, isSelected: true);
            if (x >= left - outset && x < left + selected.Width + outset)
            {
                return selected;
            }
        }

        return FindBlockAt(x);
    }

    private static StudioLanePart GetPart(double xInBlock, double blockWidth, bool isSelected) =>
        StudioEditorModel.GetLaneBlockPart(xInBlock, blockWidth, isSelected) switch
        {
            StudioLaneBlockPart.Start => StudioLanePart.Start,
            StudioLaneBlockPart.End => StudioLanePart.End,
            _ => StudioLanePart.Body,
        };
}
