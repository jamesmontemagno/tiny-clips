using System.Diagnostics;
using System.Globalization;
using StudioEngineSpike.Engine;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace StudioEngineSpike.Modes;

/// <summary>One export to run and check.</summary>
/// <param name="Segments">Kept ranges of source time in seconds (trim in/out and cuts).</param>
/// <param name="CopySources">Copy each decoded frame into the exporter's own texture before drawing, instead of wrapping the decoder's texture.</param>
/// <param name="Encode">False = draw into a plain texture and skip the encoder (stage timing).</param>
/// <param name="Decode">False = draw one fixed frame over and over (stage timing).</param>
internal sealed record ExportJob(
    string Name,
    int Width,
    int Height,
    (double Start, double End)[] Segments,
    bool CopySources = false,
    bool Audio = true,
    bool LowLatency = true,
    bool DisableThrottling = true,
    bool Encode = true,
    bool Decode = true,
    int PoolSize = 8,
    ShadowMode Shadows = ShadowMode.Cached,
    string? AudioSourcePath = null);

internal sealed class ExportResult
{
    public string? Path { get; init; }

    public int Frames { get; init; }

    public double Seconds { get; init; }

    public double FinalizeSeconds { get; init; }

    public double FramesPerSecond => Frames / Seconds;

    /// <summary>Source frame numbers each output frame should show; -1 = camera not visible.</summary>
    public List<(int Screen, int Camera)> Expected { get; } = new();

    public Dictionary<string, Samples> Stages { get; } = new();

    public long AllocatorWaits { get; init; }

    public string Pipeline { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public double CpuCores { get; init; }
}

internal sealed partial class ExportSession
{
    private void Exports()
    {
        var full = new[] { (0.0, (double)TestMedia.DurationSeconds) };
        var repeat = _options.GetInt("repeat", 3);

        _report.Section($"Export of the full 30 s at 2560x1440 (the natural canvas), audio re-encoded, {repeat} runs; encoder set up as Core's recorder does (low latency, no writer throttling)");
        ExportResult? first = null;
        for (var run = 0; run < repeat; run++)
        {
            var result = Export(new ExportJob($"full-1440p-run{run + 1}", 2560, 1440, full));
            Summarize(result);
            first ??= result;
        }

        var firstOffset = Verify(first!, 2560, 1440, full);

        _report.Section($"The same export with offline encoder settings (not low latency, writer throttling left on), {repeat} runs");
        ExportResult? offline = null;
        for (var run = 0; run < repeat; run++)
        {
            var result = Export(new ExportJob($"full-1440p-offline-run{run + 1}", 2560, 1440, full, LowLatency: false, DisableThrottling: false));
            Summarize(result);
            offline ??= result;
        }

        Verify(offline!, 2560, 1440, full, brief: true);

        _report.Section("Audio through a second generation: the same export, taking its audio from the first export's MP4 (written by Media Foundation)");
        var second = Export(new ExportJob("second-generation-audio", 1280, 720, full, AudioSourcePath: first!.Path));
        Summarize(second);
        var secondOffset = Verify(second, 1280, 720, full, brief: true);
        _report.Line($"   first generation (ffmpeg-made source): bursts {firstOffset.F("+0.00;-0.00")} ms from where the video puts them; second generation (Media Foundation-made source): {secondOffset.F("+0.00;-0.00")} ms");
        _report.Line($"   => one Media Foundation decode + AAC encode round trip moves audio by {(secondOffset - firstOffset).F("+0.00;-0.00")} ms");

        _report.Section("Export of the full 30 s at 1920x1080 (export size limit 1920)");
        var hd = Export(new ExportJob("full-1080p", 1920, 1080, full));
        Summarize(hd);
        Verify(hd, 1920, 1080, full);

        _report.Section("Export with a trim-in that is not on a keyframe and one cut: keep 3.5–6.0 s and 8.5–12.0 s");
        var segments = new[] { (3.5, 6.0), (8.5, 12.0) };
        var cut = Export(new ExportJob("trim-and-cut", 2560, 1440, segments));
        Summarize(cut);
        Verify(cut, 2560, 1440, segments);

        _report.Section("Variants of the 2560x1440 export (speed only)");
        Summarize(Export(new ExportJob("copy-sources", 2560, 1440, full, CopySources: true)));
        Summarize(Export(new ExportJob("no-audio", 2560, 1440, full, Audio: false)));
        Summarize(Export(new ExportJob("live-shadows", 2560, 1440, full, Shadows: ShadowMode.Live)));
        Summarize(Export(new ExportJob("low-latency-with-throttling", 2560, 1440, full, LowLatency: true, DisableThrottling: false)));
        Summarize(Export(new ExportJob("not-low-latency-no-throttling", 2560, 1440, full, LowLatency: false, DisableThrottling: true)));

        _report.Section("Where the time goes: stages run on their own at 2560x1440");
        Summarize(Export(new ExportJob("decode-and-composite-only", 2560, 1440, full, Audio: false, Encode: false)));
        Summarize(Export(new ExportJob("composite-and-encode-only", 2560, 1440, full, Audio: false, Decode: false)));
    }

    /// <summary>
    /// Which encoder setting decides the export speed? The four combinations of low latency and
    /// writer throttling, and the system timer period, run in turn several times so that load from
    /// other processes on this shared machine hits them all alike.
    /// </summary>
    private void EncoderSettingsMatrix()
    {
        var rounds = _options.GetInt("rounds", 3);
        var full = new[] { (0.0, (double)TestMedia.DurationSeconds) };
        _report.Section($"Encoder settings and export speed: the full 2560x1440 export with audio, {rounds} rounds, variants in turn");
        var variants = new (string Name, bool LowLatency, bool DisableThrottling, bool FineTimer)[]
        {
            ("low latency, throttling off (recorder settings)", true, true, false),
            ("low latency, throttling on", true, false, false),
            ("not low latency, throttling off", false, true, false),
            ("not low latency, throttling on (offline settings)", false, false, false),
            ("recorder settings + timeBeginPeriod(1)", true, true, true),
            ("offline settings + timeBeginPeriod(1)", false, false, true),
        };
        var speeds = variants.Select(_ => new List<double>()).ToArray();
        var waits = variants.Select(_ => new List<double>()).ToArray();
        var timers = variants.Select(_ => new List<double>()).ToArray();
        long? size = null;
        var sameSize = true;
        for (var round = 0; round < rounds; round++)
        {
            for (var i = 0; i < variants.Length; i++)
            {
                var variant = variants[i];
                using var fine = variant.FineTimer ? PreciseTimer.RequestFineSystemTimer() : null;
                Thread.Sleep(50);
                timers[i].Add(PreciseTimer.SystemTimerResolutionMs());
                var result = Export(new ExportJob($"settings-{i}", 2560, 1440, full, LowLatency: variant.LowLatency, DisableThrottling: variant.DisableThrottling));
                speeds[i].Add(result.FramesPerSecond);
                waits[i].Add(result.Stages["wait for texture"].Mean);
                var length = new FileInfo(result.Path!).Length;
                size ??= length;
                sameSize &= length == size;
            }
        }

        var c = CultureInfo.InvariantCulture;
        _report.Line($"{"variant",-52} {"fps per round",-28} {"ms in AllocateSample per frame",-34} system timer period before the run (ms)");
        for (var i = 0; i < variants.Length; i++)
        {
            _report.Line(string.Create(c, $"{variants[i].Name,-52} {string.Join("  ", speeds[i].Select(v => v.ToString("0", c))),-28} {string.Join("  ", waits[i].Select(v => v.ToString("0.0", c))),-34} {string.Join("  ", timers[i].Select(v => v.ToString("0.0", c)))}"));
        }

        _report.Line(sameSize ? $"every one of the {rounds * variants.Length} files is {size} bytes: the settings change when frames are encoded, not what is encoded" : "file sizes differ between variants");
    }

    private void Summarize(ExportResult result)
    {
        var c = CultureInfo.InvariantCulture;
        _report.Line(string.Create(c, $"{System.IO.Path.GetFileName(result.Path) ?? "(no file)"}: {result.Frames} frames in {result.Seconds:0.00} s = {result.FramesPerSecond:0.0} fps ({result.FramesPerSecond / Fps:0.00}× real time), finalize {result.FinalizeSeconds * 1000:0} ms, process CPU {result.CpuCores:0.00} cores, waits for a free encoder texture {result.AllocatorWaits}{(result.Notes.Length > 0 ? "; " + result.Notes : string.Empty)}"));
        var stages = result.Stages.Where(stage => stage.Value.Count > 0).Select(stage => string.Create(c, $"{stage.Key} {stage.Value.Mean:0.00} (p95 {stage.Value.Percentile(95):0.00}, max {stage.Value.Max:0.0})"));
        _report.Line("  ms per frame: " + string.Join("; ", stages));
        if (result.Pipeline.Length > 0)
        {
            _report.Line("  " + result.Pipeline);
        }
    }

    /// <summary>The export loop.</summary>
    private ExportResult Export(ExportJob job)
    {
        var path = job.Encode ? System.IO.Path.Combine(_output, job.Name + ".mp4") : null;
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }

        var audioFormat = new AudioEncoderSettings();
        var layout = SceneLayout.Resolve(_settings, job.Width, job.Height, TestMedia.Screen.Width, TestMedia.Screen.Height, TestMedia.Camera.Width, TestMedia.Camera.Height);
        var options = new RenderOptions(job.Shadows);
        var frameDuration = TicksPerSecond / Fps;
        var cameraDuration = (long)TestMedia.DurationSeconds * TicksPerSecond;

        using var screenReader = VideoReader.Open(_screenPath, _deviceManager, new VideoReaderSettings(VideoOutputFormat.Rgb32));
        using var cameraReader = VideoReader.Open(_cameraPath, _deviceManager, new VideoReaderSettings(VideoOutputFormat.Rgb32));
        using var audioReader = job.Audio && job.Encode ? AudioReader.Open(job.AudioSourcePath ?? _screenPath, audioFormat) : null;
        using var screenCursor = new FrameCursor(screenReader);
        using var cameraCursor = new FrameCursor(cameraReader);

        SinkWriterEncoder? encoder = null;
        ID3D11Texture2D? plainTarget = null;
        ID3D11Texture2D? screenCopy = null;
        ID3D11Texture2D? cameraCopy = null;
        var pipeline = string.Empty;
        if (job.Encode)
        {
            var video = new VideoEncoderSettings(job.Width, job.Height, Fps, VideoEncoderSettings.DefaultBitrate(job.Width, job.Height, Fps), LowLatency: job.LowLatency, DisableThrottling: job.DisableThrottling);
            encoder = SinkWriterEncoder.Create(path!, _graphics.Device, video, audioReader is null ? null : audioFormat);
            encoder.CreateFrameAllocator(4, job.PoolSize);
            pipeline = $"video: {encoder.DescribeVideoPipeline()}; audio: {encoder.DescribeAudioPipeline()}";
        }
        else
        {
            plainTarget = _graphics.CreateRenderTexture(job.Width, job.Height);
        }

        var stages = new Dictionary<string, Samples>
        {
            ["screen decode"] = new(),
            ["camera decode"] = new(),
            ["wait for texture"] = new(),
            ["composite"] = new(),
            ["encode"] = new(),
            ["audio"] = new(),
        };
        var expected = new List<(int, int)>();
        long allocatorWaits = 0;
        long outputFrame = 0;
        long audioSamplesWritten = 0;
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var total = Stopwatch.StartNew();

        // For the decode-less variant, one fixed pair of frames is fetched up front.
        VideoSample? fixedScreen = null;
        VideoSample? fixedCamera = null;
        if (!job.Decode)
        {
            screenCursor.Seek(MidFrameTicks(300));
            cameraCursor.Seek(MidFrameTicks(300 - Lag));
            fixedScreen = screenCursor.Get(MidFrameTicks(300));
            fixedCamera = cameraCursor.Get(MidFrameTicks(300 - Lag));
            cpuBefore = process.TotalProcessorTime;
            total.Restart();
        }

        try
        {
            for (var segmentIndex = 0; segmentIndex < job.Segments.Length; segmentIndex++)
            {
                var (startSeconds, endSeconds) = job.Segments[segmentIndex];
                var segmentStart = (long)Math.Round(startSeconds * TicksPerSecond);
                var segmentEnd = (long)Math.Round(endSeconds * TicksPerSecond);
                var frames = (int)Math.Round((endSeconds - startSeconds) * Fps);

                if (job.Decode)
                {
                    // Reposition: a reader that is more than a second behind the segment start seeks,
                    // otherwise it simply decodes forward (see the cut measurements in this report).
                    var firstInstant = segmentStart + (frameDuration / 2);
                    if (firstInstant - Math.Max(0, screenCursor.CurrentTime) > TicksPerSecond)
                    {
                        screenCursor.Seek(firstInstant);
                    }

                    var firstCameraInstant = firstInstant - CameraOffsetTicks;
                    if (firstCameraInstant - Math.Max(0, cameraCursor.CurrentTime) > TicksPerSecond)
                    {
                        cameraCursor.Seek(Math.Max(0, firstCameraInstant));
                    }
                }

                // Audio for this segment: seek, then drop the samples before the segment start.
                var audioRate = audioFormat.SampleRate;
                var segmentStartSample = (long)Math.Round(startSeconds * audioRate);
                var segmentEndSample = (long)Math.Round(endSeconds * audioRate);
                var audioDone = audioReader is null;
                if (audioReader is not null && (segmentIndex > 0 || segmentStart > 0))
                {
                    audioReader.Seek(segmentStart);
                }

                var audioSegmentTarget = audioSamplesWritten + (segmentEndSample - segmentStartSample);
                void PumpAudio(long untilOutputTicks)
                {
                    while (!audioDone && audioSamplesWritten * TicksPerSecond / audioRate < untilOutputTicks)
                    {
                        if (audioReader!.Read() is not { } block)
                        {
                            audioDone = true;
                            break;
                        }

                        var blockStart = (long)Math.Round(block.Time * (double)audioRate / TicksPerSecond);
                        var blockSamples = block.Pcm.Length / audioFormat.BlockAlign;
                        var from = Math.Max(blockStart, segmentStartSample);
                        var to = Math.Min(blockStart + blockSamples, segmentEndSample);
                        if (blockStart >= segmentEndSample)
                        {
                            audioDone = true;
                            break;
                        }

                        if (to <= from)
                        {
                            continue;
                        }

                        var slice = block.Pcm.AsSpan((int)((from - blockStart) * audioFormat.BlockAlign), (int)((to - from) * audioFormat.BlockAlign));
                        encoder!.WriteAudio(slice, audioSamplesWritten * TicksPerSecond / audioRate, (to - from) * TicksPerSecond / audioRate);
                        audioSamplesWritten += to - from;
                    }
                }

                for (var index = 0; index < frames; index++)
                {
                    // Sample the sources at the middle of the output frame: the same instant the
                    // preview uses for a paused position, so both pick the same source frames.
                    var instant = segmentStart + (long)((index + 0.5) * TicksPerSecond / Fps);
                    var cameraInstant = instant - CameraOffsetTicks;
                    var cameraVisible = cameraInstant >= 0 && cameraInstant <= cameraDuration;

                    var mark = Stopwatch.GetTimestamp();
                    var screen = job.Decode ? screenCursor.Get(instant) : fixedScreen;
                    stages["screen decode"].Add(Stopwatch.GetElapsedTime(mark).TotalMilliseconds);

                    mark = Stopwatch.GetTimestamp();
                    var camera = !cameraVisible ? null : job.Decode ? cameraCursor.Get(cameraInstant) : fixedCamera;
                    stages["camera decode"].Add(Stopwatch.GetElapsedTime(mark).TotalMilliseconds);

                    expected.Add(((int)(instant * Fps / TicksPerSecond), cameraVisible ? (int)(cameraInstant * Fps / TicksPerSecond) : -1));

                    EncoderFrame? frame = null;
                    if (encoder is not null)
                    {
                        mark = Stopwatch.GetTimestamp();
                        while ((frame = encoder.TryAcquireFrame()) is null)
                        {
                            // Every pooled texture is still inside the encoder: an offline export waits, it does not drop.
                            allocatorWaits++;
                            PreciseTimer.Sleep(0.25);
                        }

                        stages["wait for texture"].Add(Stopwatch.GetElapsedTime(mark).TotalMilliseconds);
                    }

                    mark = Stopwatch.GetTimestamp();
                    lock (_graphics.Gate)
                    {
                        var screenSource = ToSource(screen, screenReader, job.CopySources, ref screenCopy);
                        var cameraSource = ToSource(camera, cameraReader, job.CopySources, ref cameraCopy);
                        _renderer.Render(frame?.Texture ?? plainTarget!, layout, screenSource, cameraSource, options);

                        // Submit before the encoder (which may use its own context) reads the texture.
                        _graphics.Context.Flush();
                    }

                    stages["composite"].Add(Stopwatch.GetElapsedTime(mark).TotalMilliseconds);

                    if (encoder is not null)
                    {
                        mark = Stopwatch.GetTimestamp();
                        encoder.WriteVideo(frame!, outputFrame * TicksPerSecond / Fps, frameDuration);
                        stages["encode"].Add(Stopwatch.GetElapsedTime(mark).TotalMilliseconds);

                        mark = Stopwatch.GetTimestamp();
                        PumpAudio(((outputFrame + 1) * TicksPerSecond / Fps) + (TicksPerSecond / 2));
                        stages["audio"].Add(Stopwatch.GetElapsedTime(mark).TotalMilliseconds);
                    }

                    outputFrame++;
                }

                // The rest of this segment's audio.
                if (encoder is not null)
                {
                    PumpAudio(audioSegmentTarget * TicksPerSecond / audioRate);
                }
            }

            lock (_graphics.Gate)
            {
                _graphics.WaitForGpu();
            }

            var finalize = Stopwatch.StartNew();
            encoder?.Finish();
            finalize.Stop();
            total.Stop();
            process.Refresh();

            var notes = new List<string>();
            if (screenCursor.Seeks + cameraCursor.Seeks > 0)
            {
                notes.Add($"reader seeks: screen {screenCursor.Seeks}, camera {cameraCursor.Seeks}; samples decoded and discarded: screen {screenCursor.Discarded}, camera {cameraCursor.Discarded}");
            }

            if (job.Decode)
            {
                notes.Add($"Direct2D source wrappers created {_renderer.SourcesWrapped}");
            }

            notes.Add($"system timer period {PreciseTimer.SystemTimerResolutionMs().F("0.0")} ms");

            var output = new ExportResult
            {
                Path = path,
                Frames = (int)outputFrame,
                Seconds = total.Elapsed.TotalSeconds,
                FinalizeSeconds = finalize.Elapsed.TotalSeconds,
                AllocatorWaits = allocatorWaits,
                Pipeline = pipeline,
                Notes = string.Join("; ", notes),
                CpuCores = (process.TotalProcessorTime - cpuBefore).TotalSeconds / total.Elapsed.TotalSeconds,
            };
            output.Expected.AddRange(expected);
            foreach (var (name, samples) in stages)
            {
                output.Stages[name] = samples;
            }

            return output;
        }
        finally
        {
            encoder?.Dispose();
            lock (_graphics.Gate)
            {
                // Decoder and encoder textures go away with their owners; drop the wrappers that pin them.
                _renderer.ForgetSources();
                _renderer.ForgetAllTargets();
            }

            plainTarget?.Dispose();
            screenCopy?.Dispose();
            cameraCopy?.Dispose();
        }
    }

    /// <summary>
    /// A decoded sample as something the renderer can draw: the decoder's own texture, or a copy
    /// of it in a texture the exporter owns (the variant being compared).
    /// </summary>
    private SceneSource? ToSource(VideoSample? sample, VideoReader reader, bool copy, ref ID3D11Texture2D? copyTexture)
    {
        if (sample is null)
        {
            return null;
        }

        if (sample.Texture is null)
        {
            throw new InvalidOperationException("The reader delivered a system-memory frame; the spike only draws GPU frames.");
        }

        if (!copy)
        {
            return new SceneSource(sample.Texture, sample.Subresource, reader.Width, reader.Height);
        }

        copyTexture ??= _graphics.CreateSourceTexture(reader.Width, reader.Height, sample.Texture.Description.Format);
        _graphics.Context.CopySubresourceRegion(copyTexture, 0, 0, 0, 0, sample.Texture, sample.Subresource, new Box(0, 0, 0, reader.Width, reader.Height, 1));
        return new SceneSource(copyTexture, 0, reader.Width, reader.Height);
    }
}
