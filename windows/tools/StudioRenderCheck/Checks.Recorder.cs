using System.Globalization;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// The recorder's own code, fed frames made here instead of a camera or a screen: which way up
/// the files it writes are, and what it reads back from them for a project. A file is decoded
/// and its frame numbers are read twice, as the picture is and turned upside down, so an
/// upside-down file is told apart from an unreadable one.
/// </summary>
internal static class RecorderChecks
{
    /// <summary>The buffers WebcamCaptureService takes turns with (its FrameRingDepth).</summary>
    private const int WebcamRingDepth = 6;

    public static async Task Run(Harness harness)
    {
        await harness.Check("recorder", "camera recorder: frames as the webcam service delivers them, hardware encoder", context => Camera(context, forceSoftware: false)).ConfigureAwait(false);
        await harness.Check("recorder", "camera recorder: the same frames, software encoder", context => Camera(context, forceSoftware: true)).ConfigureAwait(false);
        await harness.Check("recorder", "camera recorder: frames of an odd size, 1281×721", context => Camera(context, forceSoftware: false, extra: 1)).ConfigureAwait(false);
        await harness.Check("recorder", "regular recorder: the CPU path as it is, H.264", context => CpuPath(context, VideoCodec.H264)).ConfigureAwait(false);
        await harness.Check("recorder", "regular recorder: the CPU path as it is, HEVC", context => CpuPath(context, VideoCodec.Hevc)).ConfigureAwait(false);
        await harness.Check("recorder", "regular recorder: the GPU path for comparison, H.264", context => GpuPath(context, VideoCodec.H264)).ConfigureAwait(false);
        await harness.Check("recorder", "regular recorder: the GPU path for comparison, HEVC", context => GpuPath(context, VideoCodec.Hevc)).ConfigureAwait(false);
        await harness.Check("recorder", "MediaFileProbe: the size of the picture, H.264 and 1080-line HEVC", context => Probe(context, harness.Clips)).ConfigureAwait(false);
    }

    /// <summary>
    /// The regular recorder's usual path, for comparison with its CPU path: the same encoder,
    /// created the same way, given each frame as a texture from its own allocator, which is what
    /// <c>GpuCaptureSession</c> draws into. Nothing is captured: the test picture is copied
    /// into the texture, whose first row is the top one.
    /// </summary>
    private static unsafe void GpuPath(CheckContext context, VideoCodec codec)
    {
        var c = CultureInfo.InvariantCulture;
        var hevc = codec == VideoCodec.Hevc;
        if (hevc && !Media.HasEncoder(hevc: true, hardware: null))
        {
            throw new CheckSkippedException("this PC has no HEVC encoder");
        }

        const int width = 1920;
        const int height = 1080;
        const int fps = 30;
        const int frames = 60;
        var spec = ClipSpec.Screen(width, height);
        var path = Path.Combine(context.Folder(), "screen.mp4");

        var device = WgcInterop.GetSharedDevice().D3D;
        var bitrate = (uint)Math.Clamp((long)width * height * fps / 10, 2_000_000, 24_000_000);
        if (hevc)
        {
            bitrate = (uint)(bitrate * 0.6);
        }

        string description;
        var encoder = MfSinkWriterEncoder.Create(
            path,
            device,
            width,
            height,
            fps,
            bitrate,
            codec,
            includeAudio: false,
            AudioCaptureService.SampleRate,
            AudioCaptureService.Channels,
            AudioCaptureService.BitsPerSample,
            192_000);
        try
        {
            description = encoder.Description;
            using var allocator = encoder.CreateFrameAllocator(4, 16);

            // The device hands out the one context it keeps; it is not this check's to release.
            var immediate = device.ImmediateContext;
            var frameDuration = TimeSpan.FromSeconds(1.0 / fps);
            var pixels = spec.DrawBgra(0);
            for (var frame = 0; frame < frames; frame++)
            {
                spec.StampBgra(pixels, frame);
                GpuFrame texture;
                var waiting = System.Diagnostics.Stopwatch.StartNew();
                while (!allocator.TryAcquire(out texture))
                {
                    if (waiting.Elapsed > TimeSpan.FromSeconds(10))
                    {
                        throw new CheckFailedException($"the encoder gave no texture for frame {frame} in ten seconds");
                    }

                    Thread.Sleep(1);
                }

                try
                {
                    fixed (byte* source = pixels)
                    {
                        immediate.UpdateSubresource(texture.Texture, 0, null, (nint)source, (uint)(width * 4), 0);
                    }

                    immediate.Flush();
                    texture.Pts = TimeSpan.FromTicks(Media.FrameTime(frame, fps, 1));
                    encoder.WriteVideo(texture, frameDuration);
                }
                finally
                {
                    texture.Release();
                }
            }

            encoder.Finish();
        }
        finally
        {
            encoder.Dispose();
        }

        var info = Media.Probe(path);
        context.Expect(info.IsHevc == hevc, $"the file is {(info.IsHevc ? "HEVC" : "H.264")}");
        var reading = Read(path, spec, time => (int)Math.Clamp(Math.Round(time * fps / 1e7), 0, frames - 1));
        context.Expect(reading.Frames == frames, $"{reading.Frames} frames in the file, want {frames}");
        context.Expect(reading.Frames > 0 && reading.Upright == reading.Frames, $"the file is {reading.Describe()}");
        context.Note(string.Create(c, $"{description}, {width}×{height}, frames as textures: {reading.Describe()}; colours within {reading.WorstColor:0.0} of 255"));
    }

    /// <summary>
    /// What the recorder reads back from a finished track for the project. A 1080-line HEVC file
    /// holds frames of 1088 rows and says which 1080 are picture; the project needs the picture.
    /// </summary>
    private static void Probe(CheckContext context, TestClips clips)
    {
        var c = CultureInfo.InvariantCulture;
        var read = new List<string>();
        foreach (var (name, clip) in new[] { ("H.264 1920×1080", clips.Screen), ("H.264 1280×720", clips.Camera), ("HEVC 1920×1080", clips.HevcScreen), ("HEVC 1280×720", clips.HevcCamera) })
        {
            var info = Media.Probe(clip.Path);
            context.Expect(info.IsHevc == name.StartsWith("HEVC", StringComparison.Ordinal), $"{name}: the clip is {(info.IsHevc ? "HEVC" : "H.264")}");
            var probed = MediaFileProbe.Probe(clip.Path, 12);
            context.Expect(probed.Width == clip.Spec.Width && probed.Height == clip.Spec.Height, $"{name}: the probe says {probed.Width}×{probed.Height}, and the picture is {clip.Spec.Width}×{clip.Spec.Height}");
            context.Expect(Math.Abs(probed.FrameRate - 30) < 0.001, string.Create(c, $"{name}: the probe says {probed.FrameRate:0.###} fps, want the file's 30"));

            // The file's length is that of its longest track, and sound runs up to 22 ms over.
            var extra = probed.Duration - clip.DurationSeconds;
            context.Expect(extra is > -0.0005 and < 0.0225, string.Create(c, $"{name}: the probe says {probed.Duration:0.0000} s, and the picture lasts {clip.DurationSeconds:0.0000} s"));
            read.Add(string.Create(c, $"{name} reads {probed.Width}×{probed.Height}, {probed.FrameRate:0.###} fps, {probed.Duration:0.000} s"));
        }

        context.Note(string.Join("; ", read));
    }

    /// <summary>
    /// Drives the real <see cref="StudioCameraRecorder"/> through <c>OnFrameArrived</c> with what
    /// <c>WebcamCaptureService.CopyFrame</c> hands out: tightly packed BGRA, top row first, in one
    /// of six buffers it takes turns with, stamped with the camera's time on the system clock.
    /// </summary>
    /// <param name="extra">
    /// Columns and rows the camera's frames have beyond 1280×720. The recorder encodes an even
    /// size, so one extra of each, painted magenta here, must be left out of the track.
    /// </param>
    private static void Camera(CheckContext context, bool forceSoftware, int extra = 0)
    {
        var c = CultureInfo.InvariantCulture;
        const int width = 1280;
        const int height = 720;
        const int frames = 90;
        const int fps = 30;
        var frameWidth = width + extra;
        var frameHeight = height + extra;
        var spec = ClipSpec.Camera(width, height);
        var path = Path.Combine(context.Folder(), "camera.mp4");

        // The recording began at 5000 s on the system clock. The camera's first frame is 137 ms
        // later, and its frames come a 30th of a second apart, give or take 2 ms. After frame 39
        // the camera stalls for a third of a second: ten frames that never come.
        var origin = TimeSpan.FromSeconds(5000);
        long Arrives(int frame) => origin.Ticks + 1_370_000 + Media.FrameTime(frame + (frame >= 40 ? 10 : 0), fps, 1) + (((frame % 3) - 1) * 20_000);

        var recorder = new StudioCameraRecorder(path, RecordingTimeline.FromOrigin(origin), fps) { ForceSoftwareEncoder = forceSoftware };
        try
        {
            var ring = new byte[WebcamRingDepth][];
            var picture = spec.DrawBgra(0);
            for (var frame = 0; frame < frames; frame++)
            {
                var buffer = ring[frame % WebcamRingDepth] ??= NewCameraBuffer(frameWidth, frameHeight);
                spec.StampBgra(picture, frame);
                for (var row = 0; row < height; row++)
                {
                    picture.AsSpan(row * width * 4, width * 4).CopyTo(buffer.AsSpan(row * frameWidth * 4));
                }

                for (var i = 3; i < buffer.Length; i += 4)
                {
                    buffer[i] = 255;
                }

                if (frame % 2 == 1)
                {
                    // The fourth byte means nothing in a camera frame, and not every camera sets it.
                    for (var i = 3; i < buffer.Length; i += 4)
                    {
                        buffer[i] = 0;
                    }
                }

                var arrived = new WebcamFrame(new ReadOnlyMemory<byte>(buffer, 0, frameWidth * frameHeight * 4), frameWidth, frameHeight, TimeSpan.FromTicks(Arrives(frame)));
                recorder.OnFrameArrived(null, new WebcamFrameArrivedEventArgs(arrived));
            }

            recorder.Finish();
        }
        finally
        {
            recorder.Dispose();
        }

        var wanted = forceSoftware ? "software" : "hardware";
        context.Expect(
            recorder.EncoderDescription?.Contains(forceSoftware ? "software fallback" : "(hardware", StringComparison.Ordinal) == true,
            $"the recorder used '{recorder.EncoderDescription}', and this check is for the {wanted} encoder");
        context.Expect(recorder.FramesWritten == frames, $"the recorder wrote {recorder.FramesWritten} frames of {frames}");
        var first = TimeSpan.FromTicks(Arrives(0)) - origin;
        context.Expect(recorder.StartOffset == first, string.Create(c, $"the recorder says the track starts {recorder.StartOffset?.TotalMilliseconds:0.0} ms into the recording, want {first.TotalMilliseconds:0.0}"));
        var length = ((Arrives(frames - 1) - Arrives(0)) / 1e7) + (1.0 / fps);
        context.Expect(Math.Abs(recorder.DurationSeconds - length) < 0.0005, string.Create(c, $"the recorder says the track lasts {recorder.DurationSeconds:0.0000} s, want {length:0.0000}"));

        var info = Media.Probe(path);
        context.Expect(!info.IsHevc && !info.HasAudio, $"the track is {(info.IsHevc ? "HEVC" : "H.264")}{(info.HasAudio ? " with sound" : string.Empty)}, want H.264 without sound");

        // The frame a moment of the track must show is the last one that had arrived by then.
        // An encoder may write a frame up to half a frame time away from when it arrived (the
        // software path puts every frame on a 30 fps grid), so that much is allowed here and the
        // distance is measured below.
        long Pts(int frame) => Arrives(frame) - Arrives(0);
        var half = Media.FrameTime(1, fps, 1) / 2;
        int ShowingAt(long time)
        {
            var frame = 0;
            while (frame + 1 < frames && Pts(frame + 1) <= time + half)
            {
                frame++;
            }

            return frame;
        }

        var reading = Read(path, spec, ShowingAt);
        context.Expect(reading.Frames >= frames, $"{reading.Frames} frames in the file, and {frames} were delivered");
        context.Expect(reading.EdgeProblem is null, $"the last column or row of the picture is wrong: {reading.EdgeProblem}");
        context.Expect(reading.Frames > 0 && reading.Upright == reading.Frames, $"the camera track is {reading.Describe()}");
        context.Expect(!(reading.WorstColor > 8), string.Create(c, $"colours are {reading.WorstColor:0.0} of 255 from what was drawn"));

        // Every delivered frame is in the track, first shown when it arrived.
        var numbers = reading.Upright >= reading.UpsideDown ? reading.AsIs : reading.Turned;

        var missing = 0;
        var worstOffset = 0L;
        var worstOffsetFrame = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var at = Array.IndexOf(numbers, frame);
            if (at < 0)
            {
                missing++;
            }
            else if (Math.Abs(reading.Times[at] - Pts(frame)) > Math.Abs(worstOffset))
            {
                worstOffset = reading.Times[at] - Pts(frame);
                worstOffsetFrame = frame;
            }
        }

        context.Expect(missing == 0, $"{missing} of the {frames} delivered frames are not in the track");
        context.Expect(Math.Abs(worstOffset) <= half + 3000, string.Create(c, $"frame {worstOffsetFrame} is first shown {worstOffset / 10000.0:+0.0;-0.0} ms from when it arrived, more than half a frame"));
        var last = reading.Frames > 0 ? reading.Times[^1] : 0;
        context.Expect(Math.Abs(last - Pts(frames - 1)) <= half + 3000, string.Create(c, $"the last frame of the track is at {last / 10000.0:0.0} ms, and the last delivered frame arrived at {Pts(frames - 1) / 10000.0:0.0} ms"));
        var repeats = reading.Frames - frames;
        context.Note(string.Create(c, $"{recorder.EncoderDescription}: {reading.Describe()}"));
        context.Note(string.Create(c, $"{reading.Frames} frames in the file for {frames} delivered{(repeats > 0 ? $" ({repeats} repeats, filling the camera's stall on an even grid)" : " (the stall stays a gap)")}; each first shown within {Math.Abs(worstOffset) / 10000.0:0.0} ms of when it arrived; colours within {reading.WorstColor:0.0} of 255"));
    }

    /// <summary>A camera frame buffer, magenta all over until a picture is copied into it.</summary>
    private static byte[] NewCameraBuffer(int width, int height)
    {
        var buffer = new byte[width * height * 4];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 255;
            buffer[i + 2] = 255;
        }

        return buffer;
    }

    /// <summary>
    /// The regular recorder's CPU path, reproduced without capturing anything: the encoder as
    /// <c>VideoRecordingService.TryCreateSinkWriter</c> creates it (default arguments: hardware
    /// transforms on, the shared device, low latency), and each frame as <c>WriteCpuFrame</c>
    /// passes it on, to the encoder's own <c>WriteVideo(CapturedFrame, …)</c>, which turns the
    /// rows bottom-up before it writes them. This measures that path as it is. An upside-down
    /// file is reported, and does not fail the run.
    /// </summary>
    private static void CpuPath(CheckContext context, VideoCodec codec)
    {
        var c = CultureInfo.InvariantCulture;
        var hevc = codec == VideoCodec.Hevc;
        if (hevc && !Media.HasEncoder(hevc: true, hardware: null))
        {
            throw new CheckSkippedException("this PC has no HEVC encoder");
        }


        const int width = 1920;
        const int height = 1080;
        const int fps = 30;
        const int frames = 60;
        var spec = ClipSpec.Screen(width, height);
        var path = Path.Combine(context.Folder(), "screen.mp4");

        var device = WgcInterop.GetSharedDevice().D3D;
        var bitrate = (uint)Math.Clamp((long)width * height * fps / 10, 2_000_000, 24_000_000);
        if (hevc)
        {
            bitrate = (uint)(bitrate * 0.6);
        }

        string description;
        var encoder = MfSinkWriterEncoder.Create(
            path,
            device,
            width,
            height,
            fps,
            bitrate,
            codec,
            includeAudio: false,
            AudioCaptureService.SampleRate,
            AudioCaptureService.Channels,
            AudioCaptureService.BitsPerSample,
            192_000);
        try
        {
            description = encoder.Description;
            var frameDuration = TimeSpan.FromSeconds(1.0 / fps);
            var pixels = spec.DrawBgra(0);
            for (var frame = 0; frame < frames; frame++)
            {
                // A captured frame: tightly packed BGRA, top row first.
                spec.StampBgra(pixels, frame);
                encoder.WriteVideo(new CapturedFrame(pixels, width, height), TimeSpan.FromTicks(Media.FrameTime(frame, fps, 1)), frameDuration);
            }

            encoder.Finish();
        }
        finally
        {
            encoder.Dispose();
        }

        var info = Media.Probe(path);
        context.Expect(info.IsHevc == hevc, $"the file is {(info.IsHevc ? "HEVC" : "H.264")}");
        var reading = Read(path, spec, time => (int)Math.Clamp(Math.Round(time * fps / 1e7), 0, frames - 1));
        context.Expect(reading.Frames == frames, $"{reading.Frames} frames in the file, want {frames}");
        context.Expect(reading.Frames > 0 && (reading.Upright == reading.Frames || reading.UpsideDown == reading.Frames), $"the file is {reading.Describe()}");
        context.Note(string.Create(c, $"{description}, {width}×{height}, no sound: {reading.Describe()}; colours within {reading.WorstColor:0.0} of 255"));
        if (reading.Frames > 0 && reading.UpsideDown == reading.Frames && reading.Upright == 0)
        {
            context.Known($"the regular recorder's CPU path writes {(hevc ? "HEVC" : "H.264")} upside down on this PC, all {frames} frames of {frames}. It is measured here and left as it is.");
        }
    }

    /// <summary>
    /// What a file's frames show. Each frame's number is read twice: as the picture is, and from
    /// the picture turned upside down. <see cref="Upright"/> and <see cref="UpsideDown"/> count
    /// the frames whose number, read that way, is the one that belongs at the frame's time.
    /// </summary>
    private sealed record OrientationReading(int Frames, int Upright, int UpsideDown, bool MarkerTopRight, bool MarkerBottomRight, double WorstColor, long[] Times, int[] AsIs, int[] Turned, string? SizeProblem, string? EdgeProblem)
    {
        public string Describe()
        {
            if (SizeProblem is not null)
            {
                return SizeProblem;
            }

            if (Frames > 0 && Upright == Frames)
            {
                return $"upright: all {Frames} frames show the number that belongs at their time, read as the picture is, and the red block is {(MarkerTopRight ? "top-right, where it was drawn" : "not top-right")}";
            }

            if (Frames > 0 && UpsideDown == Frames)
            {
                return $"UPSIDE DOWN: {(Upright == 0 ? "no frame" : $"only {Upright} of {Frames} frames")} shows its number as the picture is and all {UpsideDown} do once it is turned over; the red block drawn top-right is {(MarkerBottomRight ? "bottom-right" : "not bottom-right either")}";
            }

            return $"unreadable: {Upright} of {Frames} frames show the right number as the picture is, {UpsideDown} turned upside down";
        }
    }

    /// <param name="showingAt">The frame number that belongs at a time in the file, in 100 ns units.</param>
    private static OrientationReading Read(string path, ClipSpec spec, Func<long, int> showingAt)
    {
        var turned = new ClipMap(0, spec.Height, 1, -1);
        var times = new List<long>();
        var asIs = new List<int>();
        var over = new List<int>();
        var upright = 0;
        var upsideDown = 0;
        var markerTop = 0;
        var markerBottom = 0;
        var worstColor = 0.0;
        string? sizeProblem = null;
        string? edgeProblem = null;
        var frames = Media.ReadFrames(path, chroma: true, frame =>
        {
            var picture = frame.Picture;
            if (picture.Width != spec.Width || picture.Height != spec.Height)
            {
                sizeProblem = $"{picture.Width}×{picture.Height}, and {spec.Width}×{spec.Height} was written";
                return false;
            }

            var want = showingAt(frame.Time);
            var numberAsIs = FrameCode.Decode(picture, spec, ClipMap.Identity);
            var numberTurned = FrameCode.Decode(picture, spec, turned);
            times.Add(frame.Time);
            asIs.Add(numberAsIs);
            over.Add(numberTurned);
            upright += numberAsIs == want ? 1 : 0;
            upsideDown += numberTurned == want ? 1 : 0;

            // The red block is drawn top-right, and nothing like it is drawn bottom-right.
            var x = spec.MarkerX + (spec.MarkerWidth / 2.0);
            var y = spec.MarkerY + (spec.MarkerHeight / 2.0);
            markerTop += picture.Average(x, y, 4).Distance(ClipSpec.MarkerColor) < 40 ? 1 : 0;
            markerBottom += picture.Average(x, spec.Height - y, 4).Distance(ClipSpec.MarkerColor) < 40 ? 1 : 0;

            // The picture's last column and its first and last rows are background, and not
            // anything that lay beside the picture in the buffer it came from.
            foreach (var (edgeX, edgeY, edge) in new[] { (spec.Width - 1.0, spec.Height * 0.75, "the last column"), (spec.Width * 0.7, spec.Height - 1.0, "the last row"), (spec.Width * 0.5, 0.0, "the first row") })
            {
                var there = picture.Average(edgeX, edgeY, 0);
                if (edgeProblem is null && there.Distance(spec.Background) > 14)
                {
                    edgeProblem = $"frame {frame.Index}, {edge} is {there}, and the background drawn there is {spec.Background}";
                }
            }

            if (numberAsIs == want || numberTurned == want)
            {
                var patches = FrameCode.ReadPatches(picture, spec, numberAsIs == want ? ClipMap.Identity : turned);
                for (var index = 0; index < patches.Length; index++)
                {
                    worstColor = Math.Max(worstColor, patches[index].Distance(ClipSpec.PatchColors[index]));
                }
            }
            else
            {
                worstColor = double.NaN;
            }

            return true;
        });

        return new OrientationReading(frames, upright, upsideDown, frames > 0 && markerTop == frames, frames > 0 && markerBottom == frames, worstColor, [.. times], [.. asIs], [.. over], sizeProblem, edgeProblem);
    }
}
