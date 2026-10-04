using System.Globalization;
using System.Runtime.InteropServices;
using TinyClips.Core.Studio.Rendering;
using Vortice.MediaFoundation;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// The exporter's readers on their own: the frame showing at a time, asked for in any order,
/// and sound from any sample. What comes back is read from the texture and compared with the
/// number painted into the frame, and with a straight decode of the track.
/// </summary>
internal static class FrameSourceChecks
{
    public static async Task Run(Harness harness)
    {
        var clips = harness.Clips;
        foreach (var warp in new[] { false, true })
        {
            var device = warp ? " on WARP" : string.Empty;
            await harness.Check("sources", $"frame source{device}: H.264, the frame at any time in any order", context => Frames(context, clips.Screen, warp)).ConfigureAwait(false);
            await harness.Check("sources", $"frame source{device}: HEVC, 1080 lines", context => Frames(context, clips.HevcScreen, warp)).ConfigureAwait(false);
            await harness.Check("sources", $"frame source{device}: untagged camera", context => Frames(context, clips.Camera, warp)).ConfigureAwait(false);
            await harness.Check("sources", $"frame source{device}: 640×480, untagged BT.601", context => Frames(context, clips.SmallScreen, warp)).ConfigureAwait(false);
            await harness.Check("sources", $"frame source{device}: a recording with dropped frames, at every 30th of a second", context => EveryMoment(context, clips.GappyScreen, warp)).ConfigureAwait(false);
        }

        await harness.Check("sources", "audio source: sound from any sample is the sound of a straight decode", context => Audio(context, clips.Screen)).ConfigureAwait(false);
    }

    private static void Frames(CheckContext context, TestClip clip, bool warp)
    {
        var c = CultureInfo.InvariantCulture;
        using var graphics = StudioGraphicsDevice.Create(warp);
        IMFDXGIDeviceManager? manager = null;
        try
        {
            if (!graphics.IsSoftware)
            {
                manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(graphics.Device).CheckError();
            }

            using var source = StudioVideoSource.Open(clip.Path, "test clip", graphics, manager);
            context.Expect(source.Width == clip.Spec.Width && source.Height == clip.Spec.Height, $"the source says {source.Width}×{source.Height}, want {clip.Spec.Width}×{clip.Spec.Height}");
            context.Expect(Math.Abs((source.DurationTicks / 1e7) - clip.DurationSeconds) < 0.1, string.Create(c, $"the source says it lasts {source.DurationTicks / 1e7:0.000} s"));

            // When each frame starts by the file's own account, read here without the exporter.
            // A track keeps time in its own units, so a frame written at 3 666 667 can come back
            // as starting at 3 666 666, and a request one tick before a frame is only that if
            // it is made against these.
            var starts = new List<long>();
            Media.ReadFrames(clip.Path, chroma: false, frame =>
            {
                starts.Add(frame.Time);
                return true;
            });
            context.Expect(starts.Count == clip.Frames, $"{starts.Count} frames in the file, want {clip.Frames}");
            if (starts.Count != clip.Frames)
            {
                return;
            }

            var offGrid = 0L;
            for (var frame = 0; frame < starts.Count; frame++)
            {
                offGrid = Math.Max(offGrid, Math.Abs(starts[frame] - Media.FrameTime(frame, clip.Numerator, clip.Denominator)));
            }

            long Start(int frame) => starts[frame];
            var half = (Start(1) - Start(0)) / 2;
            var last = clip.Frames - 1;
            var steps = new List<(long Time, int Frame, string What)>();
            for (var frame = 0; frame <= 10; frame++)
            {
                steps.Add((Start(frame) + 3, frame, "one frame on"));
            }

            steps.Add((Start(11) - 1, 10, "one tick before the next frame"));
            steps.Add((Start(11), 11, "exactly on a frame"));
            steps.Add((Start(11) + half, 11, "the middle of a frame"));
            steps.Add((Start(38), 38, "0.9 s on: decoded through, no seek"));
            steps.Add((Start(last - 6), last - 6, "more than a second on: a seek"));
            steps.Add((Start(30), 30, "back: a seek"));
            steps.Add((Start(29), 29, "back one frame: a seek"));
            steps.Add((Start(last) + 5, last, "the last frame: a seek"));
            steps.Add((Start(last) + 40_000_000, last, "four seconds past the end"));
            steps.Add((-10_000_000, 0, "before the start: a seek"));
            steps.Add((Start(61) + half, 61, "the middle of the frame after a keyframe: a seek"));
            steps.Add((Start(62) - 1, 61, "its last tick"));
            const int ExpectedSeeks = 6;

            var wrong = new List<string>();
            var worstColor = 0.0;
            var worstEdge = 0.0;
            foreach (var (time, want, what) in steps)
            {
                var frame = source.GetFrame(time, CancellationToken.None);
                Picture picture;
                lock (graphics.Gate)
                {
                    picture = Picture.FromBgra(graphics.ReadTexture(frame.Texture, frame.Subresource), frame.Width, frame.Height);
                }

                var number = FrameCode.Decode(picture, clip.Spec, ClipMap.Identity);
                if (number != want)
                {
                    wrong.Add(string.Create(c, $"{what} ({time / 1e7:0.0000} s): frame {number}, want {want}"));
                }

                var patches = FrameCode.ReadPatches(picture, clip.Spec, ClipMap.Identity);
                for (var index = 0; index < patches.Length; index++)
                {
                    worstColor = Math.Max(worstColor, patches[index].Distance(ClipSpec.PatchColors[index]));
                }

                // The picture fills the frame exactly: not shifted, stretched or letterboxed.
                var edge = FrameCode.PatchEdgeError(picture, clip.Spec, ClipMap.Identity) ?? double.NaN;
                worstEdge = double.IsNaN(worstEdge) || double.IsNaN(edge) ? double.NaN : Math.Max(worstEdge, edge);

                // The last row and the last column are picture, not decoder padding.
                var bottom = picture.Average(clip.Spec.Width * 0.7, clip.Spec.Height - 1, 0);
                var right = picture.Average(clip.Spec.Width - 1, clip.Spec.Height * 0.75, 0);
                if (bottom.Distance(clip.Spec.Background) > 12 || right.Distance(clip.Spec.Background) > 12)
                {
                    wrong.Add($"{what}: the bottom row is {bottom} and the right column {right}, and the picture has {clip.Spec.Background} there");
                }
            }

            context.Expect(wrong.Count == 0, $"{wrong.Count} of {steps.Count} requests came back wrong ({string.Join("; ", wrong.Take(3))})");
            context.Expect(source.Seeks == ExpectedSeeks, $"the reader seeked {source.Seeks} times, want {ExpectedSeeks}");
            context.Expect(worstColor <= 6, string.Create(c, $"decoded colours are {worstColor:0.0} of 255 from what was drawn"));
            context.Expect(worstEdge <= 0.75, string.Create(c, $"an edge in the decoded picture is {worstEdge:0.00} px from where it was drawn"));
            context.Expect(source.UsesDevice == !graphics.IsSoftware, $"the reader {(source.UsesDevice ? "has" : "does not have")} the device");
            context.Expect(source.KeepsFrameTimes, "the reader's video processor was not found, so it may re-time frames");
            context.Note(string.Create(c, $"{steps.Count} requests right, {source.Seeks} seeks, {source.FramesRead} frames decoded; frames arrive as {source.FrameStorage}{(source.CopiesPicture ? ", and the picture is copied out of the padding" : source.UsesDevice ? ", wrapped without a copy" : ", uploaded")}; colours within {worstColor:0.0} of 255, edges within {worstEdge:0.00} px; the file's frame times are within {offGrid} × 100 ns of where they were written"));
        }
        finally
        {
            manager?.Dispose();
        }
    }

    /// <summary>The frame showing at the middle of every 30th of a second, forwards: what an export asks for.</summary>
    private static void EveryMoment(CheckContext context, TestClip clip, bool warp)
    {
        var c = CultureInfo.InvariantCulture;
        using var graphics = StudioGraphicsDevice.Create(warp);
        IMFDXGIDeviceManager? manager = null;
        try
        {
            if (!graphics.IsSoftware)
            {
                manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(graphics.Device).CheckError();
            }

            using var source = StudioVideoSource.Open(clip.Path, "test clip", graphics, manager);
            var moments = (int)Math.Round(clip.DurationSeconds * 30);
            var wrong = new List<string>();
            for (var index = 0; index < moments; index++)
            {
                var seconds = (index + 0.5) / 30;
                var frame = source.GetFrame((long)Math.Round(seconds * 1e7), CancellationToken.None);
                Picture picture;
                lock (graphics.Gate)
                {
                    picture = Picture.FromBgra(graphics.ReadTexture(frame.Texture, frame.Subresource), frame.Width, frame.Height);
                }

                var number = FrameCode.Decode(picture, clip.Spec, ClipMap.Identity);
                var want = clip.FrameAt(seconds);
                if (number != want)
                {
                    wrong.Add(string.Create(c, $"at {seconds:0.0000} s frame {number}, want {want} (which starts at {clip.FrameTime(want) / 1e7:0.0000} s)"));
                }
            }

            context.Expect(wrong.Count == 0, $"{wrong.Count} of {moments} moments show the wrong frame: {string.Join("; ", wrong.Take(8))}");
            context.Expect(source.KeepsFrameTimes, "the reader's video processor was not found, so it may re-time frames");
            context.Note($"{moments} moments, {clip.Frames} frames in the file, {source.Seeks} seeks, {source.FramesRead} frames decoded; frames arrive as {source.FrameStorage}");
        }
        finally
        {
            manager?.Dispose();
        }
    }

    private static void Audio(CheckContext context, TestClip clip)
    {
        var reference = Media.ReadAudio(clip.Path);
        if (reference is null)
        {
            context.Fail("the clip's sound cannot be decoded");
            return;
        }

        using var source = StudioAudioSource.TryOpen(clip.Path, "test clip");
        if (source is null)
        {
            context.Fail("the audio source found no sound track");
            return;
        }

        context.Expect(source.Format.SampleRate == reference.SampleRate && source.Format.Channels == reference.Channels && source.Format.BitsPerSample == 16, $"the source decodes to {source.Format.SampleRate} Hz, {source.Format.Channels} channels, {source.Format.BitsPerSample} bits");
        var channels = source.Format.Channels;
        var total = reference.Left.Length;
        var worst = 0;
        var compared = 0L;
        var problems = new List<string>();

        // From the start without a seek, then from places on and off AAC frame boundaries, forwards and back.
        foreach (var (position, seek) in new[] { (0L, false), (12345L, true), (96017L, true), (250000L, true), (5L, true), (1024L * 100, true), (total - 3000L, true) })
        {
            if (seek)
            {
                source.Seek(position);
            }

            var want = Math.Min(4800, total - position);
            var seen = new bool[want];
            long first = -1;
            while (source.TryRead(out var start, out var pcm))
            {
                first = first < 0 ? start : first;
                var samples = MemoryMarshal.Cast<byte, short>(pcm);
                var count = samples.Length / channels;
                for (var index = 0; index < count; index++)
                {
                    var at = start + index - position;
                    if (at < 0 || at >= want)
                    {
                        continue;
                    }

                    seen[at] = true;
                    compared++;
                    worst = Math.Max(worst, Math.Abs(samples[index * channels] - reference.Left[start + index]));
                    if (channels > 1)
                    {
                        worst = Math.Max(worst, Math.Abs(samples[(index * channels) + 1] - reference.Right[start + index]));
                    }
                }

                if (start + count >= position + want)
                {
                    break;
                }
            }

            var missing = seen.Count(value => !value);
            if (missing > 0)
            {
                problems.Add($"from sample {position}: {missing} of {want} samples never arrived");
            }

            if (first > position)
            {
                problems.Add($"from sample {position}: the first block starts at {first}, after it");
            }

            if (seek && position >= StudioAudioSource.SeekRunInSamples && first > position - 1024)
            {
                problems.Add($"from sample {position}: the first block starts at {first}, less than one AAC frame before it");
            }
        }

        context.Expect(problems.Count == 0, string.Join("; ", problems));
        context.Expect(worst <= 1, $"after a seek the sound differs from a straight decode by {worst} of 32768");
        context.Measure($"{compared} samples read after seeks differ from a straight decode by at most {worst} of 32768");
    }
}
