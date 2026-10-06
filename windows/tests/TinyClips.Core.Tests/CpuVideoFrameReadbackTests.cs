using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

[Collection("CPU recording buffers")]
public sealed class CpuVideoFrameReadbackTests
{
    [Theory]
    [InlineData(4, 2, 0, 1, 8, 4)]
    [InlineData(2, 4, 2, 0, 3, 6)]
    [InlineData(12, 6, 0, 1, 8, 4)]
    [InlineData(6, 12, 2, 0, 3, 6)]
    public void ShrinkingAndGrowingWindowsKeepConfiguredDimensions(
        int width, int height, int left, int top, int drawWidth, int drawHeight)
    {
        var output = new CapturedFrame(new byte[8 * 6 * 4], 8, 6);
        var pitch = (width * 4) + 8;
        var surface = Surface(width, height, pitch, version: 7);
        Array.Fill(output.BgraPixels, (byte)99);
        CpuVideoFrameReadback.CopyTo(surface, pitch, width, height, width, height, output, null, letterbox: true);

        Assert.Equal(8, output.Width);
        Assert.Equal(6, output.Height);
        Assert.Equal(192, output.BgraPixels.Length);
        Assert.Equal(192u, CpuVideoBuffer.Create(output).Length);
        for (var row = 0; row < output.Height; row++)
        {
            for (var column = 0; column < output.Width; column++)
            {
                var inContent = column >= left && column < left + drawWidth &&
                    row >= top && row < top + drawHeight;
                Assert.Equal(new byte[] { (byte)(inContent ? 7 : 0), 0, 0, 255 }, Pixel(output, column, row));
            }
        }
    }

    [Fact]
    public void OldPoolSurfacePaddingNeverBecomesResizedWindowContent()
    {
        const int width = 8;
        const int height = 6;
        const int pitch = 40;
        var surface = Surface(width, height, pitch);
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                if (column >= 4 || row >= 2)
                {
                    surface.AsSpan((row * pitch) + (column * 4), 4).Fill(238);
                }
            }
        }

        var output = new CapturedFrame(new byte[192], width, height);
        CpuVideoFrameReadback.CopyTo(surface, pitch, width, height, 4, 2, output, null, letterbox: true);

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(output, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(output, 7, 5));
        Assert.Equal(new byte[] { 1, 1, 100, 255 }, Pixel(output, 0, 1));
        Assert.Equal(new byte[] { 4, 2, 100, 255 }, Pixel(output, 7, 4));
        Assert.DoesNotContain((byte)238, output.BgraPixels);
    }

    [Fact]
    public void GrowthBeyondOldPoolBoundsIsClippedUntilTheNextFullResolutionFrame()
    {
        var output = new CapturedFrame(new byte[192], 8, 6);
        CpuVideoFrameReadback.CopyTo(Surface(8, 6, 40), 40, 8, 6, 16, 12, output, null, letterbox: true);
        Assert.Equal(new byte[] { 8, 6, 100, 255 }, Pixel(output, 7, 5));

        CpuVideoFrameReadback.CopyTo(Surface(16, 12, 72), 72, 16, 12, 16, 12, output, null, letterbox: true);
        Assert.Equal(new byte[] { 2, 2, 100, 255 }, Pixel(output, 0, 0));
        Assert.Equal(new byte[] { 16, 12, 100, 255 }, Pixel(output, 7, 5));
        Assert.Equal(192u, CpuVideoBuffer.Create(output).Length);
    }

    [Fact]
    public void ClippedRegionKeepsPhysicalCoordinatesAndPadsMissingPixelsWithoutScaling()
    {
        var output = new CapturedFrame(new byte[64], 4, 4);
        Array.Fill(output.BgraPixels, (byte)99);
        CpuVideoFrameReadback.CopyTo(
            Surface(4, 3, 24), 24, 4, 3, 4, 3, output, new PixelRect(2, 1, 4, 4), letterbox: true);

        Assert.Equal(new byte[] { 3, 2, 100, 255 }, Pixel(output, 0, 0));
        Assert.Equal(new byte[] { 4, 3, 100, 255 }, Pixel(output, 1, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(output, 2, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(output, 0, 2));
        Assert.Equal(64u, CpuVideoBuffer.Create(output).Length);
    }

    [Theory]
    [InlineData(-2, -1, 8, 6)]
    [InlineData(2, 1, 10, 10)]
    public void DiagnosticGeometryMatchesBorrowedReadbackBeforeAndAfterContentShrink(
        int x, int y, int width, int height)
    {
        var geometry = CaptureOutputGeometry.Calculate(8, 6, new PixelRect(x, y, width, height));
        var encoded = geometry.Encoded;
        var output = new CapturedFrame(new byte[encoded.Width * encoded.Height * 4], encoded.Width, encoded.Height);
        CpuVideoFrameReadback.CopyTo(
            Surface(8, 6, 40), 40, 8, 6, 8, 6, output, encoded, letterbox: false);

        Assert.Equal(new byte[] { (byte)(encoded.X + 1), (byte)(encoded.Y + 1), 100, 255 }, Pixel(output, 0, 0));
        Assert.Equal(new byte[] { (byte)(encoded.X + encoded.Width), (byte)(encoded.Y + encoded.Height), 100, 255 },
            Pixel(output, encoded.Width - 1, encoded.Height - 1));

        CpuVideoFrameReadback.CopyTo(
            Surface(8, 6, 40), 40, 8, 6, encoded.X + 1, encoded.Y + 1, output, encoded, letterbox: false);
        Assert.Equal(encoded.Width, output.Width);
        Assert.Equal(encoded.Height, output.Height);
        Assert.Equal(new byte[] { (byte)(encoded.X + 1), (byte)(encoded.Y + 1), 100, 255 }, Pixel(output, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(output, encoded.Width - 1, encoded.Height - 1));
    }

    [Theory]
    [InlineData(0, 3, 0, 0)]
    [InlineData(4, 0, 0, 0)]
    [InlineData(4, 3, 20, 30)]
    public void EmptyContentOrOutOfBoundsRegionEmitsOpaqueBlackNotStalePixels(
        int contentWidth, int contentHeight, int x, int y)
    {
        var output = new CapturedFrame(new byte[64], 4, 4);
        Array.Fill(output.BgraPixels, (byte)99);
        CpuVideoFrameReadback.CopyTo(
            Surface(4, 3, 24), 24, 4, 3, contentWidth, contentHeight,
            output, new PixelRect(x, y, 4, 4), letterbox: false);

        for (var offset = 0; offset < output.BgraPixels.Length; offset += 4)
        {
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, output.BgraPixels.AsSpan(offset, 4).ToArray());
        }
    }

    [Fact]
    public void OddDimensionEvenTrimIsStillAnUnscaledCrop()
    {
        var output = new CapturedFrame(new byte[192], 8, 6);
        CpuVideoFrameReadback.CopyTo(Surface(9, 7, 44), 44, 9, 7, 9, 7, output, null, letterbox: true);
        Assert.Equal(new byte[] { 1, 1, 100, 255 }, Pixel(output, 0, 0));
        Assert.Equal(new byte[] { 8, 6, 100, 255 }, Pixel(output, 7, 5));
    }

    [Fact]
    public void ShrinkAndGrowReusePrivateBuffersButDoNotOverwriteRetainedEncoderPixels()
    {
        var buffers = new CpuCaptureBuffers();
        var output = buffers.GetReadbackFrame(8, 6, publishSnapshot: false);
        CpuVideoFrameReadback.CopyTo(Surface(8, 6, 40, version: 1), 40, 8, 6, 8, 6, output, null, true);
        var borrowed = buffers.CopyLatest(borrow: true)!;
        var retained = CpuVideoBuffer.Create(borrowed);
        var expected = retained.ToArray();
        var native = Marshal.AllocHGlobal(expected.Length);
        try
        {
            CpuVideoBuffer.CopyBottomUp(borrowed, native, expected.Length);
            foreach (var (width, height) in new[] { (4, 2), (16, 12), (2, 4), (8, 6) })
            {
                var next = buffers.GetReadbackFrame(8, 6, publishSnapshot: false);
                CpuVideoFrameReadback.CopyTo(
                    Surface(width, height, width * 4, version: 2), width * 4,
                    width, height, width, height, next, null, true);
                Assert.Same(output, next);
                Assert.Same(borrowed, buffers.CopyLatest(borrow: true));
                Assert.Equal(192u, CpuVideoBuffer.Create(borrowed).Length);
            }

            Assert.Equal(expected, retained.ToArray());
            var actual = new byte[expected.Length];
            Marshal.Copy(native, actual, 0, actual.Length);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [Fact]
    public void FixedSizeReadbackDoesNotAllocateFullFrameArraysDuringResize()
    {
        const int width = 1280;
        const int height = 720;
        const int pitch = width * 4;
        var surface = Surface(width, height, pitch, version: 3);
        var buffers = new CpuCaptureBuffers();
        var output = buffers.GetReadbackFrame(width, height, publishSnapshot: false);
        CpuVideoFrameReadback.CopyTo(surface, pitch, width, height, 640, 480, output, null, true);
        buffers.CopyLatest(borrow: true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var submitted = 0; submitted < 12; submitted++)
        {
            output = buffers.GetReadbackFrame(width, height, publishSnapshot: false);
            CpuVideoFrameReadback.CopyTo(surface, pitch, width, height, 640, 480, output, null, true);
            buffers.CopyLatest(borrow: true);
        }

        var bytesPerFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 12;
        Assert.True(bytesPerFrame < 16_384, $"Managed bytes per fixed-size resized readback: {bytesPerFrame}.");
        Assert.Equal(width * height * 4, output.BgraPixels.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(output, 0, 0));
        Assert.Equal(new byte[] { 3, 0, 0, 255 }, Pixel(output, width / 2, height / 2));
    }

    [Fact]
    public void InvalidMappedSurfaceIsRejectedBeforeReadingPastItsBounds()
    {
        var output = new CapturedFrame(new byte[64], 4, 4);
        Assert.Throws<ArgumentException>(() =>
            CpuVideoFrameReadback.CopyTo(new byte[63], 16, 4, 4, 4, 4, output, null, true));
        Assert.Throws<ArgumentException>(() =>
            CpuVideoFrameReadback.CopyTo(new byte[64], 12, 4, 4, 4, 4, output, null, true));
    }

    private static byte[] Surface(int width, int height, int pitch, byte? version = null)
    {
        var pixels = new byte[pitch * height];
        Array.Fill(pixels, (byte)238);
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var offset = (row * pitch) + (column * 4);
                pixels[offset] = version ?? (byte)(column + 1);
                pixels[offset + 1] = version is not null ? (byte)0 : (byte)(row + 1);
                pixels[offset + 2] = version is not null ? (byte)0 : (byte)100;
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] Pixel(CapturedFrame frame, int x, int y) =>
        frame.BgraPixels.AsSpan(((y * frame.Width) + x) * 4, 4).ToArray();
}
