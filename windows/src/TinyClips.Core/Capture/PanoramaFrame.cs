namespace TinyClips.Core.Capture;

/// <summary>
/// One tightly-packed BGRA8 frame of a scrolling capture plus a precomputed per-row mean luma
/// signature used for fast, repeatable vertical alignment.
/// </summary>
public sealed class PanoramaFrame
{
    /// <summary>Columns are sampled at this density when building row signatures and pixel scores.</summary>
    internal const int SignatureColumns = 160;

    public PanoramaFrame(CapturedFrame frame)
        : this(frame.BgraPixels, frame.Width, frame.Height)
    {
    }

    public PanoramaFrame(byte[] bgraPixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        if (bgraPixels.LongLength < (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than width * height * 4.", nameof(bgraPixels));
        }

        BgraPixels = bgraPixels;
        Width = width;
        Height = height;
        RowLuma = MakeRowLuma(bgraPixels, width, height);
    }

    public byte[] BgraPixels { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Mean luma per row (0-255 scale), sampled across up to 160 columns.</summary>
    public float[] RowLuma { get; }

    public long ByteCount => (long)Width * Height * 4;

    public CapturedFrame ToCapturedFrame() => new(BgraPixels, Width, Height);

    /// <summary>Rec.601 luma of the BGRA pixel starting at <paramref name="index"/>.</summary>
    internal static double Luma(byte[] bgra, int index)
        => (0.114 * bgra[index]) + (0.587 * bgra[index + 1]) + (0.299 * bgra[index + 2]);

    private static float[] MakeRowLuma(byte[] pixels, int width, int height)
    {
        var columnStep = Math.Max(1, width / SignatureColumns);
        var result = new float[height];
        for (var y = 0; y < height; y++)
        {
            long total = 0;
            var samples = 0;
            var rowStart = y * width;
            for (var x = 0; x < width; x += columnStep)
            {
                var index = (rowStart + x) * 4;
                // Integer approximation of Rec.601 luma (x256) keeps this hot loop cheap.
                total += (29 * pixels[index]) + (150 * pixels[index + 1]) + (77 * pixels[index + 2]);
                samples++;
            }

            result[y] = samples > 0 ? total / (float)(samples * 256) : 0f;
        }

        return result;
    }
}
