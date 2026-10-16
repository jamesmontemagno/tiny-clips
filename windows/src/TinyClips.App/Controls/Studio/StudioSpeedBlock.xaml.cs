using Microsoft.UI.Xaml;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One speed change on the Studio speed lane: a block with round ends that carries a mark for
/// faster or slower and, where there is room, the rate.
/// </summary>
public sealed partial class StudioSpeedBlock : StudioRangeBlock
{
    // Below these widths the mark, and then the rate next to it, would not fit between the round ends.
    private const double MarkMinimumWidth = 26;
    private const double RateTextMinimumWidth = 64;

    internal StudioSpeedBlock(StudioLane lane)
        : base(lane, "StudioSpeed_", "speed change", "where")
    {
        InitializeComponent();
    }

    /// <summary>Shows a speed change in a block of the given width.</summary>
    internal void Update(int index, StudioSpeedRange speed, double width, bool isSelected)
    {
        // A rate of 1 is no speed change, so a stretch is either faster or slower.
        var isFaster = speed.Rate > 1;
        var hasRoomForMark = width >= MarkMinimumWidth;
        FasterIcon.Visibility = hasRoomForMark && isFaster ? Visibility.Visible : Visibility.Collapsed;
        SlowerIcon.Visibility = hasRoomForMark && !isFaster ? Visibility.Visible : Visibility.Collapsed;
        RateText.Text = StudioEditorText.GetSpeedRateText(speed.Rate);
        RateText.Visibility = width >= RateTextMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        ShowHandles(BlockHandles, width);
        Show(index, StudioEditorText.GetSpeedDescription(speed), isSelected);
    }

    // Round ends are a corner radius of half the height, whatever height the lane gives the block.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Fill.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        BlockHandles.EndRadius = e.NewSize.Height / 2;
    }
}
