using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>Decodes only the current clip (both during a dissolve), composing each before joining them.</summary>
internal sealed class StudioTimelineRenderer : IDisposable
{
    private readonly StudioProject _project;
    private readonly StudioProjectPaths _paths;
    private readonly StudioGraphicsDevice _graphics;
    private readonly IMFDXGIDeviceManager? _manager;
    private readonly StudioSceneRenderer _renderer;
    private readonly Dictionary<string, Clip> _clips = [];
    private ID3D11Texture2D? _outgoing;
    private ID3D11Texture2D? _incoming;
    private int _width;
    private int _height;

    public StudioTimelineRenderer(StudioProject project, StudioProjectPaths paths, StudioGraphicsDevice graphics,
        Func<IStudioPersonFinder?>? personFinderFactory = null)
    {
        Map = StudioTimelineProjection.Map(project);
        _project = project;
        _paths = paths;
        _graphics = graphics;
        _renderer = new StudioSceneRenderer(graphics.Device, personFinderFactory);
        try
        {
            if (!graphics.IsSoftware)
            {
                _manager = MediaFactory.MFCreateDXGIDeviceManager();
                _manager.ResetDevice(graphics.Device).CheckError();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public StudioTimelineMap Map { get; }

    public void Render(double projectTime, ID3D11Texture2D target, int width, int height,
        StudioRenderQuality quality, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = StudioTimelineFrame.At(Map, projectTime);
        var wanted = frame.Layers.Select(layer => layer.Clip.ClipId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _clips.Keys.Where(id => !wanted.Contains(id)).ToArray())
        {
            _renderer.ForgetSources();
            _clips[id].Dispose();
            _clips.Remove(id);
        }
        if (frame.Layers.Length == 1 && frame.ColorOpacity == 0)
        {
            Draw(frame.Layers[0], target, width, height, quality, cancellationToken);
            return;
        }
        if (_width != width || _height != height)
        {
            ReleaseBuffers();
            _outgoing = _graphics.CreateRenderTexture(width, height);
            _incoming = _graphics.CreateRenderTexture(width, height);
            _width = width;
            _height = height;
        }
        Draw(frame.Layers[0], _outgoing!, width, height, quality, cancellationToken);
        if (frame.Layers.Length == 2)
        {
            Draw(frame.Layers[1], _incoming!, width, height, quality, cancellationToken);
        }
        _renderer.BlendTimeline(target, _outgoing!, frame.Layers.Length == 2 ? _incoming : null,
            width, height, frame.Layers[^1].Opacity, frame.ColorOpacity, frame.White);
    }

    private void Draw(StudioTimelineLayer layer, ID3D11Texture2D target, int width, int height,
        StudioRenderQuality quality, CancellationToken cancellationToken)
    {
        if (!_clips.TryGetValue(layer.Clip.ClipId, out var clip))
        {
            var project = StudioTimelineProjection.ProjectForClip(_project, layer.Clip.ClipId);
            var paths = StudioTimelineProjection.PathsForClip(_project, _paths, layer.Clip.ClipId);
            var events = StudioTimelineProjection.EventsForClip(_project, _paths, layer.Clip.ClipId);
            var screen = StudioVideoSource.Open(paths.ScreenPath, "screen recording", _graphics, _manager);
            try
            {
                var sideways = screen.RotationDegrees is 90 or 270;
                project = project with
                {
                    Sources = project.Sources with
                    {
                        Screen = project.Sources.Screen with
                        {
                            Width = sideways ? screen.Height : screen.Width,
                            Height = sideways ? screen.Width : screen.Height,
                        },
                    },
                };
                var layout = StudioLayoutPlan.Create(project, events);
                var camera = paths.CameraPath is { } cameraPath
                    ? StudioVideoSource.Open(cameraPath, "camera recording", _graphics, _manager) : null;
                clip = new Clip(project, events, layout, screen, camera);
                _clips.Add(layer.Clip.ClipId, clip);
            }
            catch
            {
                screen.Dispose();
                throw;
            }
        }
        var resolved = clip.Layout.Resolve(layer.SourceTime, width, height);
        StudioGpuVideoFrame? screenFrame = resolved.Screen is null ? null
            : clip.Screen.GetFrame(StudioRenderingMath.SecondsToMfTicks(layer.SourceTime), cancellationToken);
        StudioGpuVideoFrame? cameraFrame = clip.Camera is not null && resolved.Camera is { Visible: true } visible
            ? clip.Camera.GetFrame(StudioRenderingMath.SecondsToMfTicks(visible.SourceTime), cancellationToken) : null;
        _renderer.Render(new StudioRenderRequest(clip.Project, clip.Events, _paths.ProjectDirectory,
            layer.SourceTime, width, height, screenFrame, cameraFrame, target, resolved, quality));
    }

    public void ForgetTarget(ID3D11Texture2D target) => _renderer.ForgetTarget(target);

    private void ReleaseBuffers()
    {
        _renderer.ForgetSources();
        if (_outgoing is not null) _renderer.ForgetTarget(_outgoing);
        if (_incoming is not null) _renderer.ForgetTarget(_incoming);
        _outgoing?.Dispose();
        _incoming?.Dispose();
        _outgoing = _incoming = null;
    }

    public void Dispose()
    {
        ReleaseBuffers();
        _renderer.Dispose();
        foreach (var clip in _clips.Values) clip.Dispose();
        _clips.Clear();
        _manager?.Dispose();
    }

    private sealed record Clip(StudioProject Project, StudioEvents Events, StudioLayoutPlan Layout,
        StudioVideoSource Screen, StudioVideoSource? Camera) : IDisposable
    {
        public void Dispose()
        {
            Camera?.Dispose();
            Screen.Dispose();
        }
    }
}
