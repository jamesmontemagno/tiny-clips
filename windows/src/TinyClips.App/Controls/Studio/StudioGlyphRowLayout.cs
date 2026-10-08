namespace TinyClips.App.Controls.Studio;

/// <summary>
/// Whether the buttons of a row show the glyphs before their words. They do while the row has
/// room for every button with its glyph, and where it has not, as at a large text size in a
/// narrow window, the glyphs give way and the words stay: the row is then no wider than it was
/// before its buttons had glyphs.
/// </summary>
internal static class StudioGlyphRowLayout
{
    /// <summary>
    /// How much more room than the glyphs need a row must have before they come back. Widths are
    /// rounded to whole pixels, so the same row measures a little differently with its glyphs
    /// than without them, and a row that is just wide enough would otherwise change back and forth.
    /// </summary>
    public const double RoomToComeBack = 4;

    /// <param name="rowWidth">The room the row has.</param>
    /// <param name="itemsWidth">What its items take as they are now, with the gaps between them.</param>
    /// <param name="glyphsWidth">What the glyphs, each with the gap after it, take when they show.</param>
    /// <param name="glyphsShown">Whether the glyphs show now, and so are part of <paramref name="itemsWidth"/>.</param>
    public static bool ShowsGlyphs(double rowWidth, double itemsWidth, double glyphsWidth, bool glyphsShown) =>
        glyphsShown
            ? itemsWidth <= rowWidth
            : itemsWidth + glyphsWidth + RoomToComeBack <= rowWidth;
}
