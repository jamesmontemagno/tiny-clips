using System.Drawing;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using Color4 = Vortice.Mathematics.Color4;

namespace TinyClips.Core.Tests;

/// <summary>The renderer's pure helpers. What it draws is checked from pixels by the StudioRenderCheck tool.</summary>
public sealed class StudioRenderingHelperTests
{
    [Theory]
    [InlineData(115.2, 64.8, 1689.6, 950.4, 115, 65, 1690, 950)]
    [InlineData(1498.8, 658.8, 388.8, 388.8, 1499, 659, 389, 389)]
    [InlineData(64.8, 191.7675, 1238.16, 696.465, 65, 192, 1238, 696)]
    [InlineData(10, 20, 300, 200, 10, 20, 300, 200)]
    public void PixelAlign_MovesEveryEdgeToTheNearestWholePixel(double x, double y, double width, double height, double left, double top, double alignedWidth, double alignedHeight)
    {
        Assert.Equal(new StudioFrameRect(left, top, alignedWidth, alignedHeight), StudioSceneRenderer.PixelAlign(new StudioFrameRect(x, y, width, height)));
    }

    [Fact]
    public void PixelAlign_RoundsHalvesAwayFromZeroAsTheFormatAndTheMacDo()
    {
        // Edges at 0.5, 2.5, 1.5 and 3.5: to even they would go to 0, 2, 2 and 4.
        Assert.Equal(new StudioFrameRect(1, 3, 1, 1), StudioSceneRenderer.PixelAlign(new StudioFrameRect(0.5, 2.5, 1, 1)));
        Assert.Equal(new StudioFrameRect(-1, -3, 2, 1), StudioSceneRenderer.PixelAlign(new StudioFrameRect(-0.5, -2.5, 1, 1)));
    }

    [Fact]
    public void PixelAlign_NeverMakesALayerDisappear()
    {
        Assert.Equal(new StudioFrameRect(7, 9, 1, 1), StudioSceneRenderer.PixelAlign(new StudioFrameRect(7.1, 9.2, 0.2, 0.1)));
    }

    [Theory]
    [InlineData("#C86432", 200, 100, 50, 255)]
    [InlineData("#c86432", 200, 100, 50, 255)]
    [InlineData("C86432", 200, 100, 50, 255)]
    [InlineData("  #C86432 ", 200, 100, 50, 255)]
    [InlineData("#C8643280", 200, 100, 50, 128)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    [InlineData("#FFFFFF", 255, 255, 255, 255)]
    public void ParseColor_ReadsRgbAndRgba(string text, int red, int green, int blue, int alpha)
    {
        var color = StudioSceneRenderer.ParseColor(text, new Color4(0.1f, 0.2f, 0.3f, 0.4f));

        Assert.Equal(red / 255f, color.R, 6);
        Assert.Equal(green / 255f, color.G, 6);
        Assert.Equal(blue / 255f, color.B, 6);
        Assert.Equal(alpha / 255f, color.A, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("orange")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GG0000")]
    [InlineData("#+12345")]
    [InlineData("#12 456")]
    public void ParseColor_FallsBackForAnythingElse(string? text)
    {
        var fallback = new Color4(0.1f, 0.2f, 0.3f, 0.4f);

        Assert.Equal(fallback, StudioSceneRenderer.ParseColor(text, fallback));
    }

    /// <summary>
    /// For each EXIF orientation: store an upright picture the way that orientation says a camera
    /// did, turn it by what the renderer applies, and get the upright picture back.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void OrientationToRotateFlip_BringsEveryExifOrientationUpright(int orientation)
    {
        const int Width = 3;
        const int Height = 2;
        static Color Upright(int x, int y) => Color.FromArgb(255, 40 + (x * 70), 30 + (y * 100), 200);

        // EXIF says where the stored picture's first row and first column are in the upright one:
        // 1 top/left, 2 top/right, 3 bottom/right, 4 bottom/left, 5 left/top, 6 right/top,
        // 7 right/bottom, 8 left/bottom. From 5 on the stored picture is on its side.
        var sideways = orientation >= 5;
        using var stored = new Bitmap(sideways ? Height : Width, sideways ? Width : Height);
        for (var y = 0; y < stored.Height; y++)
        {
            for (var x = 0; x < stored.Width; x++)
            {
                var (ux, uy) = orientation switch
                {
                    2 => (Width - 1 - x, y),
                    3 => (Width - 1 - x, Height - 1 - y),
                    4 => (x, Height - 1 - y),
                    5 => (y, x),
                    6 => (Width - 1 - y, x),
                    7 => (Width - 1 - y, Height - 1 - x),
                    8 => (y, Height - 1 - x),
                    _ => (x, y),
                };
                stored.SetPixel(x, y, Upright(ux, uy));
            }
        }

        stored.RotateFlip(StudioBackgroundImage.OrientationToRotateFlip(orientation));

        Assert.Equal(Width, stored.Width);
        Assert.Equal(Height, stored.Height);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                Assert.Equal(Upright(x, y).ToArgb(), stored.GetPixel(x, y).ToArgb());
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void OrientationToRotateFlip_LeavesAnUnknownOrientationAlone(int orientation)
    {
        Assert.Equal(RotateFlipType.RotateNoneFlipNone, StudioBackgroundImage.OrientationToRotateFlip(orientation));
    }

    [Fact]
    public void BackgroundImage_TryDecode_ScalesToTheLimitAndAppliesTheOrientation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TinyClipsStudioTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // 40×20: red on the left half, blue on the right.
            var path = Path.Combine(folder, "image.png");
            using (var bitmap = new Bitmap(40, 20))
            {
                for (var y = 0; y < 20; y++)
                {
                    for (var x = 0; x < 40; x++)
                    {
                        bitmap.SetPixel(x, y, x < 20 ? Color.FromArgb(255, 200, 0, 0) : Color.FromArgb(255, 0, 0, 200));
                    }
                }

                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            Assert.True(StudioBackgroundImage.TryDecode(path, 3840, out var pixels, out var width, out var height));
            Assert.Equal((40, 20), (width, height));
            Assert.Equal(40 * 20 * 4, pixels.Length);

            // BGRA, top row first: the first pixel is red, the last one blue.
            Assert.Equal(new byte[] { 0, 0, 200, 255 }, pixels[..4]);
            Assert.Equal(new byte[] { 200, 0, 0, 255 }, pixels[^4..]);

            // A limit below its long side scales it down, keeping its shape.
            Assert.True(StudioBackgroundImage.TryDecode(path, 10, out pixels, out width, out height));
            Assert.Equal((10, 5), (width, height));
            Assert.Equal(new byte[] { 0, 0, 200, 255 }, pixels[..4]);
            Assert.Equal(new byte[] { 200, 0, 0, 255 }, pixels[^4..]);

            // Not an image, and not there at all.
            var broken = Path.Combine(folder, "broken.png");
            File.WriteAllText(broken, "not an image");
            Assert.False(StudioBackgroundImage.TryDecode(broken, 3840, out pixels, out width, out height));
            Assert.Empty(pixels);
            Assert.False(StudioBackgroundImage.TryDecode(Path.Combine(folder, "missing.png"), 3840, out _, out _, out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void BackgroundImage_TryDecode_ReadsWebp()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TinyClipsStudioTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // 64×32: red on the left half, blue on the right, as the app would save a screenshot.
            var source = new byte[64 * 32 * 4];
            for (var i = 0; i < source.Length; i += 4)
            {
                var left = (i / 4) % 64 < 32;
                source[i] = (byte)(left ? 0 : 200);
                source[i + 2] = (byte)(left ? 200 : 0);
                source[i + 3] = 255;
            }

            var path = Path.Combine(folder, "image.webp");
            File.WriteAllBytes(path, TinyClips.Core.Capture.WebpImageEncoder.Encode(source, 64, 32, 100, 1.0));

            Assert.True(StudioBackgroundImage.TryDecode(path, 3840, out var pixels, out var width, out var height));
            Assert.Equal((64, 32), (width, height));
            AssertNear([0, 0, 200, 255], pixels.AsSpan(((8 * 64) + 8) * 4, 4));
            AssertNear([200, 0, 0, 255], pixels.AsSpan(((24 * 64) + 56) * 4, 4));

            // A limit below its long side scales it down, keeping its shape.
            Assert.True(StudioBackgroundImage.TryDecode(path, 16, out pixels, out width, out height));
            Assert.Equal((16, 8), (width, height));
            AssertNear([0, 0, 200, 255], pixels.AsSpan(((2 * 16) + 2) * 4, 4));
            AssertNear([200, 0, 0, 255], pixels.AsSpan(((6 * 16) + 13) * 4, 4));

            // A WebP cut short is not an image.
            var cut = Path.Combine(folder, "cut.webp");
            File.WriteAllBytes(cut, File.ReadAllBytes(path)[..40]);
            Assert.False(StudioBackgroundImage.TryDecode(cut, 3840, out pixels, out _, out _));
            Assert.Empty(pixels);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // WebP is lossy: a flat colour comes back within a few levels.
        static void AssertNear(byte[] want, ReadOnlySpan<byte> got)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                Assert.InRange(got[channel], want[channel] - 6, want[channel] + 6);
            }
        }
    }
}
