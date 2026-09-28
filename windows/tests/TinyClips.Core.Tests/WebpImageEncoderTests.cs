using SkiaSharp;
using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class WebpImageEncoderTests
{
    [Theory]
    [InlineData(100, 2, 2)]
    [InlineData(50, 1, 1)]
    public void Encode_WritesDecodableWebpWithRequestedScale(int scale, int expectedWidth, int expectedHeight)
    {
        // The Windows-only native codec ships with the app; Windows CI exercises this path.
        // Do not try loading the Win32 native library when Core tests are run on Linux.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Premultiplied BGRA: transparent, red, green, blue.
        byte[] pixels =
        [
            0, 0, 0, 0, 0, 0, 255, 255,
            0, 255, 0, 255, 255, 0, 0, 255,
        ];

        var encoded = WebpImageEncoder.Encode(pixels, 2, 2, scale, 0.85);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(encoded, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(encoded, 8, 4));
        using var decoded = SKBitmap.Decode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(expectedWidth, decoded.Width);
        Assert.Equal(expectedHeight, decoded.Height);
        if (scale == 100)
        {
            Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
        }
    }
}
