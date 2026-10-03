using System.Diagnostics;
using StudioEngineSpike.Engine;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace StudioEngineSpike.Modes;

/// <summary>
/// Question 3: offline export. Two Media Foundation source readers on the shared device decode
/// the clips, the same Direct2D scene as the preview is drawn into encoder-owned textures, and a
/// sink writer encodes H.264 with the screen clip's audio re-encoded to AAC. Everything the
/// report claims about the output is read back from the MP4 with ffprobe/ffmpeg.
/// </summary>
internal static class ExportMode
{
    public static int Run(SpikeOptions options)
    {
        using var report = options.OpenReport();
        using var graphics = GraphicsDevice.Create();
        SpikeEnvironment.Describe(report, graphics);
        if (!TestMedia.Exists(options.Root))
        {
            report.Line("Test media is missing. Run `StudioEngineSpike media` first.");
            return 3;
        }

        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using var session = new ExportSession(options, report, graphics);
            return session.Run();
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }
}

internal sealed partial class ExportSession : IDisposable
{
    private const int Fps = TestMedia.Fps;
    private const int Lag = TestMedia.CameraFrameLag;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private static readonly long CameraOffsetTicks = (long)(TestMedia.CameraStartOffsetSeconds * TicksPerSecond);

    private readonly SpikeOptions _options;
    private readonly Report _report;
    private readonly GraphicsDevice _graphics;
    private readonly SceneRenderer _renderer;
    private readonly FrameCodeReader _reader;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly string _output;
    private readonly string _screenPath;
    private readonly string _cameraPath;
    private readonly SceneSettings _settings = new();
    private readonly Rgb[]? _screenReference;
    private readonly Rgb[]? _cameraReference;
    private int _failures;

    public ExportSession(SpikeOptions options, Report report, GraphicsDevice graphics)
    {
        _options = options;
        _report = report;
        _graphics = graphics;
        _renderer = new SceneRenderer(graphics);
        _reader = new FrameCodeReader(graphics);
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(graphics.Device).CheckError();
        _output = options.OutputDirectory();
        var media = TestMedia.Directory(options.Root);
        _screenPath = Path.Combine(media, TestMedia.Screen.FileName);
        _cameraPath = Path.Combine(media, TestMedia.Camera.FileName);
        _screenReference = ReferenceColors.Load(options.Root, TestMedia.Screen);
        _cameraReference = ReferenceColors.Load(options.Root, TestMedia.Camera);
    }

    public int Run()
    {
        var only = _options.GetString("only", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Wanted(string name) => only.Length == 0 || only.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (Wanted("probe"))
        {
            ProbeReaderOutput();
        }

        if (Wanted("decode"))
        {
            DecodeSpeeds();
        }

        if (Wanted("seek"))
        {
            ReaderSeeking();
            AudioSeeking();
        }

        if (Wanted("export"))
        {
            Exports();
        }

        if (Wanted("settings"))
        {
            EncoderSettingsMatrix();
        }

        _report.Line();
        _report.Line(_failures == 0 ? "RESULT: every check passed" : $"RESULT: {_failures} check(s) did not hold (see FAIL lines above)");
        return 0;
    }

    private static long MidFrameTicks(int frame) => (long)((frame + 0.5) * TicksPerSecond / Fps);

    private static string Verdict(bool ok) => ok ? "OK" : "FAIL";

    // ---------------------------------------------------------------------------------------
    // What do the readers hand out, and can Direct2D draw it without a copy?
    // ---------------------------------------------------------------------------------------

    private void ProbeReaderOutput()
    {
        _report.Section("Source reader output: formats, textures, and wrapping them as Direct2D bitmaps");
        foreach (var (clip, path, reference) in new[] { (TestMedia.Screen, _screenPath, _screenReference), (TestMedia.Camera, _cameraPath, _cameraReference) })
        {
            foreach (var format in new[] { VideoOutputFormat.Rgb32, VideoOutputFormat.Argb32, VideoOutputFormat.Nv12 })
            {
                _report.Line();
                _report.Line($"{clip.FileName}, requested {format}:");
                VideoReader reader;
                try
                {
                    reader = VideoReader.Open(path, _deviceManager, new VideoReaderSettings(format));
                }
                catch (Exception ex)
                {
                    _report.Line($"  could not configure: 0x{ex.HResult:X8} {ex.Message.Trim()}");
                    continue;
                }

                using (reader)
                {
                    _report.Line($"  type: {reader.DescribeType()}");
                    _report.Line($"  pipeline: {reader.DescribePipeline()}");

                    const int count = 90;
                    var textures = new HashSet<nint>();
                    var slices = new HashSet<(nint, uint)>();
                    var times = new List<long>();
                    var durations = new HashSet<long>();
                    var wrongFrames = 0;
                    var systemMemory = 0;
                    string? description = null;
                    string? wrap = null;
                    string? drawn = null;
                    string? colours = null;
                    for (var index = 0; index < count; index++)
                    {
                        using var sample = reader.Read();
                        if (sample is null)
                        {
                            break;
                        }

                        times.Add(sample.Time);
                        durations.Add(sample.Duration);
                        if (sample.Texture is null)
                        {
                            systemMemory++;
                            continue;
                        }

                        textures.Add(sample.Texture.NativePointer);
                        slices.Add((sample.Texture.NativePointer, sample.Subresource));
                        var texture = sample.Texture.Description;
                        description ??= $"{texture.Format} {texture.Width}x{texture.Height}, array size {texture.ArraySize}, mips {texture.MipLevels}, usage {texture.Usage}, bind [{texture.BindFlags}], misc [{texture.MiscFlags}], buffers per sample {sample.BufferCount}";

                        var source = new SceneSource(sample.Texture, sample.Subresource, reader.Width, reader.Height);
                        if (index == 0)
                        {
                            lock (_graphics.Gate)
                            {
                                wrap = _renderer.TryWrap(source) ?? "OK";
                            }
                        }

                        if (format == VideoOutputFormat.Nv12)
                        {
                            continue;
                        }

                        // Frame accuracy of the reader itself: the Nth sample must show frame N.
                        var number = _reader.Read(sample.Texture, sample.Subresource, reader.Width, reader.Height, clip, ClipMap.Identity);
                        if (number != index)
                        {
                            wrongFrames++;
                        }

                        if (index == 30 && wrap == "OK")
                        {
                            // Draw through the wrapped bitmap and read the result back, to prove the
                            // no-copy path produces the right pixels and not just a valid object.
                            using var target = _graphics.CreateRenderTexture(1280, 720);
                            var layout = SceneLayout.Resolve(_settings, 1280, 720, TestMedia.Screen.Width, TestMedia.Screen.Height, TestMedia.Camera.Width, TestMedia.Camera.Height);
                            lock (_graphics.Gate)
                            {
                                if (clip.HasAudio)
                                {
                                    _renderer.Render(target, layout, source, null, new RenderOptions());
                                }
                                else
                                {
                                    _renderer.Render(target, layout, null, source, new RenderOptions());
                                }
                            }

                            var composited = _reader.Read(target, 0, 1280, 720, clip, clip.HasAudio ? layout.ScreenMap(clip) : layout.CameraMap(clip));
                            drawn = $"frame {composited} read back from a composite drawn through the wrapped bitmap (expected {index}) {Verdict(composited == index)}";
                            _failures += composited == index ? 0 : 1;
                            lock (_graphics.Gate)
                            {
                                _renderer.ForgetTarget(target);
                            }

                            if (reference is not null)
                            {
                                var actual = ReferenceColors.Read(_graphics, sample.Texture, sample.Subresource, reader.Width, reader.Height, clip, ClipMap.Identity);
                                colours = ReferenceColors.Compare(reference, actual);
                            }
                        }
                    }

                    lock (_graphics.Gate)
                    {
                        // The decoder's textures must not stay pinned by cached Direct2D wrappers.
                        _renderer.ForgetSources();
                    }

                    _report.Line($"  first sample times (100 ns): {string.Join(' ', times.Take(7))}; durations seen: {string.Join(' ', durations.OrderBy(d => d))}");
                    if (description is null)
                    {
                        _report.Line($"  samples are system memory ({systemMemory} of {times.Count}), not textures");
                        continue;
                    }

                    _report.Line($"  texture: {description}");
                    _report.Line($"  over {times.Count} samples: {textures.Count} distinct texture object(s), {slices.Count} distinct (texture, slice) pair(s), {systemMemory} in system memory");
                    _report.Line($"  CreateBitmapFromDxgiSurface on the sample's own texture: {wrap}");
                    if (format != VideoOutputFormat.Nv12)
                    {
                        _report.Line($"  frame numbers of samples 0..{times.Count - 1} read from the texture: {times.Count - wrongFrames} correct, {wrongFrames} wrong");
                        _failures += wrongFrames > 0 ? 1 : 0;
                    }

                    if (drawn is not null)
                    {
                        _report.Line($"  {drawn}");
                    }

                    if (colours is not null)
                    {
                        _report.Line($"  colours against the reference: {colours}");
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Decode speed on its own
    // ---------------------------------------------------------------------------------------

    private void DecodeSpeeds()
    {
        _report.Section("Decode only: read all 900 frames, nothing else (frames per second)");
        var cases = new (string Name, string Path, VideoReaderSettings Settings)[]
        {
            ("screen 2560x1440 → RGB32, DXVA + GPU video processor", _screenPath, new VideoReaderSettings(VideoOutputFormat.Rgb32)),
            ("screen 2560x1440 → NV12, DXVA (no conversion)", _screenPath, new VideoReaderSettings(VideoOutputFormat.Nv12)),
            ("screen 2560x1440 → RGB32 scaled to 1280x720 by the video processor", _screenPath, new VideoReaderSettings(VideoOutputFormat.Rgb32, OutputWidth: 1280, OutputHeight: 720)),
            ("camera 1280x720 → RGB32, DXVA + GPU video processor", _cameraPath, new VideoReaderSettings(VideoOutputFormat.Rgb32)),
            ("screen 2560x1440 → RGB32, no D3D device (software decode, CPU conversion)", _screenPath, new VideoReaderSettings(VideoOutputFormat.Rgb32, UseDevice: false, HardwareTransforms: false, AdvancedProcessing: false)),
        };

        foreach (var (name, path, settings) in cases)
        {
            try
            {
                var runs = new List<double>();
                var frames = 0;
                string? pipeline = null;
                for (var run = 0; run < 2; run++)
                {
                    using var reader = VideoReader.Open(path, _deviceManager, settings);
                    pipeline ??= reader.DescribePipeline();
                    var watch = Stopwatch.StartNew();
                    frames = 0;
                    while (true)
                    {
                        using var sample = reader.Read();
                        if (sample is null)
                        {
                            break;
                        }

                        frames++;
                    }

                    lock (_graphics.Gate)
                    {
                        _graphics.WaitForGpu();
                    }

                    runs.Add(frames / watch.Elapsed.TotalSeconds);
                }

                _report.Line($"{name}: {frames} frames, {string.Join(" and ", runs.Select(r => r.F("0")))} fps in two runs ({(runs.Max() / Fps).F("0.0")}× real time at best)");
                _report.Line($"  {pipeline}");
            }
            catch (Exception ex)
            {
                _report.Line($"{name}: failed 0x{ex.HResult:X8} {ex.Message.Trim()}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Seeking the readers
    // ---------------------------------------------------------------------------------------

    private void ReaderSeeking()
    {
        _report.Section("Seeking a video reader (screen clip, RGB32; keyframes every 60 frames)");
        using var reader = VideoReader.Open(_screenPath, _deviceManager, new VideoReaderSettings(VideoOutputFormat.Rgb32));

        int FrameOf(VideoSample sample) => _reader.Read(sample.Texture!, sample.Subresource, reader.Width, reader.Height, TestMedia.Screen, ClipMap.Identity);

        _report.Line($"{"SetCurrentPosition to",-34} {"first sample",-28} {"read to reach it",-18} time");
        var perFrame = new Samples();
        foreach (var (label, time, target) in new (string, long, int)[]
                 {
                     ("middle of frame 0", MidFrameTicks(0), 0),
                     ("middle of frame 1", MidFrameTicks(1), 1),
                     ("middle of frame 30", MidFrameTicks(30), 30),
                     ("middle of frame 59", MidFrameTicks(59), 59),
                     ("middle of frame 60 (keyframe)", MidFrameTicks(60), 60),
                     ("middle of frame 61", MidFrameTicks(61), 61),
                     ("middle of frame 105", MidFrameTicks(105), 105),
                     ("middle of frame 119", MidFrameTicks(119), 119),
                     ("start of frame 120 (keyframe)", 120L * TicksPerSecond / Fps, 120),
                     ("1 tick before frame 120", (120L * TicksPerSecond / Fps) - 1, 119),
                     ("middle of frame 450", MidFrameTicks(450), 450),
                     ("middle of frame 899 (last)", MidFrameTicks(899), 899),
                     ("middle of frame 15 (backwards)", MidFrameTicks(15), 15),
                 })
        {
            var watch = Stopwatch.StartNew();
            reader.Seek(time);
            var seekMs = watch.Elapsed.TotalMilliseconds;
            var read = 0;
            var first = "(end of stream)";
            var reached = false;
            long firstTime = -1;
            VideoSample? previous = null;
            try
            {
                while (true)
                {
                    var sample = reader.Read();
                    if (sample is null)
                    {
                        // Ran off the end: the frame showing at `time` is the last one read.
                        reached = previous is not null && FrameOf(previous) == target;
                        break;
                    }

                    read++;
                    if (read == 1)
                    {
                        firstTime = sample.Time;
                        first = $"frame {FrameOf(sample)} at {sample.Time}";
                    }

                    if (sample.Time > time)
                    {
                        // `previous` is the frame showing at `time`.
                        reached = previous is not null ? FrameOf(previous) == target : FrameOf(sample) == target;
                        sample.Dispose();
                        break;
                    }

                    previous?.Dispose();
                    previous = sample;
                }
            }
            finally
            {
                previous?.Dispose();
            }

            lock (_graphics.Gate)
            {
                _graphics.WaitForGpu();
            }

            var totalMs = watch.Elapsed.TotalMilliseconds;
            if (read > 10)
            {
                perFrame.Add((totalMs - seekMs) / read);
            }

            _report.Line($"{label,-34} {first,-28} {read + " samples",-18} SetCurrentPosition {seekMs.F("0.0")} ms, target in hand after {totalMs.F("0.0")} ms {Verdict(reached)}");
            _failures += reached ? 0 : 1;
        }

        _report.Line($"decode forward after a seek: {perFrame.Mean.F("0.00")} ms per 2560x1440 frame on average");

        // Past the end, then back.
        try
        {
            reader.Seek(31 * TicksPerSecond);
            using var beyond = reader.Read();
            _report.Line($"SetCurrentPosition(31 s) on a 30 s clip, then ReadSample: {(beyond is null ? "end of stream" : $"frame {FrameOf(beyond)} at {beyond.Time}")}");
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            _report.Line($"SetCurrentPosition(31 s) on a 30 s clip: fails with {MfHelpers.Describe(ex)}; positions must be clamped to the duration");
        }

        try
        {
            reader.Seek(MidFrameTicks(899) + (TicksPerSecond / Fps));
            using var atEnd = reader.Read();
            _report.Line($"SetCurrentPosition(exactly one frame past the middle of the last frame, 30.0167 s): {(atEnd is null ? "end of stream" : $"frame {FrameOf(atEnd)} at {atEnd.Time}")}");
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            _report.Line($"SetCurrentPosition(30.0167 s): fails with {MfHelpers.Describe(ex)}");
        }

        reader.Seek(0);
        using (var again = reader.Read())
        {
            var ok = again is not null && FrameOf(again) == 0;
            _report.Line($"SetCurrentPosition(0) after the end of stream, then ReadSample: {(again is null ? "end of stream" : $"frame {FrameOf(again)} at {again.Time}")} {Verdict(ok)}");
            _failures += ok ? 0 : 1;
        }

        // Is it cheaper to seek across a cut or to decode through it?
        _report.Line("crossing a cut of N frames that starts at frame 70: decode through it, or seek to the frame after it");
        foreach (var skip in new[] { 10, 30, 60, 120, 300 })
        {
            var resume = 70 + skip;
            double Measure(bool seek)
            {
                reader.Seek(MidFrameTicks(60));
                using var cursor = new FrameCursor(reader);
                cursor.Get(MidFrameTicks(70));
                var watch = Stopwatch.StartNew();
                if (seek)
                {
                    cursor.Seek(MidFrameTicks(resume));
                }

                var sample = cursor.Get(MidFrameTicks(resume));
                lock (_graphics.Gate)
                {
                    _graphics.WaitForGpu();
                }

                var elapsed = watch.Elapsed.TotalMilliseconds;
                if (sample is null || FrameOf(sample) != resume)
                {
                    _failures++;
                    return -1;
                }

                return elapsed;
            }

            var through = Measure(seek: false);
            var seeking = Measure(seek: true);
            _report.Line($"  cut of {skip,3} frames (resume at frame {resume}, {resume % 60} after its keyframe): decode through {through.F("0")} ms, seek {seeking.F("0")} ms");
        }
    }

    private void AudioSeeking()
    {
        _report.Section("Seeking the audio reader (screen clip → PCM 48 kHz stereo 16-bit)");
        var format = new AudioEncoderSettings();
        using var reader = AudioReader.Open(_screenPath, format);
        var chunkSamples = new HashSet<int>();
        var first = reader.Read();
        if (first is { } start)
        {
            _report.Line($"first PCM block from the start: time {start.Time} (100 ns), {start.Pcm.Length / format.BlockAlign} samples");
        }

        for (var index = 0; index < 40; index++)
        {
            if (reader.Read() is { } block)
            {
                chunkSamples.Add(block.Pcm.Length / format.BlockAlign);
            }
        }

        _report.Line($"block sizes seen (samples): {string.Join(' ', chunkSamples.OrderBy(s => s))}");
        foreach (var seconds in new[] { 0.5, 3.21, 3.5, 10.004, 29.9 })
        {
            var target = (long)(seconds * TicksPerSecond);
            var watch = Stopwatch.StartNew();
            reader.Seek(target);
            var block = reader.Read();
            if (block is not { } got)
            {
                _report.Line($"seek to {seconds.F("0.000")} s: end of stream");
                continue;
            }

            var early = (target - got.Time) / 10_000.0;
            _report.Line($"seek to {seconds.F("0.000")} s: first block starts at {(got.Time / 1e7).F("0.00000")} s, {early.F("0.00")} ms before the target ({(early * format.SampleRate / 1000).F("0")} samples to drop); {watch.Elapsed.TotalMilliseconds.F("0.0")} ms");
        }
    }

    public void Dispose()
    {
        lock (_graphics.Gate)
        {
            _renderer.Dispose();
        }

        _deviceManager.Dispose();
    }
}
