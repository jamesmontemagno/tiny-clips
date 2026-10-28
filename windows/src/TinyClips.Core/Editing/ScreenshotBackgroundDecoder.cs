using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace TinyClips.Core.Editing;

public sealed record ScreenshotBackgroundPixels(byte[] BgraPixels, int Width, int Height);

public static class ScreenshotBackgroundDecoder
{
    public const long MaximumPixels = 32_000_000;

    public static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The background image has invalid dimensions.");
        if ((long)width * height > MaximumPixels)
            throw new InvalidDataException("Choose a background image of 32 megapixels or less.");
    }

    public static ScreenshotBackgroundPixels Decode(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        RejectAnimatedPng(stream, cancellationToken);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("The background image could not be read. Choose a PNG, JPEG, or WebP image.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            throw new InvalidDataException("Choose a PNG, JPEG, or WebP background image.");
        ValidateDimensions(codec.Info.Width, codec.Info.Height);
        if (codec.FrameCount > 1)
            throw new InvalidDataException("Animated backgrounds are not supported. Choose a still image.");
        cancellationToken.ThrowIfCancellationRequested();

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("The background image is incomplete or damaged.");
        cancellationToken.ThrowIfCancellationRequested();

        var swapAxes = (int)codec.EncodedOrigin >= 5;
        var width = swapAxes ? info.Height : info.Width;
        var height = swapAxes ? info.Width : info.Height;
        using var upright = codec.EncodedOrigin == SKEncodedOrigin.TopLeft
            ? null : new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (upright is not null)
        {
            using var canvas = new SKCanvas(upright);
            canvas.Clear(SKColors.Transparent);
            // EXIF orientations map encoded pixel edges into upright image coordinates.
            var matrix = (int)codec.EncodedOrigin switch
            {
                2 => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
                3 => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
                4 => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
                5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                6 => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
                7 => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
                8 => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
                _ => SKMatrix.Identity,
            };
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var output = upright ?? decoded;
        var rowBytes = checked(width * 4);
        var pixels = new byte[checked(rowBytes * height)];
        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(output.GetPixels() + row * output.RowBytes, pixels, row * rowBytes, rowBytes);
        }
        return new ScreenshotBackgroundPixels(pixels, width, height);
    }

    private static void RejectAnimatedPng(Stream stream, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[8];
        var count = stream.Read(header);
        stream.Position = 0;
        if (count != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return;

        stream.Position = 8;
        // Skia's PNG codec ignores APNG metadata. acTL precedes IDAT in an animated PNG.
        while (stream.Length - stream.Position >= 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (header[4..].SequenceEqual("acTL"u8))
                throw new InvalidDataException("Animated backgrounds are not supported. Choose a still image.");
            if (length > stream.Length - stream.Position - 4)
                throw new InvalidDataException("The background image is incomplete or damaged.");
            if (header[4..].SequenceEqual("IDAT"u8) || header[4..].SequenceEqual("IEND"u8)) break;
            stream.Seek((long)length + 4, SeekOrigin.Current);
        }
        stream.Position = 0;
    }
}
