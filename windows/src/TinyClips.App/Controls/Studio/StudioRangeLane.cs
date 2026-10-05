using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// A lane whose blocks are stretches of the recording that are selected, moved, and made longer
/// and shorter: the zoom lane, the cut lane and the speed lane. Pressing a block selects it,
/// dragging it moves it, and dragging one of its ends changes when it starts or stops. Pressing
/// or dragging on the empty lane moves the playhead and selects nothing.
/// </summary>
public abstract partial class StudioRangeLane : StudioLane
{
    // This much of each end of a block moves that end, on a block wide enough to have a middle left.
    private const double EndGripWidth = 6;
    private const double EndGripMinimumBlockWidth = 24;

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
        if (FindBlockAt(x) is { } block && block.Index >= 0 && block.Index < ItemCount)
        {
            var (start, end) = GetRange(block.Index);
            Select(block.Index);
            return new StudioLanePress(block.Index, GetPart(x - Canvas.GetLeft(block), block.Width), start, end, x);
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

    private static StudioLanePart GetPart(double xInBlock, double blockWidth)
    {
        if (blockWidth < EndGripMinimumBlockWidth)
        {
            return StudioLanePart.Body;
        }

        if (xInBlock <= EndGripWidth)
        {
            return StudioLanePart.Start;
        }

        return xInBlock >= blockWidth - EndGripWidth ? StudioLanePart.End : StudioLanePart.Body;
    }
}
