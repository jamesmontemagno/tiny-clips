using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StudioEngineSpike.Engine;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StudioEngineSpike.Present;

internal enum RenderMode
{
    /// <summary>Present only when asked (paused preview) or when a panel changed size.</summary>
    OnDemand,

    /// <summary>Present whenever a MediaPlayer delivers a frame (playback).</summary>
    OnVideoFrame,

    /// <summary>Present in a tight loop, paced only by the swap chain (upper bound).</summary>
    Continuous,
}

internal readonly record struct PresentSample(long Timestamp, double GateWaitMs, double DrawMs, double PresentMs, bool HasStatistics, FrameStatistics Statistics, uint LastPresentCount);

/// <summary>One panel under test: its presenter, its own renderer, and what was measured.</summary>
internal sealed class PanelState
{
    public PanelState(string label, IPanelPresenter presenter, SceneRenderer renderer, (byte B, byte G, byte R) markerKey)
    {
        Label = label;
        Presenter = presenter;
        Renderer = renderer;
        MarkerKey = markerKey;
        MarkerBlock = PanelMarkers.BuildBlock(markerKey);
    }

    public string Label { get; }

    public IPanelPresenter Presenter { get; }

    public SceneRenderer Renderer { get; }

    public (byte B, byte G, byte R) MarkerKey { get; }

    public byte[] MarkerBlock { get; }

    public DrawCallback Draw { get; set; } = null!;

    /// <summary>Guards the pending size and the sample lists.</summary>
    public object Sync { get; } = new();

    /// <summary>One resize or present at a time, whichever thread does it.</summary>
    public object PresentLock { get; } = new();

    public volatile bool Active = true;

    /// <summary>Set by a redraw request, cleared when the panel is next drawn.</summary>
    public volatile bool Dirty;

    // Which copy of each clip the panel last drew (PlayerTrack.CopyCount, read under the device gate).
    public long DrawnScreenCopy = -1;
    public long DrawnCameraCopy = -1;

    // Written on the UI thread by SizeChanged / CompositionScaleChanged, applied before the next present.
    public double PendingWidth;
    public double PendingHeight;
    public double PendingScale;
    public int PendingVersion;
    public long PendingTimestamp;
    public int AppliedVersion;
    public int SizeEvents;
    public int ScaleEvents;

    public List<PresentSample> Samples = new();
    public Samples ResizeMs = new();
    public Samples EventToPresentedMs = new();
}

/// <summary>
/// Question 2: drives the two presenters in the spike window and measures them. Runs on its own
/// thread; a second thread renders and presents; the UI thread only attaches the swap chains,
/// reports panel size changes and resizes the window when asked.
/// </summary>
internal sealed partial class PresentSession
{
    private const int Fps = TestMedia.Fps;
    private const int Lag = TestMedia.CameraFrameLag;

    private readonly SpikeOptions _options;
    private readonly PresentWindow _window;
    private readonly nint _hwnd;
    private readonly string _output;
    private readonly AutoResetEvent _wake = new(false);
    private readonly RenderOptions _renderOptions = new(ShadowMode.Cached);

    private Report _report = null!;
    private GraphicsDevice _graphics = null!;
    private PreviewEngine _engine = null!;
    private SeekCoordinator _coordinator = null!;
    private WindowCapture? _capture;
    private SceneRenderer _win2dRenderer = null!;
    private SceneRenderer _nativeRenderer = null!;
    private Win2DPresenter _win2dPresenter = null!;
    private NativePresenter _nativePresenter = null!;
    private PanelState _win2d = null!;
    private PanelState _native = null!;
    private PanelState[] _panels = [];
    private Thread? _renderThread;
    private volatile SceneSettings _settings = new();
    private volatile bool _stop;
    private volatile RenderMode _mode = RenderMode.OnDemand;
    private volatile bool _waitForGpu;
    private int _syncInterval;
    private int _uiPass;
    private volatile bool _resizeOnUiThread;
    private volatile string? _renderFailure;
    private double _rasterizationScale;
    private int _failures;

    public PresentSession(SpikeOptions options, PresentWindow window)
    {
        _options = options;
        _window = window;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _output = options.OutputDirectory();
        _syncInterval = options.GetInt("sync-interval", 0);
    }

    public void Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Spike.Present" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Run()
    {
        using var report = _options.OpenReport();
        _report = report;
        try
        {
            var only = _options.GetString("only", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var skip = _options.GetString("skip", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool Wanted(string name) => (only.Length == 0 || only.Contains(name, StringComparer.OrdinalIgnoreCase)) && !skip.Contains(name, StringComparer.OrdinalIgnoreCase);

            if (Setup())
            {
                Describe();
                Run("continuous", Wanted, Continuous);
                Run("playback", Wanted, Playback);
                Run("block", Wanted, BlockUiThread);
                Run("drag", Wanted, Drag);
                Run("resize", Wanted, Resize);
                Run("scale", Wanted, Scale);
                Run("screenshot", Wanted, Screenshots);
                _report.Section("Result");
                _report.Line(_renderFailure is null ? "render thread: no failure" : $"render thread FAILED: {_renderFailure}");
                _report.Line($"presents answered with DXGI_STATUS_OCCLUDED on the DXGI path: {_nativePresenter.OccludedPresents}");
                ReportForeground("at the end");
                _report.Line(_failures == 0 && _renderFailure is null ? "all checks passed" : $"{_failures} check(s) FAILED");
                PresentApp.ExitCode = _failures == 0 && _renderFailure is null ? 0 : 5;
            }
            else
            {
                PresentApp.ExitCode = 4;
            }
        }
        catch (Exception ex)
        {
            _report.Line($"FAILED: {ex}");
            PresentApp.ExitCode = 2;
        }
        finally
        {
            Teardown();
        }
    }

    private void Run(string name, Func<string, bool> wanted, Action test)
    {
        if (!wanted(name) || _renderFailure is not null)
        {
            return;
        }

        Status(name);
        test();
    }

    // ---------------------------------------------------------------------------------------
    // Setup and teardown
    // ---------------------------------------------------------------------------------------

    private bool Setup()
    {
        _report.Section("Environment");
        _report.Line($"runtime: {SpikeOptions.RuntimeFlavor}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        _graphics = GraphicsDevice.Create();
        _report.Line($"adapter: {_graphics.AdapterName}");
        using (var dxgiDevice = _graphics.Device.QueryInterface<IDXGIDevice1>())
        {
            _report.Line($"IDXGIDevice1 maximum frame latency: {dxgiDevice.MaximumFrameLatency}");
        }

        var media = TestMedia.Directory(_options.Root);
        _engine = new PreviewEngine(_graphics, Path.Combine(media, TestMedia.Screen.FileName), Path.Combine(media, TestMedia.Camera.FileName))
        {
            IdentifyFrames = false,
        };
        _engine.FrameArrived += _ =>
        {
            if (_mode == RenderMode.OnVideoFrame)
            {
                _wake.Set();
            }
        };
        if (!_engine.WaitForOpen(TimeSpan.FromSeconds(20)))
        {
            _report.Line($"FAIL: players did not open: {_engine.Failure ?? "timeout"}");
            return false;
        }

        _engine.SetCameraOffset(TimeSpan.FromSeconds(-TestMedia.CameraStartOffsetSeconds));
        Thread.Sleep(300);
        _coordinator = new SeekCoordinator(_engine, Fps, TestMedia.FrameCount, TestMedia.FrameCount, Lag);
        _coordinator.Reset(0);
        if (!SeekTo(100))
        {
            _report.Line("FAIL: the players did not reach frame 100");
            return false;
        }

        // Each presenter gets its own renderer so that one path's caches (shadow bitmaps, wrapped
        // targets) cannot be disturbed by the other path's panel size.
        _win2dRenderer = new SceneRenderer(_graphics);
        _nativeRenderer = new SceneRenderer(_graphics);
        var win2dAttachMs = 0.0;
        var nativeAttachMs = 0.0;
        OnUi(() =>
        {
            _rasterizationScale = _window.RootElement.XamlRoot.RasterizationScale;

            var start = Stopwatch.GetTimestamp();
            _win2dPresenter = new Win2DPresenter(_graphics, _win2dRenderer);
            var panelA = _window.Win2DPanelElement;
            _win2dPresenter.Attach(panelA, panelA.ActualWidth, panelA.ActualHeight, panelA.CompositionScaleX);
            win2dAttachMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            _nativePresenter = new NativePresenter(_graphics, _nativeRenderer);
            var panelB = _window.NativePanelElement;
            _nativePresenter.Attach(panelB, panelB.ActualWidth, panelB.ActualHeight, panelB.CompositionScaleX);
            nativeAttachMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return 0;
        });

        _win2d = CreateState("(a)", _win2dPresenter, _win2dRenderer, PanelMarkers.Win2DKey);
        _native = CreateState("(b)", _nativePresenter, _nativeRenderer, PanelMarkers.NativeKey);
        _panels = [_win2d, _native];
        SetSyncInterval(_syncInterval);
        OnUi(() =>
        {
            Hook(_window.Win2DPanelElement, _win2d);
            Hook(_window.NativePanelElement, _native);
            _window.PanelHostElement.LayoutUpdated += (_, _) => OnLayoutUpdated();
            return 0;
        });

        _report.Line($"attach on the UI thread (create the presenter and its swap chain, hand it to the panel): (a) {win2dAttachMs.F("0.0")} ms, (b) {nativeAttachMs.F("0.0")} ms");
        if (_win2dPresenter.InteropFailure is not null)
        {
            _report.Line($"(a) ICanvasResourceWrapperNative -> IDXGISwapChain1 failed: {_win2dPresenter.InteropFailure} (no DXGI frame statistics for the Win2D path)");
        }

        try
        {
            _capture = new WindowCapture(_graphics, _hwnd, 3440, 1440, _options.GetDouble("capture-interval-ms", 1));
            foreach (var note in _capture.Notes)
            {
                _report.Line($"window capture note: {note}");
            }
        }
        catch (Exception ex)
        {
            _report.Line($"window capture is not available: 0x{ex.HResult:X8} {ex.Message.Trim()} (no screenshots, no pixel checks)");
        }

        ReportForeground("at the start");
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Spike.PresentRender" };
        _renderThread.Start();
        RequestRedraw();
        Thread.Sleep(200);
        return true;
    }

    private void ReportForeground(string when)
    {
        var (isOurs, coversMonitor, title) = Interop.PanelInterop.DescribeForeground(_hwnd);
        if (isOurs)
        {
            _report.Line($"foreground window {when}: the spike window");
        }
        else
        {
            _report.Line($"foreground window {when}: another application's (\"{title}\"){(coversMonitor ? ", and it covers the whole monitor: the spike window is not on the display, so DXGI frame statistics stay at zero and only the screenshots (Windows.Graphics.Capture) tell what would be shown" : string.Empty)}");
        }
    }

    private PanelState CreateState(string label, IPanelPresenter presenter, SceneRenderer renderer, (byte B, byte G, byte R) key)
    {
        var state = new PanelState(label, presenter, renderer, key);
        state.Draw = (target, width, height) => Draw(state, target, width, height);
        return state;
    }

    private void Hook(SwapChainPanel panel, PanelState state)
    {
        panel.SizeChanged += (_, e) =>
        {
            Interlocked.Increment(ref state.SizeEvents);
            Post(state, e.NewSize.Width, e.NewSize.Height, panel.CompositionScaleX);
        };
        panel.CompositionScaleChanged += (sender, _) =>
        {
            Interlocked.Increment(ref state.ScaleEvents);
            Post(state, sender.ActualWidth, sender.ActualHeight, sender.CompositionScaleX);
        };
    }

    private void Teardown()
    {
        _stop = true;
        _wake.Set();
        _renderThread?.Join(3000);
        try
        {
            _capture?.Dispose();
            _coordinator?.Dispose();
            _engine?.Dispose();
            if (_nativePresenter is not null)
            {
                OnUi(() =>
                {
                    _nativePresenter.Detach(_window.NativePanelElement);
                    _window.Win2DPanelElement.SwapChain = null;
                    return 0;
                });
            }

            foreach (var state in _panels)
            {
                state.Presenter.Dispose();
                lock (_graphics.Gate)
                {
                    state.Renderer.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"teardown: {ex.Message}");
        }

        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _window.Close();
            Application.Current.Exit();
        });
    }

    // ---------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------

    private void RenderLoop()
    {
        try
        {
            var pass = 0;
            while (!_stop)
            {
                if (_mode != RenderMode.Continuous)
                {
                    // Woken by a frame, a redraw request or a size change; the timeout only bounds
                    // how long a missed wake-up could go unnoticed.
                    _wake.WaitOne(50);
                }

                // The panel that goes first sees a clip's new frame a little earlier and waits for
                // the other clip's copy; alternating keeps that from looking like a difference
                // between the two paths.
                var first = (int)((_engine.Screen.CopyCount + pass) & 1);
                if (_mode != RenderMode.OnVideoFrame)
                {
                    pass++;
                }
                for (var k = 0; k < _panels.Length; k++)
                {
                    var state = _panels[(first + k) % _panels.Length];
                    if (state.Active && !_stop && (_mode == RenderMode.Continuous || NeedsPresent(state)))
                    {
                        PresentPanel(state, allowResize: !_resizeOnUiThread);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _renderFailure = ex.ToString();
        }
    }

    /// <summary>
    /// Is there anything new to show? A redraw was asked for, the panel changed size, or (during
    /// playback) a clip's texture holds a newer frame than the one this panel last drew. Both
    /// clips' frames usually arrive within a millisecond, so the second wake-up finds the pair
    /// already drawn and presents nothing.
    /// </summary>
    private bool NeedsPresent(PanelState state)
    {
        if (state.Dirty)
        {
            return true;
        }

        if (!_resizeOnUiThread && HasPendingSize(state))
        {
            return true;
        }

        return _mode == RenderMode.OnVideoFrame &&
            (_engine.Screen.CopyCount != Interlocked.Read(ref state.DrawnScreenCopy) || _engine.Camera.CopyCount != Interlocked.Read(ref state.DrawnCameraCopy));
    }

    private static bool HasPendingSize(PanelState state)
    {
        lock (state.Sync)
        {
            return state.PendingVersion != state.AppliedVersion;
        }
    }

    /// <summary>Applies a pending size, draws and presents one panel. Render thread, or the UI thread in the synchronous resize variant.</summary>
    private void PresentPanel(PanelState state, bool allowResize)
    {
        lock (state.PresentLock)
        {
            state.Dirty = false;
            long eventTimestamp = 0;
            var resized = allowResize && ApplyPending(state, out eventTimestamp);
            var timing = state.Presenter.Present(state.Draw, _waitForGpu);
            var now = Stopwatch.GetTimestamp();
            var hasStatistics = state.Presenter.TryGetFrameStatistics(out var statistics, out var lastPresentCount);
            lock (state.Sync)
            {
                state.Samples.Add(new PresentSample(now, timing.GateWaitMs, timing.DrawMs, timing.PresentMs, hasStatistics, statistics, lastPresentCount));
                if (resized)
                {
                    state.EventToPresentedMs.Add(Stopwatch.GetElapsedTime(eventTimestamp, now).TotalMilliseconds);
                }
            }
        }
    }

    private bool ApplyPending(PanelState state, out long eventTimestamp)
    {
        double width;
        double height;
        double scale;
        int version;
        lock (state.Sync)
        {
            eventTimestamp = state.PendingTimestamp;
            if (state.PendingVersion == state.AppliedVersion)
            {
                return false;
            }

            width = state.PendingWidth;
            height = state.PendingHeight;
            scale = state.PendingScale;
            version = state.PendingVersion;
        }

        var start = Stopwatch.GetTimestamp();
        state.Presenter.Resize(width, height, scale);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        lock (state.Sync)
        {
            state.AppliedVersion = version;
            state.ResizeMs.Add(elapsed);
        }

        return true;
    }

    /// <summary>UI thread: a panel reported a new size or composition scale.</summary>
    private void Post(PanelState state, double width, double height, double scale)
    {
        if (width <= 0 || height <= 0 || scale <= 0)
        {
            return;
        }

        lock (state.Sync)
        {
            state.PendingWidth = width;
            state.PendingHeight = height;
            state.PendingScale = scale;
            state.PendingVersion++;
            state.PendingTimestamp = Stopwatch.GetTimestamp();
        }

        // In the UI-thread variant OnLayoutUpdated picks this up before the layout pass ends.
        _wake.Set();
    }

    /// <summary>
    /// UI thread, at the end of a layout pass. Variant: resize, redraw and present here, before
    /// XAML commits the new layout, instead of leaving it to the render thread.
    /// </summary>
    private void OnLayoutUpdated()
    {
        if (!_resizeOnUiThread || _stop)
        {
            return;
        }

        var first = _uiPass++ & 1;
        for (var k = 0; k < _panels.Length; k++)
        {
            var state = _panels[(first + k) % _panels.Length];
            if (state.Active && HasPendingSize(state))
            {
                PresentPanel(state, allowResize: true);
            }
        }
    }

    private void Draw(PanelState state, ID3D11Texture2D target, int width, int height)
    {
        lock (_graphics.Gate)
        {
            // No frame copy is in progress while the gate is held, so these name what the textures contain.
            Interlocked.Exchange(ref state.DrawnScreenCopy, _engine.Screen.CopyCount);
            Interlocked.Exchange(ref state.DrawnCameraCopy, _engine.Camera.CopyCount);
            var layout = SceneLayout.Resolve(_settings, width, height, TestMedia.Screen.Width, TestMedia.Screen.Height, TestMedia.Camera.Width, TestMedia.Camera.Height);
            state.Renderer.Render(target, layout, _engine.Screen.Source, _engine.Camera.Source, _renderOptions);
            PanelMarkers.Draw(_graphics, target, width, height, state.MarkerBlock);
        }
    }

    private void RequestRedraw()
    {
        foreach (var state in _panels)
        {
            state.Dirty = true;
        }

        _wake.Set();
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private T OnUi<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        var queued = _window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        if (!queued)
        {
            throw new InvalidOperationException("The UI thread's dispatcher queue is shut down.");
        }

        done.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException($"UI thread action failed: {failure.Message}", failure);
        }

        return result;
    }

    private void Status(string phase)
    {
        var text = $"{SpikeOptions.RuntimeFlavor} · {phase}";
        _window.DispatcherQueue.TryEnqueue(() => _window.SetStatus(text));
    }

    private bool SeekTo(int frame)
    {
        _coordinator.Request(frame);
        return _coordinator.WaitUntilSettled(frame, TimeSpan.FromSeconds(5));
    }

    /// <summary>Pauses and brings both players onto the frame the screen clip shows (the pause policy from question 1).</summary>
    private int PauseAndSnap()
    {
        _engine.Controller.Pause();
        Thread.Sleep(80);
        long screenTicks = -1;
        long cameraTicks = -1;
        while (_engine.Events.TryDequeue(out var e))
        {
            if (e.Track == 0)
            {
                screenTicks = e.SessionTicks;
            }
            else
            {
                cameraTicks = e.SessionTicks;
            }
        }

        if (screenTicks < 0 || cameraTicks < 0)
        {
            return -1;
        }

        var screenShown = (int)Math.Floor(screenTicks * (double)Fps / TimeSpan.TicksPerSecond);
        var cameraShown = (int)Math.Floor(cameraTicks * (double)Fps / TimeSpan.TicksPerSecond);
        _coordinator.ResetAfterPlayback(screenShown, cameraShown);
        SeekTo(screenShown);
        RequestRedraw();
        return screenShown;
    }

    private void Play(int fromFrame)
    {
        SeekTo(fromFrame);
        while (_engine.Events.TryDequeue(out _))
        {
        }

        _engine.Controller.ClockRate = 1.0;
        _mode = RenderMode.OnVideoFrame;
        _engine.Controller.Resume();
    }

    private void StopPlaying()
    {
        PauseAndSnap();
        _mode = RenderMode.OnDemand;
        Thread.Sleep(60);
    }

    private void SetActive(bool win2d, bool native)
    {
        _win2d.Active = win2d;
        _native.Active = native;
    }

    private void SetSyncInterval(int interval)
    {
        foreach (var state in _panels)
        {
            lock (state.PresentLock)
            {
                state.Presenter.SyncInterval = interval;
            }
        }
    }

    private Dictionary<PanelState, List<PresentSample>> DrainSamples()
    {
        var result = new Dictionary<PanelState, List<PresentSample>>();
        foreach (var state in _panels)
        {
            lock (state.Sync)
            {
                result[state] = state.Samples;
                state.Samples = new List<PresentSample>();
            }
        }

        return result;
    }

    private static double CpuSeconds() => Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;

    private void Check(bool ok, string what)
    {
        if (!ok)
        {
            _failures++;
            _report.Line($"  CHECK FAILED: {what}");
        }
    }

    /// <summary>What one panel's presents looked like over a measured stretch.</summary>
    private void Summarize(PanelState state, List<PresentSample> samples, double seconds)
    {
        if (samples.Count < 3)
        {
            _report.Line($"  {state.Presenter.Name}: {samples.Count} presents");
            return;
        }

        var intervals = new Samples();
        var gate = new Samples();
        var draw = new Samples();
        var present = new Samples();
        for (var i = 0; i < samples.Count; i++)
        {
            gate.Add(samples[i].GateWaitMs);
            draw.Add(samples[i].DrawMs);
            present.Add(samples[i].PresentMs);
            if (i > 0)
            {
                intervals.Add(Stopwatch.GetElapsedTime(samples[i - 1].Timestamp, samples[i].Timestamp).TotalMilliseconds);
            }
        }

        _report.Line($"  {state.Presenter.Name}: {samples.Count} presents in {seconds.F("0.0")} s = {(samples.Count / seconds).F("0.0")}/s");
        _report.Line($"    time between presents: {intervals.Summary()}");
        _report.Line($"    wait for the device gate: {gate.Summary()}");
        _report.Line($"    draw: {draw.Summary()}");
        _report.Line($"    Present(): {present.Summary()}");
        _report.Line($"    {DescribeStatistics(samples)}");
    }

    /// <summary>
    /// DXGI frame statistics, sampled after every present: which display refresh each presented
    /// frame reached the screen on, and how many presents were queued ahead of the screen.
    /// </summary>
    private static string DescribeStatistics(List<PresentSample> samples)
    {
        // The first few samples still describe whatever the swap chain presented before this
        // stretch (possibly seconds ago), so they are left out.
        var valid = samples.Where(s => s.HasStatistics && s.Statistics.SyncQPCTime != 0).Skip(5).ToList();
        if (valid.Count < 3)
        {
            var succeeded = samples.Where(s => s.HasStatistics).ToList();
            var raw = succeeded.Count == 0
                ? "no call succeeded"
                : $"first PresentCount {succeeded[0].Statistics.PresentCount}, PresentRefreshCount {succeeded[0].Statistics.PresentRefreshCount}, SyncRefreshCount {succeeded[0].Statistics.SyncRefreshCount}, SyncQPCTime {succeeded[0].Statistics.SyncQPCTime}; last {succeeded[^1].Statistics.PresentCount}, {succeeded[^1].Statistics.PresentRefreshCount}, {succeeded[^1].Statistics.SyncRefreshCount}, {succeeded[^1].Statistics.SyncQPCTime}; GetLastPresentCount {succeeded[0].LastPresentCount} -> {succeeded[^1].LastPresentCount}";
            return $"DXGI frame statistics: not available ({succeeded.Count} of {samples.Count} calls succeeded; {raw})";
        }

        var first = valid[0].Statistics;
        var last = valid[^1].Statistics;
        var refreshes = last.SyncRefreshCount - first.SyncRefreshCount;
        var seconds = (last.SyncQPCTime - first.SyncQPCTime) / (double)Stopwatch.Frequency;
        var shown = last.PresentCount - first.PresentCount;
        var issued = valid[^1].LastPresentCount - valid[0].LastPresentCount;

        // The refresh on which each present count first appeared on screen.
        var refreshOf = new SortedDictionary<uint, uint>();
        foreach (var sample in valid)
        {
            refreshOf.TryAdd(sample.Statistics.PresentCount, sample.Statistics.PresentRefreshCount);
        }

        var gaps = new List<int>();
        uint previousCount = 0;
        uint previousRefresh = 0;
        var havePrevious = false;
        foreach (var (count, refresh) in refreshOf)
        {
            if (havePrevious && count == previousCount + 1)
            {
                gaps.Add((int)(refresh - previousRefresh));
            }

            previousCount = count;
            previousRefresh = refresh;
            havePrevious = true;
        }

        var queued = new Samples();
        foreach (var sample in valid)
        {
            queued.Add(sample.LastPresentCount - sample.Statistics.PresentCount);
        }

        var rate = seconds > 0 ? (refreshes / seconds).F("0.0") : "?";
        return $"DXGI statistics: display {rate} Hz; {shown} of {issued} presents reached the screen in {refreshes} refreshes; refreshes between consecutive presents on screen: {Fmt.Histogram(gaps)}; presents queued behind the screen when the next was issued: mean {queued.Mean.F("0.0")}, max {queued.Max.F("0")}";
    }
}
