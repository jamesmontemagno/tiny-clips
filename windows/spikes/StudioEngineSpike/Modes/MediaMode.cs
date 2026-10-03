using System.Globalization;
using StudioEngineSpike.Engine;

namespace StudioEngineSpike.Modes;

/// <summary>
/// Generates the two synthetic clips and checks them from the outside with ffprobe/ffmpeg: stream
/// layout, keyframe spacing, that the frame-number strip decodes on sampled frames, and the
/// reference colours every other mode compares against.
/// </summary>
internal static class MediaMode
{
    private static readonly int[] SampleFrames = { 0, 1, 59, 60, 61, 450, 899 };

    public static int Run(SpikeOptions options)
    {
        using var report = options.OpenReport();
        report.Section("Test media (ffmpeg)");
        TestMedia.Generate(options.Root, report, options.Flag("force"));

        var failures = 0;
        var reference = new List<string>();
        foreach (var clip in new[] { TestMedia.Screen, TestMedia.Camera })
        {
            var path = Path.Combine(TestMedia.Directory(options.Root), clip.FileName);
            report.Section(clip.FileName);
            report.Line(Probe(path, "v:0", "codec_name,profile,level,width,height,pix_fmt,r_frame_rate,avg_frame_rate,time_base,start_time,duration,nb_frames,bit_rate,has_b_frames,color_range,color_space,color_transfer,color_primaries"));
            if (clip.HasAudio)
            {
                report.Line(Probe(path, "a:0", "codec_name,profile,sample_rate,channels,time_base,start_time,start_pts,duration,nb_frames,bit_rate"));
            }

            var keyframes = TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey", "-show_entries", "frame=pts_time", "-of", "csv=p=0", path }, report)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => double.Parse(text.TrimEnd(','), CultureInfo.InvariantCulture))
                .ToArray();
            var spacing = keyframes.Zip(keyframes.Skip(1), (a, b) => b - a).ToArray();
            report.Line($"keyframes: {keyframes.Length}, first {keyframes.FirstOrDefault().F("0.###")} s, spacing min {spacing.DefaultIfEmpty().Min().F("0.###")} s max {spacing.DefaultIfEmpty().Max().F("0.###")} s");

            // Decode a handful of frames with ffmpeg (the reference decoder) and read them back.
            var select = string.Join('+', SampleFrames.Select(n => $"eq(n\\,{n})"));
            var raw = TestMedia.RunToolBinary("ffmpeg", new[]
            {
                "-v", "error", "-i", path, "-vf", $"select='{select}'", "-fps_mode", "passthrough",
                "-f", "rawvideo", "-pix_fmt", "bgra", "-",
            });
            var frameBytes = clip.Width * clip.Height * 4;
            if (raw.Length != frameBytes * SampleFrames.Length)
            {
                report.Line($"FAIL: expected {SampleFrames.Length} decoded frames, got {raw.Length / (double)frameBytes:0.##}");
                failures++;
                continue;
            }

            var decoded = new List<string>();
            for (var index = 0; index < SampleFrames.Length; index++)
            {
                var frame = raw.AsSpan(index * frameBytes, frameBytes);
                var number = FrameCode.Decode(frame, clip.Width, clip.Height, 4, clip, ClipMap.Identity);
                decoded.Add($"{SampleFrames[index]}→{number}");
                if (number != SampleFrames[index])
                {
                    failures++;
                }
            }

            report.Line($"strip self-check (expected→decoded): {string.Join("  ", decoded)}");

            // Reference colours: take the decoder's own YUV for frame 0 (no conversion by ffmpeg at
            // all) and convert it with the exact BT.709 limited-range matrix. swscale's BGRA output
            // is shown next to it only to illustrate how far a fast converter rounds.
            var yuv = TestMedia.RunToolBinary("ffmpeg", new[]
            {
                "-v", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-",
            });
            var colors = ReferenceColors.FromYuv420(yuv, clip.Width, clip.Height, clip);
            report.Line($"reference colours (decoded YUV → exact BT.709 limited-range matrix): {ReferenceColors.Describe(colors)}");
            var swscale = ReferenceColors.Read(raw.AsSpan(0, frameBytes), clip.Width, clip.Height, clip, ClipMap.Identity);
            report.Line($"  for comparison, ffmpeg/swscale BGRA: {ReferenceColors.Compare(colors, swscale)}");
            reference.Add(ReferenceColors.Serialize(clip, colors));

            if (!options.Flag("no-png"))
            {
                var png = Path.Combine(options.OutputDirectory(), Path.GetFileNameWithoutExtension(clip.FileName) + "-frame0.png");
                PngWriter.WriteBgra(png, raw.AsSpan(0, frameBytes), clip.Width, clip.Height);
                report.Line($"wrote {Path.GetRelativePath(options.Root, png)}");
            }
        }

        File.WriteAllLines(ReferenceColors.FilePath(options.Root), reference);
        report.Line();
        report.Line(failures == 0 ? "RESULT: media OK" : $"RESULT: {failures} self-check failure(s)");
        return failures == 0 ? 0 : 3;
    }

    private static string Probe(string path, string stream, string entries)
    {
        var output = TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-select_streams", stream, "-show_entries", "stream=" + entries, "-of", "default=nw=1", path }, null);
        return stream + ": " + string.Join(", ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
