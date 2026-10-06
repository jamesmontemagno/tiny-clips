using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using Vortice.MediaFoundation;
using RenderCheck = TinyClips.Tools.StudioRenderCheck;

namespace TinyClips.Tools.StudioPreviewCheck.Media;

/// <summary>Which of the app's writers a recorded clip is made with.</summary>
internal enum RecordedWriter
{
    /// <summary>
    /// The screen recorder's: <c>MfSinkWriterEncoder</c> created as <c>VideoRecordingService</c>
    /// creates it, given each frame as a texture from its own allocator with the frame's time on
    /// it and a thirtieth (or sixtieth) of a second for its length, and sound in 20 ms pieces.
    /// </summary>
    Screen,

    /// <summary>The camera recorder itself, <c>StudioCameraRecorder</c>, given frames as the webcam service delivers them.</summary>
    Camera,

    /// <summary>StudioRenderCheck's own clip writer, with that tool's picture: the clip the exporter is checked with.</summary>
    RenderCheck,
}

/// <summary>What a recorded clip is to be: its name, how it is written, and when its frames are.</summary>
/// <param name="Asked">The time each frame is given when it is written, in 100 ns units. For a camera, counted from its first frame.</param>
/// <param name="Slots">How many frame slots of the recording's rate the clip covers.</param>
internal sealed record RecordedPlan(string Name, string What, RecordedWriter Writer, int Fps, int Slots, long[] Asked)
{
    public bool IsCamera => Writer == RecordedWriter.Camera;

    public string FileName => Path.Combine(RecordedMedia.Folder, Name + ".mp4");
}

/// <summary>
/// A clip written the way the app writes a recording, and what is known of its frames. The strip
/// in each frame holds the frame's number in the file, counted from 0: which slot of the
/// recording's timeline shows it follows from the frame's time (<see cref="FrameOfSlot"/>).
/// </summary>
/// <param name="Times">
/// When each frame starts, in 100 ns units, as Media Foundation reads the file: what a player
/// and the exporter go by. One entry for each frame.
/// </param>
/// <param name="StartOffset">For a camera: where its first frame sits on the recording's timeline, in seconds, as its recorder said.</param>
/// <param name="Duration">The file's length in seconds, as the app's probe reads it for the project.</param>
/// <param name="ProbedFrameRate">The frame rate the app's probe reads from the file.</param>
internal sealed record RecordedClip(RecordedPlan Plan, ClipSpec Spec, long[] Times, double StartOffset, double Duration, double ProbedFrameRate)
{
    public string Name => Plan.Name;

    public int Frames => Times.Length;

    private long Offset => (long)Math.Round(StartOffset * 10_000_000, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The frame of the file that slot <paramref name="slot"/> of a timeline at
    /// <paramref name="fps"/> shows: the one showing at the middle of the slot, which is the
    /// instant the exporter draws that frame for and a paused preview rests on
    /// (<see cref="SlotMath"/>). For a camera, whether it is part of the picture then is another matter.
    /// </summary>
    public int FrameOfSlot(int slot, double fps) => SlotMath.FrameOfSlot(Times, slot, fps, Offset);

    /// <summary>The slots of a timeline at <paramref name="fps"/> that show a frame of the file: the first and the last, or null when none does.</summary>
    public (int First, int Last)? SlotsOf(int frame, double fps, int slots) => SlotMath.SlotsOf(Times, frame, fps, slots, Offset);
}

/// <summary>
/// Clips with a recording's frame times. Every clip the checks play otherwise is made by ffmpeg
/// with one frame exactly at the start of every slot; the app's recordings are not like that.
/// These are written by the app's own writers, each frame at a time of this tool's choosing,
/// with its number in the pixels.
/// </summary>
internal static class RecordedMedia
{
    public const string Folder = "recorded";

    private const long Second = 10_000_000;

    // Bump when the clips change, so that clips from an older build are made again.
    private const string Version = "studio-preview-check recorded clips v1";

    /// <summary>Screen clips are 12 s at 1920×1080 with sound, camera clips 12 s at 1280×720 without, as the clips of the other checks are.</summary>
    private const int Seconds = 12;

    private static readonly Lazy<RecordedPlan[]> AllPlans = new(BuildPlans);

    public static IReadOnlyList<RecordedPlan> Plans => AllPlans.Value;

    public static RecordedPlan Plan(string name) =>
        Plans.FirstOrDefault(plan => string.Equals(plan.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"There is no recorded clip called '{name}'. There are: {string.Join(", ", Plans.Select(plan => plan.Name))}.");

    /// <summary>The start of a slot, in 100 ns units.</summary>
    public static long SlotStart(int slot, int fps) => (long)Math.Round(slot * (double)Second / fps, MidpointRounding.AwayFromZero);

    /// <summary>Makes the clip when it is missing or was made to another plan, reads its frame times back, and says what it did.</summary>
    public static RecordedClip Ensure(string mediaDirectory, RecordedPlan plan, Report report)
    {
        var path = Path.Combine(mediaDirectory, plan.FileName);
        var notes = path + ".txt";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var signature = Signature(plan);
        var startOffset = 0.0;
        var kept = false;
        if (File.Exists(path) && File.Exists(notes))
        {
            var lines = File.ReadAllLines(notes);
            if (lines.Length >= 2 && lines[0] == signature && double.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out startOffset))
            {
                kept = true;
            }
        }

        if (!kept)
        {
            var watch = Stopwatch.StartNew();
            File.Delete(notes);
            File.Delete(path);
            startOffset = plan.Writer switch
            {
                RecordedWriter.Screen => WriteScreen(path, plan),
                RecordedWriter.Camera => WriteCamera(path, plan),
                _ => WriteWithRenderCheck(path, plan),
            };
            File.WriteAllLines(notes, [signature, startOffset.ToString("R", CultureInfo.InvariantCulture)]);
            report.Line(string.Create(CultureInfo.InvariantCulture, $"{plan.FileName}: written in {watch.Elapsed.TotalSeconds:0.0} s ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)"));
        }
        else
        {
            report.Line(string.Create(CultureInfo.InvariantCulture, $"{plan.FileName}: present ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)"));
        }

        var (times, _) = ReadVideoSampleTimes(path);
        var probed = MediaFileProbe.Probe(path, plan.Fps);
        var spec = SpecOf(plan) with { Duration = probed.Duration };
        return new RecordedClip(plan, spec, times, startOffset, probed.Duration, probed.FrameRate);
    }

    /// <summary>The geometry of a plan's clip: where its strip and patches are.</summary>
    public static ClipSpec SpecOf(RecordedPlan plan)
    {
        if (plan.Writer == RecordedWriter.RenderCheck)
        {
            var theirs = RenderCheck.ClipSpec.Screen(1280, 720);
            return new ClipSpec(plan.FileName, "GAPS", theirs.Width, theirs.Height, plan.Slots / plan.Fps, HasAudio: true, theirs.CodeX, theirs.CodeY, theirs.CodeCell, theirs.PatchX, theirs.PatchY, theirs.PatchSize, theirs.PatchPitch) { Fps = plan.Fps };
        }

        var like = plan.IsCamera ? TestMedia.Camera : TestMedia.Screen;
        return like with { FileName = plan.FileName, Seconds = plan.Slots / plan.Fps, Fps = plan.Fps };
    }

    /// <summary>
    /// When each video sample of a file starts and how long it says it lasts, in 100 ns units and
    /// in stored order, as Media Foundation's source reader hands the samples out. Nothing is
    /// decoded: the samples are taken as the file has them.
    /// </summary>
    public static (long[] Times, long[] Durations) ReadVideoSampleTimes(string path)
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            var times = new List<long>();
            var durations = new List<long>();
            while (true)
            {
                var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
                try
                {
                    if ((flags & SourceReaderFlag.Error) != 0)
                    {
                        throw new InvalidDataException($"The reader reported an error reading {Path.GetFileName(path)}.");
                    }

                    if (sample is not null)
                    {
                        times.Add(time);
                        long duration = 0;
                        try
                        {
                            duration = sample.SampleDuration;
                        }
                        catch (SharpGen.Runtime.SharpGenException)
                        {
                            // A sample that does not say how long it lasts.
                        }

                        durations.Add(duration);
                    }

                    if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    {
                        break;
                    }
                }
                finally
                {
                    sample?.Dispose();
                }
            }

            return ([.. times], [.. durations]);
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>When the first piece of a file's sound starts, as Media Foundation's source reader hands it out; null for a file without sound.</summary>
    public static long? ReadFirstAudioSampleTime(string path)
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            try
            {
                using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                return null;
            }

            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out _, out var time);
            if (sample is null)
            {
                return null;
            }

            sample.Dispose();
            return time;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    // Enough to tell a clip made to another plan: the stamp, the writer, and the times asked for.
    private static string Signature(RecordedPlan plan) =>
        string.Create(CultureInfo.InvariantCulture, $"{Version}; {plan.Writer}; {plan.Fps} fps; {plan.Slots} slots; {plan.Asked.Length} frames; first {plan.Asked[0]}; last {plan.Asked[^1]}; sum {plan.Asked.Sum()}");

    // ---------------------------------------------------------------------------------------
    // The plans
    // ---------------------------------------------------------------------------------------

    private static RecordedPlan[] BuildPlans()
    {
        var plans = new List<RecordedPlan>();
        const int fps = 30;
        var slots = Seconds * fps;

        // A fixed amount into the slot, each frame up to 2 ms later at random: as the recorder
        // stamps a frame a moment after its pacer's tick.
        foreach (var into in (ReadOnlySpan<int>)[4, 12, 20, 28])
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"screen-into{into:00}");
            plans.Add(new RecordedPlan(name, $"a frame in every slot, {into} to {into + 2} ms into it", RecordedWriter.Screen, fps, slots, Stamps(fps, slots, _ => into, 2, _ => false, seed: 100 + into)));
            plans.Add(new RecordedPlan(name + "-single", $"frames {into} to {into + 2} ms into their slot, and a slot left empty about once a second", RecordedWriter.Screen, fps, slots, Stamps(fps, slots, _ => into, 2, SingleEmpty, seed: 100 + into)));
            plans.Add(new RecordedPlan(name + "-runs", $"frames {into} to {into + 2} ms into their slot, and two or three slots in a row left empty about every two seconds", RecordedWriter.Screen, fps, slots, Stamps(fps, slots, _ => into, 2, RunsEmpty, seed: 100 + into)));
        }

        // A recording that was paused: the amount is another one after the pause. From 4 ms to
        // 22 ms a third of the way in, where two frames are 51 ms apart, and back to 4 ms two
        // thirds in, where two frames are 15 ms apart.
        plans.Add(new RecordedPlan("screen-shift", "frames 4 to 6 ms into their slot for four seconds, 22 to 24 ms for the next four, and 4 to 6 ms again", RecordedWriter.Screen, fps, slots, Stamps(fps, slots, slot => slot is >= 120 and < 240 ? 22 : 4, 2, _ => false, seed: 150)));

        // Around the middle of the slot, which is where the export looks: a frame that begins
        // after one middle, followed by one that begins before the next, is shown by the export
        // in no slot, and the frame before it in two.
        plans.Add(new RecordedPlan("screen-middle", "a frame in every slot, 15.5 to 17.5 ms into it: around the middle of the slot, now before it and now after", RecordedWriter.Screen, fps, slots, Stamps(fps, slots, _ => 15.5, 2, _ => false, seed: 155)));

        // What the recorder's code makes of a recording that was not paused: its pacer starts a
        // moment after the timeline's zero and ticks first one interval later, so the first slot
        // has no frame and every frame sits a couple of milliseconds into its slot.
        plans.Add(new RecordedPlan("screen-as-recorded", "no frame in the first slot, then a frame in every slot 2 to 3.5 ms into it: what the recorder's pacer makes of a recording that was not paused, read from its code", RecordedWriter.Screen, fps, slots, Stamps(fps, slots, _ => 2, 1.5, slot => slot == 0, seed: 160)));

        // The frame times of StudioRenderCheck's GappyScreen, with this tool's picture, through the recorder's writer.
        plans.Add(new RecordedPlan("screen-gaps", "the frame times of StudioRenderCheck's clip with dropped frames (half a second missing, two seconds missing, every other frame, frames 7 ms after and 5 ms before the start of their slot), written by the recorder's writer", RecordedWriter.Screen, fps, 150, GappyTimes()));

        // That clip itself, as StudioRenderCheck writes it.
        plans.Add(new RecordedPlan("rendercheck-gaps", "StudioRenderCheck's clip with dropped frames itself (GappyScreen), written by that tool's code, 1280×720", RecordedWriter.RenderCheck, fps, 150, GappyTimes()));

        // 60 frames a second, the most Tiny Clips records.
        const int fast = 60;
        var fastSlots = Seconds * fast;
        foreach (var into in (ReadOnlySpan<int>)[2, 8])
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"screen60-into{into:00}");
            plans.Add(new RecordedPlan(name, $"60 frames a second, a frame in every slot, {into} to {into + 2} ms into it", RecordedWriter.Screen, fast, fastSlots, Stamps(fast, fastSlots, _ => into, 2, _ => false, seed: 200 + into)));
            plans.Add(new RecordedPlan(name + "-single", $"60 frames a second, frames {into} to {into + 2} ms into their slot, and a slot left empty about once a second", RecordedWriter.Screen, fast, fastSlots, Stamps(fast, fastSlots, _ => into, 2, slot => SingleEmpty(slot / 2) && slot % 2 == 0, seed: 200 + into)));
        }

        // A camera: its frames a thirtieth of a second apart, give or take 2 ms, counted from
        // its first; and three times it stalls for a third of a second, ten frames that never come.
        plans.Add(new RecordedPlan("camera-stalls", "a camera's frames a thirtieth of a second apart give or take 2 ms, with three stalls of a third of a second", RecordedWriter.Camera, fps, slots, CameraTimes(slots, every: 1, stallBefore: [60, 150, 240], stallSlots: 10, seed: 300)));

        // A camera in low light: 15 frames a second on a track that is written as 30.
        plans.Add(new RecordedPlan("camera-15", "a camera that delivers 15 frames a second, give or take 2 ms, to a track written as 30", RecordedWriter.Camera, fps, slots, CameraTimes(slots, every: 2, stallBefore: [], stallSlots: 0, seed: 310)));
        return [.. plans];
    }

    /// <summary>About one slot a second, never in the first half second or the last third of one.</summary>
    private static bool SingleEmpty(int slot) => slot is >= 20 and < 350 && (slot - 20) % 31 == 0;

    /// <summary>Two slots in a row, then three, in turn, about every two seconds.</summary>
    private static bool RunsEmpty(int slot)
    {
        for (var run = 0; run < 6; run++)
        {
            var start = 25 + (61 * run);
            if (slot >= start && slot < start + 2 + (run % 2) && slot < 350)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One frame for every slot that is not left empty: so far into its slot, and up to
    /// <paramref name="jitterMilliseconds"/> more at random. The random part is drawn for every
    /// slot, so that leaving slots empty does not move the frames of the others.
    /// </summary>
    private static long[] Stamps(int fps, int slots, Func<int, double> intoMilliseconds, double jitterMilliseconds, Func<int, bool> empty, int seed)
    {
        var random = new Random(seed);
        var times = new List<long>(slots);
        for (var slot = 0; slot < slots; slot++)
        {
            var late = jitterMilliseconds * random.NextDouble();
            if (!empty(slot))
            {
                times.Add(SlotStart(slot, fps) + (long)Math.Round((intoMilliseconds(slot) + late) * 10_000));
            }
        }

        return [.. times];
    }

    /// <summary>The frame times of StudioRenderCheck's <c>GappyScreen</c>, worked out as that tool does.</summary>
    private static long[] GappyTimes()
    {
        var times = new List<long>();
        for (var slot = 0; slot < 150; slot++)
        {
            var dropped = slot is >= 20 and < 35 || slot is >= 50 and < 110 || (slot is >= 115 and < 130 && slot % 2 == 1);
            if (!dropped)
            {
                var jitter = slot >= 132 ? (slot % 2 == 0 ? 70_000 : -50_000) : 0;
                times.Add(RenderCheck.Media.FrameTime(slot, 30, 1) + jitter);
            }
        }

        return [.. times];
    }

    /// <summary>
    /// A camera's frame times, counted from its first frame: every <paramref name="every"/>th
    /// thirtieth of a second, give or take 2 ms. Before each frame named in
    /// <paramref name="stallBefore"/> the camera stalls: that frame and all after it come
    /// <paramref name="stallSlots"/> thirtieths of a second later.
    /// </summary>
    private static long[] CameraTimes(int slots, int every, int[] stallBefore, int stallSlots, int seed)
    {
        var random = new Random(seed);
        var times = new List<long>();
        var stalled = 0;
        for (var slot = 0; slot + stalled < slots; slot += every)
        {
            if (slot > 0 && Array.IndexOf(stallBefore, slot) >= 0)
            {
                stalled += stallSlots;
            }

            // The first frame is the camera's zero.
            var jitter = slot == 0 ? 0 : (long)Math.Round(((random.NextDouble() * 4) - 2) * 10_000);
            times.Add(SlotStart(slot + stalled, 30) + jitter);
        }

        return [.. times];
    }

    // ---------------------------------------------------------------------------------------
    // The picture
    // ---------------------------------------------------------------------------------------

    /// <summary>The whole frame as tightly packed top-down BGRA, without a number in its strip yet.</summary>
    private static byte[] DrawBackground(ClipSpec clip, bool camera)
    {
        var pixels = new byte[clip.Width * clip.Height * 4];
        Fill(pixels, clip, 0, 0, clip.Width, clip.Height, camera ? (byte)24 : (byte)28, camera ? (byte)84 : (byte)44, camera ? (byte)48 : (byte)92);
        Fill(pixels, clip, clip.CodeX, clip.CodeY, clip.CodeWidth, clip.CodeHeight, 0, 0, 0);
        for (var index = 0; index < TestMedia.PatchColors.Length; index++)
        {
            var (_, r, g, b) = TestMedia.PatchColors[index];
            Fill(pixels, clip, clip.PatchX + (index * clip.PatchPitch), clip.PatchY, clip.PatchSize, clip.PatchSize, r, g, b);
        }

        return pixels;
    }

    /// <summary>Writes a frame's number into the strip: the 12 bits in the top row, most significant first, and their complement underneath.</summary>
    private static void Stamp(byte[] pixels, ClipSpec clip, int number)
    {
        for (var column = 0; column < TestMedia.CodeBits; column++)
        {
            var bit = ((number >> (TestMedia.CodeBits - 1 - column)) & 1) != 0;
            var x = clip.CodeX + (column * clip.CodeCell);
            var top = bit ? (byte)255 : (byte)0;
            var bottom = bit ? (byte)0 : (byte)255;
            Fill(pixels, clip, x, clip.CodeY, clip.CodeCell, clip.CodeCell, top, top, top);
            Fill(pixels, clip, x, clip.CodeY + clip.CodeCell, clip.CodeCell, clip.CodeCell, bottom, bottom, bottom);
        }
    }

    private static void Fill(byte[] pixels, ClipSpec clip, int x, int y, int width, int height, byte r, byte g, byte b)
    {
        for (var row = Math.Max(0, y); row < Math.Min(clip.Height, y + height); row++)
        {
            var line = pixels.AsSpan(((row * clip.Width) + Math.Max(0, x)) * 4, (Math.Min(clip.Width, x + width) - Math.Max(0, x)) * 4);
            for (var i = 0; i < line.Length; i += 4)
            {
                line[i] = b;
                line[i + 1] = g;
                line[i + 2] = r;
                line[i + 3] = 255;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // The writers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a screen track as the recorder does on its usual path: the encoder created as
    /// <c>VideoRecordingService.TryCreateSinkWriter</c> creates it, each frame a texture from the
    /// encoder's own allocator stamped with its time (<c>GpuCaptureSession.ProduceFrame</c>) and
    /// written with the frame time of the recording's rate for its length, and silence for the
    /// sound, from zero, in the 20 ms pieces of the recorder's audio loop.
    /// </summary>
    private static unsafe double WriteScreen(string path, RecordedPlan plan)
    {
        var clip = SpecOf(plan);
        var device = WgcInterop.GetSharedDevice().D3D;
        var bitrate = (uint)Math.Clamp((long)clip.Width * clip.Height * plan.Fps / 10, 2_000_000, 24_000_000);
        var encoder = MfSinkWriterEncoder.Create(
            path,
            device,
            clip.Width,
            clip.Height,
            plan.Fps,
            bitrate,
            VideoCodec.H264,
            includeAudio: true,
            AudioCaptureService.SampleRate,
            AudioCaptureService.Channels,
            AudioCaptureService.BitsPerSample,
            192_000);
        try
        {
            using var allocator = encoder.CreateFrameAllocator(4, 16);

            // The device hands out the one context it keeps; it is not this method's to release.
            var immediate = device.ImmediateContext;
            var frameDuration = TimeSpan.FromSeconds(1.0 / plan.Fps);
            const int audioFrames = AudioCaptureService.SampleRate / 50;
            var silence = new byte[audioFrames * AudioCaptureService.Channels * (AudioCaptureService.BitsPerSample / 8)];
            long audioWritten = 0;
            var end = SlotStart(plan.Slots, plan.Fps);
            var pixels = DrawBackground(clip, camera: false);
            for (var frame = 0; frame <= plan.Asked.Length; frame++)
            {
                // Sound up to the frame's time first, as the recorder's two loops run side by side.
                var until = frame < plan.Asked.Length ? plan.Asked[frame] : end;
                while (audioWritten * Second / AudioCaptureService.SampleRate < until)
                {
                    var pts = TimeSpan.FromTicks(audioWritten * Second / AudioCaptureService.SampleRate);
                    var duration = TimeSpan.FromTicks((long)audioFrames * Second / AudioCaptureService.SampleRate);
                    encoder.WriteAudio(silence, pts, duration);
                    audioWritten += audioFrames;
                }

                if (frame == plan.Asked.Length)
                {
                    break;
                }

                Stamp(pixels, clip, frame);
                GpuFrame texture;
                var waiting = Stopwatch.StartNew();
                while (!allocator.TryAcquire(out texture))
                {
                    if (waiting.Elapsed > TimeSpan.FromSeconds(10))
                    {
                        throw new InvalidOperationException($"The encoder gave no texture for frame {frame} of {plan.Name} in ten seconds.");
                    }

                    Thread.Sleep(1);
                }

                try
                {
                    fixed (byte* source = pixels)
                    {
                        immediate.UpdateSubresource(texture.Texture, 0, null, (nint)source, (uint)(clip.Width * 4), 0);
                    }

                    immediate.Flush();
                    texture.Pts = TimeSpan.FromTicks(plan.Asked[frame]);
                    if (!encoder.WriteVideo(texture, frameDuration))
                    {
                        throw new InvalidOperationException($"The encoder did not take frame {frame} of {plan.Name}.");
                    }
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

        return 0;
    }

    /// <summary>
    /// Drives the real <c>StudioCameraRecorder</c> with what the webcam service hands out:
    /// tightly packed BGRA, top row first, stamped with the camera's time on the system clock.
    /// Returns where the recorder says the track starts on the recording's timeline.
    /// </summary>
    private static double WriteCamera(string path, RecordedPlan plan)
    {
        var clip = SpecOf(plan);

        // The recording began at 5000 s on the system clock, and the camera's first frame came 137 ms later.
        var origin = TimeSpan.FromSeconds(5000);
        const long firstArrival = 1_370_000;
        var recorder = new StudioCameraRecorder(path, RecordingTimeline.FromOrigin(origin), plan.Fps);
        try
        {
            var pixels = DrawBackground(clip, camera: true);
            for (var frame = 0; frame < plan.Asked.Length; frame++)
            {
                Stamp(pixels, clip, frame);
                var arrived = new WebcamFrame(pixels, clip.Width, clip.Height, origin + TimeSpan.FromTicks(firstArrival + plan.Asked[frame]));
                recorder.OnFrameArrived(null, new WebcamFrameArrivedEventArgs(arrived));
            }

            recorder.Finish();
        }
        finally
        {
            recorder.Dispose();
        }

        if (recorder.FramesWritten != plan.Asked.Length)
        {
            throw new InvalidOperationException($"The camera recorder wrote {recorder.FramesWritten} of the {plan.Asked.Length} frames of {plan.Name}.");
        }

        return recorder.StartOffset?.TotalSeconds ?? 0;
    }

    /// <summary>StudioRenderCheck's clip with dropped frames, made by that tool's own code.</summary>
    private static double WriteWithRenderCheck(string path, RecordedPlan plan)
    {
        var folder = Path.Combine(Path.GetDirectoryName(path)!, "rendercheck");

        // That tool starts Media Foundation once for its whole run; its clip writer counts on it.
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            var clip = new RenderCheck.TestClips(folder).GappyScreen;
            if (clip.FrameTimes is not { } times || times.Length != plan.Asked.Length + 1 || !times.AsSpan(0, plan.Asked.Length).SequenceEqual(plan.Asked))
            {
                throw new InvalidOperationException("StudioRenderCheck's clip with dropped frames no longer has the frame times this tool takes it to have.");
            }

            File.Copy(clip.Path, path, overwrite: true);
        }
        finally
        {
            MediaFactory.MFShutdown();
        }

        return 0;
    }
}
