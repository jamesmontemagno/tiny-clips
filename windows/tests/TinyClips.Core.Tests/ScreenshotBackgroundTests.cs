using System.Buffers.Binary;
using SkiaSharp;
using TinyClips.Core.Editing;
using Windows.Foundation;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotBackgroundTests
{
    [Theory]
    [InlineData(400, 100, BackgroundImagePlacement.Fill, -150, 0, 400, 100)]
    [InlineData(400, 100, BackgroundImagePlacement.Fit, 0, 37.5, 100, 25)]
    [InlineData(100, 400, BackgroundImagePlacement.Fill, 0, -150, 100, 400)]
    [InlineData(100, 400, BackgroundImagePlacement.Fit, 37.5, 0, 25, 100)]
    [InlineData(100, 100, BackgroundImagePlacement.Fill, 0, 0, 100, 100)]
    [InlineData(1, 1, BackgroundImagePlacement.Fit, 0, 0, 100, 100)]
    public void Placement_PreservesAspectAndCentersInFrame(double width, double height,
        BackgroundImagePlacement placement, double x, double y, double expectedWidth, double expectedHeight)
    {
        Assert.Equal(new Rect(x, y, expectedWidth, expectedHeight),
            ScreenshotBackgroundLayout.Create(width, height, new Size(100, 100), placement));
    }

    [Theory]
    [InlineData(0, 100, 100, 100)]
    [InlineData(100, -1, 100, 100)]
    [InlineData(double.NaN, 100, 100, 100)]
    [InlineData(100, 100, double.PositiveInfinity, 100)]
    [InlineData(100, 100, 0, 100)]
    public void Placement_RejectsInvalidDimensions(double width, double height, double frameWidth, double frameHeight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotBackgroundLayout.Create(
            width, height, new Size(frameWidth, frameHeight), BackgroundImagePlacement.Fill));

    [Theory]
    [InlineData(8000, 4000, true)]
    [InlineData(8000, 4001, false)]
    [InlineData(int.MaxValue, int.MaxValue, false)]
    [InlineData(0, 1, false)]
    public void Decoder_ValidatesPixelBudgetWithoutAllocatingImage(int width, int height, bool valid)
    {
        if (valid) ScreenshotBackgroundDecoder.ValidateDimensions(width, height);
        else Assert.Throws<InvalidDataException>(() => ScreenshotBackgroundDecoder.ValidateDimensions(width, height));
    }

    [Theory]
    [InlineData(16384, 1000, true)]
    [InlineData(20000, 1000, false)]
    [InlineData(1000, 20000, false)]
    public void Decoder_ValidatesBothDeviceAxesWithinPixelBudget(int width, int height, bool valid)
    {
        if (valid) ScreenshotBackgroundDecoder.ValidateDimensions(width, height, 16384);
        else Assert.Throws<InvalidDataException>(() => ScreenshotBackgroundDecoder.ValidateDimensions(width, height, 16384));
    }

    [Fact]
    public void Decoder_RejectsEncodedImageBeyondDeviceLimit()
    {
        using var folder = new ImageFolder();
        var path = folder.Write(Encode(SKEncodedImageFormat.Png));
        Assert.Throws<InvalidDataException>(() =>
            ScreenshotBackgroundDecoder.Decode(path, TestContext.Current.CancellationToken, maximumDimension: 2));
        Assert.Equal(3, ScreenshotBackgroundDecoder.Decode(
            path, TestContext.Current.CancellationToken, maximumDimension: 3).Width);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void Decoder_ReadsSupportedFormatsByContentAndClosesFile(SKEncodedImageFormat format)
    {
        using var folder = new ImageFolder();
        var path = folder.Write(Encode(format));
        var decoded = ScreenshotBackgroundDecoder.Decode(path, TestContext.Current.CancellationToken);
        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(24, decoded.BgraPixels.Length);
        File.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Equal(24, decoded.BgraPixels.Length);
    }

    [Fact]
    public void Decoder_PreservesPremultipliedAlpha()
    {
        using var folder = new ImageFolder();
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.SetPixel(0, 0, new SKColor(200, 100, 50, 128));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var pixels = ScreenshotBackgroundDecoder.Decode(folder.Write(encoded.ToArray()), TestContext.Current.CancellationToken);
        Assert.InRange(pixels.BgraPixels[0], (byte)24, (byte)26);
        Assert.InRange(pixels.BgraPixels[1], (byte)49, (byte)51);
        Assert.InRange(pixels.BgraPixels[2], (byte)99, (byte)101);
        Assert.Equal(128, pixels.BgraPixels[3]);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(4, 0, 1)]
    [InlineData(5, 0, 0)]
    [InlineData(6, 1, 0)]
    [InlineData(7, 1, 2)]
    [InlineData(8, 0, 2)]
    public void Decoder_AppliesAllExifOrientations(int orientation, int redX, int redY)
    {
        using var folder = new ImageFolder();
        var jpeg = Encode(SKEncodedImageFormat.Jpeg);
        var original = ScreenshotBackgroundDecoder.Decode(folder.Write(jpeg), TestContext.Current.CancellationToken);
        // JPEG APP1 contains Exif followed by a TIFF header with the orientation entry.
        byte[] exif = [0x49, 0x49, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0,
            (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        byte[] oriented = [0xff, 0xd8, 0xff, 0xe1, 0, (byte)(exif.Length + 8),
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, .. exif, .. jpeg.AsSpan(2)];
        var decoded = ScreenshotBackgroundDecoder.Decode(folder.Write(oriented), TestContext.Current.CancellationToken);
        Assert.Equal(orientation >= 5 ? 2 : 3, decoded.Width);
        Assert.Equal(orientation >= 5 ? 3 : 2, decoded.Height);
        var offset = (redY * decoded.Width + redX) * 4;
        Assert.Equal(original.BgraPixels[..4], decoded.BgraPixels[offset..(offset + 4)]);
    }

    [Fact]
    public void Decoder_RejectsAnimationBeforeDecodingPixels()
    {
        using var folder = new ImageFolder();
        var png = InsertPngChunk(Encode(SKEncodedImageFormat.Png), "acTL", [0, 0, 0, 2, 0, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() =>
            ScreenshotBackgroundDecoder.Decode(folder.Write(png), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Decoder_RejectsAnimatedWebp()
    {
        using var folder = new ImageFolder();
        var still = Encode(SKEncodedImageFormat.Webp);
        using var chunks = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            chunks.Write(System.Text.Encoding.ASCII.GetBytes(type));
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
            chunks.Write(size);
            chunks.Write(data);
            if (data.Length % 2 != 0) chunks.WriteByte(0);
        }
        Chunk("VP8X", [2, 0, 0, 0, 2, 0, 0, 1, 0, 0]);
        Chunk("ANIM", new byte[6]);
        byte[] frame = [0, 0, 0, 0, 0, 0, 2, 0, 0, 1, 0, 0, 100, 0, 0, 0, .. still.AsSpan(12)];
        Chunk("ANMF", frame);
        Chunk("ANMF", frame);
        var body = chunks.ToArray();
        var webp = new byte[body.Length + 12];
        "RIFF"u8.CopyTo(webp);
        BinaryPrimitives.WriteInt32LittleEndian(webp.AsSpan(4), body.Length + 4);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        body.CopyTo(webp, 12);
        var error = Assert.Throws<InvalidDataException>(() =>
            ScreenshotBackgroundDecoder.Decode(folder.Write(webp), TestContext.Current.CancellationToken));
        Assert.Contains("Animated", error.Message);
    }

    [Fact]
    public void Decoder_RejectsOversizedEncodedDimensionsBeforeAllocatingPixels()
    {
        using var folder = new ImageFolder();
        var png = Encode(SKEncodedImageFormat.Png);
        var header = png.AsSpan(16, 13).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(header, 8000);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 4001);
        // Replace IHDR while retaining a tiny IDAT; the decoder must reject before reading it.
        var large = InsertPngChunk([.. png[..8], .. png[33..]], "IHDR", header, 8);
        var error = Assert.Throws<InvalidDataException>(() =>
            ScreenshotBackgroundDecoder.Decode(folder.Write(large), TestContext.Current.CancellationToken));
        Assert.Contains("32 megapixels", error.Message);
    }

    [Fact]
    public void Decoder_RejectsUnsupportedAndDamagedFilesAndHonorsCancellation()
    {
        using var folder = new ImageFolder();
        Assert.Throws<InvalidDataException>(() => ScreenshotBackgroundDecoder.Decode(
            folder.Write([1, 2, 3]), TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => ScreenshotBackgroundDecoder.Decode(
            folder.Write(Encode(SKEncodedImageFormat.Bmp)), TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ScreenshotBackgroundDecoder.Decode("not-a-file", canceled.Token));
    }

    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(3, 2);
        bitmap.Erase(SKColors.Blue);
        bitmap.SetPixel(0, 0, SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        // Skia encodes PNG, JPEG and WebP, but not BMP.
        if (format == SKEncodedImageFormat.Bmp)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0x4D42);
            writer.Write(78);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(3);
            writer.Write(2);
            writer.Write((ushort)1);
            writer.Write((ushort)24);
            writer.Write(new byte[24]);
            writer.Write(new byte[24]);
            return stream.ToArray();
        }
        using var encoded = image.Encode(format, 100);
        return encoded.ToArray();
    }

    private static byte[] InsertPngChunk(byte[] png, string type, byte[] data, int offset = 33)
    {
        using var stream = new MemoryStream();
        stream.Write(png.AsSpan(0, offset));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var chunk = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(chunk);
        uint crc = uint.MaxValue;
        foreach (var value in chunk)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        stream.Write(length);
        stream.Write(png.AsSpan(offset));
        return stream.ToArray();
    }

    private sealed class ImageFolder : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "TinyClipsBackgroundTests-" + Guid.NewGuid().ToString("N"));
        public ImageFolder() => Directory.CreateDirectory(_path);
        public string Write(byte[] data)
        {
            var path = Path.Combine(_path, Guid.NewGuid().ToString("N") + ".image");
            File.WriteAllBytes(path, data);
            return path;
        }
        public void Dispose() => Directory.Delete(_path, true);
    }
}
