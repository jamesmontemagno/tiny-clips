using TinyClips.Core.Capture;
using TinyClips.Core.Models;

namespace TinyClips.Core.Tests;

public sealed class WebcamOverlayCompositorTests
{
    [Fact]
    public void Draw_TreatsWebcamPixelsAsOpaque_WhenSourceAlphaIsZero()
    {
        var compositor = new WebcamOverlayCompositor(
            WebcamCornerPosition.TopLeft,
            WebcamSizePreset.Small,
            WebcamShape.Rectangle,
            cornerRadius: null);
        var destination = new byte[100 * 100 * 4];
        var webcam = new byte[10 * 10 * 4];

        for (var i = 0; i < webcam.Length; i += 4)
        {
            webcam[i] = 0;
            webcam[i + 1] = 0;
            webcam[i + 2] = 255;
            webcam[i + 3] = 0;
        }

        compositor.Draw(destination, 100, 100, new WebcamFrame(webcam, 10, 10, TimeSpan.Zero));

        Assert.Contains(destination.Chunk(4), pixel => pixel[2] == 255);
    }

    [Fact]
    public void Draw_UsesCornerSelectedForEachTimelineFrame()
    {
        var compositor = new WebcamOverlayCompositor(
            WebcamCornerPosition.TopLeft,
            WebcamSizePreset.Small,
            WebcamShape.Rectangle,
            cornerRadius: null);
        var webcam = Enumerable.Repeat((byte)255, 10 * 10 * 4).ToArray();
        var topLeft = new byte[200 * 200 * 4];
        var bottomRight = new byte[200 * 200 * 4];
        var frame = new WebcamFrame(webcam, 10, 10, TimeSpan.Zero);

        compositor.Draw(topLeft, 200, 200, frame, WebcamCornerPosition.TopLeft);
        compositor.Draw(bottomRight, 200, 200, frame, WebcamCornerPosition.BottomRight);

        Assert.Equal(255, RedAt(topLeft, 200, 12, 12));
        Assert.Equal(0, RedAt(bottomRight, 200, 12, 12));
        Assert.Equal(255, RedAt(bottomRight, 200, 187, 187));
    }

    private static byte RedAt(byte[] pixels, int width, int x, int y) =>
        pixels[((y * width) + x) * 4 + 2];
}
