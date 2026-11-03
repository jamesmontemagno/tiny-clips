using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class AnnotationStrokeTests
{
    [Fact]
    public void OnlyOpenStrokesTakeCapsAndOnlyRectanglesTakeCorners()
    {
        Assert.True(EditTool.Line.UsesStartCap());
        Assert.True(EditTool.Line.UsesEndCap());
        Assert.True(EditTool.Arrow.UsesStartCap());
        Assert.False(EditTool.Arrow.UsesEndCap());
        Assert.False(EditTool.Rectangle.UsesStartCap());
        Assert.False(EditTool.Pen.UsesStartCap());

        Assert.True(EditTool.Rectangle.UsesJoin());
        Assert.True(EditTool.Arrow.UsesJoin());
        Assert.False(EditTool.Ellipse.UsesJoin());
        Assert.False(EditTool.Line.UsesJoin());
    }

    [Fact]
    public void PenStrokesFadeAsOneLayer()
    {
        Assert.False(EditTool.Arrow.NeedsOpacityLayer());
        Assert.True(EditTool.Pen.NeedsOpacityLayer());
        Assert.False(EditTool.Line.NeedsOpacityLayer());
        Assert.False(EditTool.Rectangle.NeedsOpacityLayer());
    }

    [Fact]
    public void NewAnnotationsDefaultToRoundEndsAndCorners()
    {
        var snapshot = new ScreenshotAnnotation { Tool = EditTool.Line }.CaptureSnapshot();

        Assert.Equal(StrokeCap.Round, snapshot.StartCap);
        Assert.Equal(StrokeCap.Round, snapshot.EndCap);
        Assert.Equal(StrokeJoin.Round, snapshot.LineJoin);
    }

    [Fact]
    public void SnapshotsCarryCapsAndJoin()
    {
        var annotation = new ScreenshotAnnotation
        {
            Tool = EditTool.Line,
            StartCap = StrokeCap.Butt,
            EndCap = StrokeCap.Square,
            LineJoin = StrokeJoin.Bevel,
        };

        var snapshot = annotation.CaptureSnapshot();

        Assert.Equal(StrokeCap.Butt, snapshot.StartCap);
        Assert.Equal(StrokeCap.Square, snapshot.EndCap);
        Assert.Equal(StrokeJoin.Bevel, snapshot.LineJoin);
    }
}
