using TinyClips.App;

namespace TinyClips.App.Tests;

/// <summary>
/// How wide the tray popup is for its row of capture tiles: three as it always had, or four
/// while Studio is switched on (Screenshot, Video, Studio, GIF). The widths of the labels used
/// here were worked out from the font's own figures; nobody has seen the popup with four tiles.
/// </summary>
public sealed class TrayPopupLayoutTests
{
    // "Screenshot", the longest label, in Segoe UI Variable: about 61 at the tiles' 12 pixels,
    // and about 122 when the text size in Windows doubles it.
    private const double ScreenshotAtUsualTextSize = 61;
    private const double ScreenshotAtDoubleTextSize = 122;

    [Fact]
    public void ThreeTiles_HaveTheWidthTheyAlwaysHad_WhateverTheirLabelsNeed()
    {
        Assert.Equal(344, TrayPopupLayout.Width);
        Assert.Equal(92, TrayPopupLayout.LabelRoom(3, TrayPopupLayout.Width));

        Assert.All(
            new[] { 0, ScreenshotAtUsualTextSize, ScreenshotAtDoubleTextSize, 500, double.NaN },
            label => Assert.Equal(TrayPopupLayout.Width, TrayPopupLayout.WidthFor(3, label)));
    }

    [Fact]
    public void FourTiles_AtTheUsualTextSize_FitTheUsualWidth()
    {
        // 344 less 16 on each side and three gaps of 6 is 294: 73.5 a tile, 65.5 for its label.
        Assert.Equal(65.5, TrayPopupLayout.LabelRoom(4, TrayPopupLayout.Width));
        Assert.True(ScreenshotAtUsualTextSize < TrayPopupLayout.LabelRoom(4, TrayPopupLayout.Width));

        Assert.Equal(TrayPopupLayout.Width, TrayPopupLayout.WidthFor(4, ScreenshotAtUsualTextSize));
    }

    [Fact]
    public void FourTiles_AtDoubleTheTextSize_MakeThePopupWideEnoughForTheLongestLabel()
    {
        var width = TrayPopupLayout.WidthFor(4, ScreenshotAtDoubleTextSize);

        // Four tiles of 122 for the label, 4 to spare and 4 on each side, three gaps, and 16 on each side.
        Assert.Equal(586, width);
        Assert.True(TrayPopupLayout.LabelRoom(4, width) >= ScreenshotAtDoubleTextSize);

        // In the usual width the label would have wrapped.
        Assert.True(TrayPopupLayout.LabelRoom(4, TrayPopupLayout.Width) < ScreenshotAtDoubleTextSize);
    }

    [Fact]
    public void FourTiles_AlwaysHaveRoomForTheLabelThatWasMeasured_AndAreNeverNarrowerThanThree()
    {
        for (var label = 0.5; label <= 400; label += 0.5)
        {
            var width = TrayPopupLayout.WidthFor(4, label);

            Assert.True(width >= TrayPopupLayout.Width, $"A label of {label} made the popup {width} wide.");
            Assert.True(TrayPopupLayout.LabelRoom(4, width) >= label, $"A label of {label} has {TrayPopupLayout.LabelRoom(4, width)} in a popup {width} wide.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ALabelThatCouldNotBeMeasured_LeavesThePopupAtItsUsualWidth(double label)
    {
        Assert.Equal(TrayPopupLayout.Width, TrayPopupLayout.WidthFor(4, label));
    }
}
