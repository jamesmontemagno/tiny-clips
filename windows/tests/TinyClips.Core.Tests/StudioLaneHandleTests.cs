using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// What a press on a block of the zoom, cut, or speed lane takes hold of, and where the block's
/// handles are. The numbers and the results are the Mac's ("Lane Block Handles" in
/// StudioEditorModelTests.swift).
/// </summary>
public sealed class StudioLaneHandleTests
{
    [Fact]
    public void AWideBlockIsTakenByAnEndWithinAHandlesWidthAndByItsBodyBetween()
    {
        foreach (var isSelected in new[] { false, true })
        {
            Assert.Equal(StudioLaneBlockPart.Start, StudioEditorModel.GetLaneBlockPart(0, 100, isSelected));
            Assert.Equal(StudioLaneBlockPart.Start, StudioEditorModel.GetLaneBlockPart(8, 100, isSelected));
            Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(8.5, 100, isSelected));
            Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(50, 100, isSelected));
            Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(91.5, 100, isSelected));
            Assert.Equal(StudioLaneBlockPart.End, StudioEditorModel.GetLaneBlockPart(92, 100, isSelected));
            Assert.Equal(StudioLaneBlockPart.End, StudioEditorModel.GetLaneBlockPart(100, 100, isSelected));
            Assert.Equal(0, StudioEditorModel.GetLaneHandleOutset(100, isSelected));
        }
    }

    [Fact]
    public void TheNarrowestBlockWithHandlesInsideStillHasABodyToMoveItBy()
    {
        const double Width = StudioEditorModel.LaneHandleMinimumBlockWidth;
        Assert.True(StudioEditorModel.LaneBlockHasInsideHandles(Width));
        Assert.False(StudioEditorModel.LaneBlockHasInsideHandles(Width - 0.5));
        Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(Width / 2, Width, isSelected: false));
        Assert.True(Width > StudioEditorModel.LaneHandleWidth * 2);
    }

    [Fact]
    public void ANarrowBlockIsOnlyMovedUntilItIsSelected()
    {
        foreach (var x in new[] { -6.0, 0, 5, 10, 16 })
        {
            Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(x, 10, isSelected: false));
        }

        Assert.Equal(0, StudioEditorModel.GetLaneHandleOutset(10, isSelected: false));
    }

    [Fact]
    public void ANarrowSelectedBlockHasItsHandlesOutsideItsEnds()
    {
        Assert.Equal(StudioEditorModel.LaneHandleWidth, StudioEditorModel.GetLaneHandleOutset(10, isSelected: true));
        Assert.Equal(StudioLaneBlockPart.Start, StudioEditorModel.GetLaneBlockPart(-6, 10, isSelected: true));
        Assert.Equal(StudioLaneBlockPart.Start, StudioEditorModel.GetLaneBlockPart(-0.1, 10, isSelected: true));
        Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(0, 10, isSelected: true));
        Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(10, 10, isSelected: true));
        Assert.Equal(StudioLaneBlockPart.End, StudioEditorModel.GetLaneBlockPart(10.1, 10, isSelected: true));
        Assert.Equal(StudioLaneBlockPart.End, StudioEditorModel.GetLaneBlockPart(16, 10, isSelected: true));
    }

    [Fact]
    public void AWidthThatIsNotANumberHasNoHandles()
    {
        Assert.False(StudioEditorModel.LaneBlockHasInsideHandles(double.NaN));
        Assert.False(StudioEditorModel.LaneBlockHasInsideHandles(double.PositiveInfinity));
        Assert.Equal(StudioLaneBlockPart.Body, StudioEditorModel.GetLaneBlockPart(3, double.NaN, isSelected: false));
    }
}
