using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// How blocks that overlap on the annotation lane share one label, and which blocks a press
/// takes. The numbers and the results are the Mac's ("Lane Overlaps" in StudioEditorModelTests.swift).
/// </summary>
public sealed class StudioLaneOverlapTests
{
    [Fact]
    public void BlocksWithTheSameRangeShowOneLabelTheTopOneUntilOneIsSelected()
    {
        (double, double)[] blocks = [(10, 50), (10, 50)];
        var overlaps = StudioEditorModel.GetLaneOverlaps(blocks, selectedIndex: null);
        Assert.Equal([2, 2], overlaps.Select(o => o.Count));
        Assert.Equal([false, true], overlaps.Select(o => o.ShowsLabel));
        Assert.Equal(overlaps[0].Group, overlaps[1].Group);

        var selected = StudioEditorModel.GetLaneOverlaps(blocks, selectedIndex: 0);
        Assert.Equal([true, false], selected.Select(o => o.ShowsLabel));
    }

    [Fact]
    public void OverlapsChainIntoOneGroupAndBlocksThatOnlyTouchStayApart()
    {
        // 0 and 2 do not overlap, but each overlaps 1; 3 starts where 2 ends.
        (double, double)[] blocks = [(0, 40), (30, 40), (60, 40), (100, 20)];
        var overlaps = StudioEditorModel.GetLaneOverlaps(blocks, selectedIndex: 3);
        Assert.Equal([3, 3, 3, 1], overlaps.Select(o => o.Count));
        Assert.Equal([0, 0, 0, 3], overlaps.Select(o => o.Group));
        Assert.Equal([false, false, true, true], overlaps.Select(o => o.ShowsLabel));
        Assert.False(overlaps[3].OverlapsOthers);
    }

    [Fact]
    public void NarrowBlocksOverlapWhereTheyAreDrawnAndTheOrderTheyAreGivenInDoesNotMatter()
    {
        // A later annotation that starts earlier, and a narrow one drawn wider than its range.
        (double, double)[] blocks = [(50, 10), (20, 35), (200, 10)];
        var overlaps = StudioEditorModel.GetLaneOverlaps(blocks, selectedIndex: null);
        Assert.Equal([2, 2, 1], overlaps.Select(o => o.Count));
        Assert.Equal([false, true, true], overlaps.Select(o => o.ShowsLabel));
        Assert.Empty(StudioEditorModel.GetLaneOverlaps([], selectedIndex: 0));
    }

    [Fact]
    public void APressTakesTheSelectedBlockFirstThenTheOneOnTop()
    {
        (double, double)[] blocks = [(0, 100), (20, 50), (40, 50)];
        Assert.Equal([2, 1, 0], StudioEditorModel.GetLaneBlocksAt(45, blocks, selectedIndex: null));
        Assert.Equal([0, 2, 1], StudioEditorModel.GetLaneBlocksAt(45, blocks, selectedIndex: 0));
        Assert.Equal([0], StudioEditorModel.GetLaneBlocksAt(10, blocks, selectedIndex: 2));
        Assert.Empty(StudioEditorModel.GetLaneBlocksAt(100, blocks, selectedIndex: null));
    }

    [Fact]
    public void ABlockThatOverlapsOthersSaysHowMany()
    {
        Assert.Null(StudioEditorModel.GetLaneOverlapText(1));
        Assert.Equal("Overlaps 1 other annotation", StudioEditorModel.GetLaneOverlapText(2));
        Assert.Equal("Overlaps 3 other annotations", StudioEditorModel.GetLaneOverlapText(4));
    }
}
