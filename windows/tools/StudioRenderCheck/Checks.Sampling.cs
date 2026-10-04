using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// Which sampling an export used, read from its pixels. The source carries a field of stripes
/// three pixels apart; drawn at 0.36 of its size the field comes out flat grey when the picture
/// was filtered first (high-quality sampling) and with a strong ripple when it was sampled
/// linearly. An export that is not asked for a quality filters on graphics hardware and samples
/// linearly when its frames are drawn in software, where filtering is slower than the video plays.
/// </summary>
internal static class SamplingChecks
{
    /// <summary>The ripple linear sampling must leave, and high-quality sampling must stay under, in luma levels of 255.</summary>
    private const double RippleOfLinear = 25;
    private const double RippleOfHighQuality = 8;

    public static async Task Run(Harness harness)
    {
        await harness.Check("warp", "sampling: linear by default when frames are drawn in software, and what is asked for is used", async context =>
        {
            var c = CultureInfo.InvariantCulture;
            var screen = harness.Clips.StripedScreen;
            var spec = screen.Spec;
            var stripes = spec.Stripes ?? throw new CheckFailedException("the clip has no stripe field");

            // The clip itself: the stripes have to be there for their absence to mean anything.
            Picture? last = null;
            Media.ReadFrames(screen.Path, chroma: false, frame =>
            {
                last = frame.Picture;
                return true;
            });
            var inSource = Ripple(last!, stripes.X, stripes.Y, stripes.X + stripes.Width, stripes.Y + stripes.Height);
            context.Expect(inSource.Deviation >= 60, string.Create(c, $"the stripes in the source clip have a deviation of only {inSource.Deviation:0.0}; drawn, it is 120"));

            // The screen alone, edge to edge, at 460/1280 = 0.36 of its size.
            var project = Projects.Create(screen, null) with
            {
                Canvas = new StudioCanvas { Padding = 0, Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#202020" } },
                Screen = new StudioScreenStyle { CornerRadius = 0, Shadow = 0 },
            };
            var size = StudioCanvasMath.ExportSize(StudioCanvasMath.NaturalSize(project), 460);
            var width = (int)size.Width;
            var height = (int)size.Height;
            context.Expect(width == 460, $"the export would be {width}×{height}, and the check was worked out for a width of 460");

            var measured = new List<string>();
            foreach (var (name, software, asked) in new (string, bool, StudioRenderQuality?)[]
            {
                ("software, not asked", true, null),
                ("software, high quality asked for", true, StudioRenderQuality.Export),
                ("software, linear asked for", true, StudioRenderQuality.Preview),
                ("hardware, not asked", false, null),
                ("hardware, linear asked for", false, StudioRenderQuality.Preview),
            })
            {
                var options = new StudioExportOptions(
                    LongSideLimit: 460,
                    DevicePreference: software ? StudioRenderDevicePreference.WarpOnly : StudioRenderDevicePreference.HardwareWithWarpFallback,
                    RenderQuality: asked);
                var label = name.Replace(", ", "-", StringComparison.Ordinal).Replace(' ', '-');
                var (result, _, _) = await Exporting.ExportAndVerify(context, label, project, screen, null, width, height, 30, 1, expectAudio: false, options, verify: false).ConfigureAwait(false);
                context.Expect(result.Width == width && result.Height == height && result.FrameCount == 30, $"{name}: the result is {result.Width}×{result.Height}, {result.FrameCount} frames");
                if (software)
                {
                    context.Expect(result.SoftwareRendering, $"{name}: the result says the frames were not drawn in software");
                }

                // What must come out: what was asked for, and unasked, by where the frames were drawn.
                var wantLinear = asked is { } quality ? quality == StudioRenderQuality.Preview : result.SoftwareRendering;

                Picture? picture = null;
                var frames = Media.ReadFrames(result.OutputPath, chroma: false, frame =>
                {
                    picture = frame.Picture;
                    return true;
                });
                if (!context.Expect(frames == 30 && picture is not null && picture.Width == width && picture.Height == height, $"{name}: {frames} frames of {picture?.Width}×{picture?.Height} in the file"))
                {
                    continue;
                }

                // The stripe field in the export, three pixels inside its edges.
                var scaleX = width / (double)spec.Width;
                var scaleY = height / (double)spec.Height;
                var ripple = Ripple(
                    picture!,
                    (int)Math.Ceiling(stripes.X * scaleX) + 3,
                    (int)Math.Ceiling(stripes.Y * scaleY) + 3,
                    (int)Math.Floor((stripes.X + stripes.Width) * scaleX) - 3,
                    (int)Math.Floor((stripes.Y + stripes.Height) * scaleY) - 3);
                if (wantLinear)
                {
                    context.Expect(ripple.Deviation >= RippleOfLinear, string.Create(c, $"{name}: the stripe field has a ripple of {ripple.Deviation:0.0}, so it was not sampled linearly (linear leaves {RippleOfLinear:0} or more)"));
                }
                else
                {
                    context.Expect(ripple.Deviation <= RippleOfHighQuality, string.Create(c, $"{name}: the stripe field has a ripple of {ripple.Deviation:0.0}, so it was not filtered (high-quality sampling leaves {RippleOfHighQuality:0} or less)"));
                    context.Expect(Math.Abs(ripple.Mean - 85) <= 8, string.Create(c, $"{name}: the stripe field averages {ripple.Mean:0.0}, want the stripes' own mean, 85"));
                }

                context.Expect(result.RenderQuality == (wantLinear ? StudioRenderQuality.Preview : StudioRenderQuality.Export), $"{name}: the result says {result.RenderQuality}");
                measured.Add(string.Create(c, $"{name}: ripple {ripple.Deviation:0.0} ({(wantLinear ? "linear" : "high quality")})"));
            }

            context.Measure(string.Create(c, $"stripes at 0.36 of their size in a {width}×{height} export, as luma deviation of 255 (the source clip's is {inSource.Deviation:0.0}): {string.Join("; ", measured)}"));
        }).ConfigureAwait(false);
    }

    /// <summary>The mean and the standard deviation of luma over a rectangle of a picture.</summary>
    private static (double Mean, double Deviation) Ripple(Picture picture, int left, int top, int right, int bottom)
    {
        double sum = 0;
        double squares = 0;
        var count = 0;
        for (var y = Math.Max(0, top); y < Math.Min(picture.Height, bottom); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(picture.Width, right); x++)
            {
                var value = picture.Luma(x, y);
                sum += value;
                squares += value * value;
                count++;
            }
        }

        if (count == 0)
        {
            return (double.NaN, double.NaN);
        }

        var mean = sum / count;
        return (mean, Math.Sqrt(Math.Max(0, (squares / count) - (mean * mean))));
    }
}
