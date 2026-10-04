using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>A picture read back from a texture: tightly packed top-down BGRA.</summary>
internal sealed record Picture(byte[] Bgra, int Width, int Height)
{
    public Rgb ColorAt(double x, double y, int radius = 1) => FrameCode.Color(Bgra, Width, Height, x, y, radius);

    public Shown Shown(SceneView view, ClipSpec screen, ClipSpec? camera) => Media.Shown.Read(Bgra, Width, Height, view, screen, camera);
}

/// <summary>
/// A surface without a window: one BGRA texture on the engine's device. It is what the headless
/// checks let the engine draw into, and it counts what the engine asks of it.
/// </summary>
internal sealed class OffscreenSurface : IStudioPreviewSurface
{
    private readonly object _sync = new();
    private int _wantedWidth;
    private int _wantedHeight;
    private ID3D11Texture2D? _texture;
    private int _width;
    private int _height;
    private nint _device;
    private int _presents;
    private int _configures;
    private int _releases;
    private long _lastPresentAt;

    public OffscreenSurface(int width, int height)
    {
        _wantedWidth = width;
        _wantedHeight = height;
    }

    public event EventHandler? Invalidated;

    /// <summary>Frames the engine has drawn and presented here.</summary>
    public int Presents => Volatile.Read(ref _presents);

    public int Configures => Volatile.Read(ref _configures);

    /// <summary>How often the engine told the surface to release its texture.</summary>
    public int Releases => Volatile.Read(ref _releases);

    public long LastPresentAt => Interlocked.Read(ref _lastPresentAt);

    public (int Width, int Height) WantedSize
    {
        get
        {
            lock (_sync)
            {
                return (_wantedWidth, _wantedHeight);
            }
        }
    }

    /// <summary>The size of the texture that exists now; (0, 0) when there is none.</summary>
    public (int Width, int Height) BufferSize
    {
        get
        {
            lock (_sync)
            {
                return _texture is null ? (0, 0) : (_width, _height);
            }
        }
    }

    public bool NeedsConfigure
    {
        get
        {
            lock (_sync)
            {
                return _wantedWidth <= 0 || _wantedHeight <= 0
                    ? _texture is not null
                    : _texture is null || _width != _wantedWidth || _height != _wantedHeight;
            }
        }
    }

    /// <summary>What a window does when it is resized: note the new size and tell the engine.</summary>
    public void Resize(int width, int height)
    {
        lock (_sync)
        {
            _wantedWidth = width;
            _wantedHeight = height;
        }

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Configure(ID3D11Device device)
    {
        Interlocked.Increment(ref _configures);
        lock (_sync)
        {
            if (_texture is not null && (_device != device.NativePointer || _width != _wantedWidth || _height != _wantedHeight))
            {
                _texture.Dispose();
                _texture = null;
            }

            if (_texture is null && _wantedWidth > 0 && _wantedHeight > 0)
            {
                _texture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_wantedWidth,
                    Height = (uint)_wantedHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                });
                _width = _wantedWidth;
                _height = _wantedHeight;
                _device = device.NativePointer;
            }
        }
    }

    public ID3D11Texture2D? AcquireTarget(out int pixelWidth, out int pixelHeight)
    {
        lock (_sync)
        {
            pixelWidth = _width;
            pixelHeight = _height;

            // A reference of its own for the caller, as a swap chain's GetBuffer gives.
            return _texture?.QueryInterface<ID3D11Texture2D>();
        }
    }

    public void Present()
    {
        Interlocked.Exchange(ref _lastPresentAt, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _presents);
    }

    public void ReleaseDeviceResources()
    {
        Interlocked.Increment(ref _releases);
        lock (_sync)
        {
            _texture?.Dispose();
            _texture = null;
            _device = 0;
        }
    }

    /// <summary>Reads the whole texture back, or null when there is none on the engine's current device.</summary>
    public Picture? Read(StudioPreviewEngine engine)
    {
        var graphics = engine.GraphicsDevice;
        lock (graphics.Gate)
        {
            lock (_sync)
            {
                if (_texture is null || _device != graphics.Device.NativePointer)
                {
                    return null;
                }

                return new Picture(graphics.ReadTexture(_texture), _width, _height);
            }
        }
    }
}

/// <summary>Reads small rectangles of a texture with one GPU round trip. The caller holds the device lock.</summary>
internal sealed class RegionReader : IDisposable
{
    private ID3D11Texture2D? _staging;
    private nint _device;
    private int _width;
    private int _height;

    public unsafe byte[][] Read(StudioGraphicsDevice graphics, ID3D11Texture2D texture, ReadOnlySpan<(int X, int Y, int Width, int Height)> regions)
    {
        var width = 1;
        var height = 0;
        foreach (var region in regions)
        {
            width = Math.Max(width, region.Width);
            height += region.Height;
        }

        if (_staging is null || _device != graphics.Device.NativePointer || _width < width || _height < height)
        {
            _staging?.Dispose();
            _width = Math.Max(width, _device == graphics.Device.NativePointer ? _width : 0);
            _height = Math.Max(height, _device == graphics.Device.NativePointer ? _height : 0);
            _device = graphics.Device.NativePointer;
            _staging = graphics.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }

        var top = 0;
        foreach (var region in regions)
        {
            graphics.Context.CopySubresourceRegion(_staging, 0, 0, (uint)top, 0, texture, 0, new Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1));
            top += region.Height;
        }

        var results = new byte[regions.Length][];
        var mapped = graphics.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)mapped.DataPointer;
            top = 0;
            for (var index = 0; index < regions.Length; index++)
            {
                var region = regions[index];
                var pixels = new byte[region.Width * region.Height * 4];
                fixed (byte* destination = pixels)
                {
                    for (var row = 0; row < region.Height; row++)
                    {
                        Buffer.MemoryCopy(source + ((long)(top + row) * mapped.RowPitch), destination + ((long)row * region.Width * 4), region.Width * 4, region.Width * 4);
                    }
                }

                results[index] = pixels;
                top += region.Height;
            }
        }
        finally
        {
            graphics.Context.Unmap(_staging, 0);
        }

        return results;
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _staging = null;
    }
}

/// <summary>One scene the engine drew, as read from its pixels before it was presented.</summary>
/// <param name="At">Stopwatch timestamp of the read.</param>
/// <param name="Corner">The colour of the canvas' top-left corner: the background.</param>
internal readonly record struct Composite(long At, int Screen, int Camera, Rgb Corner, int Width, int Height);

/// <summary>A wait for the engine to draw a scene that shows something. Armed with <see cref="CompositeRecorder.Expect"/>.</summary>
internal sealed class SceneWait(Func<Composite, bool> wanted) : IDisposable
{
    private readonly ManualResetEventSlim _drawn = new(false);
    private long _at;

    /// <summary>The scene has been drawn.</summary>
    public bool IsDrawn => _drawn.IsSet;

    /// <summary>When its pixels were read, which is before it was presented. 0 until then.</summary>
    public long At => Interlocked.Read(ref _at);

    public bool Wait(TimeSpan timeout) => _drawn.Wait(timeout);

    /// <summary>Called by the recorder for each scene, on the render thread.</summary>
    public bool Offer(in Composite scene)
    {
        if (!wanted(scene))
        {
            return false;
        }

        Interlocked.Exchange(ref _at, scene.At);
        _drawn.Set();
        return true;
    }

    public void Dispose() => _drawn.Dispose();
}

/// <summary>
/// Reads the frame-number strips out of every scene the engine draws, through the engine's
/// after-render hook. This is how the checks know what was shown during playback without trusting
/// anything the engine says about itself.
/// </summary>
internal sealed class CompositeRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly RegionReader _reader = new();
    private readonly ClipSpec _screen;
    private readonly ClipSpec? _camera;
    private List<Composite> _composites = [];
    private StudioProject _project;
    private SceneView? _view;
    private SceneWait? _awaited;

    public CompositeRecorder(StudioProject project, ClipSpec screen, ClipSpec? camera)
    {
        _project = project;
        _screen = screen;
        _camera = camera;
    }

    /// <summary>The project the engine is drawing, so the strips are looked for where its layout puts them.</summary>
    public StudioProject Project
    {
        get
        {
            lock (_sync)
            {
                return _project;
            }
        }

        set
        {
            lock (_sync)
            {
                _project = value;
                _view = null;
            }
        }
    }

    /// <summary>The engine's after-render hook. Runs on the render thread with the device lock held.</summary>
    public void OnRender(StudioGraphicsDevice graphics, ID3D11Texture2D target, int width, int height)
    {
        SceneView view;
        lock (_sync)
        {
            if (_view is null || _view.Width != width || _view.Height != height)
            {
                _view = SceneView.Resolve(_project, _screen, _camera, width, height);
            }

            view = _view;
        }

        Span<(int X, int Y, int Width, int Height)> regions = stackalloc (int, int, int, int)[3];
        regions[0] = view.ScreenMap is { } screenMap ? FrameCode.Bounds(_screen, screenMap, width, height) : (0, 0, 1, 1);
        regions[1] = view.CameraMap is { } cameraMap && _camera is not null ? FrameCode.Bounds(_camera, cameraMap, width, height) : (0, 0, 1, 1);
        regions[2] = (0, 0, Math.Min(4, width), Math.Min(4, height));
        var pixels = _reader.Read(graphics, target, regions);

        var screen = view.ScreenMap is { } s
            ? FrameCode.Decode(pixels[0], regions[0].Width, regions[0].Height, _screen, s.Offset(-regions[0].X, -regions[0].Y))
            : FrameCode.Unreadable;
        var camera = view.CameraMap is { } c && _camera is not null
            ? FrameCode.Decode(pixels[1], regions[1].Width, regions[1].Height, _camera, c.Offset(-regions[1].X, -regions[1].Y))
            : FrameCode.Unreadable;
        var corner = FrameCode.Color(pixels[2], regions[2].Width, regions[2].Height, 1, 1, 1);
        var scene = new Composite(Stopwatch.GetTimestamp(), screen, camera, corner, width, height);
        lock (_sync)
        {
            _composites.Add(scene);
            if (_awaited is { } wait && wait.Offer(scene))
            {
                _awaited = null;
            }
        }
    }

    /// <summary>
    /// Arms a wait for the next scene that <paramref name="wanted"/> accepts. Arm it before the
    /// call that is to bring the scene about, and hand it back to <see cref="Forget"/> before it
    /// is disposed.
    /// </summary>
    public SceneWait Expect(Func<Composite, bool> wanted)
    {
        var wait = new SceneWait(wanted);
        lock (_sync)
        {
            _awaited = wait;
        }

        return wait;
    }

    /// <summary>Ends a wait. When this returns the render thread no longer refers to it.</summary>
    public void Forget(SceneWait wait)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_awaited, wait))
            {
                _awaited = null;
            }
        }
    }

    /// <summary>Everything drawn since the last call.</summary>
    public List<Composite> Drain()
    {
        lock (_sync)
        {
            var result = _composites;
            _composites = [];
            return result;
        }
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>What the engine's events said, and when.</summary>
internal sealed class EventLog
{
    private readonly object _sync = new();
    private readonly StudioPreviewEngine _engine;
    private readonly List<(long At, double Position)> _positions = [];
    private readonly List<(long At, bool IsPlaying, double Position)> _playing = [];
    private readonly List<string> _failures = [];
    private long _lastEventAt;

    public EventLog(StudioPreviewEngine engine)
    {
        _engine = engine;
        engine.PositionChanged += OnPositionChanged;
        engine.IsPlayingChanged += OnIsPlayingChanged;
        engine.Failed += OnFailed;
    }

    public int PositionEvents
    {
        get
        {
            lock (_sync)
            {
                return _positions.Count;
            }
        }
    }

    public int PlayingEvents
    {
        get
        {
            lock (_sync)
            {
                return _playing.Count;
            }
        }
    }

    public int FailedEvents
    {
        get
        {
            lock (_sync)
            {
                return _failures.Count;
            }
        }
    }

    /// <summary>Stopwatch timestamp of the latest event of any kind; 0 before the first.</summary>
    public long LastEventAt => Interlocked.Read(ref _lastEventAt);

    public List<(long At, double Position)> Positions()
    {
        lock (_sync)
        {
            return [.. _positions];
        }
    }

    /// <summary>Each <c>IsPlayingChanged</c>, with what its handler read from <c>IsPlaying</c> and <c>Position</c>.</summary>
    public List<(long At, bool IsPlaying, double Position)> Playing()
    {
        lock (_sync)
        {
            return [.. _playing];
        }
    }

    public List<string> Failures()
    {
        lock (_sync)
        {
            return [.. _failures];
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _positions.Clear();
            _playing.Clear();
        }
    }

    /// <summary>
    /// Waits, briefly, until at least <paramref name="count"/> <c>PositionChanged</c> events have
    /// been handled. The events are raised on the engine's event thread, which gets to the one
    /// for a landing a moment after the picture was drawn. Returns the number handled so far.
    /// </summary>
    public int WaitForPositionEvents(int count, double seconds = 1)
    {
        var start = Stopwatch.GetTimestamp();
        var spinner = default(SpinWait);
        while (PositionEvents < count && Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
        {
            spinner.SpinOnce();
        }

        return PositionEvents;
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var position = _engine.Position;
        Interlocked.Exchange(ref _lastEventAt, now);
        lock (_sync)
        {
            _positions.Add((now, position));
        }
    }

    private void OnIsPlayingChanged(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var isPlaying = _engine.IsPlaying;
        var position = _engine.Position;
        Interlocked.Exchange(ref _lastEventAt, now);
        lock (_sync)
        {
            _playing.Add((now, isPlaying, position));
        }
    }

    private void OnFailed(object? sender, StudioPreviewFailedEventArgs e)
    {
        Interlocked.Exchange(ref _lastEventAt, Stopwatch.GetTimestamp());
        lock (_sync)
        {
            _failures.Add($"{e.Message} ({e.Exception?.GetType().Name}: {e.Exception?.Message})");
        }
    }
}

/// <summary>The last lines of an engine's trace, kept for the moment a check does not hold.</summary>
internal sealed class TraceLog(int capacity)
{
    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        lock (_lines)
        {
            if (_lines.Count >= capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
        }
    }

    public string[] Lines()
    {
        lock (_lines)
        {
            return [.. _lines];
        }
    }
}

/// <summary>An opened preview with everything a check needs around it.</summary>
internal sealed class Session : IAsyncDisposable
{
    private const string FailedOpenTraceKey = "StudioPreviewCheck.Trace";

    // Over the whole run: the previews opened, and the traces of those that needed a second attempt.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string[]> SecondAttempts = new();
    private static int _opened;

    private readonly Dictionary<(int, int), SceneView> _views = [];
    private readonly bool _ownsEngine;
    private StudioProject _project;
    private bool _disposed;

    private Session(TestFolder folder, StudioPreviewEngine engine, OffscreenSurface? surface, TraceLog trace, bool ownsEngine = true)
    {
        Folder = folder;
        Engine = engine;
        Surface = surface;
        Trace = trace;
        _ownsEngine = ownsEngine;
        _project = folder.Project;
        Events = new EventLog(engine);
        Recorder = new CompositeRecorder(folder.Project, folder.Screen, folder.Camera);
        engine.AfterRender = Recorder.OnRender;
    }

    public TestFolder Folder { get; }

    public StudioPreviewEngine Engine { get; }

    /// <summary>The offscreen surface the engine draws into, when one is attached.</summary>
    public OffscreenSurface? Surface { get; private set; }

    public EventLog Events { get; }

    public CompositeRecorder Recorder { get; }

    /// <summary>What the players reported and what the engine asked of them, most recently.</summary>
    public TraceLog Trace { get; }

    /// <summary>Milliseconds the factory took to open the preview.</summary>
    public double OpenMilliseconds { get; private init; }

    /// <summary>The project last given to the engine.</summary>
    public StudioProject Project => _project;

    public double Position => Engine.Position;

    /// <summary>The frame number the engine's position stands for.</summary>
    public int PositionFrame => (int)Math.Round(Engine.Position * TestMedia.Fps);

    /// <summary>When the last <see cref="SeekToTime"/> made its call.</summary>
    public long LastSoughtAt { get; private set; }

    /// <summary>When the scene that call waited for was drawn; 0 when none had to be drawn.</summary>
    public long LastDrawnAt { get; private set; }

    /// <summary>The trace of an engine that did not open, or null. <see cref="OpenEngine"/> leaves it on what it throws.</summary>
    public static string[]? TraceOfFailedOpen(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[FailedOpenTraceKey] is string[] lines)
            {
                return lines;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens an engine the way the editor does, through the factory contract, with a trace of
    /// its own. Every preview of the tool is opened here, so that one that does not open leaves
    /// behind what its players reported until then: it goes with the exception, which is the
    /// exception the editor would get. One that needed a second attempt is noted with its trace.
    /// </summary>
    public static StudioPreviewEngine OpenEngine(StudioPreviewOptions options, TestFolder folder, out TraceLog trace)
    {
        var log = trace = new TraceLog(600);
        IStudioPreviewFactory factory = new StudioPreviewFactory(options with { Trace = log.Add });
        StudioPreviewEngine engine;
        try
        {
            engine = (StudioPreviewEngine)factory.OpenAsync(folder.Project, folder.Events, folder.Paths).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ex.Data[FailedOpenTraceKey] = log.Lines();
            throw;
        }

        NoteOpened(engine, log, firstAttemptMadeToFail: options.DevicesLostWhileOpening > 0 || options.PlayersFailedWhileOpening > 0);
        return engine;
    }

    /// <summary>Counts a preview that opened, and keeps its trace when it needed a second attempt that no check asked for.</summary>
    private static void NoteOpened(StudioPreviewEngine engine, TraceLog trace, bool firstAttemptMadeToFail)
    {
        Interlocked.Increment(ref _opened);
        if (engine.OpenAttempt > 1 && !firstAttemptMadeToFail)
        {
            SecondAttempts.Enqueue(trace.Lines());
        }
    }

    /// <summary>
    /// Says how many previews of this run opened only at the second attempt, without a check
    /// having made the first one fail, and leaves the trace of each in the run's failures folder.
    /// It is reported and not judged: the engine did what it is meant to do. What made the first
    /// attempt fail is in the trace.
    /// </summary>
    public static void ReportSecondAttempts(Report report, string failuresDirectory)
    {
        var traces = SecondAttempts.ToArray();
        var text = $"{Volatile.Read(ref _opened)} previews were opened in this run. {traces.Length} of them opened at the second attempt, the first having failed in a way that may pass: a graphics device lost, or a player that failed after every player had handed over a frame. (The previews of the checks that make that happen are not counted.)";
        if (traces.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(failuresDirectory);
                for (var index = 0; index < traces.Length; index++)
                {
                    File.WriteAllLines(Path.Combine(failuresDirectory, $"second-attempt-{index + 1}-trace.txt"), traces[index]);
                }

                text += $" [traces of both attempts in {DisplayPath(failuresDirectory)}\\second-attempt-*-trace.txt]";
            }
            catch (Exception ex)
            {
                text += $" [no traces: {ex.Message}]";
            }
        }

        report.Note(text);
    }

    /// <summary>
    /// Writes the trace of a preview that did not open into a run's failures folder, and returns
    /// the words that say so in the report: nothing when the exception carries no trace.
    /// </summary>
    public static string DumpFailedOpen(Exception exception, string failuresDirectory, string name)
    {
        if (TraceOfFailedOpen(exception) is not { } lines)
        {
            return string.Empty;
        }

        try
        {
            Directory.CreateDirectory(failuresDirectory);
            File.WriteAllLines(Path.Combine(failuresDirectory, $"{name}-trace.txt"), lines);
            return $" [trace of the open in {DisplayPath(failuresDirectory)}\\{name}-trace.txt]";
        }
        catch (Exception ex)
        {
            return $" [no trace: {ex.Message}]";
        }
    }

    public static Session Open(StudioPreviewOptions options, TestFolder folder, int width = 1280, int height = 720, bool attach = true)
    {
        var watch = Stopwatch.StartNew();
        var engine = OpenEngine(options, folder, out var trace);
        var elapsed = watch.Elapsed.TotalMilliseconds;
        OffscreenSurface? surface = null;
        if (attach)
        {
            surface = new OffscreenSurface(width, height);
            engine.AttachSurface(surface);
        }

        var session = new Session(folder, engine, surface, trace) { OpenMilliseconds = elapsed };
        engine.WaitForIdle(TimeSpan.FromSeconds(5));
        return session;
    }

    /// <summary>
    /// Puts the same instruments around an engine that somebody else opened and will dispose: the
    /// editor session's. Closing this then leaves the engine and the folder alone.
    /// </summary>
    /// <param name="project">The project that engine is drawing.</param>
    public static Session Adopt(TestFolder folder, StudioPreviewEngine engine, StudioProject project, TraceLog trace, int width = 1280, int height = 720)
    {
        NoteOpened(engine, trace, firstAttemptMadeToFail: false);
        var surface = new OffscreenSurface(width, height);
        var session = new Session(folder, engine, surface, trace, ownsEngine: false);
        session.Follow(project);
        engine.AttachSurface(surface);
        engine.WaitForIdle(TimeSpan.FromSeconds(5));
        return session;
    }

    /// <summary>Gives the engine another surface in place of the current one.</summary>
    public OffscreenSurface Attach(int width, int height)
    {
        var surface = new OffscreenSurface(width, height);
        Engine.AttachSurface(surface);
        Surface = surface;
        return surface;
    }

    public void Detach()
    {
        if (Surface is { } surface)
        {
            Engine.DetachSurface(surface);
            Surface = null;
        }
    }

    public void Update(StudioProject project)
    {
        Follow(project);
        Engine.UpdateProject(project);
    }

    /// <summary>The engine was given another project by somebody else: its strips are looked for where that layout puts them.</summary>
    public void Follow(StudioProject project)
    {
        _project = project;
        _views.Clear();
        Recorder.Project = project;
    }

    public bool WaitForIdle(double seconds = 5) => Engine.WaitForIdle(TimeSpan.FromSeconds(seconds));

    public SceneView View(int width, int height)
    {
        if (!_views.TryGetValue((width, height), out var view))
        {
            view = SceneView.Resolve(_project, Folder.Screen, Folder.Camera, width, height);
            _views[(width, height)] = view;
        }

        return view;
    }

    public Picture? ReadPicture() => Surface?.Read(Engine);

    /// <summary>Which frames the surface shows now, read from its pixels.</summary>
    public Shown ReadShown()
    {
        var picture = ReadPicture();
        return picture is null
            ? new Shown(FrameCode.Unreadable, FrameCode.Unreadable)
            : picture.Shown(View(picture.Width, picture.Height), Folder.Screen, Folder.Camera);
    }

    /// <summary>Which frame the texture a clip's player copies into holds, read from its pixels.</summary>
    public int ReadClipFrame(int clip)
    {
        var pixels = Engine.ReadClipTexture(clip, out var width, out var height);
        if (pixels is null)
        {
            return FrameCode.Unreadable;
        }

        var spec = clip == 0 ? Folder.Screen : Folder.Camera!;
        return FrameCode.Decode(pixels, width, height, spec, ClipMap.Scaled(spec, width, height));
    }

    public int ExpectedCamera(int screenFrame) => Folder.ExpectedCamera(screenFrame, _project);

    /// <summary>
    /// Seeks to a frame and waits until a scene that shows it on both clips has been drawn and the
    /// engine has nothing left to do. Returns false when the picture did not get there in time.
    /// </summary>
    /// <param name="latencyMilliseconds">From the call to the scene drawn, or 0 when none had to be drawn.</param>
    /// <param name="fraction">How far into the frame the requested time lies.</param>
    public bool SeekTo(int frame, out double latencyMilliseconds, double fraction = 0.5, double timeoutSeconds = 6) =>
        SeekToTime(TestFolder.TimeOf(frame, fraction), frame, out latencyMilliseconds, timeoutSeconds);

    public bool SeekTo(int frame) => SeekTo(frame, out _);

    /// <summary>
    /// The same for any source time: seeks to it and waits for a scene that shows
    /// <paramref name="expectedFrame"/>. The position is no use for this: the engine reports the
    /// frame from the moment it is asked for. Without a surface nothing is drawn, and the wait is
    /// for the engine to come to rest; the caller then reads the players' textures.
    /// </summary>
    /// <param name="waitForIdle">
    /// Also wait until the engine has nothing left to do. Without it the call returns as soon as
    /// the picture is drawn, which is when a caller that steps again at once would go on.
    /// </param>
    public bool SeekToTime(double sourceTime, int expectedFrame, out double latencyMilliseconds, double timeoutSeconds = 6, bool waitForIdle = true)
    {
        latencyMilliseconds = 0;
        LastDrawnAt = 0;
        if (Surface is null || PositionFrame == expectedFrame)
        {
            // Nothing to see, or nothing to draw: the players are on the frame already.
            LastSoughtAt = Stopwatch.GetTimestamp();
            Engine.Seek(sourceTime);
            return WaitForIdle(timeoutSeconds);
        }

        var camera = ExpectedCamera(expectedFrame);
        using var scene = Recorder.Expect(c => c.Screen == expectedFrame && c.Camera == camera);
        try
        {
            var start = Stopwatch.GetTimestamp();
            LastSoughtAt = start;
            Engine.Seek(sourceTime);

            // An engine that comes to rest without having drawn the scene will not draw it any more.
            while (!scene.Wait(TimeSpan.FromMilliseconds(2)) && !Engine.IsIdle && Stopwatch.GetElapsedTime(start).TotalSeconds < timeoutSeconds)
            {
            }

            if (scene.IsDrawn)
            {
                LastDrawnAt = scene.At;
                latencyMilliseconds = Stopwatch.GetElapsedTime(start, scene.At).TotalMilliseconds;
                return !waitForIdle || WaitForIdle(timeoutSeconds);
            }

            // Not drawn after the call. The picture may have shown the frame before it.
            return WaitForIdle(timeoutSeconds) && ReadShown() == new Shown(expectedFrame, camera);
        }
        finally
        {
            Recorder.Forget(scene);
        }
    }

    /// <summary>
    /// Writes the surface's picture and both players' textures as PNG files, and the engine's
    /// trace as text, for a look at what went wrong.
    /// </summary>
    /// <param name="directory">The run's own folder under <c>out\failures</c>.</param>
    public string Dump(string directory, string name)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, name + "-trace.txt"), Trace.Lines());
            if (ReadPicture() is { } picture)
            {
                PngWriter.WriteBgra(Path.Combine(directory, name + "-picture.png"), picture.Bgra, picture.Width, picture.Height);
            }

            for (var clip = 0; clip < (Folder.Camera is null ? 1 : 2); clip++)
            {
                if (Engine.ReadClipTexture(clip, out var width, out var height) is { } pixels)
                {
                    PngWriter.WriteBgra(Path.Combine(directory, $"{name}-{(clip == 0 ? "screen" : "camera")}.png"), pixels, width, height);
                }
            }

            return $" [pictures and trace in {DisplayPath(directory)}\\{name}-*]";
        }
        catch (Exception ex)
        {
            return $" [no pictures: {ex.Message}]";
        }
    }

    /// <summary>A run's failures folder as a report names it: from the <c>out</c> folder on.</summary>
    public static string DisplayPath(string failuresDirectory) =>
        Path.Combine("out", "failures", Path.GetFileName(failuresDirectory));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsEngine)
        {
            await Engine.DisposeAsync().ConfigureAwait(false);
        }

        Recorder.Dispose();
        if (_ownsEngine)
        {
            Folder.Dispose();
        }
    }

    /// <summary>
    /// Disposes the engine and deletes the project folder, for checks that have no reason to look
    /// at either. An adopted engine and its folder are left to whoever owns them.
    /// </summary>
    public void Close() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
