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
}
