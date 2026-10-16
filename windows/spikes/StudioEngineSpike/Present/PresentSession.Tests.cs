using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StudioEngineSpike.Engine;
using Windows.Graphics;

namespace StudioEngineSpike.Present;

/// <summary>The measurements of the `present` mode. Each method is one group for <c>--only</c>.</summary>
internal sealed partial class PresentSession
{
    private sealed record Measurement(double Seconds, double CpuPercent, Dictionary<PanelState, List<PresentSample>> Samples);

    private sealed record Shot(CapturedImage Image, PanelReading Win2D, PanelReading Native)
    {
        public PanelReading For(PanelState state) => state.Presenter is Win2DPresenter ? Win2D : Native;
    }

    private readonly record struct PanelInfo(double Width, double Height, double ScaleX, double ScaleY)
    {
        public int PixelWidth => (int)Math.Round(Width * ScaleX);

        public int PixelHeight => (int)Math.Round(Height * ScaleY);
    }

    private static PanelInfo InfoOf(SwapChainPanel panel) => new(panel.ActualWidth, panel.ActualHeight, panel.CompositionScaleX, panel.CompositionScaleY);

    private (PanelInfo Win2D, PanelInfo Native) PanelInfos() => OnUi(() => (InfoOf(_window.Win2DPanelElement), InfoOf(_window.NativePanelElement)));

    private static PanelInfo InfoFor(PanelState state, (PanelInfo Win2D, PanelInfo Native) infos) => state.Presenter is Win2DPresenter ? infos.Win2D : infos.Native;

    private Measurement Measure(RenderMode mode, double seconds)
    {
        DrainSamples();
        var cpu = CpuSeconds();
        var start = Stopwatch.GetTimestamp();
        _mode = mode;
        _wake.Set();
        Thread.Sleep((int)(seconds * 1000));
        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var cpuUsed = CpuSeconds() - cpu;
        if (mode == RenderMode.Continuous)
        {
            _mode = RenderMode.OnDemand;
            Thread.Sleep(60);
        }

        return new Measurement(elapsed, 100 * cpuUsed / elapsed, DrainSamples());
    }

    // ---------------------------------------------------------------------------------------
    // What was created
    // ---------------------------------------------------------------------------------------

    private void Describe()
    {
        _report.Section("Window and swap chains");
        var (outer, client) = OnUi(() => (_window.AppWindow.Size, _window.AppWindow.ClientSize));
        var infos = PanelInfos();
        _report.Line($"window {outer.Width}x{outer.Height} px, client {client.Width}x{client.Height} px, XamlRoot.RasterizationScale {_rasterizationScale.F("0.###")}");
        _report.Line($"sync interval used unless a test says otherwise: {_syncInterval}");
        foreach (var state in _panels)
        {
            var info = InfoFor(state, infos);
            var presenter = state.Presenter;
            _report.Line($"{presenter.Name}: panel {info.Width.F("0.##")}x{info.Height.F("0.##")} DIP, CompositionScale {info.ScaleX.F("0.###")}x{info.ScaleY.F("0.###")} => {info.PixelWidth}x{info.PixelHeight} px; buffers {presenter.PixelWidth}x{presenter.PixelHeight} px");
            Check(presenter.PixelWidth == info.PixelWidth && presenter.PixelHeight == info.PixelHeight, $"{state.Label} buffer size equals panel size in physical pixels");
            if (presenter.Describe() is { } d)
            {
                _report.Line($"  swap chain: {d.Width}x{d.Height} {d.Format}, {d.BufferCount} buffers, {d.SwapEffect}, scaling {d.Scaling}, alpha {d.AlphaMode}, usage {d.BufferUsage}, flags {d.Flags}");
            }
            else
            {
                _report.Line("  swap chain: description not available");
            }
        }

        if (CaptureSettled() is { } shot)
        {
            _report.Line($"screenshot {shot.Image.Width}x{shot.Image.Height}:");
            ReportShot(shot, expectScreenFrame: 100);
        }
        else
        {
            _report.Line("screenshot: Windows.Graphics.Capture delivered no frame (is the desktop locked or the display off?)");
            _failures++;
        }
    }

    /// <summary>Prints what a screenshot shows for both panels and checks it against the swap chains.</summary>
    private void ReportShot(Shot shot, int? expectScreenFrame, int tolerance = 1)
    {
        foreach (var state in _panels)
        {
            var reading = shot.For(state);
            _report.Line($"  {reading.Describe()}");
            Check(reading.AllCrisp, $"{state.Label} all four corner markers are on screen pixel for pixel");
            Check(reading.Width == state.Presenter.PixelWidth && reading.Height == state.Presenter.PixelHeight, $"{state.Label} on-screen size {reading.Width}x{reading.Height} equals the buffer size {state.Presenter.PixelWidth}x{state.Presenter.PixelHeight}");
            Check(reading.InSync(tolerance), $"{state.Label} swap chain ends where XAML layout puts the panel's corner");
            if (expectScreenFrame is { } frame)
            {
                Check(reading.ScreenFrame == frame && reading.CameraFrame == frame - Lag, $"{state.Label} shows screen frame {frame} and camera frame {frame - Lag}");
            }
        }
    }

    private Shot ReadShot(CapturedImage image) => new(
        image,
        PanelMarkers.Read("(a)", image.Pixels, image.Width, image.Height, PanelMarkers.Win2DKey, _settings, cameraVisible: true),
        PanelMarkers.Read("(b)", image.Pixels, image.Width, image.Height, PanelMarkers.NativeKey, _settings, cameraVisible: true));

    /// <summary>A screenshot composed after the panels were last presented (takes the second fresh frame).</summary>
    private Shot? CaptureSettled()
    {
        if (_capture is null)
        {
            return null;
        }

        CapturedImage? image = null;
        for (var i = 0; i < 2; i++)
        {
            _capture.Drain();
            RequestRedraw();
            image = _capture.Next(TimeSpan.FromSeconds(2)) ?? image;
        }

        return image is null ? null : ReadShot(image);
    }

    // ---------------------------------------------------------------------------------------
    // Pacing
    // ---------------------------------------------------------------------------------------

    private void Continuous()
    {
        _report.Section("Presenting in a loop while paused, sync interval 1 (nothing but the swap chain paces the loop: an upper bound, not a design)");
        SetSyncInterval(1);
        foreach (var (label, win2d, native) in new[] { ("(a) alone", true, false), ("(b) alone", false, true), ("both from one thread, (a) then (b)", true, true) })
        {
            SetActive(win2d, native);
            var measured = Measure(RenderMode.Continuous, 3);
            _report.Line($"{label}: process CPU {measured.CpuPercent.F("0")} % of one core");
            foreach (var state in _panels.Where(p => p.Active))
            {
                Summarize(state, measured.Samples[state], measured.Seconds);
            }
        }

        SetSyncInterval(_syncInterval);
        Thread.Sleep(200);

        _report.Line();
        _report.Line("What one frame costs: each path alone, redrawn on demand 60 times 30 ms apart, timed until the GPU has finished the drawing:");
        _waitForGpu = true;
        foreach (var state in _panels)
        {
            SetActive(state == _win2d, state == _native);
            DrainSamples();
            for (var i = 0; i < 60; i++)
            {
                RequestRedraw();
                Thread.Sleep(30);
            }

            var draw = new Samples();
            var present = new Samples();
            foreach (var sample in DrainSamples()[state])
            {
                draw.Add(sample.DrawMs);
                present.Add(sample.PresentMs);
            }

            _report.Line($"  {state.Presenter.Name} at {state.Presenter.PixelWidth}x{state.Presenter.PixelHeight}: draw {draw.Summary()}; Present() {present.Summary()}");
        }

        _waitForGpu = false;
        SetActive(true, true);
        foreach (var state in _panels)
        {
            if (state.Presenter.OccludedPresents > 0)
            {
                _report.Line($"  note: {state.Label} had {state.Presenter.OccludedPresents} presents answered with DXGI_STATUS_OCCLUDED (window covered?); pacing numbers are then not meaningful");
            }
        }
    }

    /// <summary>What reached the screen during playback, read from screenshots.</summary>
    private sealed class SyncTally
    {
        public int Screenshots;
        public double Seconds;
        public readonly int[] Matching = new int[2];
        public readonly int[] Apart = new int[2];
        public readonly int[] Unreadable = new int[2];
        public readonly HashSet<int>[] ScreenFrames = [new HashSet<int>(), new HashSet<int>()];
        public readonly List<int> PanelDifference = new();

        // Time on screen, from the composition times of consecutive screenshots.
        public double TotalMs;
        public readonly double[] ApartMs = new double[2];
        public readonly double[] LongestApartMs = new double[2];
        public readonly List<int> RefreshesBetweenScreenshots = new();
    }

    /// <summary>
    /// Takes every screenshot the system delivers while it runs and decodes the two clips' frame
    /// numbers in both panels, so "are the clips in sync" and "which path is ahead" are answered by
    /// what DWM actually composed rather than by what the app presented.
    /// </summary>
    private sealed class SyncWatch
    {
        private readonly PresentSession _session;

        // Four small rectangles of the window: each panel's screen strip and camera strip.
        private readonly (int X, int Y, int Width, int Height)[] _regions = new (int, int, int, int)[4];
        private readonly ClipMap[] _maps = new ClipMap[4];
        private readonly Thread _thread;
        private readonly SyncTally _tally = new();
        private readonly int _width;
        private readonly int _height;
        private volatile bool _stop;

        public SyncWatch(PresentSession session, Shot geometry)
        {
            _session = session;
            _width = geometry.Image.Width;
            _height = geometry.Image.Height;
            var readings = new[] { geometry.Win2D, geometry.Native };
            for (var i = 0; i < 2; i++)
            {
                var layout = SceneLayout.Resolve(session._settings, readings[i].Width, readings[i].Height, TestMedia.Screen.Width, TestMedia.Screen.Height, TestMedia.Camera.Width, TestMedia.Camera.Height);
                var screen = layout.ScreenMap(TestMedia.Screen).Offset(readings[i].OriginX, readings[i].OriginY);
                var camera = layout.CameraMap(TestMedia.Camera).Offset(readings[i].OriginX, readings[i].OriginY);
                _regions[2 * i] = FrameCode.Bounds(TestMedia.Screen, screen, _width, _height);
                _regions[(2 * i) + 1] = FrameCode.Bounds(TestMedia.Camera, camera, _width, _height);
                _maps[2 * i] = screen.Offset(-_regions[2 * i].X, -_regions[2 * i].Y);
                _maps[(2 * i) + 1] = camera.Offset(-_regions[(2 * i) + 1].X, -_regions[(2 * i) + 1].Y);
            }

            _thread = new Thread(Run) { IsBackground = true, Name = "Spike.PresentSyncWatch" };
            _thread.Start();
        }

        public SyncTally Finish()
        {
            _stop = true;
            _thread.Join(2000);
            return _tally;
        }

        private void Run()
        {
            var capture = _session._capture!;
            capture.Drain();
            var start = Stopwatch.GetTimestamp();
            Span<int> frames = stackalloc int[2];
            Span<bool> apart = stackalloc bool[2];
            Span<double> apartRun = stackalloc double[2];
            TimeSpan? previous = null;
            while (!_stop)
            {
                if (capture.NextRegions(TimeSpan.FromMilliseconds(100), _width, _height, _regions) is not { } image)
                {
                    continue;
                }

                // The previous screenshot stayed on screen until this one was composed.
                if (previous is { } before)
                {
                    var shownMs = (image.ComposedAt - before).TotalMilliseconds;
                    _tally.TotalMs += shownMs;
                    _tally.RefreshesBetweenScreenshots.Add((int)Math.Round(shownMs / 10.0));
                    for (var i = 0; i < 2; i++)
                    {
                        if (apart[i])
                        {
                            _tally.ApartMs[i] += shownMs;
                            apartRun[i] += shownMs;
                            _tally.LongestApartMs[i] = Math.Max(_tally.LongestApartMs[i], apartRun[i]);
                        }
                        else
                        {
                            apartRun[i] = 0;
                        }
                    }
                }

                previous = image.ComposedAt;
                _tally.Screenshots++;
                for (var i = 0; i < 2; i++)
                {
                    var screen = FrameCode.Decode(image.Regions[2 * i], _regions[2 * i].Width, _regions[2 * i].Height, 4, TestMedia.Screen, _maps[2 * i]);
                    var camera = FrameCode.Decode(image.Regions[(2 * i) + 1], _regions[(2 * i) + 1].Width, _regions[(2 * i) + 1].Height, 4, TestMedia.Camera, _maps[(2 * i) + 1]);
                    frames[i] = screen;
                    apart[i] = false;
                    if (screen == FrameCode.Unreadable || camera == FrameCode.Unreadable)
                    {
                        _tally.Unreadable[i]++;
                    }
                    else if (screen - camera == Lag)
                    {
                        _tally.Matching[i]++;
                    }
                    else
                    {
                        _tally.Apart[i]++;
                        apart[i] = true;
                    }

                    if (screen != FrameCode.Unreadable)
                    {
                        _tally.ScreenFrames[i].Add(screen);
                    }
                }

                if (frames[0] != FrameCode.Unreadable && frames[1] != FrameCode.Unreadable)
                {
                    _tally.PanelDifference.Add(frames[0] - frames[1]);
                }
            }

            _tally.Seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        }
    }

    private void ReportTally(SyncTally tally, bool win2d, bool native)
    {
        _report.Line($"  on screen, from {tally.Screenshots} screenshots ({(tally.Screenshots / Math.Max(0.001, tally.Seconds)).F("0")}/s):");
        for (var i = 0; i < 2; i++)
        {
            if ((i == 0 && !win2d) || (i == 1 && !native))
            {
                continue;
            }

            var frames = tally.ScreenFrames[i];
            var span = frames.Count == 0 ? 0 : frames.Max() - frames.Min() + 1;
            var read = tally.Matching[i] + tally.Apart[i];
            _report.Line($"    {_panels[i].Label}: the two clips on matching frames in {tally.Matching[i]} of {read} ({(read == 0 ? 0 : 100.0 * tally.Matching[i] / read).F("0.0")} %), not matching in {tally.Apart[i]}, unreadable in {tally.Unreadable[i]}; time on screen not matching {tally.ApartMs[i].F("0")} ms of {tally.TotalMs.F("0")} ms = {(tally.TotalMs <= 0 ? 0 : 100.0 * tally.ApartMs[i] / tally.TotalMs).F("0.00")} %, longest stretch {tally.LongestApartMs[i].F("0")} ms; {frames.Count} of the {span} screen frames in that stretch were seen in a screenshot");
        }

        _report.Line($"    time between consecutive screenshots, in 10 ms refreshes: {Fmt.Histogram(tally.RefreshesBetweenScreenshots)}");

        if (win2d && native)
        {
            _report.Line($"    screen frame in (a) minus screen frame in (b), same screenshot: {Fmt.Histogram(tally.PanelDifference)}");
        }
    }

    private void Playback()
    {
        _report.Section("Playback at 1x: a panel is redrawn and presented when a clip's texture holds a newer frame than the panel last drew");
        SetActive(true, true);
        SeekTo(60);
        RequestRedraw();
        Thread.Sleep(150);
        var geometry = CaptureSettled();
        var rounds = _options.GetInt("playback-rounds", 3);
        foreach (var sync in Enumerable.Range(0, rounds * 2).Select(i => 1 - (i % 2)))
        {
            SetSyncInterval(sync);
            Play(fromFrame: 60);
            Thread.Sleep(500);
            var screenBefore = _engine.Screen.FrameCount;
            var cameraBefore = _engine.Camera.FrameCount;
            var watch = geometry is null || _capture is null ? null : new SyncWatch(this, geometry);
            var measured = Measure(RenderMode.OnVideoFrame, 10);
            var tally = watch?.Finish();
            var screenFrames = _engine.Screen.FrameCount - screenBefore;
            var cameraFrames = _engine.Camera.FrameCount - cameraBefore;
            StopPlaying();
            _report.Line($"both panels, sync interval {sync}: video frames delivered in {measured.Seconds.F("0.0")} s: screen {screenFrames}, camera {cameraFrames}; process CPU {measured.CpuPercent.F("0")} % of one core");
            foreach (var state in _panels)
            {
                Summarize(state, measured.Samples[state], measured.Seconds);
            }

            if (tally is not null)
            {
                ReportTally(tally, win2d: true, native: true);
            }
        }

        SetSyncInterval(_syncInterval);
    }

    private void BlockUiThread()
    {
        const int blockMs = 1500;
        _report.Section($"UI thread blocked for {blockMs} ms during playback (both panels)");
        SetActive(true, true);
        Play(fromFrame: 60);
        Thread.Sleep(1000);
        DrainSamples();
        Thread.Sleep(blockMs);
        var before = DrainSamples();

        long entered = 0;
        long left = 0;
        using var done = new ManualResetEventSlim(false);
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            entered = Stopwatch.GetTimestamp();
            Thread.Sleep(blockMs);
            left = Stopwatch.GetTimestamp();
            done.Set();
        });

        // Two screenshots taken while the UI thread sleeps: do the frame numbers on screen advance?
        Thread.Sleep(250);
        var early = _capture?.Next(TimeSpan.FromMilliseconds(400), fresh: true);
        Thread.Sleep(600);
        var late = _capture?.Next(TimeSpan.FromMilliseconds(400), fresh: true);
        done.Wait();
        var during = DrainSamples();
        Thread.Sleep(300);
        StopPlaying();

        _report.Line($"UI thread was inside the blocking call for {Stopwatch.GetElapsedTime(entered, left).TotalMilliseconds.F("0")} ms");
        foreach (var state in _panels)
        {
            var reference = before[state];
            var blocked = during[state].Where(s => s.Timestamp >= entered && s.Timestamp <= left).ToList();
            _report.Line($"  {state.Presenter.Name}: {reference.Count} presents in the {blockMs} ms before, {blocked.Count} while blocked; longest gap between presents {LongestGap(reference).F("0.0")} ms before, {LongestGap(blocked).F("0.0")} ms while blocked");
            _report.Line($"    while blocked: {DescribeStatistics(blocked)}");
            Check(blocked.Count >= reference.Count * 0.9, $"{state.Label} kept presenting while the UI thread was blocked");
        }

        if (early is not null && late is not null)
        {
            var first = ReadShot(early);
            var second = ReadShot(late);
            var seconds = Stopwatch.GetElapsedTime(early.Timestamp, late.Timestamp).TotalSeconds;
            foreach (var state in _panels)
            {
                var a = first.For(state);
                var b = second.For(state);
                _report.Line($"  {state.Label} screenshots {seconds.F("0.00")} s apart while blocked: screen frame {a.ScreenFrame} -> {b.ScreenFrame} (advance expected about {(seconds * Fps).F("0")})");
                Check(a.ScreenFrame >= 0 && b.ScreenFrame > a.ScreenFrame, $"{state.Label} picture kept advancing on screen while the UI thread was blocked");
            }
        }
        else
        {
            _report.Line("  no screenshots could be taken while blocked");
        }
    }

    private static double LongestGap(List<PresentSample> samples)
    {
        double longest = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            longest = Math.Max(longest, Stopwatch.GetElapsedTime(samples[i - 1].Timestamp, samples[i].Timestamp).TotalMilliseconds);
        }

        return longest;
    }

    private void Drag()
    {
        const int steps = 120;
        _report.Section($"Dragging the camera while paused: {steps} layout changes at 60 Hz, each redrawn and presented on demand");
        SetActive(true, true);
        SeekTo(200);
        RequestRedraw();
        Thread.Sleep(150);
        DrainSamples();
        var latency = new Samples();
        var missed = 0;
        var callbacksBefore = _engine.Screen.FrameCount + _engine.Camera.FrameCount;
        for (var i = 0; i < steps; i++)
        {
            var t = i / (double)(steps - 1);
            _settings = new SceneSettings { BubbleOffsetX = -0.55 * t, BubbleOffsetY = -0.45 * Math.Sin(t * Math.PI) };
            var counts = _panels.Select(SampleCount).ToArray();
            var start = Stopwatch.GetTimestamp();
            RequestRedraw();
            var presented = false;
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 250)
            {
                if (SampleCount(_win2d) > counts[0] && SampleCount(_native) > counts[1])
                {
                    presented = true;
                    break;
                }

                PreciseTimer.Sleep(0.2);
            }

            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (presented)
            {
                latency.Add(elapsed);
            }
            else
            {
                missed++;
            }

            PreciseTimer.Sleep(Math.Max(0.5, (1000.0 / 60) - elapsed));
        }

        var samples = DrainSamples();
        _report.Line($"request -> both panels presented: {latency.Summary()}; steps without a present within 250 ms: {missed}; MediaPlayer callbacks during the drag: {_engine.Screen.FrameCount + _engine.Camera.FrameCount - callbacksBefore}");
        foreach (var state in _panels)
        {
            var draw = new Samples();
            var present = new Samples();
            foreach (var sample in samples[state])
            {
                draw.Add(sample.DrawMs);
                present.Add(sample.PresentMs);
            }

            _report.Line($"  {state.Presenter.Name}: draw (CPU side) {draw.Summary()}; Present() {present.Summary()}");
        }

        Check(missed == 0, "every drag step was presented");
        if (CaptureSettled() is { } shot)
        {
            _report.Line("screenshot at the end of the drag (camera moved to the left edge):");
            ReportShot(shot, expectScreenFrame: 200);
        }

        _settings = new SceneSettings();
        RequestRedraw();
        Thread.Sleep(100);
    }

    // ---------------------------------------------------------------------------------------
    // Resize and scale
    // ---------------------------------------------------------------------------------------

    /// <summary>Waits until both swap chains have the pixel size XAML layout asks for, and it has stayed that way for 150 ms.</summary>
    private bool WaitForPanelsSettled(TimeSpan timeout, out double settledAfterMs)
    {
        var start = Stopwatch.GetTimestamp();
        long matchedSince = 0;
        settledAfterMs = -1;
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            var infos = PanelInfos();
            var matches = true;
            foreach (var state in _panels)
            {
                var info = InfoFor(state, infos);
                bool applied;
                lock (state.Sync)
                {
                    applied = state.AppliedVersion == state.PendingVersion;
                }

                matches &= applied && state.Presenter.PixelWidth == info.PixelWidth && state.Presenter.PixelHeight == info.PixelHeight;
            }

            if (!matches)
            {
                matchedSince = 0;
            }
            else if (matchedSince == 0)
            {
                matchedSince = Stopwatch.GetTimestamp();
                settledAfterMs = Stopwatch.GetElapsedTime(start, matchedSince).TotalMilliseconds;
            }
            else if (Stopwatch.GetElapsedTime(matchedSince).TotalMilliseconds >= 150)
            {
                return true;
            }

            PreciseTimer.Sleep(5);
        }

        return false;
    }

    private void ClearResizeStatistics()
    {
        foreach (var state in _panels)
        {
            lock (state.Sync)
            {
                state.ResizeMs = new Samples();
                state.EventToPresentedMs = new Samples();
                state.SizeEvents = 0;
                state.ScaleEvents = 0;
            }
        }
    }

    private void ResizeWindow(int width, int height) => OnUi(() =>
    {
        _window.AppWindow.Resize(new SizeInt32(width, height));
        return 0;
    });

    private void Resize()
    {
        _report.Section("Resizing the window during playback");
        var (baseWidth, baseHeight) = _options.GetSize("window", 3000, 1100);
        SetActive(true, true);
        Play(fromFrame: 60);
        Thread.Sleep(300);

        foreach (var (width, height) in new[] { (2400, 900), (3300, 1250), (2000, 800), (2800, 1000), (baseWidth, baseHeight) })
        {
            ClearResizeStatistics();
            ResizeWindow(width, height);
            var settled = WaitForPanelsSettled(TimeSpan.FromSeconds(3), out var settledAfterMs);
            var infos = PanelInfos();
            _report.Line($"window -> {width}x{height} px: both swap chains at the new size {settledAfterMs.F("0")} ms after AppWindow.Resize returned{(settled ? string.Empty : " (NOT settled)")}");
            Check(settled, $"swap chains follow the window to {width}x{height}");
            foreach (var state in _panels)
            {
                var info = InfoFor(state, infos);
                lock (state.Sync)
                {
                    _report.Line($"  {state.Presenter.Name}: panel {info.Width.F("0.##")}x{info.Height.F("0.##")} DIP => {info.PixelWidth}x{info.PixelHeight} px, buffers {state.Presenter.PixelWidth}x{state.Presenter.PixelHeight}; SizeChanged events {state.SizeEvents}; resize took {string.Join(", ", state.ResizeMs.Values.Select(v => v.F("0.0")))} ms; event -> presented at the new size {string.Join(", ", state.EventToPresentedMs.Values.Select(v => v.F("0.0")))} ms");
                }
            }

            if (CaptureSettled() is { } shot)
            {
                ReportShot(shot, expectScreenFrame: null);
            }
        }

        StopPlaying();

        // The right-hand panel also moves when the window is resized, so each path is measured in
        // both columns, and with the swap chain resized on either thread.
        foreach (var win2dOnTheRight in new[] { false, true })
        {
            OnUi(() =>
            {
                _window.Arrange(win2dOnTheRight);
                return 0;
            });
            WaitForPanelsSettled(TimeSpan.FromSeconds(2), out _);
            foreach (var onUiThread in new[] { false, true })
            {
                AnimatedResize(onUiThread, win2dOnTheRight, baseWidth, baseHeight);
            }
        }

        _resizeOnUiThread = false;
        OnUi(() =>
        {
            _window.Arrange(false);
            return 0;
        });
        WaitForPanelsSettled(TimeSpan.FromSeconds(2), out _);
        RequestRedraw();
    }

    /// <summary>
    /// A window edge dragged in and out again: 90 size changes about 16.7 ms apart during
    /// playback, with every screenshot the system delivers checked for whether each swap chain
    /// ends where XAML layout has the panel's corner at that moment.
    /// </summary>
    private void AnimatedResize(bool onUiThread, bool win2dOnTheRight, int baseWidth, int baseHeight)
    {
        const int steps = 90;
        _resizeOnUiThread = onUiThread;
        _report.Line();
        _report.Line($"animated resize, {steps} steps; (a) in the {(win2dOnTheRight ? "right" : "left")} column, (b) in the {(win2dOnTheRight ? "left" : "right")}; swap chains resized " +
            (onUiThread ? "+ redrawn + presented on the UI thread at the end of the layout pass (LayoutUpdated):" : "by the render thread before its next present:"));
        Play(fromFrame: 60);
        Thread.Sleep(300);
        ClearResizeStatistics();
        DrainSamples();

        var analysing = true;
        var frames = 0;
        var inStep = new int[2];
        var notExact = new int[2];
        var missing = new int[2];
        var worst = new int[2];
        var analysis = new Thread(() =>
        {
            while (Volatile.Read(ref analysing) && _capture is not null)
            {
                var image = _capture.Next(TimeSpan.FromMilliseconds(100), fresh: true);
                if (image is null)
                {
                    continue;
                }

                var shot = ReadShot(image);
                frames++;
                for (var i = 0; i < 2; i++)
                {
                    var reading = i == 0 ? shot.Win2D : shot.Native;
                    if (!reading.AllFound || !reading.XamlFound)
                    {
                        missing[i]++;
                        continue;
                    }

                    if (!reading.AllCrisp)
                    {
                        notExact[i]++;
                    }

                    var errorX = Math.Abs(reading.XamlDx - (PanelMarkers.Inset + PanelMarkers.BlockWidth - reading.XamlSize));
                    var errorY = Math.Abs(reading.XamlDy - (PanelMarkers.Inset + PanelMarkers.BlockHeight - reading.XamlSize));
                    worst[i] = Math.Max(worst[i], Math.Max(errorX, errorY));
                    if (reading.InSync())
                    {
                        inStep[i]++;
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "Spike.PresentCaptureAnalysis",
        };
        analysis.Start();

        var uiMs = new Samples();
        var start = Stopwatch.GetTimestamp();
        for (var i = 1; i <= steps; i++)
        {
            // In to 73 % of the size and back out again.
            var t = 1 - Math.Abs(1 - (2.0 * i / steps));
            var width = (int)Math.Round(baseWidth * (1 - (0.27 * t)));
            var height = (int)Math.Round(baseHeight * (1 - (0.23 * t)));
            var call = Stopwatch.GetTimestamp();
            ResizeWindow(width, height);
            uiMs.Add(Stopwatch.GetElapsedTime(call).TotalMilliseconds);
            var due = i * (1000.0 / 60);
            PreciseTimer.Sleep(Math.Max(0.5, due - Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }

        var animationSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        Volatile.Write(ref analysing, false);
        analysis.Join(2000);
        var settled = WaitForPanelsSettled(TimeSpan.FromSeconds(3), out _);
        var samples = DrainSamples();
        StopPlaying();

        _report.Line($"  {steps} AppWindow.Resize calls took {animationSeconds.F("0.00")} s (1.50 s if the UI thread keeps up); each call, UI thread round trip: {uiMs.Summary()}; settled afterwards: {(settled ? "yes" : "NO")}");
        Check(settled, "swap chains settle after the animated resize");
        for (var i = 0; i < 2; i++)
        {
            var state = _panels[i];
            lock (state.Sync)
            {
                _report.Line($"  {state.Presenter.Name}: SizeChanged events {state.SizeEvents}, resizes applied {state.ResizeMs.Count}; resize {state.ResizeMs.Summary()}; event -> presented at the new size {state.EventToPresentedMs.Summary()}; presents {samples[state].Count}");
            }

            _report.Line($"    {frames} screenshots during the animation: swap chain edge in step with XAML layout in {inStep[i]} ({(frames == 0 ? 0 : 100.0 * inStep[i] / frames).F("0")} %), off by up to {worst[i]} px otherwise; a marker or the XAML square cut off or not found in {missing[i]}; found but not pixel-exact in {notExact[i]}");
        }

        if (CaptureSettled() is { } shot)
        {
            _report.Line("  screenshot after the animation:");
            ReportShot(shot, expectScreenFrame: null);
        }
    }

    private void Scale()
    {
        _report.Section("Composition scale change: a RenderTransform on the panels' parent stands in for a monitor with another DPI");
        SetActive(true, true);
        SeekTo(300);

        // The transform scales positions as well as sizes. For the right-hand panel to stay on
        // whole pixels at x2/3 and x1/2, its offset inside the transformed parent (panel width +
        // 18 px gap) has to be a multiple of 6 px. Real DPI changes do not have this problem,
        // because XAML then lays out again and rounds to the new pixel grid.
        var (windowSize, before) = OnUi(() => (_window.AppWindow.Size, InfoOf(_window.Win2DPanelElement)));
        var excess = before.PixelWidth % 6;
        if (excess != 0)
        {
            ResizeWindow(windowSize.Width - (2 * excess), windowSize.Height);
            WaitForPanelsSettled(TimeSpan.FromSeconds(2), out _);
        }

        RequestRedraw();
        Thread.Sleep(100);
        foreach (var factor in new[] { 2.0 / 3.0, 0.5, 1.0 })
        {
            ClearResizeStatistics();
            OnUi(() =>
            {
                _window.PanelHostElement.RenderTransform = new ScaleTransform { ScaleX = factor, ScaleY = factor };
                return 0;
            });
            var settled = WaitForPanelsSettled(TimeSpan.FromSeconds(3), out var settledAfterMs);
            var infos = PanelInfos();
            _report.Line($"transform x{factor.F("0.###")}: settled {(settled ? $"after {settledAfterMs.F("0")} ms" : "NO")}");
            Check(settled, $"swap chains follow composition scale x{factor.F("0.###")}");
            foreach (var state in _panels)
            {
                var info = InfoFor(state, infos);
                lock (state.Sync)
                {
                    _report.Line($"  {state.Presenter.Name}: CompositionScale {info.ScaleX.F("0.###")}, panel {info.Width.F("0.##")}x{info.Height.F("0.##")} DIP => {info.PixelWidth}x{info.PixelHeight} px, buffers {state.Presenter.PixelWidth}x{state.Presenter.PixelHeight}; CompositionScaleChanged events {state.ScaleEvents}, SizeChanged events {state.SizeEvents}");
                }
            }

            if (CaptureSettled() is { } shot)
            {
                ReportShot(shot, expectScreenFrame: 300, tolerance: 2);
            }
        }

        if (excess != 0)
        {
            ResizeWindow(windowSize.Width, windowSize.Height);
            WaitForPanelsSettled(TimeSpan.FromSeconds(2), out _);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Evidence
    // ---------------------------------------------------------------------------------------

    private void Screenshots()
    {
        _report.Section("Screenshots (Windows.Graphics.Capture of the spike's own window)");
        SetActive(true, true);
        SeekTo(345);
        RequestRedraw();
        Thread.Sleep(150);
        var shot = CaptureSettled();
        if (shot is null)
        {
            _report.Line("no frame was delivered; no screenshots");
            _failures++;
            return;
        }

        Save("window.png", shot.Image.Pixels, shot.Image.Width, shot.Image.Height);
        foreach (var state in _panels)
        {
            var reading = shot.For(state);
            if (reading.AllFound && reading.OriginX >= 0 && reading.OriginY >= 0 &&
                reading.OriginX + reading.Width <= shot.Image.Width && reading.OriginY + reading.Height <= shot.Image.Height)
            {
                var name = state.Presenter is Win2DPresenter ? "panel-a-win2d.png" : "panel-b-swapchainpanel.png";
                Save(name, shot.Image.Crop(reading.OriginX, reading.OriginY, reading.Width, reading.Height), reading.Width, reading.Height);
            }
        }

        ReportShot(shot, expectScreenFrame: 345);
    }

    private void Save(string name, byte[] pixels, int width, int height)
    {
        var path = Path.Combine(_output, name);
        PngWriter.WriteBgra(path, pixels, width, height);
        _report.Line($"wrote {Path.GetRelativePath(_options.Root, path)} ({width}x{height})");
    }

    private static int SampleCount(PanelState state)
    {
        lock (state.Sync)
        {
            return state.Samples.Count;
        }
    }
}
