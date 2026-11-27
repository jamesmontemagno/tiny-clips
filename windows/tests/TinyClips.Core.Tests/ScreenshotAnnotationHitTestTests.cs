using System.Numerics;
using TinyClips.Core.Editing;
using Windows.Foundation;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotAnnotationHitTestTests
{
    [Theory]
    [InlineData(EditTool.Rectangle)]
    [InlineData(EditTool.Ellipse)]
    [InlineData(EditTool.Pen)]
    [InlineData(EditTool.Line)]
    [InlineData(EditTool.Arrow)]
    [InlineData(EditTool.Text)]
    [InlineData(EditTool.Counter)]
    [InlineData(EditTool.Emoji)]
    [InlineData(EditTool.Redact)]
    public void DrawingToolHitsOnlyItsSelection(EditTool tool)
    {
        var old = new ScreenshotAnnotation { Tool = EditTool.Text, Bounds = new Rect(0, 0, 100, 100) };
        var selected = new ScreenshotAnnotation { Tool = tool, Bounds = new Rect(80, 80, 80, 80) };
        selected.Points.Add(new Vector2(80, 80));
        selected.Points.Add(new Vector2(160, 160));
        ScreenshotAnnotation[] items = [old, selected];

        Assert.Null(ScreenshotAnnotationHitTest.FindBody(tool, items, selected, new Point(20, 20)));
        Assert.Same(selected, ScreenshotAnnotationHitTest.FindBody(tool, items, selected, new Point(90, 90)));
        Assert.Null(ScreenshotAnnotationHitTest.FindBody(tool, items, null, new Point(90, 90)));
        Assert.Null(ScreenshotAnnotationHitTest.FindBody(tool, items, selected, new Point(300, 300)));

        // Explicit Select can pick old items, and still picks the topmost body in an overlap.
        Assert.Same(old, ScreenshotAnnotationHitTest.FindBody(EditTool.Select, items, selected, new Point(20, 20)));
        Assert.Same(selected, ScreenshotAnnotationHitTest.FindBody(EditTool.Select, items, old, new Point(90, 90)));
    }

    [Fact]
    public void DiagonalLineLeavesRoomToDrawInsideItsBoundsButAwayFromItsShaft()
    {
        var line = new ScreenshotAnnotation
        {
            Tool = EditTool.Line, Bounds = new Rect(10, 10, 200, 200), Thickness = 8,
            OutlineWidth = 12, StrokeAlignment = StrokeAlignment.Outside,
        };
        line.Points.Add(new Vector2(10, 10));
        line.Points.Add(new Vector2(210, 210));
        var offLine = new Point(35, 185);
        Assert.Null(ScreenshotAnnotationHitTest.FindBody(EditTool.Line, [line], line, offLine));
        Assert.Same(line, ScreenshotAnnotationHitTest.FindBody(EditTool.Select, [line], null, offLine));
        Assert.Same(line, ScreenshotAnnotationHitTest.FindBody(EditTool.Line, [line], line, new Point(100, 100)));
        // About 14 px from the centerline: on its outside stroke, not on the 8 px shaft.
        Assert.Same(line, ScreenshotAnnotationHitTest.FindBody(EditTool.Line, [line], line, new Point(90, 110)));
    }

    [Fact]
    public void BentArrowHitsItsCurveRatherThanItsBoundingBox()
    {
        var arrow = new ScreenshotAnnotation
        {
            Tool = EditTool.Arrow, Bounds = new Rect(10, 100, 200, 0),
            Arrow = ArrowShapeOptions.Classic with { Bend = 0.4 }, Thickness = 4,
        };
        arrow.Points.Add(new Vector2(10, 100));
        arrow.Points.Add(new Vector2(210, 100));
        var handle = ArrowGeometry.Handle(new PointD(10, 100), new PointD(210, 100), arrow.Arrow.Bend);
        Assert.Same(arrow, ScreenshotAnnotationHitTest.FindBody(EditTool.Arrow, [arrow], arrow, new Point(handle.X, handle.Y)));
        Assert.Null(ScreenshotAnnotationHitTest.FindBody(EditTool.Arrow, [arrow], arrow, new Point(110, 100)));
    }

    [Fact]
    public void SelectedTextIncludesItsRotatedBoxPadding()
    {
        var text = new ScreenshotAnnotation
        {
            Tool = EditTool.Text, Bounds = new Rect(50, 90, 100, 20), Rotation = 90,
            TextBoxStyle = TextBoxStyle.Plain with { Padding = 20 },
        };
        // In the decorated box after turning about (100, 100), beyond the bare text.
        Assert.Same(text, ScreenshotAnnotationHitTest.FindBody(EditTool.Text, [text], text, new Point(100, 35)));
        Assert.Null(ScreenshotAnnotationHitTest.FindBody(EditTool.Text, [text], text, new Point(40, 100)));
    }
}
