using Microsoft.UI.Xaml;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One scene on the Studio scene lane: a block with the name of the scene's layout and, for
/// every scene but the first, a mark at its start, which can be dragged.
/// </summary>
public sealed partial class StudioSceneBlock : StudioLaneBlock
{
    // Below these widths the name, and then the mark, would not fit inside the block.
    private const double NameMinimumWidth = 52;
    private const double GripMinimumWidth = 16;

    internal StudioSceneBlock(StudioLane lane)
        : base(lane, "StudioScene_")
    {
        InitializeComponent();
    }

    /// <summary>Shows a scene in a block of the given width.</summary>
    /// <param name="layoutName">The name of the scene's layout, such as "Side by side".</param>
    /// <param name="description">The scene as a screen reader says it.</param>
    /// <param name="isCurrent">Whether the playhead is in the scene.</param>
    internal void Update(int index, string layoutName, string description, double width, bool isCurrent)
    {
        NameText.Text = layoutName;
        NameText.Visibility = width >= NameMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        Grip.Visibility = index >= 1 && width >= GripMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        Show(index, description, isCurrent);
    }
}
