namespace TinyClips.App;

/// <summary>
/// The sizes of the tray popup that follow from what is in it: how wide it has to be for its
/// row of capture tiles. Kept apart from the popup so that the arithmetic can be tested.
/// </summary>
internal static class TrayPopupLayout
{
    /// <summary>The popup's width, as it always was with three tiles.</summary>
    public const double Width = 344;

    /// <summary>The room around the popup's content, on each side.</summary>
    public const double ContentPadding = 16;

    /// <summary>The gap between two tiles.</summary>
    public const double TileSpacing = 6;

    /// <summary>The room between a tile's edge and its label, on each side.</summary>
    public const double TileSidePadding = 4;

    // What a label is given beyond its measured width, for rounding to whole pixels.
    private const double LabelSlack = 4;

    /// <summary>How wide a tile's label can be before it wraps, in a popup of this width.</summary>
    public static double LabelRoom(int tileCount, double popupWidth) =>
        ((popupWidth - (2 * ContentPadding) - ((tileCount - 1) * TileSpacing)) / tileCount) - (2 * TileSidePadding);

    /// <summary>
    /// The popup's width for a row of tiles whose widest label is this wide. Three tiles are
    /// laid out as they always were, whatever their labels need. A fourth tile, which is there
    /// while Studio is switched on, makes the popup wider when its usual width would wrap a
    /// label, as a larger text size in Windows does. Never narrower than <see cref="Width"/>.
    /// </summary>
    /// <param name="widestLabel">
    /// The measured width of the widest label on one line. Nothing, or nothing usable, where
    /// it could not be measured: the popup then has its usual width.
    /// </param>
    public static double WidthFor(int tileCount, double widestLabel)
    {
        if (tileCount <= 3 || !double.IsFinite(widestLabel) || widestLabel <= 0)
        {
            return Width;
        }

        var tile = Math.Ceiling(widestLabel) + LabelSlack + (2 * TileSidePadding);
        var needed = (2 * ContentPadding) + (tileCount * tile) + ((tileCount - 1) * TileSpacing);
        return Math.Max(Width, needed);
    }
}
