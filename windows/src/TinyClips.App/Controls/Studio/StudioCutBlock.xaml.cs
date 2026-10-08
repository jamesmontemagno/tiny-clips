using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One cut on the Studio cut lane: a hatched block with scissors on it and, where there is room,
/// how long the stretch is that the video leaves out.
/// </summary>
public sealed partial class StudioCutBlock : StudioRangeBlock
{
    // Below these widths the scissors, and then the length, would not fit inside the block.
    private const double IconMinimumWidth = 26;
    private const double LengthTextMinimumWidth = 62;

    // A line of the hatching every twelve along the block, slanted by one across for one up.
    private const double HatchPitch = 12;

    // The hatching stops this far inside the block, under its outline.
    private const double HatchInset = 1;

    internal StudioCutBlock(StudioLane lane)
        : base(lane, "StudioCut_", "cut", "where")
    {
        InitializeComponent();
        CutIcon.Glyph = StudioGlyphs.AddCut;
    }

    /// <summary>Shows a cut in a block of the given width.</summary>
    internal void Update(int index, StudioTimeRange cut, double width, bool isSelected)
    {
        var length = double.IsFinite(cut.End - cut.Start) ? Math.Max(0, cut.End - cut.Start) : 0;
        LengthText.Text = string.Create(CultureInfo.InvariantCulture, $"{length:0.0} s");
        LengthText.Visibility = width >= LengthTextMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        Plate.Visibility = width >= IconMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        ShowHandles(BlockHandles, width);
        Show(index, StudioEditorText.GetCutDescription(cut), isSelected);
    }

    // The lines are as many as the block is wide, so they are laid out again when its size changes.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var (width, height) = (e.NewSize.Width, e.NewSize.Height);
        var lines = new GeometryGroup();
        if (width > HatchInset * 2 && height > HatchInset * 2)
        {
            // Each line runs from below the block to above it, and is cut off at the block's edges.
            const double Reach = 3;
            for (var x = HatchPitch * 0.15; x < width + height; x += HatchPitch)
            {
                lines.Children.Add(new LineGeometry
                {
                    StartPoint = new Point(x - height - Reach, height + Reach),
                    EndPoint = new Point(x + Reach, -Reach),
                });
            }
        }

        Hatch.Data = lines;
        HatchArea.Clip = new RectangleGeometry
        {
            Rect = new Rect(HatchInset, HatchInset, Math.Max(0, width - (HatchInset * 2)), Math.Max(0, height - (HatchInset * 2))),
        };
    }
}
