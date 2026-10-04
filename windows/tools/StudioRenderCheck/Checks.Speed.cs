using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// How fast an export runs: the default look (gradient, shadows, camera bubble with a border)
/// from a 20 s 2560×1440 recording with a 1280×720 camera. Every figure is one whole export,
/// start-up included, and the first export of each kind is verified frame by frame.
/// </summary>
internal static class SpeedChecks
{
    public static async Task Run(Harness harness)
    {
        if (!harness.Wants("speed"))
        {
            return;
        }

        var clips = harness.Clips;
        foreach (var (limit, width, height) in new[] { (1920.0, 1920, 1080), (2560.0, 2560, 1440) })
        {
            foreach (var quality in new[] { StudioRenderQuality.Preview, StudioRenderQuality.Export })
            {
                var sampling = quality == StudioRenderQuality.Export ? "high-quality" : "linear";
                await harness.Check("speed", $"speed: 20 s at {width}×{height}, {sampling} sampling", async context =>
                {
                    var project = Projects.Create(clips.BigScreen, clips.LongCamera);
                    var options = new StudioExportOptions(LongSideLimit: limit, RenderQuality: quality);
                    var rates = new List<double>();
                    var encoder = string.Empty;
                    for (var run = 0; run < 3; run++)
                    {
                        var (result, reading, seconds) = await Exporting.ExportAndVerify(context, $"run{run + 1}", project, clips.BigScreen, clips.LongCamera, width, height, 30, 1, expectAudio: true, options, verify: run == 0).ConfigureAwait(false);
                        context.Expect(result.FrameCount == 600, $"run {run + 1}: {result.FrameCount} frames, want 600");
                        if (run == 0)
                        {
                            ExportChecks.ExpectNumbers(context, reading, 600, index => index, index => index < 6 ? FrameCode.Unreadable : index - 6);
                        }

                        rates.Add(result.FrameCount / seconds);
                        encoder = result.EncoderDescription;
                    }

                    var c = CultureInfo.InvariantCulture;
                    context.Expect(rates.Min() >= 30, string.Create(c, $"the slowest run made {rates.Min():0} frames a second: slower than the video plays"));
                    context.Measure(string.Create(c, $"{rates.Min():0} to {rates.Max():0} fps over three runs ({rates.Min() / 30:0.0}× to {rates.Max() / 30:0.0}× real time), {encoder}"));
                });
            }
        }

        // The slow paths, over ten seconds of the same recording.
        var cases = new (string Name, StudioExportOptions Options)[]
        {
            ("software encoder, high-quality sampling", new StudioExportOptions(LongSideLimit: 1920, EncoderPreference: StudioEncoderPreference.SoftwareOnly)),
            ("drawn by WARP, linear sampling", new StudioExportOptions(LongSideLimit: 1920, DevicePreference: StudioRenderDevicePreference.WarpOnly, RenderQuality: StudioRenderQuality.Preview)),
            ("drawn by WARP, high-quality sampling", new StudioExportOptions(LongSideLimit: 1920, DevicePreference: StudioRenderDevicePreference.WarpOnly, RenderQuality: StudioRenderQuality.Export)),
        };
        foreach (var (name, options) in cases)
        {
            await harness.Check("speed", $"speed: 10 s at 1920×1080, {name}", async context =>
            {
                var project = Projects.Create(clips.BigScreen, clips.LongCamera) with { Edits = new StudioEdits { TrimStart = 2, TrimEnd = 12 } };
                var (result, reading, seconds) = await Exporting.ExportAndVerify(context, "slow", project, clips.BigScreen, clips.LongCamera, 1920, 1080, 30, 1, expectAudio: true, options).ConfigureAwait(false);
                ExportChecks.ExpectNumbers(context, reading, 300, index => 60 + index, index => 54 + index);
                var rate = result.FrameCount / seconds;
                context.Measure(string.Create(CultureInfo.InvariantCulture, $"{rate:0} fps ({rate / 30:0.0}× real time), {result.EncoderDescription}{(result.SoftwareRendering ? ", drawn by WARP" : string.Empty)}"));
            });
        }
    }
}
