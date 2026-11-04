using System.Text.Json.Nodes;
using SkiaSharp;
using TinyClips.Core.Automation;

namespace TinyClips.Core.Tests;

public sealed class AutomationImageCropTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("TinyClipsCrop-").FullName;
    private string Source => Path.Combine(_folder, "shot.png");
    private string Output => Path.Combine(_folder, "crop.png");
    private const string TopRight = """{"x":2,"y":0,"width":2,"height":1}""";

    private byte[] Image(SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var source = new SKBitmap(4, 2);
        source.Erase(SKColors.Blue);
        source.SetPixel(2, 0, SKColors.Red);
        source.SetPixel(3, 0, SKColors.Green);
        using var encoded = source.Encode(format, 100);
        return encoded.ToArray();
    }

    [Fact]
    public async Task CropWritesExactTopLeftPixelsToANewPngEvenOnNameCollision()
    {
        var original = Image();
        File.WriteAllBytes(Source, original);
        var crop = await AutomationImageCrop.SaveAsync(Source, JsonNode.Parse(TopRight), Source, TestContext.Current.CancellationToken);
        Assert.NotEqual(Source, crop.Path);
        Assert.Equal(original, File.ReadAllBytes(Source));
        Assert.Equal((2, 1), (crop.Width, crop.Height));
        using var codec = SKCodec.Create(crop.Path);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        using var png = SKBitmap.Decode(codec);
        Assert.Equal((2, 1), (png.Width, png.Height));
        Assert.Equal(SKColors.Red, png.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, png.GetPixel(1, 0));
    }

    [Fact]
    public async Task CropIgnoresJpegPreviewRotation()
    {
        var jpeg = Image(SKEncodedImageFormat.Jpeg);
        // EXIF orientation 6 presents the 4 × 2 raster as a 2 × 4 preview.
        byte[] exif = [0x49, 0x49, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        byte[] oriented = [0xff, 0xd8, 0xff, 0xe1, 0, (byte)(exif.Length + 8), .. "Exif"u8, 0, 0, .. exif, .. jpeg.AsSpan(2)];
        var source = Path.Combine(_folder, "rotated.jpg");
        File.WriteAllBytes(source, oriented);
        var crop = await AutomationImageCrop.SaveAsync(source, JsonNode.Parse(TopRight), Output, TestContext.Current.CancellationToken);
        Assert.Equal((2, 1), (crop.Width, crop.Height));
        Assert.Equal(oriented, File.ReadAllBytes(source));
        using var codec = SKCodec.Create(crop.Path);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"x":-1,"y":0,"width":1,"height":1}""")]
    [InlineData("""{"x":0,"y":0,"width":0,"height":1}""")]
    [InlineData("""{"x":0,"y":0.5,"width":1,"height":1}""")]
    [InlineData("""{"x":0,"y":false,"width":1,"height":1}""")]
    [InlineData("""{"x":0,"y":0,"width":1,"height":1,"angle":90}""")]
    [InlineData("""{"x":0,"y":1,"width":1,"height":2}""")]
    [InlineData("""{"x":3,"y":0,"width":2147483647,"height":1}""")]
    [InlineData("""{"x":9223372036854775807,"y":0,"width":1,"height":1}""")]
    public async Task InvalidRegionsAreRejectedWithoutOutput(string region)
    {
        var original = Image();
        File.WriteAllBytes(Source, original);
        var error = await Assert.ThrowsAsync<AutomationException>(() =>
            AutomationImageCrop.SaveAsync(Source, JsonNode.Parse(region), Output, TestContext.Current.CancellationToken));
        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
        Assert.False(File.Exists(Output));
        Assert.Equal(original, File.ReadAllBytes(Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasedSourceIsNotAReadableLibraryEntry(bool parentAlias)
    {
        var original = Image();
        var outside = Directory.CreateDirectory(Path.Combine(_folder, "outside")).FullName;
        var target = Path.Combine(outside, "target.png");
        File.WriteAllBytes(target, original);
        var link = Path.Combine(_folder, parentAlias ? "alias" : "alias.png");
        var alias = parentAlias ? Path.Combine(link, "target.png") : link;
        try
        {
            if (parentAlias) Directory.CreateSymbolicLink(link, outside);
            else File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Assert.Skip("Creating a symbolic link is not allowed on this machine."); }
        var error = await Assert.ThrowsAsync<AutomationException>(() =>
            AutomationImageCrop.SaveAsync(alias, JsonNode.Parse(TopRight), Output, TestContext.Current.CancellationToken));
        Assert.Equal(AutomationErrorCodes.NotFound, error.Code);
        Assert.False(File.Exists(Output));
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
