using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class CapturedFrameTests
{
    private static CapturedFrame MakeFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)x;      // B
                pixels[i + 1] = (byte)y;  // G
                pixels[i + 2] = 0xAA;     // R
                pixels[i + 3] = 0xFF;     // A
            }
        }

        return new CapturedFrame(pixels, width, height);
    }

    [Fact]
    public void Crop_ReturnsTightlyPackedSubRectangle()
    {
        var frame = MakeFrame(8, 6);

        var cropped = frame.Crop(new PixelRect(2, 1, 3, 4));

        Assert.Equal(3, cropped.Width);
        Assert.Equal(4, cropped.Height);
        Assert.Equal(3 * 4 * 4, cropped.BgraPixels.Length);
        // Top-left of crop is source pixel (2,1).
        Assert.Equal(2, cropped.BgraPixels[0]);
        Assert.Equal(1, cropped.BgraPixels[1]);
        // Bottom-right of crop is source pixel (4,4).
        var last = ((3 * 3) + 2) * 4;
        Assert.Equal(4, cropped.BgraPixels[last]);
        Assert.Equal(4, cropped.BgraPixels[last + 1]);
    }

    [Fact]
    public void Crop_ClampsRegionToFrameBounds()
    {
        var frame = MakeFrame(5, 5);

        var cropped = frame.Crop(new PixelRect(3, 3, 10, 10));

        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(3, cropped.BgraPixels[0]);
        Assert.Equal(3, cropped.BgraPixels[1]);
    }

    [Fact]
    public void Crop_FullFrameReturnsSameInstance()
    {
        var frame = MakeFrame(4, 3);

        var cropped = frame.Crop(new PixelRect(0, 0, 4, 3));

        Assert.Same(frame, cropped);
    }
}
