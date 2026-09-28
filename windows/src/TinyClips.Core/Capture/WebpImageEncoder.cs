using SkiaSharp;

namespace TinyClips.Core.Capture;

/// <summary>Encodes premultiplied BGRA capture/editor pixels as WebP without relying on an installed Windows codec.</summary>
public static class WebpImageEncoder
{
    public static unsafe byte[] Encode(byte[] bgraPixels, int width, int height, int scalePercent, double quality)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0 || bgraPixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Expected tightly packed BGRA pixels for the image dimensions.", nameof(bgraPixels));
        }

        var outputWidth = scalePercent is > 0 and < 100 ? Math.Max(1, width * scalePercent / 100) : width;
        var outputHeight = scalePercent is > 0 and < 100 ? Math.Max(1, height * scalePercent / 100) : height;
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        fixed (byte* pixels = bgraPixels)
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(imageInfo, (IntPtr)pixels, checked(width * 4)))
            {
                throw new InvalidOperationException("Could not initialize the WebP bitmap.");
            }

            using var source = SKImage.FromBitmap(bitmap);
            if (source is null)
            {
                throw new InvalidOperationException("Could not create the WebP image.");
            }

            if (outputWidth == width && outputHeight == height)
            {
                return EncodeImage(source, quality);
            }

            using var surface = SKSurface.Create(new SKImageInfo(
                outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surface is null)
            {
                throw new InvalidOperationException("Could not create the scaled WebP image.");
            }

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(
                source,
                new SKRect(0, 0, outputWidth, outputHeight),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var scaled = surface.Snapshot();
            return EncodeImage(scaled, quality);
        }
    }

    private static byte[] EncodeImage(SKImage image, double quality)
    {
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, (int)Math.Round(Math.Clamp(quality, 0.1, 1.0) * 100));
        return encoded?.ToArray() ?? throw new InvalidOperationException("WebP encoding failed.");
    }
}
