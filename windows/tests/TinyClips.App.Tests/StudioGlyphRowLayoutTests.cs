using TinyClips.App.Controls.Studio;

namespace TinyClips.App.Tests;

/// <summary>
/// When a row of the Studio editor's buttons shows the glyphs before their words: while it has
/// room for all of them. The widths used here are the timeline row's, in effective pixels, as
/// the window check measured them on 7 October 2026 in a window at its smallest size and worked
/// them out for a text size of 200% (its note "the timeline's row by its sizes"). Nobody has
/// seen the row at 200%: the text size is a setting of Windows no tool may change.
/// </summary>
public sealed class StudioGlyphRowLayoutTests
{
    // The row's room in the smallest window, 980 wide, and in the window as it opens, 1180 wide:
    // the window has 46.7 around the row.
    private const double RoomInSmallestWindow = 933.3;
    private const double RoomInWindowAsItOpens = 1133.3;

    // The ten items with the gaps between them, and what the six glyphs take of that.
    private const double RowAtUsualTextSize = 813.3;
    private const double GlyphsAtUsualTextSize = 120;
    private const double RowAtDoubleTextSize = 1289.3;
    private const double GlyphsAtDoubleTextSize = 204;

    [Fact]
    public void AtTheUsualTextSize_TheRowShowsItsGlyphs_AlsoInTheSmallestWindow()
    {
        Assert.True(StudioGlyphRowLayout.ShowsGlyphs(RoomInSmallestWindow, RowAtUsualTextSize, GlyphsAtUsualTextSize, glyphsShown: true));

        // And a row that had given them up there gets them back.
        Assert.True(StudioGlyphRowLayout.ShowsGlyphs(RoomInSmallestWindow, RowAtUsualTextSize - GlyphsAtUsualTextSize, GlyphsAtUsualTextSize, glyphsShown: false));
    }

    [Theory]
    [InlineData(RoomInSmallestWindow)]
    [InlineData(RoomInWindowAsItOpens)]
    public void AtDoubleTextSize_TheGlyphsGiveWay_AndStayAway(double room)
    {
        Assert.False(StudioGlyphRowLayout.ShowsGlyphs(room, RowAtDoubleTextSize, GlyphsAtDoubleTextSize, glyphsShown: true));
        Assert.False(StudioGlyphRowLayout.ShowsGlyphs(room, RowAtDoubleTextSize - GlyphsAtDoubleTextSize, GlyphsAtDoubleTextSize, glyphsShown: false));
    }

    [Fact]
    public void AtDoubleTextSize_TheRowWithoutGlyphs_IsWhatItWasBeforeItHadThem()
    {
        // 1085.3: it fits the window as it opens, as it did, and not the smallest window, where
        // it did not fit before either. A window 1340 wide has room for the glyphs again.
        var withoutGlyphs = RowAtDoubleTextSize - GlyphsAtDoubleTextSize;
        Assert.True(withoutGlyphs <= RoomInWindowAsItOpens);
        Assert.True(withoutGlyphs > RoomInSmallestWindow);
        Assert.False(StudioGlyphRowLayout.ShowsGlyphs(1339 - 46.7, withoutGlyphs, GlyphsAtDoubleTextSize, glyphsShown: false));
        Assert.True(StudioGlyphRowLayout.ShowsGlyphs(1341 - 46.7, withoutGlyphs, GlyphsAtDoubleTextSize, glyphsShown: false));
    }

    [Fact]
    public void ARowThatIsJustWideEnough_KeepsItsGlyphs_AndOneThatGaveThemUpWantsALittleMoreToTakeThemBack()
    {
        Assert.True(StudioGlyphRowLayout.ShowsGlyphs(800, 800, 120, glyphsShown: true));
        Assert.False(StudioGlyphRowLayout.ShowsGlyphs(800, 800.5, 120, glyphsShown: true));

        Assert.False(StudioGlyphRowLayout.ShowsGlyphs(800, 680, 120, glyphsShown: false));
        Assert.False(StudioGlyphRowLayout.ShowsGlyphs(800 + StudioGlyphRowLayout.RoomToComeBack - 0.5, 680, 120, glyphsShown: false));
        Assert.True(StudioGlyphRowLayout.ShowsGlyphs(800 + StudioGlyphRowLayout.RoomToComeBack, 680, 120, glyphsShown: false));
    }

    [Fact]
    public void ARowDoesNotChangeBackAndForth_WhenItMeasuresALittleDifferentlyWithItsGlyphsThanWithout()
    {
        // Widths are rounded to whole pixels: six buttons can be up to two effective pixels
        // wider with their glyphs than the same six without them and the glyphs added.
        for (var room = 700.0; room <= 900; room += 0.5)
        {
            foreach (var rounding in new[] { -2.0, -1, 0, 1, 2 })
            {
                var comesBack = StudioGlyphRowLayout.ShowsGlyphs(room, 680, 120, glyphsShown: false);
                if (comesBack)
                {
                    Assert.True(StudioGlyphRowLayout.ShowsGlyphs(room, 800 + rounding, 120, glyphsShown: true), $"room {room}, rounding {rounding}");
                }
            }
        }
    }

    [Fact]
    public void ARowThatHasNotBeenLaidOut_StaysAsItIs()
    {
        Assert.True(StudioGlyphRowLayout.ShowsGlyphs(0, 0, 0, glyphsShown: true));
        Assert.False(StudioGlyphRowLayout.ShowsGlyphs(0, 0, 20, glyphsShown: false));
    }
}
