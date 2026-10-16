using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>Decodes a canvas background image for the Studio renderer.</summary>
[SupportedOSPlatform("windows")]
internal static class StudioBackgroundImage
{
    private const int ExifOrientationTag = 0x0112;

    /// <summary>
    /// Decodes <paramref name="path"/> upright (the camera's orientation tag applied) and no
    /// longer than <paramref name="maxSide"/> pixels on its long side, as premultiplied BGRA rows
    /// from the top. Reads what GDI+ reads (PNG, JPEG, BMP, GIF, TIFF) and WebP. False when the
    /// file is missing or is not one of those.
    /// </summary>
    public static bool TryDecode(string path, int maxSide, out byte[] pixels, out int width, out int height)
    {
        pixels = [];
        width = 0;
        height = 0;
        try
        {
            using var source = Open(path, maxSide, out var orientation);
            if (source is null)
            {
                return false;
            }

            var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.Width, source.Height));
            var scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
            var scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));

            // Scale first and turn afterwards: turning a 48-megapixel photo costs a second copy of it.
            using var bitmap = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var attributes = new ImageAttributes())
            {
                // Without this the scaler blends the edge rows with transparency and leaves a dark rim.
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                graphics.DrawImage(source, new Rectangle(0, 0, scaledWidth, scaledHeight), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }

            if (orientation != RotateFlipType.RotateNoneFlipNone)
            {
                bitmap.RotateFlip(orientation);
            }

            width = bitmap.Width;
            height = bitmap.Height;
            pixels = new byte[width * height * 4];
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var rowBytes = width * 4;
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(data.Scan0 + (row * data.Stride), pixels, row * rowBytes, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // GDI+ reports a file it cannot read in several ways, "out of memory" among them.
            pixels = [];
            width = 0;
            height = 0;
            return false;
        }
    }

    private static Bitmap? Open(string path, int maxSide, out RotateFlipType orientation)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(path);
            orientation = ReadOrientation(bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            // Not a format GDI+ knows. The app saves screenshots as WebP, which Skia reads.
            bitmap?.Dispose();
            return OpenWithSkia(path, maxSide, out orientation);
        }
    }

    private static Bitmap? OpenWithSkia(string path, int maxSide, out RotateFlipType orientation)
    {
        orientation = RotateFlipType.RotateNoneFlipNone;
        using var codec = SKCodec.Create(path);
        if (codec is null)
        {
            return null;
        }

        // SKEncodedOrigin counts as EXIF does.
        orientation = OrientationToRotateFlip((int)codec.EncodedOrigin);

        // WebP decodes straight to a smaller size, which keeps a very large image out of memory.
        var full = codec.Info;
        var size = codec.GetScaledDimensions(Math.Min(1f, maxSide / (float)Math.Max(full.Width, full.Height)));
        if (size.Width < 1 || size.Height < 1)
        {
            return null;
        }

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new Bitmap(info.Width, info.Height, PixelFormat.Format32bppPArgb);
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, info.Width, info.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            SKCodecResult result;
            try
            {
                result = codec.GetPixels(info, data.Scan0, data.Stride, SKCodecOptions.Default);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            if (result != SKCodecResult.Success)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>The turn that brings an image upright, from its EXIF orientation (1 to 8).</summary>
    internal static RotateFlipType OrientationToRotateFlip(int orientation) => orientation switch
    {
        2 => RotateFlipType.RotateNoneFlipX,
        3 => RotateFlipType.Rotate180FlipNone,
        4 => RotateFlipType.RotateNoneFlipY,
        5 => RotateFlipType.Rotate90FlipX,
        6 => RotateFlipType.Rotate90FlipNone,
        7 => RotateFlipType.Rotate270FlipX,
        8 => RotateFlipType.Rotate270FlipNone,
        _ => RotateFlipType.RotateNoneFlipNone,
    };

    private static RotateFlipType ReadOrientation(Image image)
    {
        if (Array.IndexOf(image.PropertyIdList, ExifOrientationTag) < 0)
        {
            return RotateFlipType.RotateNoneFlipNone;
        }

        var value = image.GetPropertyItem(ExifOrientationTag)?.Value;
        return value is { Length: >= 2 } ? OrientationToRotateFlip(BitConverter.ToUInt16(value, 0)) : RotateFlipType.RotateNoneFlipNone;
    }
}
