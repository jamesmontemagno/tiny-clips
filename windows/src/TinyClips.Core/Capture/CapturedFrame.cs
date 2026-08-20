namespace TinyClips.Core.Capture;

/// <summary>
/// A captured raster frame in tightly-packed BGRA8 (premultiplied) pixels,
/// i.e. row stride is exactly <see cref="Width"/> * 4 bytes.
/// </summary>
public sealed class CapturedFrame
{
    public CapturedFrame(byte[] bgraPixels, int width, int height)
    {
        BgraPixels = bgraPixels;
        Width = width;
        Height = height;
    }

    public byte[] BgraPixels { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Returns a tightly-packed copy of the given sub-rectangle (clamped to the frame). Returns
    /// this instance when the region covers the whole frame.
    /// </summary>
    public CapturedFrame Crop(PixelRect region)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(0, Width - 1));
        var y = Math.Clamp(region.Y, 0, Math.Max(0, Height - 1));
        var width = Math.Clamp(region.Width, 1, Width - x);
        var height = Math.Clamp(region.Height, 1, Height - y);
        if (x == 0 && y == 0 && width == Width && height == Height)
        {
            return this;
        }

        var srcStride = Width * 4;
        var dstStride = width * 4;
        var pixels = new byte[dstStride * height];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(BgraPixels, ((y + row) * srcStride) + (x * 4), pixels, row * dstStride, dstStride);
        }

        return new CapturedFrame(pixels, width, height);
    }
}
