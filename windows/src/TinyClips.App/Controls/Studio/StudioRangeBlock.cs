using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// A block of the zoom, the cut or the speed lane: a stretch that is moved by dragging it and
/// made longer or shorter by dragging a handle at one of its ends. What the three have in common
/// is here: the handles, which are stronger while the pointer is over the block or the block is
/// selected; the selected block being drawn over its neighbors, so the handles outside a narrow
/// one are not hidden; and the tooltip, which says where the handles are.
/// </summary>
public abstract partial class StudioRangeBlock : StudioLaneBlock
{
    private readonly string _what;
    private readonly string _whenOrWhere;
    private StudioLaneBlockHandles? _handles;
    private double _width = double.NaN;
    private bool _isPointerOver;
    private string _toolTip = string.Empty;

    /// <param name="what">What the block stands for in a sentence: "zoom", "cut" or "speed change".</param>
    /// <param name="whenOrWhere">The word the block's tooltip asks about its ends with: "when" or "where".</param>
    private protected StudioRangeBlock(StudioLane lane, string automationIdPrefix, string what, string whenOrWhere)
        : base(lane, automationIdPrefix)
    {
        _what = what;
        _whenOrWhere = whenOrWhere;
        PointerEntered += (_, _) => ShowPointerOver(true);
        PointerExited += (_, _) => ShowPointerOver(false);
    }

    /// <summary>The block's handles. For the check tool.</summary>
    internal StudioLaneBlockHandles? Handles => _handles;

    /// <summary>What the block's tooltip says. For the check tool.</summary>
    internal string ToolTipText => _toolTip;

    /// <summary>
    /// The pointer came over the block or left it. The pointer handlers call this, and the check
    /// tool, which has no pointer.
    /// </summary>
    internal void ShowPointerOver(bool isPointerOver)
    {
        if (isPointerOver != _isPointerOver)
        {
            _isPointerOver = isPointerOver;
            ShowHandles();
        }
    }

    /// <summary>Says which handles are the block's and how wide the block is drawn.</summary>
    private protected void ShowHandles(StudioLaneBlockHandles handles, double width)
    {
        _handles = handles;
        _width = width;
        ShowHandles();
    }

    private protected override void OnSelectionShown()
    {
        // The selected block is drawn over its neighbors.
        Canvas.SetZIndex(this, IsSelected ? 1 : 0);
        ShowHandles();
    }

    private void ShowHandles()
    {
        _handles?.Show(_width, IsSelected, _isPointerOver);

        // A narrow block has no handles until it is selected, and says how to get them.
        var toolTip = StudioEditorModel.LaneBlockHasInsideHandles(_width) || IsSelected
            ? $"Drag to move this {_what}. Drag the handle at either end to change {_whenOrWhere} it starts or stops."
            : $"Drag to move this {_what}. Select it to show the handles that change {_whenOrWhere} it starts or stops.";
        if (toolTip != _toolTip)
        {
            _toolTip = toolTip;
            ToolTipService.SetToolTip(this, toolTip);
        }
    }
}
