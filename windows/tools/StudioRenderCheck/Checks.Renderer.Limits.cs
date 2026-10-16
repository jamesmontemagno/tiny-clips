using System.Diagnostics;
using System.Globalization;
using SharpGen.Runtime;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>What the renderer accepts, how well it scales, and how long a frame takes.</summary>
internal static partial class RendererChecks
{
    private static Exception? Thrown(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string Describe(Exception? exception) => exception is null ? "nothing was thrown" : $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>The screen alone, edge to edge: the frame is the screen's picture scaled to the canvas.</summary>
    private static StudioProject ScreenOnly(RenderBench bench) => bench.Base(StudioLayout.Screen) with
    {
        Canvas = new StudioCanvas { Padding = 0, Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = Orange } },
        Screen = new StudioScreenStyle { Shadow = 0, CornerRadius = 0 },
    };

    private static void TargetsAndSources(CheckContext context, RenderBench bench)
    {
        var graphics = bench.Graphics;
        var project = bench.Base();
        var events = Projects.Events(bench.ScreenClip);
        var reference = bench.Render(project);
        using var renderer = new StudioSceneRenderer(graphics.Device);

        StudioRenderRequest Request(ID3D11Texture2D target, int width, int height, StudioGpuVideoFrame? screen = null) =>
            new(project, events, bench.EmptyFolder, 0, width, height, screen ?? bench.ScreenFrame, bench.CameraFrame, target);

        // A target that is not B8G8R8A8_UNorm is refused, in words that name both formats.
        foreach (var format in new[] { Format.B8G8R8X8_UNorm, Format.R8G8B8A8_UNorm, Format.B8G8R8A8_UNorm_SRgb })
        {
            ID3D11Texture2D target;
            try
            {
                target = graphics.CreateRenderTexture(64, 64, format);
            }
            catch (SharpGenException)
            {
                context.Note($"this device cannot make a {format} render target");
                continue;
            }

            using (target)
            {
                var thrown = Thrown(() => renderer.Render(Request(target, 64, 64)));
                context.Expect(thrown is ArgumentException && thrown.Message.Contains("B8G8R8A8_UNorm", StringComparison.Ordinal) && thrown.Message.Contains(format.ToString(), StringComparison.Ordinal), $"a {format} target: {Describe(thrown)}");
            }
        }

        var noBinding = Thrown(() => renderer.Render(Request(bench.Texture(new byte[64 * 64 * 4], 64, 64), 64, 64)));
        context.Expect(noBinding is ArgumentException && noBinding.Message.Contains("render-target", StringComparison.Ordinal), $"a target without render-target binding: {Describe(noBinding)}");
        context.Note($"a BGRX target is refused with: {Thrown(() => { using var bgrx = graphics.CreateRenderTexture(64, 64, Format.B8G8R8X8_UNorm); renderer.Render(Request(bgrx, 64, 64)); })?.Message}");

        // Sources: BGRX is drawn like BGRA; anything else, and a picture that does not fit its texture, is refused.
        var screenPixels = bench.ScreenClip.Spec.DrawBgra(RenderBench.ScreenNumber);
        ExpectSame(context, bench.Render(project, screen: new StudioGpuVideoFrame(bench.Texture(screenPixels, 1920, 1080, format: Format.B8G8R8X8_UNorm), 0, 1920, 1080)), reference, "a BGRX screen picture against a BGRA one");
        var target1080 = bench.Target(1920, 1080);
        var wrongFormat = Thrown(() => renderer.Render(Request(target1080, 1920, 1080, new StudioGpuVideoFrame(bench.Texture(screenPixels, 1920, 1080, format: Format.R8G8B8A8_UNorm), 0, 1920, 1080))));
        context.Expect(wrongFormat is ArgumentException && wrongFormat.Message.Contains("R8G8B8A8_UNorm", StringComparison.Ordinal), $"an RGBA source: {Describe(wrongFormat)}");
        var tooLarge = Thrown(() => renderer.Render(Request(target1080, 1920, 1080, new StudioGpuVideoFrame(bench.ScreenFrame.Texture, 0, 1920, 1088))));
        context.Expect(tooLarge is ArgumentException, $"a picture taller than its texture: {Describe(tooLarge)}");
        var noSlice = Thrown(() => renderer.Render(Request(target1080, 1920, 1080, new StudioGpuVideoFrame(bench.ScreenFrame.Texture, 1, 1920, 1080))));
        context.Expect(noSlice is ArgumentException, $"a slice the texture does not have: {Describe(noSlice)}");
        var noCanvas = Thrown(() => renderer.Render(Request(target1080, 0, 1080)));
        context.Expect(noCanvas is ArgumentOutOfRangeException, $"a canvas 0 pixels wide: {Describe(noCanvas)}");

        // After all those refusals the same renderer still draws.
        ExpectSame(context, bench.Render(project, renderer: renderer), reference, "the renderer after refusing bad requests");

        // A picture in a larger texture, magenta around it: drawn exactly as from a texture of its own size,
        // enlarged with linear sampling and shrunk with high-quality sampling, which both reach past the edge.
        var padded = new StudioGpuVideoFrame(bench.Texture(screenPixels, 1920, 1080, 1936, 1088), 0, 1920, 1080);
        var screenOnly = ScreenOnly(bench);
        foreach (var (width, height, quality) in new[] { (2880, 1620, StudioRenderQuality.Preview), (1920, 1080, StudioRenderQuality.Preview), (960, 540, StudioRenderQuality.Export), (1000, 562, StudioRenderQuality.Export) })
        {
            var exact = bench.Render(screenOnly, width, height, quality: quality);
            var fromPadded = bench.Render(screenOnly, width, height, quality: quality, screen: padded);
            ExpectSame(context, fromPadded, exact, $"{width}×{height} {quality}: a picture in a padded texture against one in its own");
            var corner = fromPadded.Pixel(width - 1, height - 1);
            context.Expect(corner.Distance(bench.ScreenClip.Spec.Background) <= 1, $"{width}×{height} {quality}: the bottom-right pixel is {corner}, and the screen has {bench.ScreenClip.Spec.Background} there");
        }

        // More targets than the renderer keeps wrappers for, then the first one again.
        var targets = new List<ID3D11Texture2D>();
        try
        {
            Picture? first = null;
            for (var round = 0; round < 2; round++)
            {
                for (var index = 0; index < 20; index++)
                {
                    if (round == 0)
                    {
                        targets.Add(graphics.CreateRenderTexture(320, 180));
                    }

                    Picture drawn;
                    lock (graphics.Gate)
                    {
                        renderer.Render(Request(targets[index], 320, 180));
                        drawn = Picture.FromBgra(graphics.ReadTexture(targets[index]), 320, 180);
                    }

                    first ??= drawn;
                    ExpectSame(context, drawn, first, $"target {index}, round {round}");
                }
            }

            lock (graphics.Gate)
            {
                renderer.ForgetTarget(targets[3]);
                renderer.ForgetSources();
                renderer.Render(Request(targets[3], 320, 180));
                ExpectSame(context, Picture.FromBgra(graphics.ReadTexture(targets[3]), 320, 180), first!, "after ForgetTarget and ForgetSources");
            }
        }
        finally
        {
            lock (graphics.Gate)
            {
                foreach (var target in targets)
                {
                    renderer.ForgetTarget(target);
                    target.Dispose();
                }
            }
        }

        var disposed = new StudioSceneRenderer(graphics.Device);
        disposed.Dispose();
        disposed.Dispose();
        var afterDispose = Thrown(() => disposed.Render(Request(target1080, 1920, 1080)));
        context.Expect(afterDispose is ObjectDisposedException, $"drawing with a disposed renderer: {Describe(afterDispose)}");
    }

    private static void ScalingQuality(CheckContext context, RenderBench bench)
    {
        // Fine stripes: one white column in three on the left half, one white row in three on the
        // right half. Shrunk to less than half size, a correct result is flat grey at their mean, 85.
        var stripes = new byte[1920 * 1080 * 4];
        for (var y = 0; y < 1080; y++)
        {
            for (var x = 0; x < 1920; x++)
            {
                var white = x < 960 ? x % 3 == 0 : y % 3 == 0;
                var i = ((y * 1920) + x) * 4;
                stripes[i] = stripes[i + 1] = stripes[i + 2] = white ? (byte)255 : (byte)0;
                stripes[i + 3] = 255;
            }
        }

        var frame = new StudioGpuVideoFrame(bench.Texture(stripes, 1920, 1080), 0, 1920, 1080);
        var project = ScreenOnly(bench);
        var c = CultureInfo.InvariantCulture;
        foreach (var (width, height) in new[] { (480, 270), (692, 389), (960, 540) })
        {
            (double Mean, double Deviation) Measure(StudioRenderQuality quality)
            {
                var picture = bench.Render(project, width, height, quality: quality, screen: frame);
                double worst = 0;
                double mean = 0;
                foreach (var (from, to) in new[] { (0.05, 0.45), (0.55, 0.95) })
                {
                    double sum = 0;
                    double squares = 0;
                    var count = 0;
                    for (var y = (int)(height * 0.1); y < (int)(height * 0.9); y++)
                    {
                        for (var x = (int)(width * from); x < (int)(width * to); x++)
                        {
                            var value = picture.Pixel(x, y).G;
                            sum += value;
                            squares += value * value;
                            count++;
                        }
                    }

                    var average = sum / count;
                    worst = Math.Max(worst, Math.Sqrt(Math.Max(0, (squares / count) - (average * average))));
                    mean += average / 2;
                }

                return (mean, worst);
            }

            var linear = Measure(StudioRenderQuality.Preview);
            var high = Measure(StudioRenderQuality.Export);
            var scale = width / 1920.0;
            context.Measure(string.Create(c, $"stripes drawn at {scale:0.00} of their size: linear sampling leaves a ripple of {linear.Deviation:0.0} of 255 (standard deviation) around {linear.Mean:0}, high-quality sampling {high.Deviation:0.0} around {high.Mean:0}; flat grey would be 0 around 85"));
            context.Expect(Math.Abs(high.Mean - 85) <= 4, string.Create(c, $"at {scale:0.00}, high-quality sampling averages {high.Mean:0.0}, want 85"));
            if (scale < 0.45)
            {
                context.Expect(high.Deviation <= 12, string.Create(c, $"at {scale:0.00}, high-quality sampling leaves a ripple of {high.Deviation:0.0}"));
                context.Expect(high.Deviation < linear.Deviation / 3, string.Create(c, $"at {scale:0.00}, high-quality sampling ({high.Deviation:0.0}) is not clearly better than linear ({linear.Deviation:0.0})"));
            }
        }
    }

    private static void DrawTime(CheckContext context, RenderBench bench)
    {
        // The default look at 2560×1440: gradient, both shadows, a border, a 1440p screen and a 720p camera in the bubble.
        var screenClip = new TestClip("screen.mp4", ClipSpec.Screen(2560, 1440), 30, 1, 180, 0);
        var screen = new StudioGpuVideoFrame(bench.Texture(screenClip.Spec.DrawBgra(7), 2560, 1440), 0, 2560, 1440);
        var project = Projects.Create(screenClip, bench.CameraClip, cameraOffset: 0);
        var events = Projects.Events(screenClip);
        var graphics = bench.Graphics;
        var frames = graphics.IsSoftware ? 12 : 240;
        var c = CultureInfo.InvariantCulture;
        var results = new List<string>();
        foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440) })
        {
            var target = bench.Target(width, height);
            foreach (var quality in new[] { StudioRenderQuality.Preview, StudioRenderQuality.Export })
            {
                double best = double.MaxValue;
                for (var run = 0; run < 3; run++)
                {
                    lock (graphics.Gate)
                    {
                        var request = new StudioRenderRequest(project, events, bench.EmptyFolder, 0, width, height, screen, bench.CameraFrame, target, null, quality);
                        bench.Renderer.Render(request);
                        graphics.ReadTexture(target);
                        var watch = Stopwatch.StartNew();
                        for (var index = 0; index < frames; index++)
                        {
                            bench.Renderer.Render(request);
                        }

                        // Reading back waits for the GPU to finish everything queued.
                        graphics.ReadTexture(target);
                        best = Math.Min(best, watch.Elapsed.TotalMilliseconds / frames);
                    }
                }

                results.Add(string.Create(c, $"{width}×{height} {(quality == StudioRenderQuality.Export ? "high-quality" : "linear")} {best:0.00} ms"));
            }
        }

        context.Measure($"one frame, best of three runs of {frames} on {graphics.AdapterName}: {string.Join(", ", results)}");
    }
}
