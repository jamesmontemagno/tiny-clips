using System.Diagnostics;
using System.Globalization;
using StudioEngineSpike.Engine;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace StudioEngineSpike.Modes;

/// <summary>
/// Question 4: can the recorder run a second encoder for the camera? Each stream is a sink writer
/// set up exactly as Core's recorder does (low latency, no B-frames, CBR, allocator-owned
/// textures), fed synthetic BGRA frames in real time from its own paced thread for 30 s.
/// </summary>
internal static class EncodersMode
{
    public static int Run(SpikeOptions options)
    {
        using var report = options.OpenReport();
        using var graphics = GraphicsDevice.Create();
        SpikeEnvironment.Describe(report, graphics);
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            var session = new EncodersSession(options, report, graphics);
            return session.Run();
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }
}

/// <summary>One encoded stream of a scenario.</summary>
/// <param name="Noisy">Camera-like content: every pixel changes every frame. Otherwise screen-like: mostly static with moving parts.</param>
internal sealed record StreamSpec(string Name, int Width, int Height, bool Software, bool Noisy);

internal sealed class EncodersSession
{
    private const int Fps = 30;
    private const int ContentFrames = 12;

    private readonly SpikeOptions _options;
    private readonly Report _report;
    private readonly GraphicsDevice _graphics;
    private readonly string _output;
    private readonly double _seconds;
    private readonly List<string> _table = new();

    public EncodersSession(SpikeOptions options, Report report, GraphicsDevice graphics)
    {
        _options = options;
        _report = report;
        _graphics = graphics;
        _output = options.OutputDirectory();
        _seconds = options.GetDouble("seconds", 30);
    }

    public int Run()
    {
        var screen = new StreamSpec("screen 2560x1440", 2560, 1440, Software: false, Noisy: false);
        var scenarios = new (string Name, StreamSpec[] Streams)[]
        {
            ("single", new[] { screen }),
            ("dual-1080p-hardware", new[] { screen, new StreamSpec("camera 1920x1080", 1920, 1080, Software: false, Noisy: true) }),
            ("dual-720p-hardware", new[] { screen, new StreamSpec("camera 1280x720", 1280, 720, Software: false, Noisy: true) }),
            ("dual-1080p-software", new[] { screen, new StreamSpec("camera 1920x1080 software", 1920, 1080, Software: true, Noisy: true) }),
            ("dual-720p-software", new[] { screen, new StreamSpec("camera 1280x720 software", 1280, 720, Software: true, Noisy: true) }),
        };

        var only = _options.GetString("only", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var repeat = _options.GetInt("repeat", 2);
        _report.Section($"Real-time encoding for {_seconds:0} s per scenario at {Fps} fps, {repeat} pass(es); a frame is \"late\" when its tick starts more than 5 ms after its slot");
        for (var pass = 1; pass <= repeat; pass++)
        {
            foreach (var (name, streams) in scenarios)
            {
                if (only.Length > 0 && !only.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                RunScenario($"{name}", pass, streams);

                // Let the GPU and the encoder sessions settle between scenarios.
                Thread.Sleep(1500);
            }
        }

        var headroomSeconds = _options.GetDouble("headroom-seconds", 10);
        if (headroomSeconds > 0)
        {
            _report.Section($"Headroom: the same streams fed as fast as each encoder takes frames, {headroomSeconds:0} s each (real time needs {Fps} fps)");
            foreach (var (name, streams) in scenarios)
            {
                if (only.Length > 0 && !only.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                RunHeadroom(name, streams, headroomSeconds);
                Thread.Sleep(1500);
            }
        }

        _report.Section("Summary (one row per stream)");
        _report.Line($"{"pass",-5}{"scenario",-22}{"stream",-28}{"written",8}{"dropped",8}{"skipped",8}{"late>5ms",9}{"write mean",11}{"p95",7}{"p99",7}{"max",8}{"tick p99",9}{"backlog",9}{"cpu cores",10}");
        foreach (var row in _table)
        {
            _report.Line(row);
        }

        return 0;
    }

    private void RunScenario(string name, int pass, StreamSpec[] specs)
    {
        _report.Line();
        _report.Line($"-- pass {pass}: {name} (starts {DateTime.Now:HH:mm:ss}, process {Environment.ProcessId}, system timer period {PreciseTimer.SystemTimerResolutionMs().F("0.0")} ms)");
        var runs = new List<StreamRun>();
        try
        {
            foreach (var spec in specs)
            {
                var path = Path.Combine(_output, $"{name}-{spec.Width}x{spec.Height}{(spec.Software ? "-sw" : string.Empty)}-pass{pass}.mp4");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                runs.Add(new StreamRun(_graphics, spec, path, _report));
            }

            foreach (var run in runs)
            {
                _report.Line($"   {run.Spec.Name}: {run.Encoder.DescribeVideoPipeline()}, {run.Encoder.Video.Bitrate / 1_000_000.0:0.0} Mbit/s");
            }

            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var gen2Before = GC.CollectionCount(2);
            var wall = Stopwatch.StartNew();
            foreach (var run in runs)
            {
                run.Start();
            }

            // While it runs, ask each sink writer every 100 ms how far its encoder is behind.
            while (wall.Elapsed.TotalSeconds < _seconds)
            {
                Thread.Sleep(100);
                foreach (var run in runs)
                {
                    run.SampleBacklog();
                }
            }

            foreach (var run in runs)
            {
                run.StopPacer();
            }

            wall.Stop();
            process.Refresh();
            var cpuCores = (process.TotalProcessorTime - cpuBefore).TotalSeconds / wall.Elapsed.TotalSeconds;
            var gen2 = GC.CollectionCount(2) - gen2Before;

            foreach (var run in runs)
            {
                run.Finish();
            }

            _report.Line($"   process CPU {cpuCores:0.00} cores, gen2 GCs {gen2}, working set {process.WorkingSet64 / 1024 / 1024} MB");
            foreach (var run in runs)
            {
                var expected = (int)Math.Round(_seconds * Fps);
                var late = run.Lateness.Values.Count(ms => ms > 5);
                var c = CultureInfo.InvariantCulture;
                _report.Line(string.Create(c, $"   {run.Spec.Name}: wrote {run.Written} of ~{expected} slots, dropped at source (no free texture) {run.Dropped}, slots skipped by the pacer {run.Skipped}, late ticks {late}"));
                _report.Line($"     WriteSample: {run.WriteMs.Summary()}");
                _report.Line($"     whole tick (acquire + fill + write): {run.TickMs.Summary()}; fill {run.FillMs.Mean.F()} ms mean");
                if (run.AcquireMs.Count > 0)
                {
                    _report.Line($"     acquire a texture from the allocator: {run.AcquireMs.Summary()}");
                }
                _report.Line($"     tick start − slot time: {run.Lateness.Summary()}");
                _report.Line($"     frames handed over but not yet encoded, sampled every 100 ms: {run.Backlog.Summary("frames", "0.0")}");
                _report.Line($"     sink writer: {run.Statistics}; Finalize took {run.FinalizeMs.F("0")} ms");
                _report.Line($"     file: {run.Probe()}");
                _table.Add(string.Create(c, $"{pass,-5}{name,-22}{run.Spec.Name,-28}{run.Written,8}{run.Dropped,8}{run.Skipped,8}{late,9}{run.WriteMs.Mean,11:0.00}{run.WriteMs.Percentile(95),7:0.00}{run.WriteMs.Percentile(99),7:0.00}{run.WriteMs.Max,8:0.0}{run.TickMs.Percentile(99),9:0.00}{run.Backlog.Max,9:0}{cpuCores,10:0.00}"));
            }
        }
        catch (Exception ex)
        {
            _report.Line($"   FAILED: {MfHelpers.Describe(ex)} {ex.Message.Trim()}");
        }
        finally
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }
    }

    private void RunHeadroom(string name, StreamSpec[] specs, double seconds)
    {
        var runs = new List<StreamRun>();
        try
        {
            foreach (var spec in specs)
            {
                var path = Path.Combine(_output, $"headroom-{name}-{spec.Width}x{spec.Height}{(spec.Software ? "-sw" : string.Empty)}.mp4");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                runs.Add(new StreamRun(_graphics, spec, path, _report));
            }

            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            var threads = runs.Select(run => new Thread(() => run.RunFlatOut(seconds)) { IsBackground = true, Name = "Spike.FlatOut." + run.Spec.Name }).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());
            wall.Stop();
            process.Refresh();
            var cpuCores = (process.TotalProcessorTime - cpuBefore).TotalSeconds / wall.Elapsed.TotalSeconds;
            var parts = new List<string>();
            foreach (var run in runs)
            {
                run.Finish();
                parts.Add($"{run.Spec.Name} {(run.Written / run.FlatOutSeconds).F("0")} fps ({(run.Written / run.FlatOutSeconds / Fps).F("0.0")}x real time, {run.Written} frames in the file per ffprobe: {run.ProbeFrameCount()})");
            }

            _report.Line($"{name}: {string.Join("; ", parts)}; process CPU {cpuCores.F("0.00")} cores");
        }
        catch (Exception ex)
        {
            _report.Line($"{name}: FAILED: {MfHelpers.Describe(ex)} {ex.Message.Trim()}");
        }
        finally
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }
    }

    /// <summary>One stream: its encoder, its content, and the paced thread that feeds it.</summary>
    private sealed class StreamRun : IDisposable
    {
        private readonly GraphicsDevice _graphics;
        private readonly ID3D11Texture2D[] _content;
        private readonly byte[][]? _contentCpu;
        private readonly long _frameDuration = TimeSpan.TicksPerSecond / Fps;
        private FramePacer? _pacer;

        public StreamRun(GraphicsDevice graphics, StreamSpec spec, string path, Report report)
        {
            _graphics = graphics;
            Spec = spec;
            FilePath = path;
            var settings = new VideoEncoderSettings(
                spec.Width,
                spec.Height,
                Fps,
                VideoEncoderSettings.DefaultBitrate(spec.Width, spec.Height, Fps),
                Hardware: !spec.Software,
                UseDevice: !spec.Software);
            Encoder = SinkWriterEncoder.Create(path, graphics.Device, settings, audio: null);

            var frames = SyntheticContent.Create(spec.Width, spec.Height, ContentFrames, spec.Noisy);
            if (spec.Software)
            {
                // The software encoder takes system-memory frames (bottom-up BGRA, as Core's CPU path writes them).
                _contentCpu = frames.Select(frame => SyntheticContent.FlipVertically(frame, spec.Width, spec.Height)).ToArray();
                _content = Array.Empty<ID3D11Texture2D>();
            }
            else
            {
                // Same pool bounds as the recorder at 30 fps: start at 4, grow to clamp(fps/2, 8, 30).
                Encoder.CreateFrameAllocator(4, Math.Clamp(Fps / 2, 8, 30));
                _content = frames.Select(frame => SyntheticContent.Upload(graphics, frame, spec.Width, spec.Height)).ToArray();
            }
        }

        public StreamSpec Spec { get; }

        public string FilePath { get; }

        public SinkWriterEncoder Encoder { get; }

        public Samples WriteMs { get; } = new();

        public Samples TickMs { get; } = new();

        public Samples FillMs { get; } = new();

        public Samples AcquireMs { get; } = new();

        /// <summary>Samples received by the sink writer minus samples its encoder has finished.</summary>
        public Samples Backlog { get; } = new();

        public void SampleBacklog()
        {
            var statistics = Encoder.VideoStatistics();
            Backlog.Add((double)statistics.NumSamplesReceived - statistics.NumSamplesEncoded);
        }

        public Samples Lateness => _pacer?.Lateness ?? new Samples();

        public long Written { get; private set; }

        public long Dropped { get; private set; }

        public long Skipped => _pacer?.SkippedTicks ?? 0;

        public double FinalizeMs { get; private set; }

        public string Statistics { get; private set; } = string.Empty;

        public void Start()
        {
            _pacer = new FramePacer(TimeSpan.FromSeconds(1.0 / Fps), Tick, "Spike.Pump." + Spec.Name);
            _pacer.Start();
        }

        private void Tick(long slot)
        {
            var start = Stopwatch.GetTimestamp();
            var time = slot * TimeSpan.TicksPerSecond / Fps;
            if (_contentCpu is not null)
            {
                var write = Stopwatch.GetTimestamp();
                Encoder.WriteVideoMemory(_contentCpu[slot % _contentCpu.Length], time, _frameDuration);
                WriteMs.Add(Stopwatch.GetElapsedTime(write).TotalMilliseconds);
            }
            else
            {
                var frame = Encoder.TryAcquireFrame();
                AcquireMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (frame is null)
                {
                    // The encoder holds every pooled texture: the recorder drops at the source.
                    Dropped++;
                    return;
                }

                var fill = Stopwatch.GetTimestamp();
                lock (_graphics.Gate)
                {
                    _graphics.Context.CopyResource(frame.Texture, _content[slot % _content.Length]);
                    _graphics.Context.Flush();
                }

                FillMs.Add(Stopwatch.GetElapsedTime(fill).TotalMilliseconds);
                var write = Stopwatch.GetTimestamp();
                Encoder.WriteVideo(frame, time, _frameDuration);
                WriteMs.Add(Stopwatch.GetElapsedTime(write).TotalMilliseconds);
            }

            Written++;
            TickMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        public void StopPacer() => _pacer?.Dispose();

        public double FlatOutSeconds { get; private set; }

        /// <summary>
        /// Feeds frames with nothing pacing them but the encoder: when it holds every pooled
        /// texture, wait and try again instead of dropping.
        /// </summary>
        public void RunFlatOut(double seconds)
        {
            var watch = Stopwatch.StartNew();
            long slot = 0;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                var written = Written;
                Tick(slot);
                if (Written == written)
                {
                    Dropped--;
                    PreciseTimer.Sleep(0.25);
                    continue;
                }

                slot++;
            }

            // Frames the encoder has not finished yet are finished by Finalize, which the caller times separately.
            FlatOutSeconds = watch.Elapsed.TotalSeconds;
        }

        public string ProbeFrameCount()
        {
            try
            {
                return TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=nb_frames", "-of", "csv=p=0", FilePath }, null).Trim().TrimEnd(',');
            }
            catch (Exception ex)
            {
                return "ffprobe failed: " + ex.Message.Trim();
            }
        }

        public void Finish()
        {
            var statistics = Encoder.VideoStatistics();
            Statistics = $"received {statistics.NumSamplesReceived}, encoded {statistics.NumSamplesEncoded}, processed {statistics.NumSamplesProcessed} at stop; {statistics.ByteCountQueued} bytes queued";
            var watch = Stopwatch.StartNew();
            Encoder.Finish();
            FinalizeMs = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>What ffprobe says about the finished file: frames, duration, bitrate, largest gap.</summary>
        public string Probe()
        {
            try
            {
                var stream = TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,profile,nb_frames,duration,bit_rate", "-of", "default=nw=1", FilePath }, null)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var times = TestMedia.RunTool("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", FilePath }, null)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(text => double.TryParse(text.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
                    .Where(value => !double.IsNaN(value))
                    .OrderBy(value => value)
                    .ToList();
                var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
                var holes = gaps.Count(gap => gap > 1.5 / Fps);
                return $"{string.Join(", ", stream)}; {holes} gap(s) longer than 1.5 frames, largest {(gaps.Count == 0 ? 0 : gaps.Max() * 1000).F("0.0")} ms";
            }
            catch (Exception ex)
            {
                return "ffprobe failed: " + ex.Message.Trim();
            }
        }

        public void Dispose()
        {
            _pacer?.Dispose();
            Encoder.Dispose();
            foreach (var texture in _content)
            {
                texture.Dispose();
            }
        }
    }
}

/// <summary>Synthetic BGRA frames for the encoder-load test.</summary>
internal static class SyntheticContent
{
    /// <summary>
    /// <paramref name="noisy"/> = camera-like: a soft gradient with per-pixel noise that changes on
    /// every frame and a moving blob. Otherwise screen-like: flat panels and "text" stripes that
    /// stay put, a moving window and a scrolling band.
    /// </summary>
    public static byte[][] Create(int width, int height, int count, bool noisy)
    {
        var frames = new byte[count][];
        Parallel.For(0, count, index =>
        {
            var pixels = new byte[width * height * 4];
            var random = new Random(1234 + index);
            var noise = new byte[width];
            var blobX = (int)(width * (0.2 + (0.6 * index / count)));
            var blobY = height / 2;
            var blobRadius = height / 6;
            for (var y = 0; y < height; y++)
            {
                if (noisy)
                {
                    random.NextBytes(noise);
                }

                var row = pixels.AsSpan(y * width * 4, width * 4);
                for (var x = 0; x < width; x++)
                {
                    byte b;
                    byte g;
                    byte r;
                    if (noisy)
                    {
                        var n = (noise[x] & 0x1F) - 16;
                        var inside = ((x - blobX) * (x - blobX)) + ((y - blobY) * (y - blobY)) < blobRadius * blobRadius;
                        b = (byte)Math.Clamp((inside ? 150 : 60 + (x * 80 / width)) + n, 0, 255);
                        g = (byte)Math.Clamp((inside ? 170 : 70 + (y * 90 / height)) + n, 0, 255);
                        r = (byte)Math.Clamp((inside ? 210 : 90) + n, 0, 255);
                    }
                    else
                    {
                        // Static "document": panels and stripes of text-sized detail.
                        var stripe = (y / 14 % 2 == 0) && (x / 9 % 3 != 0) && x > width / 6 && x < width * 5 / 6;
                        b = g = r = stripe ? (byte)40 : (byte)245;
                        if (x < width / 8)
                        {
                            b = 70;
                            g = 50;
                            r = 45;
                        }

                        // A window that moves a little every frame, and a band that scrolls.
                        var windowX = (width / 4) + (index * width / 3 / count);
                        if (x > windowX && x < windowX + (width / 5) && y > height / 3 && y < height * 2 / 3)
                        {
                            b = (byte)(200 - ((y + (index * 9)) % 60));
                            g = 120;
                            r = 60;
                        }

                        if (y > height * 5 / 6 && ((x + (index * 23)) / 40 % 2 == 0))
                        {
                            b = 30;
                            g = 160;
                            r = 230;
                        }
                    }

                    var offset = x * 4;
                    row[offset] = b;
                    row[offset + 1] = g;
                    row[offset + 2] = r;
                    row[offset + 3] = 255;
                }
            }

            frames[index] = pixels;
        });
        return frames;
    }

    public static unsafe ID3D11Texture2D Upload(GraphicsDevice graphics, byte[] topDownBgra, int width, int height)
    {
        var texture = graphics.CreateRenderTexture(width, height);
        fixed (byte* pixels = topDownBgra)
        {
            lock (graphics.Gate)
            {
                graphics.Context.UpdateSubresource(texture, 0, null, (nint)pixels, (uint)(width * 4), 0);
            }
        }

        return texture;
    }

    public static byte[] FlipVertically(byte[] topDown, int width, int height)
    {
        var flipped = new byte[topDown.Length];
        var stride = width * 4;
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(topDown, y * stride, flipped, (height - 1 - y) * stride, stride);
        }

        return flipped;
    }
}
