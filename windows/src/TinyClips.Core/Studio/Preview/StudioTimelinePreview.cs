using System.Diagnostics;
using NAudio.Wave;
using TinyClips.Core.Studio.Rendering;
using Vortice.Direct3D11;

namespace TinyClips.Core.Studio.Preview;

/// <summary>Project-clock preview using the same decoded composition and PCM as the exporter.</summary>
public sealed class StudioTimelinePreview : IStudioSurfacePreview
{
    private readonly object _commands = new();
    private readonly object _surfaceGate = new();
    private readonly object _soundGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _close = new();
    private readonly StudioProjectPaths _paths;
    private readonly StudioPreviewOptions _options;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private StudioProject _project;
    private double _duration;
    private readonly double _fps;
    private double _position;
    private bool _playing;
    private volatile bool _disposed;
    private volatile bool _failed;
    private Task? _disposeTask;
    private long _revision;
    private IStudioPreviewSurface? _surface;
    private bool _configured;
    private bool _redraw = true;
    private StudioGraphicsDevice? _graphics;
    private StudioTimelineRenderer? _renderer;
    private WaveOut? _wave;
    private Exception? _audioFailure;

    private StudioTimelinePreview(StudioProject project, StudioProjectPaths paths, StudioPreviewOptions options)
    {
        _project = project;
        _paths = paths;
        _options = options;
        _duration = StudioTimelineProjection.Duration(project);
        _fps = StudioRenderingMath.FrameRate(project.Sources.Screen.FrameRate).FramesPerSecond;
        _worker = StudioWorker.Run("Tiny Clips Studio timeline", Run, _close.Token);
    }

    internal static async Task<StudioTimelinePreview> OpenAsync(StudioProject project, StudioProjectPaths paths,
        StudioPreviewOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preview = new StudioTimelinePreview(project, paths, options);
        try
        {
            await preview._ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return preview;
        }
        catch
        {
            await preview.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public double Position { get { lock (_commands) return _position; } }
    public bool IsPlaying { get { lock (_commands) return _playing; } }
    public event EventHandler? PositionChanged;
    public event EventHandler? IsPlayingChanged;
    public event EventHandler<StudioPreviewFailedEventArgs>? Failed;

    public void UpdateProject(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.SchemaVersion < 2) throw new ArgumentException("Reopen the preview to switch to source time.", nameof(project));
        var duration = StudioTimelineProjection.Duration(project);
        lock (_commands)
        {
            if (_disposed || _failed) return;
            _project = project;
            _duration = duration;
            _position = Clamp(_position);
            _revision++;
        }
        PauseSound();
        Wake();
    }

    public void Seek(double sourceTime)
    {
        if (!double.IsFinite(sourceTime)) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        lock (_commands)
        {
            if (_disposed || _failed) return;
            _position = Clamp(sourceTime);
            _revision++;
        }
        PauseSound();
        Wake();
    }

    public void Play()
    {
        lock (_commands)
        {
            if (_disposed || _failed || _playing) return;
            _playing = true;
            _revision++;
        }
        Wake();
    }

    public void Pause()
    {
        lock (_commands)
        {
            if (_disposed || _failed || !_playing) return;
            _playing = false;
            _revision++;
        }
        PauseSound();
        // A frame already drawing must not appear after Pause returns.
        lock (_surfaceGate) { }
        Wake();
    }

    public void SetPlaybackRate(double rate)
    {
        if (rate != 1) throw new ArgumentOutOfRangeException(nameof(rate), "Timeline playback applies each clip's speed itself.");
    }

    public void AttachSurface(IStudioPreviewSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_surfaceGate)
        {
            if (_disposed || _failed || ReferenceEquals(surface, _surface)) return;
            ReleaseSurface();
            _surface = surface;
            surface.Invalidated += Invalidated;
            _redraw = true;
        }
        Wake();
    }

    public void DetachSurface(IStudioPreviewSurface surface)
    {
        lock (_surfaceGate)
            if (ReferenceEquals(surface, _surface)) ReleaseSurface();
    }

    private void Invalidated(object? sender, EventArgs args)
    {
        lock (_surfaceGate) _redraw = true;
        Wake();
    }

    private void ReleaseSurface()
    {
        if (_surface is not { } surface) return;
        surface.Invalidated -= Invalidated;
        if (_graphics is { } graphics)
        {
            lock (graphics.Gate)
            {
                graphics.Context.Flush();
                surface.ReleaseDeviceResources();
            }
        }
        _surface = null;
        _configured = false;
    }

    private double Clamp(double time) => Math.Clamp(Math.Floor(time * _fps + 1e-9) / _fps, 0, Math.Max(0, (Math.Ceiling(_duration * _fps) - 1) / _fps));

    private bool Run()
    {
        StudioTimelineAudio? audio = null;
        IDisposable? startup = null;
        try
        {
            startup = StudioMediaFoundation.Startup();
            var graphics = StudioGraphicsDevice.Create(_options.SoftwareDevice);
            lock (_surfaceGate) _graphics = graphics;
            StudioProject? applied = null;
            var revision = -1L;
            var origin = 0d;
            var started = Stopwatch.GetTimestamp();
            var lastFrame = -1L;
            var lastPlaying = false;
            while (!_close.IsCancellationRequested)
            {
                StudioProject project;
                double position;
                bool playing;
                long wanted;
                lock (_commands)
                {
                    project = _project;
                    position = _position;
                    playing = _playing;
                    wanted = _revision;
                }
                if (Volatile.Read(ref _audioFailure) is { } audioFailure)
                    throw new StudioExportException("The timeline sound could not be played.", audioFailure);
                if (wanted != revision)
                {
                    StopSound();
                    if (!ReferenceEquals(applied, project))
                    {
                        StudioSourceFiles.RequireTimeline(project, _paths);
                        audio?.Dispose();
                        audio = new StudioTimelineAudio(project, _paths, _close.Token);
                        lock (_surfaceGate)
                        lock (graphics.Gate)
                        {
                            _renderer?.Dispose();
                            _renderer = new StudioTimelineRenderer(project, _paths, graphics, _options.PersonFinderFactory);
                        }
                        applied = project;
                    }
                    audio!.Seek(StudioRenderingMath.SecondsToSamples(position, audio.Format.SampleRate));
                    lock (_commands)
                    {
                        if (wanted != _revision) continue;
                        if (playing && audio.HasAudio && !_options.ForceMuted)
                            StartSound(audio);
                    }
                    origin = position;
                    started = Stopwatch.GetTimestamp();
                    revision = wanted;
                    lastFrame = -1;
                }
                var time = origin;
                if (playing)
                {
                    lock (_soundGate)
                        time += _wave is { } wave
                            ? wave.GetPosition() / (double)(audio!.Format.SampleRate * audio.Format.BlockAlign)
                            : Stopwatch.GetElapsedTime(started).TotalSeconds;
                }
                var end = time >= _duration;
                var frame = (long)(Clamp(time) * _fps + 1e-6);
                var changed = frame != lastFrame;
                var shown = frame / _fps;
                if (!_ready.Task.IsCompleted)
                {
                    // Opening proves an actual decoded picture even before a panel is attached.
                    var size = StudioCanvasMath.ExportSize(StudioCanvasMath.NaturalSize(project), 640);
                    using var target = graphics.CreateRenderTexture((int)size.Width, (int)size.Height);
                    lock (graphics.Gate)
                    {
                        _renderer!.Render(shown + 0.5 / _fps, target, (int)size.Width, (int)size.Height, StudioRenderQuality.Preview, _close.Token);
                        _renderer.ForgetTarget(target);
                    }
                    _ready.TrySetResult();
                }
                lock (_surfaceGate)
                {
                    if (_surface is { } surface && (changed || _redraw || !_configured || surface.NeedsConfigure))
                    {
                        var drew = false;
                        lock (graphics.Gate)
                        {
                            if (!_configured || surface.NeedsConfigure)
                            {
                                graphics.Context.Flush();
                                surface.Configure(graphics.Device);
                                _configured = true;
                            }
                            using var target = surface.AcquireTarget(out var width, out var height);
                            if (target is not null && width > 0 && height > 0)
                            {
                                try
                                {
                                    _renderer!.Render(shown + 0.5 / _fps, target, width, height, StudioRenderQuality.Preview, _close.Token);
                                    graphics.Context.Flush();
                                    drew = true;
                                }
                                finally { _renderer!.ForgetTarget(target); }
                            }
                        }
                        lock (_commands)
                        {
                            if (drew && wanted == _revision) surface.Present();
                        }
                        _redraw = false;
                    }
                }
                lock (_commands)
                {
                    if (wanted != _revision) continue;
                    _position = shown;
                    if (end && playing) _playing = playing = false;
                }
                if (end)
                {
                    PauseSound();
                    origin = shown;
                }
                if (changed) PositionChanged?.Invoke(this, EventArgs.Empty);
                if (playing != lastPlaying) IsPlayingChanged?.Invoke(this, EventArgs.Empty);
                lastPlaying = playing;
                lastFrame = frame;
                _wake.WaitOne(playing ? Math.Max(1, (int)(500 / _fps)) : Timeout.Infinite);
            }
        }
        catch (OperationCanceledException) when (_close.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            lock (_commands)
            {
                _playing = false;
                _failed = true;
            }
            IsPlayingChanged?.Invoke(this, EventArgs.Empty);
            Failed?.Invoke(this, new StudioPreviewFailedEventArgs($"The timeline preview failed: {ex.Message}", ex));
        }
        finally
        {
            StopSound();
            audio?.Dispose();
            lock (_surfaceGate)
            {
                ReleaseSurface();
                if (_graphics is { } graphics)
                {
                    lock (graphics.Gate) _renderer?.Dispose();
                    graphics.Dispose();
                }
                _renderer = null;
                _graphics = null;
            }
            _ready.TrySetCanceled(_close.Token);
            startup?.Dispose();
        }
        return true;
    }

    private void StartSound(StudioTimelineAudio audio)
    {
        lock (_soundGate)
        {
            _wave = new WaveOut { BufferMilliseconds = 50, NumberOfBuffers = 2 };
            _wave.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is { } ex) { Volatile.Write(ref _audioFailure, ex); Wake(); }
            };
            _wave.Init(new WaveSource(audio));
            if (_options.ZeroVolume) _wave.Volume = 0;
            _wave.Play();
        }
    }

    private void PauseSound() { lock (_soundGate) _wave?.Pause(); }
    private void StopSound()
    {
        lock (_soundGate)
        {
            _wave?.Dispose();
            _wave = null;
        }
    }

    private void Wake() { lock (_commands) { if (!_disposed) _wake.Set(); } }

    public ValueTask DisposeAsync()
    {
        lock (_commands)
            return new ValueTask(_disposeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        _disposed = true;
        _playing = false;
        _close.Cancel();
        _wake.Set();
        await _worker.ConfigureAwait(false);
        _close.Dispose();
        _wake.Dispose();
    }

    private sealed class WaveSource(StudioTimelineAudio audio) : IWaveProvider
    {
        private byte[] _block = [];
        private int _offset;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                if (_offset >= _block.Length)
                {
                    if (!audio.TryRead(out _, out var pcm)) break;
                    _block = pcm.ToArray();
                    _offset = 0;
                }
                var take = Math.Min(buffer.Length - written, _block.Length - _offset);
                _block.AsSpan(_offset, take).CopyTo(buffer[written..]);
                _offset += take;
                written += take;
            }
            return written;
        }
    }
}
