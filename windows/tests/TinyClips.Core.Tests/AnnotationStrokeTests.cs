using TinyClips.Core.Editing;
using Windows.UI;

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

    [Fact]
    public void FillOpacityKeepsRgbAndClampsTheRequestedPercentage()
    {
        var color = Color.FromArgb(255, 12, 34, 56);

        var translucent = AnnotationColorMath.WithOpacity(color, 0.25);
        var clamped = AnnotationColorMath.WithOpacity(color, 2);
        var restored = AnnotationColorMath.WithOpacity(
            Color.FromArgb(0, 0, 0, 0),
            0.5,
            Color.FromArgb(255, 240, 241, 242));
        var transparentRed = AnnotationColorMath.WithOpacity(
            Color.FromArgb(0, 220, 10, 20),
            0.5,
            Color.FromArgb(255, 240, 241, 242));

        Assert.Equal((byte)64, translucent.A);
        Assert.Equal(color.R, translucent.R);
        Assert.Equal(color.G, translucent.G);
        Assert.Equal(color.B, translucent.B);
        Assert.Equal(0.25, AnnotationColorMath.Opacity(translucent), 2);
        Assert.Equal((byte)255, clamped.A);
        Assert.Equal((byte)240, restored.R);
        Assert.Equal((byte)128, restored.A);
        Assert.Equal((byte)220, transparentRed.R);
        Assert.Equal((byte)128, transparentRed.A);
    }
}
