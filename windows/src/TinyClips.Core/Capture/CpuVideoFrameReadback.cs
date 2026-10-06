using System.Runtime.InteropServices;

namespace TinyClips.Core.Capture;

/// <summary>Reads valid WGC content into a fixed-size video frame without allocating pixel arrays.</summary>
internal static class CpuVideoFrameReadback
{
    public static void CopyTo(
        ReadOnlySpan<byte> surface,
        int rowPitch,
        int surfaceWidth,
        int surfaceHeight,
        int contentWidth,
        int contentHeight,
        CapturedFrame destination,
        PixelRect? region,
        bool letterbox)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(contentHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowPitch);
        var surfaceRowBytes = checked(surfaceWidth * 4);
        if (rowPitch < surfaceRowBytes ||
            surface.Length < checked(((surfaceHeight - 1) * rowPitch) + surfaceRowBytes))
        {
            throw new ArgumentException("The mapped surface cannot hold its BGRA dimensions.", nameof(surface));
        }

        // During growth, ContentSize can already exceed the old pool's physical surface.
        contentWidth = Math.Min(contentWidth, surfaceWidth);
        contentHeight = Math.Min(contentHeight, surfaceHeight);
        var pixels = destination.BgraPixels.AsSpan(0, CpuVideoBuffer.GetLength(destination));
        if (contentWidth == 0 || contentHeight == 0)
        {
            FillBlack(pixels);
            return;
        }

        var x = region is { } r ? Math.Clamp(r.X, 0, contentWidth) : 0;
        var y = region is { } rect ? Math.Clamp(rect.Y, 0, contentHeight) : 0;
        var width = contentWidth - x;
        var height = contentHeight - y;
        if (region is not null)
        {
            width = Math.Min(width, destination.Width);
            height = Math.Min(height, destination.Height);
        }

        if (width == 0 || height == 0)
        {
            FillBlack(pixels);
            return;
        }

        // The encoder's even rounding can trim one source pixel without resizing its content.
        var needsScale = letterbox && region is null &&
            (width < destination.Width || height < destination.Height ||
             width > destination.Width + 1 || height > destination.Height + 1);
        var destinationStride = destination.Width * 4;
        if (!needsScale)
        {
            var copyWidth = Math.Min(width, destination.Width);
            var copyHeight = Math.Min(height, destination.Height);
            if (copyWidth != destination.Width || copyHeight != destination.Height)
            {
                FillBlack(pixels);
            }

            for (var row = 0; row < copyHeight; row++)
            {
                surface.Slice(((y + row) * rowPitch) + (x * 4), copyWidth * 4)
                    .CopyTo(pixels.Slice(row * destinationStride, copyWidth * 4));
            }

            return;
        }

        FillBlack(pixels);
        var scale = Math.Min(destination.Width / (double)width, destination.Height / (double)height);
        var drawWidth = Math.Clamp((int)Math.Round(width * scale), 1, destination.Width);
        var drawHeight = Math.Clamp((int)Math.Round(height * scale), 1, destination.Height);
        var left = (destination.Width - drawWidth) / 2;
        var top = (destination.Height - drawHeight) / 2;

        // Pixel-centred nearest-neighbour sampling keeps the resize path allocation-free.
        // Fixed-point stepping avoids a divide for every output pixel.
        var stepX = ((long)width << 32) / drawWidth;
        for (var row = 0; row < drawHeight; row++)
        {
            var sourceY = y + (int)(((2L * row + 1) * height) / (2L * drawHeight));
            var source = MemoryMarshal.Cast<byte, uint>(
                surface.Slice((sourceY * rowPitch) + (x * 4), width * 4));
            var target = MemoryMarshal.Cast<byte, uint>(
                pixels.Slice(((top + row) * destinationStride) + (left * 4), drawWidth * 4));
            var sourceX = stepX / 2;
            for (var column = 0; column < drawWidth; column++)
            {
                target[column] = source[(int)(sourceX >> 32)];
                sourceX += stepX;
            }
        }
    }

    private static void FillBlack(Span<byte> pixels) =>
        MemoryMarshal.Cast<byte, uint>(pixels).Fill(0xff000000u);
}
