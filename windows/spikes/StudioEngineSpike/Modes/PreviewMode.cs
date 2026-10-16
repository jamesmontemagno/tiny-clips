using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using StudioEngineSpike.Engine;
using Vortice.Direct3D11;
using Windows.Media;

namespace StudioEngineSpike.Modes;

/// <summary>
/// Question 1: preview decode and sync, measured without a window. Two frame-server MediaPlayers
/// on one MediaTimelineController copy into textures on the shared device; a render thread
/// composites with Direct2D whenever a frame arrives; every claim is checked by decoding the
/// frame-number strips out of the composited pixels.
/// </summary>
internal static class PreviewMode
{
    public static int Run(SpikeOptions options)
    {
        using var report = options.OpenReport();
        using var graphics = GraphicsDevice.Create();
        SpikeEnvironment.Describe(report, graphics);
        using var session = new PreviewSession(options, report, graphics);
        return session.Run();
    }
}

internal readonly record struct CompositeEvent(long Timestamp, long ControllerTicks, int ScreenFrame, int CameraFrame, double CpuMs, double TotalMs);

internal sealed partial class PreviewSession : IDisposable
{
    private const int Fps = TestMedia.Fps;
    private const int Lag = TestMedia.CameraFrameLag;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private readonly SpikeOptions _options;
    private readonly Report _report;
    private readonly GraphicsDevice _graphics;
    private readonly SceneRenderer _renderer;
    private readonly FrameCodeReader _reader;
    private readonly string _output;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly ID3D11Texture2D _canvas;
    private readonly object _compositeGate = new();
    private readonly ConcurrentQueue<CompositeEvent> _composites = new();
    private readonly ManualResetEventSlim _frameSignal = new(false);
    private readonly SceneSettings _settings = new();
    private readonly Rgb[]? _screenReference;
    private readonly Rgb[]? _cameraReference;

    private PreviewEngine _engine = null!;
    private ID3D11Texture2D? _snapshot;
    private SceneLayout _layout;
    private RenderOptions _renderOptions = new(ShadowMode.Cached);
    private Thread? _renderThread;
    private volatile bool _stopRender;
    private volatile bool _renderPaused;
    private volatile bool _identifyComposites = true;

    // How long the render thread waits for the other clip's frame before compositing (0 = composite on every arrival).
    private double _pairingWindowMs;
    private long _compositedScreenCount;
    private long _compositedCameraCount;
    private long _pairingTimeouts;
    private bool _cameraVisible = true;
    private int _failures;

    public PreviewSession(SpikeOptions options, Report report, GraphicsDevice graphics)
    {
        _options = options;
        _report = report;
        _graphics = graphics;
        _renderer = new SceneRenderer(graphics);
        _reader = new FrameCodeReader(graphics);
        _output = options.OutputDirectory();
        (_canvasWidth, _canvasHeight) = options.GetSize("canvas", 1920, 1080);
        _canvas = graphics.CreateRenderTexture(_canvasWidth, _canvasHeight);
        _layout = ResolveLayout(_settings, _canvasWidth, _canvasHeight);
        _screenReference = ReferenceColors.Load(options.Root, TestMedia.Screen);
        _cameraReference = ReferenceColors.Load(options.Root, TestMedia.Camera);
    }

    private static SceneLayout ResolveLayout(SceneSettings settings, int width, int height) =>
        SceneLayout.Resolve(settings, width, height, TestMedia.Screen.Width, TestMedia.Screen.Height, TestMedia.Camera.Width, TestMedia.Camera.Height);

    public int Run()
    {
        if (!TestMedia.Exists(_options.Root))
        {
            _report.Line("Test media is missing. Run `StudioEngineSpike media` first.");
            return 3;
        }

        var media = TestMedia.Directory(_options.Root);
        _engine = new PreviewEngine(_graphics, Path.Combine(media, TestMedia.Screen.FileName), Path.Combine(media, TestMedia.Camera.FileName), _options.GetDouble("texture-scale", 1.0));
        _engine.FrameArrived += _ => _frameSignal.Set();

        // `seekrace` is a long statistical run; it is included by default but can be skipped with --only.
        var only = _options.GetString("only", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var skip = _options.GetString("skip", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Wanted(string name) => (only.Length == 0 || only.Contains(name, StringComparer.OrdinalIgnoreCase)) && !skip.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (!OpenAndCalibrate())
        {
            return 4;
        }

        StartRenderThread();
        try
        {
            if (Wanted("colors"))
            {
                Colors();
            }

            if (Wanted("composite"))
            {
                CompositeTimings();
            }

            if (Wanted("play"))
            {
                Play(new PlayRun("Playback at 1x, every frame identified in its callback", 1.0, StartSeconds: 2.0, WallSeconds: _options.GetDouble("play-seconds", 20), IdentifyCallbacks: true, IdentifyComposites: true, PngName: "play-1x"));
                Play(new PlayRun("Playback at 1x, composites identified, composite on every arrival", 1.0, 2.0, 12, IdentifyCallbacks: false, IdentifyComposites: true));
                Play(new PlayRun("Playback at 1x, composites identified, wait up to 8 ms for the other clip", 1.0, 2.0, 12, IdentifyCallbacks: false, IdentifyComposites: true, PairingWindowMs: 8));
                Play(new PlayRun("Playback at 1x, nothing read back", 1.0, 2.0, 12, IdentifyCallbacks: false, IdentifyComposites: false));
            }

            if (Wanted("pause"))
            {
                PauseCycles();
            }

            if (Wanted("seek"))
            {
                PausedSeeks();
                SeekBoundaries();
                Scrub();
            }

            if (Wanted("seekrace"))
            {
                SeekRace();
            }

            if (Wanted("coordinator"))
            {
                CoordinatorSeeks();
                CoordinatorSteps();
                CoordinatorScrub();
            }

            if (Wanted("step"))
            {
                Steps();
                StepMethods();
            }

            if (Wanted("rate"))
            {
                Play(new PlayRun("Playback at 0.5x, every frame identified in its callback", 0.5, 4.0, 10, IdentifyCallbacks: true, IdentifyComposites: true, PngName: "play-0.5x"));
                Play(new PlayRun("Playback at 2x, every frame identified in its callback (the readback itself costs ~5 ms a frame)", 2.0, 4.0, 10, IdentifyCallbacks: true, IdentifyComposites: true, PngName: "play-2x"));
                Play(new PlayRun("Playback at 2x, composites identified, composite on every arrival", 2.0, 4.0, 10, IdentifyCallbacks: false, IdentifyComposites: true));
                Play(new PlayRun("Playback at 2x, composites identified, wait up to 8 ms for the other clip", 2.0, 4.0, 10, IdentifyCallbacks: false, IdentifyComposites: true, PairingWindowMs: 8));
                Play(new PlayRun("Playback at 2x, nothing read back", 2.0, 4.0, 10, IdentifyCallbacks: false, IdentifyComposites: false));
                Play(new PlayRun("Playback at 2x, nothing read back, no compositing (the players alone)", 2.0, 4.0, 10, IdentifyCallbacks: false, IdentifyComposites: false, Composite: false));
            }

            if (Wanted("seekplay"))
            {
                SeeksDuringPlayback();
            }

            if (Wanted("drag"))
            {
                DragWhilePaused();
                OnDemandCopy();
            }

            if (Wanted("edges"))
            {
                Edges();
            }
        }
        finally
        {
            StopRenderThread();
        }

        while (_engine.Log.TryDequeue(out var line))
        {
            _report.Line("  [player] " + line);
        }

        _report.Line();
        _report.Line(_failures == 0 ? "RESULT: every check passed" : $"RESULT: {_failures} check(s) did not hold (see FAIL/MISS lines above)");
        return 0;
    }

    // ---------------------------------------------------------------------------------------
    // Open, first frames, and which way TimelineControllerPositionOffset points
    // ---------------------------------------------------------------------------------------

    private bool OpenAndCalibrate()
    {
        _report.Section("Open");
        if (!_engine.WaitForOpen(TimeSpan.FromSeconds(20)))
        {
            _report.Line($"FAIL: players did not open: {_engine.Failure ?? "timeout"}");
            return false;
        }

        foreach (var track in _engine.Tracks)
        {
            var session = track.Player.PlaybackSession;
            _report.Line($"{track.Name}: MediaOpened after {(track.Index == 0 ? _engine.ScreenOpenedMs : _engine.CameraOpenedMs).F("0")} ms, natural {session.NaturalVideoWidth}x{session.NaturalVideoHeight}, duration {session.NaturalDuration.TotalSeconds.F("0.000")} s, copy target {track.TextureWidth}x{track.TextureHeight}");
        }

        _report.Line($"controller state after open: {_engine.Controller.State}, position {_engine.Controller.Position.TotalSeconds.F("0.000")} s");

        // Does a paused player hand over its first frame without being asked?
        var deadline = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(deadline).TotalSeconds < 2 && (_engine.Screen.FrameCount == 0 || _engine.Camera.FrameCount == 0))
        {
            Thread.Sleep(5);
        }

        _report.Line($"frames delivered while paused straight after open (no seek): screen {_engine.Screen.FrameCount} (frame {_engine.Screen.LatestFrame}), camera {_engine.Camera.FrameCount} (frame {_engine.Camera.LatestFrame})");

        // Which way does TimelineControllerPositionOffset point? Set +0.2 s, park the controller
        // in the middle of screen frame 150 and read the camera's frame number.
        _report.Section("TimelineControllerPositionOffset sign");
        var offset = TimeSpan.FromSeconds(TestMedia.CameraStartOffsetSeconds);
        _engine.SetCameraOffset(offset);
        var probe = SeekAndWait(MidFrame(150), 150, expectedCamera: null, TimeSpan.FromSeconds(3));
        _report.Line($"offset = +{offset.TotalSeconds.F("0.0")} s, controller in frame 150: screen shows {probe.ScreenFrame}, camera shows {probe.CameraFrame}");
        TimeSpan chosen;
        if (probe.CameraFrame == 150 - TestMedia.CameraFrameLag)
        {
            chosen = offset;
            _report.Line("=> player position = controller position − offset. A camera that started 0.2 s late takes offset = +startOffset.");
        }
        else if (probe.CameraFrame == 150 + TestMedia.CameraFrameLag)
        {
            chosen = -offset;
            _report.Line("=> player position = controller position + offset. A camera that started 0.2 s late takes offset = −startOffset.");
        }
        else
        {
            _report.Line("FAIL: the camera frame matches neither sign; continuing with −startOffset.");
            _failures++;
            chosen = -offset;
        }

        _engine.SetCameraOffset(chosen);

        // Changing the offset repositions that player by itself. Is a seek issued straight after it safe?
        var immediate = SeekAndWait(MidFrame(151), 151, 151 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(2));
        _report.Line($"offset = {chosen.TotalSeconds.F("+0.0;-0.0")} s, then Controller.Position = frame 151 immediately: screen {immediate.ScreenFrame}, camera {immediate.CameraFrame} (expected {151 - TestMedia.CameraFrameLag}) {Verdict(immediate.Correct)}; camera callbacks [{string.Join(' ', immediate.CameraFramesSeen)}]");
        if (!immediate.Correct)
        {
            _report.Line("  => the position change that arrived while the offset change was still being applied was lost on that player");
            var retry = SeekAndWait(MidFrame(151), 151, 151 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(2));
            _report.Line($"  same position assigned again: screen {retry.ScreenFrame}, camera {retry.CameraFrame} {Verdict(retry.Correct)}");
            if (!retry.Correct)
            {
                var moved = SeekAndWait(MidFrame(152), 152, 152 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(2));
                _report.Line($"  a different position (frame 152): screen {moved.ScreenFrame}, camera {moved.CameraFrame} {Verdict(moved.Correct)}");
            }
        }

        var check = SeekAndWait(MidFrame(153), 153, 153 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        _report.Line($"controller in frame 153: screen {check.ScreenFrame}, camera {check.CameraFrame} (expected {153 - TestMedia.CameraFrameLag}) {Verdict(check.Correct)}");
        if (!check.Correct)
        {
            _failures++;
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------
    // Colours
    // ---------------------------------------------------------------------------------------

    private void Colors()
    {
        _report.Section("Colour of MediaPlayer frames against the reference");
        if (_screenReference is null || _cameraReference is null)
        {
            _report.Line("no reference colours recorded; run `media` again");
            return;
        }

        SeekAndWait(MidFrame(300), 300, 300 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        var screen = ReferenceColors.Read(_graphics, _engine.Screen.Texture, 0, _engine.Screen.TextureWidth, _engine.Screen.TextureHeight, TestMedia.Screen, _engine.Screen.TextureMap);
        var camera = ReferenceColors.Read(_graphics, _engine.Camera.Texture, 0, _engine.Camera.TextureWidth, _engine.Camera.TextureHeight, TestMedia.Camera, _engine.Camera.TextureMap);
        _report.Line($"reference: {ReferenceColors.Describe(_screenReference)}");
        _report.Line($"screen texture (CopyFrameToVideoSurface): {ReferenceColors.Compare(_screenReference, screen)}");
        _report.Line($"camera texture (CopyFrameToVideoSurface): {ReferenceColors.Compare(_cameraReference, camera)}");

        CompositeNow(identify: false);
        var composited = ReferenceColors.Read(_graphics, _canvas, 0, _canvasWidth, _canvasHeight, TestMedia.Screen, _layout.ScreenMap(TestMedia.Screen));
        _report.Line($"screen card in the {_canvasWidth}x{_canvasHeight} composite (after Direct2D scaling): {ReferenceColors.Compare(_screenReference, composited)}");
    }

    // ---------------------------------------------------------------------------------------
    // Time per composite
    // ---------------------------------------------------------------------------------------

    private void CompositeTimings()
    {
        _report.Section("Time per composite (paused, both sources resident; total = submit + wait for the GPU to finish)");
        SeekAndWait(MidFrame(450), 450, 450 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        _renderPaused = true;
        try
        {
            var sizes = new (int Width, int Height)[] { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) };
            var variants = new (string Name, RenderOptions Options)[]
            {
                ("shadows cached, linear", new RenderOptions(ShadowMode.Cached)),
                ("shadows cached, HQ cubic", new RenderOptions(ShadowMode.Cached, HighQualityScaling: true)),
                ("shadows live (effect per frame), linear", new RenderOptions(ShadowMode.Live)),
                ("no shadows, linear", new RenderOptions(ShadowMode.None)),
            };

            _report.Line($"{"canvas",-10} {"variant",-42} {"cpu submit ms (mean/p95)",-26} total ms");
            foreach (var (width, height) in sizes)
            {
                using var target = _graphics.CreateRenderTexture(width, height);
                var layout = ResolveLayout(_settings, width, height);
                foreach (var (name, renderOptions) in variants)
                {
                    var cpu = new Samples();
                    var total = new Samples();
                    for (var iteration = 0; iteration < 210; iteration++)
                    {
                        lock (_graphics.Gate)
                        {
                            var start = Stopwatch.GetTimestamp();
                            _renderer.Render(target, layout, _engine.Screen.Source, _engine.Camera.Source, renderOptions);
                            var submitted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            _graphics.WaitForGpu();
                            var finished = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            if (iteration >= 10)
                            {
                                cpu.Add(submitted);
                                total.Add(finished);
                            }
                        }
                    }

                    _report.Line($"{width + "x" + height,-10} {name,-42} {cpu.Mean.F() + " / " + cpu.Percentile(95).F(),-26} {total.Summary()}");
                }

                lock (_graphics.Gate)
                {
                    _renderer.ForgetTarget(target);
                }
            }

            // The first composite after a layout change rebuilds the cached shadows: time that too.
            using (var target = _graphics.CreateRenderTexture(_canvasWidth, _canvasHeight))
            {
                var rebuild = new Samples();
                for (var iteration = 0; iteration < 30; iteration++)
                {
                    var changed = ResolveLayout(_settings with { Padding = 0.06 + (iteration * 0.002) }, _canvasWidth, _canvasHeight);
                    lock (_graphics.Gate)
                    {
                        var start = Stopwatch.GetTimestamp();
                        _renderer.Render(target, changed, _engine.Screen.Source, _engine.Camera.Source, new RenderOptions(ShadowMode.Cached));
                        _graphics.WaitForGpu();
                        if (iteration >= 3)
                        {
                            rebuild.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        }
                    }
                }

                _report.Line($"{_canvasWidth}x{_canvasHeight} composite that has to rebuild the cached screen shadow (padding changed): {rebuild.Summary()}");
                lock (_graphics.Gate)
                {
                    _renderer.ForgetTarget(target);
                }
            }
        }
        finally
        {
            _renderPaused = false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------

    private static TimeSpan MidFrame(int frame) => TimeSpan.FromTicks((long)((frame + 0.5) * TicksPerSecond / Fps));

    private static string Verdict(bool ok) => ok ? "OK" : "FAIL";

    private static string Slug(string title)
    {
        var builder = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        var slug = builder.ToString();
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    private sealed record SeekResult(
        int TargetFrame,
        int? ExpectedCamera,
        int ScreenFrame,
        int CameraFrame,
        bool ScreenDelivered,
        bool CameraDelivered,
        double ScreenLatencyMs,
        double CameraLatencyMs,
        int ScreenEvents,
        int CameraEvents,
        int[] ScreenFramesSeen,
        int[] CameraFramesSeen)
    {
        public bool Correct => ScreenFrame == TargetFrame && (ExpectedCamera is null || CameraFrame == ExpectedCamera);

        /// <summary>Milliseconds from the position change to PlaybackSession.SeekCompleted, or -1 when it was not raised.</summary>
        public double ScreenSeekCompletedMs { get; init; } = -1;

        public double CameraSeekCompletedMs { get; init; } = -1;
    }

    /// <summary>
    /// Sets the controller position while paused and waits until each clip has delivered the
    /// expected frame (or the timeout passes), then reads the frame numbers out of a fresh composite.
    /// </summary>
    private SeekResult SeekAndWait(TimeSpan position, int expectedScreen, int? expectedCamera, TimeSpan timeout, bool acceptAnyFrame = false, int settleMs = 150)
    {
        DrainFrameEvents();
        var screenCount = _engine.Screen.FrameCount;
        var cameraCount = _engine.Camera.FrameCount;
        var screenSeeks = _engine.Screen.SeekCompletedCount;
        var cameraSeeks = _engine.Camera.SeekCompletedCount;
        var start = Stopwatch.GetTimestamp();
        _engine.Controller.Position = position;

        bool ScreenDone() => _engine.Screen.FrameCount > screenCount && (acceptAnyFrame || _engine.Screen.LatestFrame == expectedScreen);
        bool CameraDone() => _engine.Camera.FrameCount > cameraCount && (expectedCamera is null || _engine.Camera.LatestFrame == expectedCamera);

        // With no expectation for the camera, give it a short grace period to deliver something.
        var cameraGrace = expectedCamera is null ? TimeSpan.FromMilliseconds(600) : timeout;
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            var screenDone = ScreenDone();
            var cameraDone = CameraDone() || Stopwatch.GetElapsedTime(start) > cameraGrace;
            if (screenDone && cameraDone)
            {
                break;
            }

            PreciseTimer.Sleep(1);
        }

        PreciseTimer.Sleep(settleMs);
        var events = DrainFrameEvents();
        var screenEvents = events.Where(e => e.Track == 0).OrderBy(e => e.Timestamp).ToList();
        var cameraEvents = events.Where(e => e.Track == 1).OrderBy(e => e.Timestamp).ToList();

        double Latency(List<FrameEvent> list, int? expected)
        {
            foreach (var e in list)
            {
                if (expected is null || acceptAnyFrame || e.Frame == expected)
                {
                    return Stopwatch.GetElapsedTime(start, e.Timestamp).TotalMilliseconds;
                }
            }

            return -1;
        }

        var composite = CompositeNow(identify: true);
        return new SeekResult(
            expectedScreen,
            expectedCamera,
            composite.ScreenFrame,
            composite.CameraFrame,
            screenEvents.Count > 0,
            cameraEvents.Count > 0,
            Latency(screenEvents, expectedScreen),
            Latency(cameraEvents, expectedCamera),
            screenEvents.Count,
            cameraEvents.Count,
            screenEvents.Select(e => e.Frame).ToArray(),
            cameraEvents.Select(e => e.Frame).ToArray())
        {
            ScreenSeekCompletedMs = _engine.Screen.SeekCompletedCount > screenSeeks ? Stopwatch.GetElapsedTime(start, _engine.Screen.LatestSeekCompleted).TotalMilliseconds : -1,
            CameraSeekCompletedMs = _engine.Camera.SeekCompletedCount > cameraSeeks ? Stopwatch.GetElapsedTime(start, _engine.Camera.LatestSeekCompleted).TotalMilliseconds : -1,
        };
    }

    private CompositeEvent CompositeNow(bool identify)
    {
        lock (_compositeGate)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var controllerTicks = _engine.Controller.Position.Ticks;
            double cpuMs;
            double totalMs;
            lock (_graphics.Gate)
            {
                var start = Stopwatch.GetTimestamp();
                _renderer.Render(_canvas, _layout, _engine.Screen.Source, _cameraVisible ? _engine.Camera.Source : null, _renderOptions);
                cpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _graphics.WaitForGpu();
                totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            var screenFrame = FrameCode.Unreadable;
            var cameraFrame = FrameCode.Unreadable;
            if (identify)
            {
                screenFrame = _reader.Read(_canvas, 0, _canvasWidth, _canvasHeight, TestMedia.Screen, _layout.ScreenMap(TestMedia.Screen));
                cameraFrame = _reader.Read(_canvas, 0, _canvasWidth, _canvasHeight, TestMedia.Camera, _layout.CameraMap(TestMedia.Camera));
            }

            return new CompositeEvent(timestamp, controllerTicks, screenFrame, cameraFrame, cpuMs, totalMs);
        }
    }

    private void StartRenderThread()
    {
        _renderThread = new Thread(() =>
        {
            while (!_stopRender)
            {
                if (!_frameSignal.Wait(50))
                {
                    continue;
                }

                if (_renderPaused)
                {
                    _frameSignal.Reset();
                    continue;
                }

                // A frame arrived on one clip. Optionally give the other clip a moment to deliver
                // its frame for the same display tick, so the pair is composited together.
                var window = _pairingWindowMs;
                if (window > 0)
                {
                    var start = Stopwatch.GetTimestamp();
                    var paired = false;
                    while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < window)
                    {
                        if (_engine.Screen.FrameCount != _compositedScreenCount && _engine.Camera.FrameCount != _compositedCameraCount)
                        {
                            paired = true;
                            break;
                        }

                        PreciseTimer.Sleep(0.25);
                    }

                    if (!paired)
                    {
                        Interlocked.Increment(ref _pairingTimeouts);
                    }
                }

                _frameSignal.Reset();
                _compositedScreenCount = _engine.Screen.FrameCount;
                _compositedCameraCount = _engine.Camera.FrameCount;
                _composites.Enqueue(CompositeNow(_identifyComposites));
            }
        })
        {
            IsBackground = true,
            Name = "Spike.PreviewRender",
        };
        _renderThread.Start();
    }

    private void StopRenderThread()
    {
        _stopRender = true;
        _frameSignal.Set();
        _renderThread?.Join(2000);
    }

    private List<FrameEvent> DrainFrameEvents()
    {
        var list = new List<FrameEvent>();
        while (_engine.Events.TryDequeue(out var e))
        {
            list.Add(e);
        }

        return list;
    }

    private List<CompositeEvent> DrainComposites()
    {
        var list = new List<CompositeEvent>();
        while (_composites.TryDequeue(out var e))
        {
            list.Add(e);
        }

        return list;
    }

    private void SavePng(string name)
    {
        byte[] pixels;
        lock (_compositeGate)
        {
            lock (_graphics.Gate)
            {
                pixels = _graphics.ReadTexture(_canvas);
            }
        }

        WritePng(name, pixels);
    }

    /// <summary>GPU-side copy of the current composite; cheap enough to take during playback.</summary>
    private void Snapshot()
    {
        lock (_compositeGate)
        {
            lock (_graphics.Gate)
            {
                _snapshot ??= _graphics.CreateRenderTexture(_canvasWidth, _canvasHeight);
                _graphics.Context.CopyResource(_snapshot, _canvas);
            }
        }
    }

    private void SaveSnapshot(string name)
    {
        if (_snapshot is null)
        {
            return;
        }

        byte[] pixels;
        lock (_graphics.Gate)
        {
            pixels = _graphics.ReadTexture(_snapshot);
        }

        WritePng(name, pixels);
    }

    private void WritePng(string name, byte[] pixels)
    {
        var path = Path.Combine(_output, name);
        PngWriter.WriteBgra(path, pixels, _canvasWidth, _canvasHeight);
        _report.Line($"  wrote {Path.GetRelativePath(_options.Root, path)}");
    }

    public void Dispose()
    {
        _engine?.Dispose();
        lock (_graphics.Gate)
        {
            _renderer.Dispose();
        }

        _snapshot?.Dispose();
        _canvas.Dispose();
        _frameSignal.Dispose();
    }
}
