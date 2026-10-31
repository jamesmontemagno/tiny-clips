using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class WindowPickerGeometryTests
{
    private static WindowPickerTarget Target(int id, int x, int y, int width, int height, bool selectable = true) =>
        new(id, new PixelRect(x, y, width, height), selectable);

    [Fact]
    public void HitTest_PicksTopmostOverlappingWindow()
    {
        var targets = new[]
        {
            Target(1, 100, 100, 200, 200),
            Target(2, 0, 0, 1000, 1000),
        };

        Assert.Equal(0, WindowPickerGeometry.HitTest(targets, 150, 150));
        Assert.Equal(1, WindowPickerGeometry.HitTest(targets, 50, 50));
    }

    [Fact]
    public void HitTest_NonSelectableTopmostWindowBlocksWindowsBehindIt()
    {
        var targets = new[]
        {
            Target(1, 0, 1040, 1920, 40, selectable: false),
            Target(2, 0, 0, 1920, 1080),
        };

        Assert.Equal(-1, WindowPickerGeometry.HitTest(targets, 500, 1060));
        Assert.Equal(1, WindowPickerGeometry.HitTest(targets, 500, 500));
    }

    [Fact]
    public void HitTest_EdgesAreHalfOpenAndEmptyAreasMiss()
    {
        var targets = new[]
        {
            Target(1, 0, 0, 100, 100),
            Target(2, 100, 0, 100, 100),
        };

        Assert.Equal(0, WindowPickerGeometry.HitTest(targets, 99, 50));
        Assert.Equal(1, WindowPickerGeometry.HitTest(targets, 100, 50));
        Assert.Equal(-1, WindowPickerGeometry.HitTest(targets, 250, 50));
        Assert.Equal(-1, WindowPickerGeometry.HitTest([Target(3, 0, 0, 0, 0)], 0, 0));
    }

    [Fact]
    public void HitTest_SupportsNegativeVirtualDesktopCoordinates()
    {
        var targets = new[] { Target(1, -1920, -200, 800, 600) };

        Assert.Equal(0, WindowPickerGeometry.HitTest(targets, -1500, 0));
        Assert.Equal(-1, WindowPickerGeometry.HitTest(targets, -1000, 0));
    }

    [Fact]
    public void ToMonitorLocal_ClipsToMonitorAndOffsetsToItsOrigin()
    {
        var monitor = new PixelRect(-1920, 0, 1920, 1080);

        Assert.Equal(
            new PixelRect(1720, 100, 200, 300),
            WindowPickerGeometry.ToMonitorLocal(new PixelRect(-200, 100, 600, 300), monitor));
        Assert.Null(WindowPickerGeometry.ToMonitorLocal(new PixelRect(10, 10, 100, 100), monitor));
    }

    [Fact]
    public void LabelMonitorIndex_PrefersMonitorContainingCenterThenLargestOverlap()
    {
        var monitors = new[]
        {
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(1920, 0, 2560, 1440),
        };

        Assert.Equal(1, WindowPickerGeometry.LabelMonitorIndex(new PixelRect(1800, 100, 800, 600), monitors));
        Assert.Equal(0, WindowPickerGeometry.LabelMonitorIndex(new PixelRect(1000, 100, 1000, 600), monitors));
        // Centre below both monitors: fall back to the biggest visible part.
        Assert.Equal(1, WindowPickerGeometry.LabelMonitorIndex(new PixelRect(1800, 1000, 900, 2000), monitors));
        Assert.Equal(-1, WindowPickerGeometry.LabelMonitorIndex(new PixelRect(9000, 9000, 10, 10), monitors));
    }

    [Fact]
    public void Cycle_SkipsNonSelectableTargetsAndWraps()
    {
        var targets = new[]
        {
            Target(1, 0, 1040, 1920, 40, selectable: false),
            Target(2, 0, 0, 100, 100),
            Target(3, 0, 0, 100, 100),
        };

        Assert.Equal(1, WindowPickerGeometry.Cycle(targets, -1, forward: true));
        Assert.Equal(2, WindowPickerGeometry.Cycle(targets, -1, forward: false));
        Assert.Equal(2, WindowPickerGeometry.Cycle(targets, 1, forward: true));
        Assert.Equal(1, WindowPickerGeometry.Cycle(targets, 2, forward: true));
        Assert.Equal(2, WindowPickerGeometry.Cycle(targets, 1, forward: false));
    }

    [Fact]
    public void Cycle_ReturnsMinusOneWhenNothingIsSelectable()
    {
        Assert.Equal(-1, WindowPickerGeometry.Cycle([], -1, forward: true));
        Assert.Equal(-1, WindowPickerGeometry.Cycle([Target(1, 0, 0, 10, 10, selectable: false)], -1, forward: true));
    }

    [Fact]
    public void FormatSize_UsesPhysicalPixels()
    {
        Assert.Equal("1920 × 1080", WindowPickerGeometry.FormatSize(new PixelRect(5, 5, 1920, 1080)));
    }
}
