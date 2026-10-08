using Microsoft.UI.Xaml;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One zoom on the Studio zoom lane: a block that shows how much the zoom magnifies, a mark when
/// it follows the pointer and a mark when it is a suggestion that has not been changed.
/// </summary>
public sealed partial class StudioZoomBlock : StudioRangeBlock
{
    // Below these widths the text, and then the marks, would not fit inside the block.
    private const double ScaleTextMinimumWidth = 30;
    private const double MarkMinimumWidth = 56;

    internal StudioZoomBlock(StudioLane lane)
        : base(lane, "StudioZoom_", "zoom", "when")
    {
        InitializeComponent();
    }

    /// <summary>Shows a zoom in a block of the given width.</summary>
    internal void Update(int index, StudioZoom zoom, double width, bool isSelected)
    {
        ScaleText.Text = StudioEditorText.GetZoomScaleText(zoom.Scale);
        ScaleText.Visibility = width >= ScaleTextMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        var hasRoomForMarks = width >= MarkMinimumWidth;
        PointerIcon.Visibility = hasRoomForMarks && zoom.Focus.Mode == StudioZoomFocusMode.Cursor ? Visibility.Visible : Visibility.Collapsed;
        SuggestedIcon.Visibility = hasRoomForMarks && zoom.Origin == StudioZoomOrigin.Auto ? Visibility.Visible : Visibility.Collapsed;
        ShowHandles(BlockHandles, width);
        Show(index, StudioEditorText.GetZoomDescription(zoom), isSelected);
    }
}
