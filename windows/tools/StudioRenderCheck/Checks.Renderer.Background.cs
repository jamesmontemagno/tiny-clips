using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Backgrounds, including image backgrounds.</summary>
internal static partial class RendererChecks
{
    private static readonly Rgb Magenta = new(255, 0, 255);
    private static readonly Rgb Cyan = new(0, 255, 255);
    private static readonly Rgb[] Quadrants = [new(200, 40, 40), new(40, 160, 40), new(40, 40, 200), new(220, 220, 60)];

    private static StudioProject WithBackground(RenderBench bench, StudioBackground background) =>
        bench.Base(StudioLayout.Screen) with { Canvas = new StudioCanvas { Background = background } };

    private static Picture BackgroundOnly(RenderBench bench, StudioBackground background, int width = 1920, int height = 1080, string? folder = null) =>
        bench.Render(WithBackground(bench, background), width, height, withScreen: false, withCamera: false, folder: folder);

    private static void ExpectFlat(CheckContext context, Picture picture, Rgb want, double tolerance, string what)
    {
        var flat = new byte[picture.Width * picture.Height * 4];
        for (var i = 0; i < flat.Length; i += 4)
        {
            flat[i] = (byte)Math.Round(want.B);
            flat[i + 1] = (byte)Math.Round(want.G);
            flat[i + 2] = (byte)Math.Round(want.R);
            flat[i + 3] = 255;
        }

        var (difference, x, y) = Picture.MaxDifference(picture, Picture.FromBgra(flat, picture.Width, picture.Height));
        context.Expect(difference <= tolerance, $"{what}: pixel ({x},{y}) is {picture.Pixel(Math.Max(0, x), Math.Max(0, y))}, want {want} everywhere");
    }

    private static void Background(CheckContext context, RenderBench bench)
    {
        ExpectFlat(context, BackgroundOnly(bench, new StudioBackground { Style = StudioBackgroundStyle.None, Primary = Orange }), new Rgb(0, 0, 0), 0, "no background");
        ExpectFlat(context, BackgroundOnly(bench, new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = Orange }), OrangeRgb, 0, "a solid background");
        ExpectFlat(context, BackgroundOnly(bench, new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#c86432" }), OrangeRgb, 0, "a colour in lower case");

        // #RRGGBBAA: the video is opaque, so alpha shows as the colour over black. 128/255 of (200,100,50).
        ExpectFlat(context, BackgroundOnly(bench, new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#C8643280" }), new Rgb(200 * 128 / 255.0, 100 * 128 / 255.0, 50 * 128 / 255.0), 1, "a colour with alpha");
        ExpectFlat(context, BackgroundOnly(bench, new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "orange" }), new Rgb(0, 0, 0), 0, "a colour that is not a colour");
        ExpectFlat(context, BackgroundOnly(bench, new StudioBackground { Style = StudioBackgroundStyle.Gradient, Primary = Orange, Secondary = null }), OrangeRgb, 0, "a gradient with no second colour");

        // A gradient runs from the top-left corner to the bottom-right one. A pixel's place along
        // it is the projection of its centre on that diagonal, and the colour is the straight
        // mix of the two encoded colours: halfway between #203060 and #80C0E0 is (80,120,160).
        // Mixed in linear light instead, the middle would be (95,137,172).
        var first = new Rgb(0x20, 0x30, 0x60);
        var second = new Rgb(0x80, 0xC0, 0xE0);
        var worst = 0.0;
        foreach (var (width, height) in new[] { (1920, 1080), (1080, 1920), (642, 362) })
        {
            var gradient = BackgroundOnly(bench, new StudioBackground { Style = StudioBackgroundStyle.Gradient, Primary = "#203060", Secondary = "#80C0E0" }, width, height);
            for (var row = 0; row <= 8; row++)
            {
                for (var column = 0; column <= 8; column++)
                {
                    var x = Math.Min(width - 1, column * width / 8);
                    var y = Math.Min(height - 1, row * height / 8);
                    var along = (((x + 0.5) * width) + ((y + 0.5) * height)) / (((double)width * width) + ((double)height * height));
                    var want = first.Mix(second, along);
                    var got = gradient.Pixel(x, y);
                    worst = Math.Max(worst, got.Distance(want));
                    context.Expect(got.Distance(want) <= 1.5, $"{width}×{height} gradient at ({x},{y}), {along:0.000} of the way: {got}, want {want}");
                }
            }

            ExpectPixel(context, gradient, 0, 0, first, 1.5, $"{width}×{height} gradient, top-left corner");
            ExpectPixel(context, gradient, width - 1, height - 1, second, 1.5, $"{width}×{height} gradient, bottom-right corner");
            ExpectPixel(context, gradient, width / 2, height / 2, new Rgb(80, 120, 160), 1.5, $"{width}×{height} gradient, the middle");
        }

        context.Measure(string.Create(CultureInfo.InvariantCulture, $"gradients within {worst:0.0} of 255 of the straight mix of the encoded colours, at 243 points on three canvases"));
    }

    /// <summary>
    /// A test image twice as wide as tall: four quadrant colours, a magenta stripe over the first
    /// 5 % of its width and a cyan one from 6.25 % to 7.5 %. Filling a 16:9 canvas cuts 5.56 %
    /// off each side, so the magenta is cut away and the cyan is just inside.
    /// </summary>
    private static (byte R, byte G, byte B, byte A) TestImage(double u, double v)
    {
        var color = u < 0.05 ? Magenta : u is >= 0.0625 and < 0.075 ? Cyan : Quadrants[(u < 0.5 ? 0 : 1) + (v < 0.5 ? 0 : 2)];
        return ((byte)color.R, (byte)color.G, (byte)color.B, 255);
    }

    private static void SaveImage(string path, int width, int height, Func<double, double, (byte R, byte G, byte B, byte A)> color, bool jpeg = false, int orientation = 0, Drawing.RotateFlipType storedTurn = Drawing.RotateFlipType.RotateNoneFlipNone)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = color((x + 0.5) / width, (y + 0.5) / height);
                var i = ((y * width) + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        using var bitmap = new Drawing.Bitmap(width, height, Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, width, height), Imaging.ImageLockMode.WriteOnly, Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels, row * width * 4, data.Scan0 + (row * data.Stride), width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // Store the picture turned, with the tag that says how to turn it back.
        bitmap.RotateFlip(storedTurn);
        if (orientation != 0)
        {
            var item = (Imaging.PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(Imaging.PropertyItem));
            item.Id = 0x0112;
            item.Type = 3;
            item.Len = 2;
            item.Value = [(byte)orientation, 0];
            bitmap.SetPropertyItem(item);
        }

        if (jpeg)
        {
            var codec = Imaging.ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == Imaging.ImageFormat.Jpeg.Guid);
            using var parameters = new Imaging.EncoderParameters(1);
            using var quality = new Imaging.EncoderParameter(Imaging.Encoder.Quality, 95L);
            parameters.Param[0] = quality;
            bitmap.Save(path, codec, parameters);
        }
        else
        {
            bitmap.Save(path, Imaging.ImageFormat.Png);
        }
    }

    private static void SaveWebp(string path, int width, int height, Func<double, double, (byte R, byte G, byte B, byte A)> color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = color((x + 0.5) / width, (y + 0.5) / height);
                var i = ((y * width) + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        File.WriteAllBytes(path, TinyClips.Core.Capture.WebpImageEncoder.Encode(pixels, width, height, 100, 1.0));
    }

    /// <summary>The test image, filling a 16:9 canvas: scaled to the canvas height and cut by 5.56 % of its width each side.</summary>
    private static void ExpectTestImage(CheckContext context, Picture picture, double tolerance, string what)
    {
        var width = picture.Width;
        var height = picture.Height;

        // Canvas x for a place u along the image: the image is drawn 1.125 canvas widths wide, centred.
        double X(double u) => (u * 1.125 * width) - (0.0625 * width);
        ExpectPixel(context, picture, 1, height * 0.3, Quadrants[0], tolerance, $"{what}: the left edge (the magenta stripe is cut away)");
        ExpectPixel(context, picture, X(0.06875), height * 0.3, Cyan, tolerance, $"{what}: the cyan stripe");
        ExpectPixel(context, picture, X(0.06875), height * 0.7, Cyan, tolerance, $"{what}: the cyan stripe lower down");
        ExpectPixel(context, picture, X(0.25), height * 0.25, Quadrants[0], tolerance, $"{what}: top-left quadrant");
        ExpectPixel(context, picture, X(0.75), height * 0.25, Quadrants[1], tolerance, $"{what}: top-right quadrant");
        ExpectPixel(context, picture, X(0.25), height * 0.75, Quadrants[2], tolerance, $"{what}: bottom-left quadrant");
        ExpectPixel(context, picture, X(0.75), height * 0.75, Quadrants[3], tolerance, $"{what}: bottom-right quadrant");

        // The image's middle is the canvas's middle, within a hundredth of the canvas each way.
        ExpectPixel(context, picture, width * 0.49, height * 0.49, Quadrants[0], tolerance, $"{what}: just up and left of the middle");
        ExpectPixel(context, picture, width * 0.51, height * 0.51, Quadrants[3], tolerance, $"{what}: just down and right of the middle");
        ExpectPixel(context, picture, width - 2, height - 2, Quadrants[3], tolerance, $"{what}: the bottom-right corner");
    }

    private static void BackgroundImage(CheckContext context, RenderBench bench)
    {
        var folder = context.Folder(bench.Graphics.IsSoftware ? "warp" : null);
        using var renderer = new StudioSceneRenderer(bench.Graphics.Device);
        Picture Draw(string? image, int width = 1920, int height = 1080, StudioProject? project = null, bool pictures = false) =>
            bench.Render(
                project ?? WithBackground(bench, new StudioBackground { Style = StudioBackgroundStyle.Image, Primary = Orange, Image = image }),
                width,
                height,
                withScreen: pictures,
                withCamera: pictures,
                folder: folder,
                renderer: renderer);

        // Aspect fill, centred.
        SaveImage(Path.Combine(folder, "fill.png"), 800, 400, TestImage);
        var filled = Draw("fill.png");
        ExpectTestImage(context, filled, 1, "an 800×400 image on 1920×1080");
        context.Expect(renderer.BackgroundImageDecodeCount == 1, $"the image was decoded {renderer.BackgroundImageDecodeCount} times, want 1");

        // On a tall canvas the image is scaled to the canvas height and its middle strip shows.
        var tall = Draw("fill.png", 1080, 1920);
        ExpectPixel(context, tall, 200, 480, Quadrants[0], 1, "on a 1080×1920 canvas: top-left");
        ExpectPixel(context, tall, 880, 480, Quadrants[1], 1, "on a 1080×1920 canvas: top-right");
        ExpectPixel(context, tall, 200, 1440, Quadrants[2], 1, "on a 1080×1920 canvas: bottom-left");
        ExpectPixel(context, tall, 880, 1440, Quadrants[3], 1, "on a 1080×1920 canvas: bottom-right");
        ExpectPixel(context, tall, 2, 960 - 20, Quadrants[0], 1, "on a 1080×1920 canvas: the left edge is well inside the image");

        // The layout changing, the pictures arriving and the canvas resizing do not decode it again.
        var imageProject = bench.Base() with { Canvas = new StudioCanvas { Padding = 0.1, Background = new StudioBackground { Style = StudioBackgroundStyle.Image, Primary = Orange, Image = "fill.png" } } };
        var withPictures = Draw(null, project: imageProject, pictures: true);
        ExpectPixel(context, withPictures, 1, 1080 * 0.3, Quadrants[0], 1, "with the screen and camera drawn: the background at the left edge");
        Draw(null, project: imageProject with { Scenes = [new StudioScene { Layout = StudioLayout.SideBySide }] }, pictures: true);
        Draw("fill.png", 1280, 720);
        Draw("fill.png");
        context.Expect(renderer.BackgroundImageDecodeCount == 1, $"after layout, picture and size changes the image had been decoded {renderer.BackgroundImageDecodeCount} times, want 1");

        // Missing, outside the project folder, or not an image: the primary colour.
        ExpectFlat(context, Draw("no-such-file.png"), OrangeRgb, 0, "a missing image");
        ExpectFlat(context, Draw(null), OrangeRgb, 0, "an image background with no image named");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.Copy(Path.Combine(folder, "fill.png"), Path.Combine(folder, "sub", "inner.png"), overwrite: true);
        ExpectFlat(context, Draw(Path.Combine("sub", "inner.png")), OrangeRgb, 0, "an image named with a folder");
        ExpectFlat(context, Draw("sub/inner.png"), OrangeRgb, 0, "an image named with a folder and a forward slash");
        File.WriteAllText(Path.Combine(folder, "broken.png"), "this is not an image");
        ExpectFlat(context, Draw("broken.png"), OrangeRgb, 0, "a file that is not an image");
        Draw("broken.png", 1280, 720);
        var decodesBefore = renderer.BackgroundImageDecodeCount;

        // A photo stored on its side with the tag that turns it upright.
        SaveImage(Path.Combine(folder, "turned.jpg"), 600, 300, TestImage, jpeg: true, orientation: 6, storedTurn: Drawing.RotateFlipType.Rotate270FlipNone);
        using (var stored = new Drawing.Bitmap(Path.Combine(folder, "turned.jpg")))
        {
            context.Expect(stored.Width == 300 && stored.Height == 600, $"the turned test photo is stored {stored.Width}×{stored.Height}, want 300×600");
        }

        ExpectTestImage(context, Draw("turned.jpg"), 8, "a JPEG with orientation 6");

        // Larger than 3840 on its long side: decoded smaller, drawn the same.
        SaveImage(Path.Combine(folder, "large.png"), 5000, 2500, TestImage);
        ExpectTestImage(context, Draw("large.png"), 2, "a 5000×2500 image");
        context.Expect(renderer.BackgroundImageDecodeCount == decodesBefore + 2, $"two new images were decoded {renderer.BackgroundImageDecodeCount - decodesBefore} times");

        // WebP, which GDI+ does not read and the app itself saves screenshots as.
        SaveWebp(Path.Combine(folder, "fill.webp"), 800, 400, TestImage);
        ExpectTestImage(context, Draw("fill.webp"), 8, "a WebP image");
        SaveWebp(Path.Combine(folder, "large.webp"), 5000, 2500, TestImage);
        ExpectTestImage(context, Draw("large.webp"), 8, "a 5000×2500 WebP image");
        context.Expect(renderer.BackgroundImageDecodeCount == decodesBefore + 4, $"two WebP images were decoded {renderer.BackgroundImageDecodeCount - decodesBefore - 2} times");

        // Transparent on its right half: black shows there.
        SaveImage(Path.Combine(folder, "clear.png"), 800, 400, (u, v) => u < 0.5 ? TestImage(u, v) : ((byte)255, (byte)255, (byte)255, (byte)0));
        var clear = Draw("clear.png");
        ExpectPixel(context, clear, 480, 270, Quadrants[0], 1, "a half-transparent image: its opaque half");
        ExpectPixel(context, clear, 1440, 540, new Rgb(0, 0, 0), 0, "a half-transparent image: its transparent half");

        // The file replaced under the same name is picked up, within the half second the renderer waits between looks.
        Draw("swap.png");
        SaveImage(Path.Combine(folder, "swap.png"), 400, 200, (_, _) => (10, 200, 30, 255));
        Thread.Sleep(650);
        ExpectFlat(context, Draw("swap.png"), new Rgb(10, 200, 30), 0, "an image that appeared after the first draw");
        SaveImage(Path.Combine(folder, "swap.png"), 400, 200, (_, _) => (30, 20, 210, 255));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "swap.png"), DateTime.UtcNow.AddSeconds(2));
        Thread.Sleep(650);
        ExpectFlat(context, Draw("swap.png"), new Rgb(30, 20, 210), 0, "an image replaced under the same name");
    }
}
