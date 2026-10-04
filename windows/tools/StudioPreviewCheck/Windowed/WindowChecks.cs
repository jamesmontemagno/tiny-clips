using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Windows.Graphics;

namespace TinyClips.Tools.StudioPreviewCheck.Windowed;

/// <summary>
/// The checks that need a window: the app's <see cref="StudioPreviewPanel"/> in a small window,
/// read back from screenshots of that window. They run on a thread of their own and come to the
/// UI thread only for what XAML requires.
/// </summary>
internal sealed partial class WindowChecks
{
    private const int Fps = TestMedia.Fps;
    private const double Late = 0.2;

    // Physical pixels between the window's client edge and the panel.
    private const int HostMargin = 48;

    /// <summary>The panel as XAML sees it. Read on the UI thread.</summary>
    private readonly record struct PanelInfo(double Width, double Height, double ScaleX, double ScaleY, double RasterScale, bool IsLoaded)
    {
        public int PixelWidth => (int)Math.Round(Width * ScaleX);

        public int PixelHeight => (int)Math.Round(Height * ScaleY);
    }

    private sealed record Shot(CapturedImage Image, PanelReading Reading);

    private readonly Report _report;
    private readonly CheckOptions _options;
    private readonly string _media;
    private readonly string _output;
    private readonly CheckWindow _window;
    private readonly StudioPreviewPanel _panel;
    private readonly DispatcherQueue _dispatcher;
    private readonly nint _handle;
    private readonly Action _finished;
    private readonly StudioPreviewOptions _muted = new() { ForceMuted = true };
    private readonly byte[] _block = PanelMarkers.BuildBlock();
    private readonly byte[] _serialStrip = new byte[PanelMarkers.SerialWidth * PanelMarkers.SerialHeight * 4];
    private readonly List<TestFolder> _folders = [];
    private readonly List<StudioPreviewEngine> _engines = [];
    private readonly bool _quick;

    private WindowCapture? _capture;
    private StudioPreviewEngine? _engine;
    private TestFolder? _folder;
    private StudioProject _project = new();
    private long _drawnSize;
    private int _sceneSerial;
    private int _started;
    private bool _wasForeground;
    private int _screenshots;

    public WindowChecks(Report report, CheckOptions options, string mediaDirectory, string outputDirectory, CheckWindow window, nint handle, Action finished)
    {
        _report = report;
        _options = options;
        _media = mediaDirectory;
        _output = outputDirectory;
        _window = window;
        _panel = window.PanelElement;
        _dispatcher = window.DispatcherQueue;
        _handle = handle;
        _finished = finished;
        _quick = options.Flag("quick");
    }

    public bool Started => Volatile.Read(ref _started) != 0;

    /// <summary>Starts the checks on their own thread. When they are done, <c>finished</c> runs on the UI thread.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(Run) { IsBackground = true, Name = "StudioPreviewCheck.Window" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Run()
    {
        try
        {
            if (Setup())
            {
                Group("window-exact", PixelExact);
                Group("window-playback", Playback);
                Group("window-resize", Resize);
                Group("window-scale", Scale);
                Group("window-blocked", BlockedUiThread);
                Group("window-reload", UnloadAndReload);
                Group("window-engines", Engines);
                Group("window-devicelost", DeviceLost);
                _report.Section("Window: the person at the machine");
                _wasForeground |= NativeMethods.IsForeground(_handle);
                _report.Note($"the tool's window was {(_wasForeground ? "AT SOME POINT" : "never")} the foreground window; it was shown without activation, behind the other windows, and {_screenshots} screenshots of it were taken");
            }
        }
        catch (Exception ex)
        {
            _report.Check("window: the checks ran to the end", false, ex + FailedOpen(ex, "window"));
        }
        finally
        {
            Teardown();
            if (!_dispatcher.TryEnqueue(() => _finished()))
            {
                Environment.Exit(_report.Finish());
            }
        }
    }

    private void Group(string name, Action body)
    {
        if (!_options.Wants(name) || _engine is null)
        {
            return;
        }

        try
        {
            body();
        }
        catch (Exception ex)
        {
            _report.Check($"{name}: the checks ran to the end", false, ex + FailedOpen(ex, name));
        }

        _wasForeground |= NativeMethods.IsForeground(_handle);
    }

    // ---------------------------------------------------------------------------------------
    // Setup and helpers
    // ---------------------------------------------------------------------------------------

    private bool Setup()
    {
        _report.Section("Window: the app's StudioPreviewPanel in a window, read back from screenshots of the window");
        var info = OnUi(() =>
        {
            SetHostMargin();
            return Info();
        });
        var size = OnUi(() => _window.AppWindow.Size);
        _report.Line($"window {size.Width}x{size.Height} px, display scale {info.RasterScale:0.###} ({info.RasterScale * 100:0} %), foreground window: \"{NativeMethods.ForegroundTitle()}\"");

        // A bubble that keeps clear of the corner markers at every size the checks use.
        _folder = NewFolder(TestMedia.Camera, p => p with { Scenes = [p.Scenes[0] with { Bubble = p.Scenes[0].Bubble with { OffsetX = -0.15, OffsetY = -0.2 } }] });
        _project = _folder.Project;
        _engine = OpenEngine(_folder);
        OnUi(() =>
        {
            _panel.Attach(_engine);
            return 0;
        });

        _capture = new WindowCapture(_handle, 2600, 1800);
        if (!WaitSettled(TimeSpan.FromSeconds(5), out _))
        {
            var panel = OnUi(Info);
            var (width, height) = DrawnSize;
            _report.Check("the engine draws into the panel at the panel's size in physical pixels", false, $"the panel is {panel.Width:0.##}x{panel.Height:0.##} at scale {panel.ScaleX:0.###}, so {panel.PixelWidth}x{panel.PixelHeight} px; the engine last drew {width}x{height}; loaded {panel.IsLoaded}");
            return false;
        }

        var first = Capture(OnUi(Info));
        if (first is null)
        {
            _report.Check("Windows.Graphics.Capture delivers screenshots of the tool's window", false, "no frame within 2 s");
            return false;
        }

        return true;
    }

    private void Teardown()
    {
        try
        {
            OnUi(() =>
            {
                _panel.Detach();
                return 0;
            });
        }
        catch (Exception)
        {
            // The window is already gone.
        }

        foreach (var engine in _engines)
        {
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _capture?.Dispose();
        foreach (var folder in _folders)
        {
            folder.Dispose();
        }
    }

    /// <summary>Leaves the trace of a preview that did not open in the run's folder under <c>out\failures</c>.</summary>
    private string FailedOpen(Exception exception, string name) =>
        Checks.Session.DumpFailedOpen(exception, Path.Combine(_output, "failures", _report.Stamp), name);

    private TestFolder NewFolder(ClipSpec? camera, Func<StudioProject, StudioProject>? edit = null)
    {
        var folder = TestFolder.Create(_media, camera, Late, edit);
        _folders.Add(folder);
        return folder;
    }

    private StudioPreviewEngine OpenEngine(TestFolder folder)
    {
        var engine = Checks.Session.OpenEngine(_muted, folder, out _);
        _engines.Add(engine);
        engine.AfterRender = OnRendered;
        return engine;
    }

    /// <summary>
    /// The engine's after-render hook: the corner markers and the scene's number go into every
    /// scene before it is presented.
    /// </summary>
    private void OnRendered(StudioGraphicsDevice graphics, ID3D11Texture2D target, int width, int height)
    {
        PanelMarkers.Draw(graphics, target, width, height, _block);
        PanelMarkers.DrawSerial(graphics, target, width, height, Interlocked.Increment(ref _sceneSerial) & PanelMarkers.SerialMask, _serialStrip);
        Interlocked.Exchange(ref _drawnSize, ((long)width << 32) | (uint)height);
    }

    /// <summary>The number of the scene an engine drew last, as it is written into the picture.</summary>
    private int LastScene => Volatile.Read(ref _sceneSerial) & PanelMarkers.SerialMask;

    private (int Width, int Height) DrawnSize
    {
        get
        {
            var value = Interlocked.Read(ref _drawnSize);
            return ((int)(value >> 32), (int)(value & 0xFFFFFFFF));
        }
    }

    private T OnUi<T>(Func<T> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return action();
        }

        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        var queued = _dispatcher.TryEnqueue(() =>
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
            throw new InvalidOperationException("The window's dispatcher has shut down.");
        }

        done.Wait();
        return failure is null ? result : throw new InvalidOperationException("A call on the UI thread failed.", failure);
    }

    // UI thread.
    private PanelInfo Info() => new(
        _panel.ActualWidth,
        _panel.ActualHeight,
        _panel.CompositionScaleX,
        _panel.CompositionScaleY,
        _window.Content.XamlRoot?.RasterizationScale ?? 1,
        _panel.IsLoaded);

    // UI thread. The margin in effective pixels that is HostMargin physical pixels on this display.
    private void SetHostMargin()
    {
        var scale = _window.Content.XamlRoot?.RasterizationScale ?? 1;
        _window.HostElement.Margin = new Thickness(HostMargin / scale);
    }

    /// <summary>
    /// Waits until the engine has drawn at the size XAML gives the panel, in physical pixels, and
    /// that has stayed so for 150 ms.
    /// </summary>
    private bool WaitSettled(TimeSpan timeout, out double afterMilliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        long matchedSince = 0;
        afterMilliseconds = -1;
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            var info = OnUi(Info);
            var engine = _engine;
            var matches = info.IsLoaded && engine is not null && DrawnSize == (info.PixelWidth, info.PixelHeight) && (engine.IsPlaying || engine.IsIdle);
            if (!matches)
            {
                matchedSince = 0;
            }
            else if (matchedSince == 0)
            {
                matchedSince = Stopwatch.GetTimestamp();
                afterMilliseconds = Stopwatch.GetElapsedTime(start, matchedSince).TotalMilliseconds;
            }
            else if (Stopwatch.GetElapsedTime(matchedSince).TotalMilliseconds >= 150)
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    private PanelReading Read(CapturedImage image, PanelInfo info, StudioProject project, ClipSpec? camera) =>
        PanelMarkers.Read(image, (width, height) => SceneView.Resolve(project, TestMedia.Screen, camera, width, height), TestMedia.Screen, camera, info.ScaleX);

    /// <summary>
    /// A screenshot that shows the scene the engine drew last. The system delivers screenshots a
    /// little after it composed them, so the first ones after a seek can still show the picture
    /// from before; every scene carries its number, and those are passed over. When the last scene
    /// has not appeared after three seconds, the newest screenshot is returned as it is: the
    /// present did not reach the screen, and the check that asked will say what it shows.
    /// </summary>
    /// <param name="expectPicture">
    /// False when the panel is expected to show nothing. Then the screenshot is the second one
    /// composed after the call.
    /// </param>
    private Shot? Capture(PanelInfo info, StudioProject? project = null, ClipSpec? camera = null, bool noCamera = false, bool expectPicture = true)
    {
        if (_capture is null)
        {
            return null;
        }

        project ??= _project;
        Shot? shot = null;
        var composed = 0;
        var start = Stopwatch.GetTimestamp();
        _capture.Drain();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 3)
        {
            // Something has to change for the system to compose the window again.
            OnUi(() =>
            {
                _window.Nudge();
                return 0;
            });
            if (_capture.Next(TimeSpan.FromMilliseconds(400)) is not { } image)
            {
                continue;
            }

            composed++;
            _screenshots++;
            shot = new Shot(image, Read(image, info, project, noCamera ? null : camera ?? TestMedia.Camera));
            if (expectPicture ? shot.Reading.Serial == LastScene : composed >= 2)
            {
                break;
            }
        }

        _wasForeground |= NativeMethods.IsForeground(_handle);
        return shot;
    }

    private int ExpectedCamera(int screenFrame) => _folder!.ExpectedCamera(screenFrame, _project);

    private int PositionFrame => (int)Math.Round(_engine!.Position * Fps);

    private bool SeekTo(int frame)
    {
        var engine = _engine!;
        engine.Seek(TestFolder.TimeOf(frame, 0.5));
        var ok = engine.WaitForIdle(TimeSpan.FromSeconds(6));
        return ok && PositionFrame == frame;
    }

    /// <summary>
    /// The four things every settled screenshot has to show: the picture pixel for pixel, filling
    /// the rectangle XAML gives the panel, the right frames, and the XAML element over it.
    /// </summary>
    private bool CheckShot(string what, int frame, int tolerance = 1, string? save = null)
    {
        var settled = WaitSettled(TimeSpan.FromSeconds(4), out var afterMilliseconds);
        var info = OnUi(Info);
        var shot = Capture(info);
        if (shot is null)
        {
            return _report.Check(what, false, "no screenshot was delivered");
        }

        if (save is not null)
        {
            PngWriter.WriteBgra(Path.Combine(_output, save), shot.Image.Pixels, shot.Image.Width, shot.Image.Height);
        }

        var reading = shot.Reading;
        var camera = ExpectedCamera(frame);
        var ok = settled &&
            reading.AllCrisp &&
            reading.Width == info.PixelWidth && reading.Height == info.PixelHeight &&
            reading.FillsRectangle(tolerance) &&
            reading.HandleVisible &&
            reading.Frames.Screen == frame && reading.Frames.Camera == camera;
        return _report.Check(
            what,
            ok,
            $"panel {info.Width:0.##}x{info.Height:0.##} at scale {info.ScaleX:0.###} = {info.PixelWidth}x{info.PixelHeight} px{(settled ? $", drawn at that size after {afterMilliseconds:0} ms" : ", NOT drawn at that size")}; screenshot: {reading.Describe()}; wanted screen {frame}, camera {camera}{(reading.Serial == LastScene ? string.Empty : $"; THE LAST SCENE DRAWN, {LastScene}, DID NOT REACH THE SCREEN")}");
    }

    // ---------------------------------------------------------------------------------------
    // The checks
    // ---------------------------------------------------------------------------------------

    private void PixelExact()
    {
        SeekTo(100);
        WaitSettled(TimeSpan.FromSeconds(4), out _);
        var info = OnUi(Info);
        var shot = Capture(info);
        if (shot is null)
        {
            _report.Check("a screenshot of the window was delivered", false);
            return;
        }

        PngWriter.WriteBgra(Path.Combine(_output, "window.png"), shot.Image.Pixels, shot.Image.Width, shot.Image.Height);
        var reading = shot.Reading;
        _report.Line($"panel {info.Width:0.##}x{info.Height:0.##} effective pixels, composition scale {info.ScaleX:0.###}x{info.ScaleY:0.###}; screenshot {shot.Image.Width}x{shot.Image.Height} (out\\window.png): {reading.Describe()}");
        _report.Check("the picture is on screen pixel for pixel at the display's scale (all four corner markers exact, 1 px stripes at full contrast)", reading.AllCrisp);
        _report.Check("the swap chain is the panel's size in physical pixels", reading.Width == info.PixelWidth && reading.Height == info.PixelHeight && Math.Abs(info.ScaleX - info.RasterScale) < 1e-6, $"{reading.Width}x{reading.Height} on screen, {info.PixelWidth}x{info.PixelHeight} wanted");
        _report.Check("it fills the panel's rectangle: its corners are on the corners XAML layout gives the panel", reading.FillsRectangle());
        _report.Check("it shows the right frames of both clips", reading.Frames.Screen == 100 && reading.Frames.Camera == ExpectedCamera(100), reading.Frames.ToString());
        _report.Check("the XAML elements placed over the panel are visible on top of the picture", reading.HandleVisible && reading.TopLeftSquare is not null && reading.BottomRightSquare is not null, $"centre element {reading.HandleCoverage:0%} drawn, {reading.HandleSpill:0%} outside its place");

        // Steps and seeks reach the screen too.
        var wrong = new List<string>();
        foreach (var frame in new[] { 101, 102, 250, 3, 359, 0, 180 })
        {
            if (!SeekTo(frame))
            {
                wrong.Add($"{frame}: the seek did not land");
                continue;
            }

            var next = Capture(info);
            if (next is null || next.Reading.Frames.Screen != frame || next.Reading.Frames.Camera != ExpectedCamera(frame) || !next.Reading.AllCrisp)
            {
                wrong.Add($"{frame}: {next?.Reading.Describe() ?? "no screenshot"}{(next is null || next.Reading.Serial == LastScene ? string.Empty : $"; the last scene drawn is {LastScene}")}");
            }
        }

        _report.Check("after each of 7 seeks and steps the window shows the requested frames", wrong.Count == 0, string.Join(" | ", wrong.Take(3)));
    }

    private void Playback()
    {
        var engine = _engine!;
        SeekTo(30);
        var info = OnUi(Info);
        var firstScene = LastScene;
        _capture!.Drain();
        engine.Play();
        var watch = Stopwatch.StartNew();
        var readings = new List<PanelReading>();
        var seconds = _quick ? 1.5 : 3.0;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            if (_capture.Next(TimeSpan.FromMilliseconds(250)) is { } image)
            {
                _screenshots++;
                var reading = Read(image, info, _project, TestMedia.Camera);

                // A screenshot composed before the seek had landed can still arrive here.
                if (reading.Serial < 0 || reading.Serial >= firstScene)
                {
                    readings.Add(reading);
                }
            }
        }

        engine.Pause();
        engine.WaitForIdle(TimeSpan.FromSeconds(5));
        var readable = readings.Where(r => r.AllFound && r.Frames.Screen != FrameCode.Unreadable).ToList();
        var backwards = 0;
        var apart = 0;
        for (var index = 0; index < readable.Count; index++)
        {
            backwards += index > 0 && readable[index].Frames.Screen < readable[index - 1].Frames.Screen ? 1 : 0;
            var expected = ExpectedCamera(readable[index].Frames.Screen);
            var got = readable[index].Frames.Camera;
            apart = Math.Max(apart, expected == FrameCode.Unreadable || got == FrameCode.Unreadable ? (expected == got ? 0 : 1) : Math.Abs(got - expected));
        }

        var distinct = readable.Select(r => r.Frames.Screen).Distinct().Count();
        var soft = readings.Count(r => !r.AllCrisp);
        var uncovered = readings.Count(r => !r.HandleVisible);
        var misplaced = readings.Count(r => !r.FillsRectangle());
        _report.Check(
            $"playing for {seconds:0.#} s: every screenshot shows frames in order with both clips right, pixel for pixel, with the XAML element on top",
            readable.Count >= 20 && readable.Count >= readings.Count - 2 && backwards == 0 && apart <= 1 && soft == 0 && uncovered == 0 && misplaced == 0 && distinct >= seconds * Fps * 0.5,
            $"{readings.Count} screenshots, {readable.Count} readable, {distinct} distinct frames {(readable.Count == 0 ? -1 : readable[0].Frames.Screen)}..{(readable.Count == 0 ? -1 : readable[^1].Frames.Screen)}, {backwards} went back, clips at most {apart} apart, {soft} not pixel-exact, {uncovered} without the XAML element, {misplaced} off the panel's rectangle");
        CheckShot("after the pause the window shows the frame Position names", PositionFrame);
    }

    private void Resize()
    {
        var engine = _engine!;
        SeekTo(140);
        var original = OnUi(() => _window.AppWindow.Size);
        var sizes = new (int Width, int Height)[]
        {
            (original.Width - 300, original.Height - 140),
            (original.Width, original.Height),
            (original.Width - 501, original.Height - 263),
            (original.Width - 99, original.Height - 89),
            (original.Width, original.Height),
        };
        foreach (var (width, height) in sizes)
        {
            ResizeWindow(width, height);
            CheckShot($"window resized to {width}x{height} while paused: the panel follows", 140);
        }

        // An edge dragged in and out again while playing: 60 size changes a 60th of a second apart.
        _capture!.Drain();
        engine.Play();
        Thread.Sleep(300);
        var info = OnUi(Info);
        var readings = new List<PanelReading>();
        const int steps = 60;
        var pace = Stopwatch.StartNew();
        for (var step = 1; step <= steps; step++)
        {
            var phase = Math.Sin(Math.PI * step / steps);
            ResizeWindow(original.Width - (int)Math.Round(260 * phase), original.Height - (int)Math.Round(150 * phase), wait: false);
            while (pace.Elapsed.TotalMilliseconds < step * 1000.0 / 60)
            {
                if (_capture.Next(TimeSpan.FromMilliseconds(4)) is { } image)
                {
                    _screenshots++;
                    readings.Add(Read(image, info, _project, TestMedia.Camera));
                }
            }
        }

        ResizeWindow(original.Width, original.Height);
        Thread.Sleep(300);
        engine.Pause();
        engine.WaitForIdle(TimeSpan.FromSeconds(5));
        var found = readings.Where(r => r.AllFound).ToList();
        var inStep = found.Count(r => r.FillsRectangle());
        var crisp = found.Count(r => r.AllCrisp);
        _report.Note($"window edge dragged while playing ({steps} size changes): {readings.Count} screenshots, the swap chain's corners on the panel's corners in {inStep} of the {found.Count} where all markers were found, pixel-exact in {crisp}");
        CheckShot("after the drag the panel is right again at the window's size", PositionFrame);
    }

    private void ResizeWindow(int width, int height, bool wait = true)
    {
        if (wait)
        {
            OnUi(() =>
            {
                _window.AppWindow.Resize(new SizeInt32(width, height));
                return 0;
            });
        }
        else
        {
            _dispatcher.TryEnqueue(() => _window.AppWindow.Resize(new SizeInt32(width, height)));
        }
    }

    private void Scale()
    {
        SeekTo(300);

        // A fixed rectangle in whole physical pixels, at the host's corner. A RenderTransform on the
        // host then changes the panel's composition scale the way another display scale would,
        // without moving the panel off the pixel grid: the transform's origin is the panel's corner.
        // Large enough that at half the scale the frame numbers can still be read next to the
        // corner markers, which do not shrink with the picture.
        const int baseWidth = 960;
        const int baseHeight = 540;
        var client = OnUi(() =>
        {
            var scale = _window.Content.XamlRoot.RasterizationScale;
            var host = _window.HostElement;
            host.HorizontalAlignment = HorizontalAlignment.Left;
            host.VerticalAlignment = VerticalAlignment.Top;
            host.Width = baseWidth / scale;
            host.Height = baseHeight / scale;
            return _window.AppWindow.ClientSize;
        });
        CheckShot($"panel given a fixed {baseWidth}x{baseHeight} px rectangle", 300);

        foreach (var factor in new[] { 1.25, 0.75, 4.0 / 3.0, 0.5, 1.0 })
        {
            if (HostMargin + (baseWidth * factor) > client.Width - 8 || HostMargin + (baseHeight * factor) > client.Height - 8)
            {
                _report.Note($"scale x{factor:0.##} skipped: {baseWidth * factor:0}x{baseHeight * factor:0} px does not fit in the window's {client.Width}x{client.Height} client area");
                continue;
            }

            var before = OnUi(Info);
            OnUi(() =>
            {
                _window.HostElement.RenderTransform = factor == 1.0 ? null : new ScaleTransform { ScaleX = factor, ScaleY = factor };
                return 0;
            });
            WaitSettled(TimeSpan.FromSeconds(4), out _);
            var after = OnUi(Info);
            var scaled = Math.Abs(after.ScaleX - (after.RasterScale * factor)) < 1e-3 && after.PixelWidth == (int)Math.Round(baseWidth * factor) && after.PixelHeight == (int)Math.Round(baseHeight * factor);
            var shown = CheckShot($"simulated scale change x{factor:0.##} (composition scale {before.ScaleX:0.###} -> {after.ScaleX:0.###}): the buffers follow and the picture is pixel-exact again", 300, tolerance: 2);
            if (shown && !scaled)
            {
                _report.Check($"scale x{factor:0.##}: the panel reports the new composition scale", false, $"composition scale {after.ScaleX:0.###}, {after.PixelWidth}x{after.PixelHeight} px");
            }
        }

        OnUi(() =>
        {
            var host = _window.HostElement;
            host.RenderTransform = null;
            host.Width = double.NaN;
            host.Height = double.NaN;
            host.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.VerticalAlignment = VerticalAlignment.Stretch;
            return 0;
        });
        CheckShot("back to the stretched panel", 300);
    }

    private void BlockedUiThread()
    {
        const int blockMilliseconds = 1500;
        var engine = _engine!;
        SeekTo(60);
        var info = OnUi(Info);
        engine.Play();
        Thread.Sleep(700);
        var before = engine.GetDiagnostics().FramesDrawn;
        Thread.Sleep(1000);
        var reference = engine.GetDiagnostics().FramesDrawn - before;

        using var entered = new ManualResetEventSlim(false);
        using var left = new ManualResetEventSlim(false);
        _dispatcher.TryEnqueue(() =>
        {
            entered.Set();
            Thread.Sleep(blockMilliseconds);
            left.Set();
        });
        entered.Wait();
        var start = engine.GetDiagnostics().FramesDrawn;
        var watch = Stopwatch.StartNew();

        // Two screenshots while the UI thread sleeps: do the frames on screen advance?
        Thread.Sleep(200);
        var early = _capture!.Next(TimeSpan.FromMilliseconds(400), fresh: true);
        Thread.Sleep(600);
        var late = _capture.Next(TimeSpan.FromMilliseconds(400), fresh: true);
        var stillBlocked = !left.IsSet;
        var drawn = engine.GetDiagnostics().FramesDrawn - start;
        var seconds = watch.Elapsed.TotalSeconds;
        left.Wait();
        engine.Pause();
        engine.WaitForIdle(TimeSpan.FromSeconds(5));

        var first = early is null ? null : Read(early, info, _project, TestMedia.Camera);
        var second = late is null ? null : Read(late, info, _project, TestMedia.Camera);
        _screenshots += (early is null ? 0 : 1) + (late is null ? 0 : 1);
        _report.Check(
            $"while the UI thread is blocked for {blockMilliseconds} ms the engine keeps drawing and presenting",
            stillBlocked && drawn >= 0.85 * seconds * reference,
            $"{drawn} scenes in {seconds:0.00} s of the block, {reference} in the second before it");
        _report.Check(
            "and the picture on screen keeps advancing",
            first is not null && second is not null && first.Frames.Screen >= 0 && second.Frames.Screen > first.Frames.Screen && first.AllCrisp && second.AllCrisp,
            $"screenshots {(early is null || late is null ? double.NaN : Stopwatch.GetElapsedTime(early.At, late.At).TotalSeconds):0.00} s apart while blocked: screen frame {first?.Frames.Screen} -> {second?.Frames.Screen}");
        CheckShot("after the block and a pause the window shows the frame Position names", PositionFrame);
    }

    private void UnloadAndReload()
    {
        var engine = _engine!;
        SeekTo(200);
        for (var round = 1; round <= 2; round++)
        {
            var info = OnUi(Info);
            OnUi(() =>
            {
                _window.HostElement.Children.Remove(_panel);
                return 0;
            });
            Thread.Sleep(200);
            var drawnBefore = engine.GetDiagnostics().FramesDrawn;
            var landed = SeekTo(200 + (round * 10));
            Thread.Sleep(150);
            var drawnWhileUnloaded = engine.GetDiagnostics().FramesDrawn - drawnBefore;
            var gone = Capture(info, expectPicture: false);
            _report.Check(
                $"round {round}: unloading the panel detaches it: the engine draws nothing more, still follows a seek, and the picture is gone from the window",
                landed && drawnWhileUnloaded == 0 && gone is not null && gone.Reading.Markers.Length == 0,
                $"seek landed {landed}, scenes drawn while unloaded {drawnWhileUnloaded}, screenshot: {gone?.Reading.Describe() ?? "none"}");

            OnUi(() =>
            {
                _window.HostElement.Children.Add(_panel);
                return 0;
            });
            CheckShot($"round {round}: loading it again shows the frame the engine is on now, without another Attach", 200 + (round * 10));
        }
    }

    private void Engines()
    {
        var first = _engine!;
        var firstProject = _project;
        var firstFolder = _folder!;
        SeekTo(220);

        // Another engine, with a project that has no camera, takes the panel over.
        var folder = NewFolder(camera: null);
        var second = OpenEngine(folder);
        second.Seek(TestFolder.TimeOf(77, 0.5));
        second.WaitForIdle(TimeSpan.FromSeconds(6));
        OnUi(() =>
        {
            _panel.Attach(second);
            return 0;
        });
        _engine = second;
        _project = folder.Project;
        _folder = folder;
        WaitSettled(TimeSpan.FromSeconds(4), out _);
        var info = OnUi(Info);
        var firstDrawn = first.GetDiagnostics().FramesDrawn;
        first.Seek(TestFolder.TimeOf(230, 0.5));
        first.WaitForIdle(TimeSpan.FromSeconds(6));
        var shot = Capture(info, noCamera: true);
        _report.Check(
            "Attach with another engine shows that engine's picture, and the first engine no longer draws",
            shot is not null && shot.Reading.AllCrisp && shot.Reading.FillsRectangle() && shot.Reading.Frames.Screen == 77 && first.GetDiagnostics().FramesDrawn == firstDrawn && OnUi(() => ReferenceEquals(_panel.Engine, second)),
            shot?.Reading.Describe() ?? "no screenshot");

        // The engine is disposed while the panel still shows it.
        second.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var deleted = true;
        string? error = null;
        try
        {
            folder.Delete();
        }
        catch (Exception ex)
        {
            deleted = false;
            error = ex.Message;
        }

        Thread.Sleep(200);
        var after = Capture(info, project: firstProject, expectPicture: false);
        _report.Check(
            "disposing an engine while the panel shows it releases the panel's swap chain: the picture is gone and the project folder can be deleted",
            deleted && after is not null && after.Reading.Markers.Length == 0,
            $"{error}; screenshot: {after?.Reading.Describe() ?? "none"}");

        // Detach after the engine is gone, then the first engine again.
        Exception? thrown = null;
        try
        {
            OnUi(() =>
            {
                _panel.Detach();
                _panel.Detach();
                return 0;
            });
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        _report.Check("Detach after the engine was disposed, and Detach twice, do nothing and throw nothing", thrown is null && OnUi(() => _panel.Engine is null), thrown?.ToString());
        _engine = first;
        _project = firstProject;
        _folder = firstFolder;
        OnUi(() =>
        {
            _panel.Attach(first);
            return 0;
        });
        CheckShot("attaching the first engine again shows the frame it is on", 230);

        // Detach leaves the engine running without a surface.
        OnUi(() =>
        {
            _panel.Detach();
            return 0;
        });
        var drawn = first.GetDiagnostics().FramesDrawn;
        var landed = SeekTo(240);
        Thread.Sleep(100);
        _report.Check("Detach stops the drawing and leaves the engine working", landed && first.GetDiagnostics().FramesDrawn == drawn);
        OnUi(() =>
        {
            _panel.Attach(first);
            return 0;
        });
        CheckShot("Attach after Detach shows the current frame", 240);
    }

    private void DeviceLost()
    {
        var engine = _engine!;
        SeekTo(260);
        var before = engine.GetDiagnostics().DeviceRebuilds;
        engine.SimulateDeviceLoss();
        engine.WaitForIdle(TimeSpan.FromSeconds(10));
        var rebuilt = engine.GetDiagnostics().DeviceRebuilds - before;
        CheckShot($"after a simulated device loss ({rebuilt} rebuild) the panel has a swap chain on the new device and shows the same frame", 260);
        _report.Check("the device was rebuilt once", rebuilt == 1, $"{rebuilt}");
        engine.Play();
        Thread.Sleep(400);
        engine.SimulateDeviceLoss();
        Thread.Sleep(700);
        var playing = engine.IsPlaying;
        engine.Pause();
        engine.WaitForIdle(TimeSpan.FromSeconds(5));
        CheckShot($"and after one while playing (still playing afterwards: {playing})", PositionFrame);
    }
}
