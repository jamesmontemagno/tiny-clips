using TinyClips.Core.Capture;
using TinyClips.Core.Models;

namespace TinyClips.Core.Tests;

public sealed class GpuPipelineSharedMathTests
{
    [Theory]
    [InlineData(WebcamCornerPosition.BottomRight, WebcamShape.Circle, WebcamSizePreset.Medium)]
    [InlineData(WebcamCornerPosition.TopLeft, WebcamShape.RoundedRectangle, WebcamSizePreset.Large)]
    [InlineData(WebcamCornerPosition.BottomLeft, WebcamShape.Rectangle, WebcamSizePreset.Small)]
    [InlineData(WebcamCornerPosition.TopRight, WebcamShape.RoundedRectangle, WebcamSizePreset.Medium)]
    public void WebcamOverlayLayout_MatchesCpuCompositorPlacement(WebcamCornerPosition corner, WebcamShape shape, WebcamSizePreset size)
    {
        const int frameWidth = 1920;
        const int frameHeight = 1080;
        const int camWidth = 1280;
        const int camHeight = 720;

        var layout = WebcamOverlayLayout.Compute(frameWidth, frameHeight, camWidth, camHeight, corner, size, shape, null);

        // Render through the CPU compositor and locate the blended bounding box; it must equal
        // the layout rectangle the GPU compositor uses, so both pipelines place the PiP identically.
        var frame = new byte[frameWidth * frameHeight * 4];
        var cam = new byte[camWidth * camHeight * 4];
        for (var i = 0; i < cam.Length; i += 4)
        {
            cam[i] = 255;
            cam[i + 1] = 255;
            cam[i + 2] = 255;
            cam[i + 3] = 255;
        }

        var compositor = new WebcamOverlayCompositor(corner, size, shape, null);
        compositor.Draw(frame, frameWidth, frameHeight, new WebcamFrame(cam, camWidth, camHeight, TimeSpan.Zero), corner);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < frameHeight; y++)
        {
            for (var x = 0; x < frameWidth; x++)
            {
                if (frame[((y * frameWidth) + x) * 4] != 0)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        Assert.False(layout.IsEmpty);
        Assert.Equal(layout.OverlayX, minX);
        Assert.Equal(layout.OverlayY, minY);
        Assert.Equal(layout.OverlayX + layout.OverlayWidth - 1, maxX);
        Assert.Equal(layout.OverlayY + layout.OverlayHeight - 1, maxY);
    }

    [Fact]
    public void WebcamOverlayLayout_CropPreservesOverlayAspect()
    {
        var layout = WebcamOverlayLayout.Compute(1920, 1080, 1280, 720, WebcamCornerPosition.BottomRight, WebcamSizePreset.Medium, WebcamShape.Circle, null);

        Assert.Equal(layout.OverlayWidth, layout.OverlayHeight);
        Assert.Equal(layout.CropWidth, layout.CropHeight);
        Assert.Equal(720, layout.CropHeight);
        Assert.Equal((1280 - 720) / 2, layout.CropX);
        Assert.Equal(0, layout.CornerRadiusPx);
    }

    [Fact]
    public void WebcamOverlayLayout_RoundedRectangleUsesConfiguredRadiusClampedToHalfSize()
    {
        var layout = WebcamOverlayLayout.Compute(1920, 1080, 1280, 720, WebcamCornerPosition.TopLeft, WebcamSizePreset.Small, WebcamShape.RoundedRectangle, 10_000);

        Assert.Equal(Math.Min(layout.OverlayWidth, layout.OverlayHeight) / 2, layout.CornerRadiusPx);
    }

    [Fact]
    public void WebcamOverlayLayout_EmptyForDegenerateFrame()
    {
        Assert.True(WebcamOverlayLayout.Compute(0, 0, 1280, 720, WebcamCornerPosition.BottomRight, WebcamSizePreset.Medium, WebcamShape.Circle, null).IsEmpty);
    }

    [Fact]
    public void TryComputeRing_AnimatesRadiusAndFadesAlpha()
    {
        var style = new MouseClickOverlayStyle("#FF0000", Size: 40, StrokeWidth: 4, Opacity: 0.8, DurationSeconds: 1.0);
        var click = new MouseClickSample(TimeSeconds: 2.0, ScreenX: 1000, ScreenY: 500);

        Assert.False(MouseClickOverlayCompositor.TryComputeRing(click, 1.9, 0, 0, style, out _));
        Assert.False(MouseClickOverlayCompositor.TryComputeRing(click, 3.1, 0, 0, style, out _));

        Assert.True(MouseClickOverlayCompositor.TryComputeRing(click, 2.0, 100, 50, style, out var start));
        Assert.Equal(900, start.CenterX);
        Assert.Equal(450, start.CenterY);
        Assert.Equal(20, start.Radius);
        Assert.Equal(2, start.HalfStroke);
        Assert.Equal(0.8, start.Alpha, 6);

        Assert.True(MouseClickOverlayCompositor.TryComputeRing(click, 2.5, 100, 50, style, out var mid));
        Assert.Equal(20 + (40 * 0.58 * 0.5), mid.Radius, 6);
        Assert.Equal(0.4, mid.Alpha, 6);
    }

    [Fact]
    public void ParseColor_HandlesRgbArgbAndFallback()
    {
        Assert.Equal(((byte)0x0A, (byte)0x84, (byte)0xFF), MouseClickOverlayCompositor.ParseColor("#0A84FF"));
        Assert.Equal(((byte)0x0A, (byte)0x84, (byte)0xFF), MouseClickOverlayCompositor.ParseColor("FF0A84FF"));
        Assert.Equal(((byte)255, (byte)214, (byte)10), MouseClickOverlayCompositor.ParseColor("nope"));
    }
}
