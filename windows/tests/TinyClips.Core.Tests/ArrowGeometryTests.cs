using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class ArrowGeometryTests
{
    [Fact]
    public void CurvedArrowsAreHitAlongTheCurveNotInsideTheArc()
    {
        var start = new PointD(100, 100);
        var end = new PointD(400, 100);
        var handle = ArrowGeometry.Handle(start, end, 0.2);

        Assert.True(ArrowGeometry.DistanceToCurve(handle, start, end, 0.2) < 0.5);
        // The chord's midpoint is inside the arc, 60 px from the curve.
        Assert.True(ArrowGeometry.DistanceToCurve(new PointD(250, 100), start, end, 0.2) > 55);
    }

    [Fact]
    public void ThickArrowsAreHitAcrossTheirHeadAndShaft()
    {
        var outline = ArrowGeometry.Build(
            new PointD(100, 100), new PointD(400, 100), 20, ArrowShapeOptions.Classic, StrokeCap.Round, StrokeJoin.Miter).Points;
        // In the head's wing, 24 px from the centerline: farther than half the shaft's width.
        Assert.True(ArrowGeometry.OutlineContains(outline, new PointD(345, 124), 0));
        // Beside the 20 px shaft.
        Assert.False(ArrowGeometry.OutlineContains(outline, new PointD(250, 130), 0));
        Assert.True(ArrowGeometry.OutlineContains(outline, new PointD(250, 113), 4));
    }
}
