using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using TinyClips.Core.Capture;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// A video frame in a texture on the renderer's device: B8G8R8A8_UNorm or B8G8R8X8_UNorm, one
/// mip level, alpha ignored. <paramref name="Width"/> and <paramref name="Height"/> are the size
/// of the picture, which starts at the texture's top-left corner. A frame that fills its texture
/// is drawn straight from it, without a copy. A texture padded beyond the picture (1088 rows for
/// a 1080-line video) is allowed; its picture is copied out on the GPU first, so that sampling at
/// the picture's edge never reaches the padding.
/// </summary>
public readonly record struct StudioGpuVideoFrame(ID3D11Texture2D Texture, uint Subresource, int Width, int Height);

/// <summary>What to draw for one Studio frame, and where.</summary>
/// <param name="ProjectDirectory">The project folder, where a background image is looked for.</param>
/// <param name="SourceTimeSeconds">The source time of the frame: it picks the scene and the click rings.</param>
/// <param name="Screen">The screen picture for that time, or null when it is not there yet; the screen card and its shadow are then left out.</param>
/// <param name="Camera">The camera picture, or null. It is drawn only when the layout shows the camera at that time.</param>
/// <param name="Target">
/// The texture to draw into: B8G8R8A8_UNorm with render-target binding, <paramref name="CanvasWidth"/> by
/// <paramref name="CanvasHeight"/> pixels. Every pixel of the canvas is written, opaque.
/// </param>
/// <param name="ResolvedFrame">The layout for this frame when the caller already has it; otherwise it is resolved here.</param>
public readonly record struct StudioRenderRequest(
    StudioProject Project,
    StudioEvents Events,
    string ProjectDirectory,
    double SourceTimeSeconds,
    int CanvasWidth,
    int CanvasHeight,
    StudioGpuVideoFrame? Screen,
    StudioGpuVideoFrame? Camera,
    ID3D11Texture2D Target,
    StudioResolvedFrame? ResolvedFrame = null,
    StudioRenderQuality Quality = StudioRenderQuality.Preview);

/// <summary>How the screen and camera pictures are scaled.</summary>
public enum StudioRenderQuality
{
    /// <summary>Linear sampling, which is what a live preview can afford. It aliases when a picture is drawn at less than half its size.</summary>
    Preview,

    /// <summary>Direct2D's high-quality cubic sampling, which filters a picture before shrinking it. For exports and posters.</summary>
    Export,
}

/// <summary>
/// Draws one Tiny Clips Studio frame with Direct2D on the caller's Direct3D 11 device, following
/// sections 6.5 and 6.7 of the project format: background, screen shadow, screen, click rings,
/// camera shadow, camera, camera border, branding.
/// </summary>
/// <remarks>
/// Not thread-safe, and it draws through the device's immediate context: create it, call it and
/// dispose it while holding the lock that guards that context (<see cref="StudioGraphicsDevice.Gate"/>).
/// It keeps a Direct2D wrapper for every source and target texture it has seen, which keeps
/// those textures alive: call <see cref="ForgetSources"/> or <see cref="ForgetTarget"/> before
/// releasing or resizing them, and dispose the renderer before the device.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StudioSceneRenderer : IDisposable
{
    private const int MaxCanvasSide = 16384;
    private const int MaxCachedSources = 64;
    private const int MaxCachedTargets = 16;
    private const int MaxCachedSquircles = 32;
    private const int SquirclePointCount = 96;
    private const int ScreenSlot = 0;
    private const int CameraSlot = 1;

    /// <summary>A background image is decoded no larger than this on its long side, as on the Mac.</summary>
    private const int BackgroundImageMaxSide = 3840;

    /// <summary>How often the background image file is looked at for a change, in milliseconds.</summary>
    private const long BackgroundImageCheckInterval = 500;

    /// <summary>The widest blur Direct2D's shadow effect takes.</summary>
    private const double MaxShadowBlur = 250;

    private static readonly Color4 Black = new(0, 0, 0, 1);
    private static readonly Color4 White = new(1, 1, 1, 1);
    private static readonly Color4 Transparent = new(0, 0, 0, 0);

    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _d3dContext;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly Dictionary<nint, ID2D1Bitmap1> _targets = new();
    private readonly Dictionary<(nint Texture, uint Subresource), Source> _sources = new();
    private readonly Source?[] _pictureCopies = new Source?[2];
    private readonly Dictionary<(float Width, float Height), ID2D1PathGeometry> _squircles = new();
    private readonly List<StudioClickRing> _rings = new();
    private readonly CachedShadow _screenShadow = new();
    private readonly CachedShadow _cameraShadow = new();
    private readonly BrandingOverlayCompositor _branding = new();

    private ID2D1Bitmap1? _underlay;
    private UnderlayKey _underlayKey;
    private ID2D1Bitmap1? _badge;
    private int _badgeBuiltForHeight = -1;
    private int _badgeWidth;
    private int _badgeHeight;
    private int _badgeMargin;
    private ID2D1Bitmap1? _backgroundImage;
    private string? _backgroundImagePath;
    private long _backgroundImageStamp;
    private int _backgroundImageWidth;
    private int _backgroundImageHeight;
    private string? _stampPath;
    private long _stampCheckedAt;
    private long _stamp;
    private bool _disposed;

    /// <param name="d3dDevice">A device created with BGRA support. It must outlive the renderer.</param>
    /// <exception cref="StudioDeviceLostException">The device has been lost and must be rebuilt.</exception>
    public StudioSceneRenderer(ID3D11Device d3dDevice)
    {
        ArgumentNullException.ThrowIfNull(d3dDevice);
        try
        {
            _d3dDevice = d3dDevice.QueryInterface<ID3D11Device>();
            _d3dContext = _d3dDevice.ImmediateContext;
            _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
            using var dxgi = d3dDevice.QueryInterface<IDXGIDevice>();
            _device = _factory.CreateDevice(dxgi);
            _context = _device.CreateDeviceContext(DeviceContextOptions.None);
            _context.AntialiasMode = AntialiasMode.PerPrimitive;
            _context.UnitMode = UnitMode.Pixels;
            _brush = _context.CreateSolidColorBrush(Black);
        }
        catch (Exception ex)
        {
            Dispose();
            if (StudioMediaFoundation.IsDeviceLost(ex))
            {
                throw Lost(ex);
            }

            throw;
        }
    }

    /// <summary>How many times a background image has been decoded from its file. A layout change must not add to it.</summary>
    public int BackgroundImageDecodeCount { get; private set; }

    /// <summary>Draws the frame. Decoding and encoding are the caller's business.</summary>
    /// <exception cref="ArgumentException">The target or a source texture has a format the renderer does not draw.</exception>
    /// <exception cref="StudioDeviceLostException">The device has been lost; rebuild it and the renderer.</exception>
    public void Render(in StudioRenderRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Events);
        ArgumentNullException.ThrowIfNull(request.ProjectDirectory);
        ArgumentNullException.ThrowIfNull(request.Target);
        var width = request.CanvasWidth;
        var height = request.CanvasHeight;
        if (width is < 1 or > MaxCanvasSide || height is < 1 or > MaxCanvasSide)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"A {width}×{height} canvas cannot be drawn.");
        }

        try
        {
            var project = request.Project;
            var target = GetTarget(request.Target);
            var frame = request.ResolvedFrame ?? StudioLayoutResolver.Resolve(project, request.Events, request.SourceTimeSeconds, width, height);

            // A layer is drawn when the layout has it and its picture is here. Whole-pixel edges
            // keep a layer, its shadow and its border exactly on top of each other.
            StudioResolvedScreen screen = default;
            StudioFrameRect screenRect = default;
            ID2D1BitmapBrush1? screenBrush = null;
            if (frame.Screen is { } resolvedScreen && request.Screen is { } screenPicture && IsDrawable(resolvedScreen.Rect, resolvedScreen.Source))
            {
                screen = resolvedScreen;
                screenRect = PixelAlign(resolvedScreen.Rect);
                screenBrush = PrepareSource(screenPicture, ScreenSlot);
            }

            StudioResolvedCamera camera = default;
            StudioFrameRect cameraRect = default;
            ID2D1BitmapBrush1? cameraBrush = null;
            if (frame.Camera is { Visible: true } resolvedCamera && request.Camera is { } cameraPicture && IsDrawable(resolvedCamera.Rect, resolvedCamera.Source))
            {
                camera = resolvedCamera;
                cameraRect = PixelAlign(resolvedCamera.Rect);
                cameraBrush = PrepareSource(cameraPicture, CameraSlot);
            }

            EnsureUnderlay(project.Canvas.Background, request.ProjectDirectory, width, height, screenBrush is null ? null : new Card(screenRect, screen.CornerRadius, screen.Shadow));
            var cameraShadow = cameraBrush is null ? null : EnsureShadow(_cameraShadow, cameraRect.Width, cameraRect.Height, camera.CornerRadius, camera.Shape, camera.Shadow);

            _context.Target = target;
            var began = false;
            try
            {
                _context.BeginDraw();
                began = true;
                _context.Transform = Matrix3x2.Identity;

                // The background and the screen's shadow, which change only when the layout does.
                _context.DrawImage(_underlay!, null, null, D2DInterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);

                if (screenBrush is not null)
                {
                    var picture = request.Screen!.Value;
                    DrawLayer(screenBrush, picture.Width, picture.Height, screenRect, screen.Source, screen.CornerRadius, screen.CornerRadius > 0 ? StudioCameraShape.RoundedRectangle : StudioCameraShape.Rectangle, mirror: false, request.Quality);
                    DrawClickRings(project, request.Events, request.SourceTimeSeconds, screen, screenRect);
                }

                if (cameraBrush is not null)
                {
                    var picture = request.Camera!.Value;
                    if (cameraShadow is not null)
                    {
                        DrawShadow(cameraShadow, cameraRect);
                    }

                    DrawLayer(cameraBrush, picture.Width, picture.Height, cameraRect, camera.Source, camera.CornerRadius, camera.Shape, camera.Mirror, request.Quality);
                    DrawCameraBorder(project.Camera.BorderColor, camera, cameraRect);
                }

                if (project.Overlays.Branding)
                {
                    DrawBranding(width, height);
                }

                _context.EndDraw().CheckError();
                began = false;
            }
            finally
            {
                if (began)
                {
                    EndDrawQuietly();
                }

                _context.Target = null;
            }
        }
        catch (Exception ex) when (ex is not StudioDeviceLostException && StudioMediaFoundation.IsDeviceLost(ex))
        {
            throw Lost(ex);
        }
    }

    /// <summary>Lets go of every source texture. Call it before source textures are released or replaced.</summary>
    public void ForgetSources()
    {
        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }

        _sources.Clear();
        for (var slot = 0; slot < _pictureCopies.Length; slot++)
        {
            _pictureCopies[slot]?.Dispose();
            _pictureCopies[slot] = null;
        }
    }

    /// <summary>Lets go of a target texture. Call it before the texture is released, or a swap chain that owns it is resized.</summary>
    public void ForgetTarget(ID3D11Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (_targets.Remove(texture.NativePointer, out var bitmap))
        {
            bitmap.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var target in _targets.Values)
        {
            target.Dispose();
        }

        _targets.Clear();
        ForgetSources();
        foreach (var squircle in _squircles.Values)
        {
            squircle.Dispose();
        }

        _squircles.Clear();
        _screenShadow.Dispose();
        _cameraShadow.Dispose();
        _badge?.Dispose();
        _underlay?.Dispose();
        _backgroundImage?.Dispose();
        _brush?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _factory?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
    }

    // Targets and sources

    private ID2D1Bitmap1 GetTarget(ID3D11Texture2D target)
    {
        var key = target.NativePointer;
        if (_targets.TryGetValue(key, out var bitmap))
        {
            return bitmap;
        }

        var description = target.Description;
        if (description.Format != Format.B8G8R8A8_UNorm)
        {
            throw new ArgumentException($"The Studio renderer draws into B8G8R8A8_UNorm textures only; this target is {description.Format}.", "request");
        }

        if ((description.BindFlags & BindFlags.RenderTarget) == 0 || description.ArraySize != 1 || description.MipLevels != 1 || description.SampleDescription.Count != 1)
        {
            throw new ArgumentException("The Studio renderer needs a target with render-target binding, one mip level, one array slice and no multisampling.", "request");
        }

        if (_targets.Count >= MaxCachedTargets)
        {
            foreach (var cached in _targets.Values)
            {
                cached.Dispose();
            }

            _targets.Clear();
        }

        using var surface = target.QueryInterface<IDXGISurface>();
        bitmap = _context.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        _targets[key] = bitmap;
        return bitmap;
    }

    /// <summary>The brush that paints <paramref name="frame"/>, in the picture's own pixel coordinates.</summary>
    private ID2D1BitmapBrush1 PrepareSource(in StudioGpuVideoFrame frame, int slot)
    {
        ArgumentNullException.ThrowIfNull(frame.Texture);
        var key = (frame.Texture.NativePointer, frame.Subresource);
        if (!_sources.TryGetValue(key, out var source))
        {
            var description = frame.Texture.Description;
            if (description.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm) || description.MipLevels != 1)
            {
                throw new ArgumentException($"A Studio video frame must be a B8G8R8A8_UNorm or B8G8R8X8_UNorm texture with one mip level; this one is {description.Format} with {description.MipLevels}.", "request");
            }

            if (frame.Subresource >= description.ArraySize)
            {
                throw new ArgumentException($"A Studio video frame names slice {frame.Subresource} of a texture that has {description.ArraySize}.", "request");
            }

            if (_sources.Count >= MaxCachedSources)
            {
                ForgetSources();
            }

            source = new Source((int)description.Width, (int)description.Height, description.Format, description.ArraySize > 1);
            _sources[key] = source;
        }

        if (frame.Width < 1 || frame.Height < 1 || frame.Width > source.Width || frame.Height > source.Height)
        {
            throw new ArgumentException($"A {frame.Width}×{frame.Height} picture does not fit its {source.Width}×{source.Height} texture.", "request");
        }

        if (frame.Width == source.Width && frame.Height == source.Height)
        {
            if (source.Brush is null)
            {
                Wrap(source, frame.Texture, frame.Subresource);
            }

            return source.Brush!;
        }

        // Padded: copy the picture out, or sampling at its right and bottom edges would blend the padding in.
        var copy = _pictureCopies[slot];
        if (copy is null || copy.Width != frame.Width || copy.Height != frame.Height || copy.Format != source.Format)
        {
            copy?.Dispose();
            _pictureCopies[slot] = null;
            copy = new Source(frame.Width, frame.Height, source.Format, false)
            {
                OwnTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)frame.Width,
                    Height = (uint)frame.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = source.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                }),
            };
            try
            {
                Wrap(copy, copy.OwnTexture, 0);
            }
            catch
            {
                copy.Dispose();
                throw;
            }

            _pictureCopies[slot] = copy;
        }

        _d3dContext.CopySubresourceRegion(copy.OwnTexture!, 0, 0, 0, 0, frame.Texture, frame.Subresource, new Box(0, 0, 0, frame.Width, frame.Height, 1));
        return copy.Brush!;
    }

    private void Wrap(Source source, ID3D11Texture2D texture, uint subresource)
    {
        IDXGISurface surface;
        if (source.IsArray)
        {
            using var resource = texture.QueryInterface<IDXGIResource1>();
            surface = resource.CreateSubresourceSurface(subresource);
        }
        else
        {
            surface = texture.QueryInterface<IDXGISurface>();
        }

        using (surface)
        {
            source.Bitmap = _context.CreateBitmapFromDxgiSurface(
                surface,
                new BitmapProperties1(new D2DPixelFormat(source.Format, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
            source.Brush = _context.CreateBitmapBrush(source.Bitmap, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, D2DInterpolationMode.Linear), null);
        }
    }

    // Background and shadows

    private void EnsureUnderlay(StudioBackground background, string projectDirectory, int width, int height, Card? card)
    {
        string? imagePath = null;
        long imageStamp = 0;
        if (background.Style == StudioBackgroundStyle.Image && StudioProjectJson.IsPlainFileName(background.Image))
        {
            imagePath = Path.Combine(projectDirectory, background.Image!);
            imageStamp = BackgroundImageStamp(imagePath);
        }

        var key = new UnderlayKey(width, height, background.Style, background.Primary, background.Secondary, imagePath, imageStamp, card);
        if (_underlay is not null && _underlayKey == key)
        {
            return;
        }

        var shadow = card is { } shadowed
            ? EnsureShadow(_screenShadow, shadowed.Rect.Width, shadowed.Rect.Height, shadowed.Radius, shadowed.Radius > 0 ? StudioCameraShape.RoundedRectangle : StudioCameraShape.Rectangle, shadowed.Shadow)
            : null;

        if (_underlay is null || _underlayKey.Width != width || _underlayKey.Height != height)
        {
            _underlay?.Dispose();
            _underlay = null;
            _underlay = CreateLayerBitmap(width, height);
        }

        try
        {
            DrawInto(_underlay, () =>
            {
                FillBackground(background, imagePath, imageStamp, width, height);
                if (shadow is not null && card is { } under)
                {
                    DrawShadow(shadow, under.Rect);
                }
            });
        }
        catch
        {
            // Half-drawn: never show it for the old key.
            _underlay.Dispose();
            _underlay = null;
            throw;
        }

        _underlayKey = key;
    }

    private void FillBackground(StudioBackground background, string? imagePath, long imageStamp, int width, int height)
    {
        switch (background.Style)
        {
            case StudioBackgroundStyle.None:
                _context.Clear(Black);
                return;

            case StudioBackgroundStyle.Gradient:
                // From the top-left corner to the bottom-right one, interpolated on the encoded values.
                using (var stops = _context.CreateGradientStopCollection(new[]
                {
                    new GradientStop(0, OverBlack(ParseColor(background.Primary, Black))),
                    new GradientStop(1, OverBlack(ParseColor(background.Secondary ?? background.Primary, Black))),
                }))
                using (var gradient = _context.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2(width, height)), stops))
                {
                    _context.FillRectangle(new RawRectF(0, 0, width, height), gradient);
                }

                return;

            case StudioBackgroundStyle.Image when imagePath is not null && imageStamp != 0 && GetBackgroundImage(imagePath, imageStamp) is { } image:
                // Aspect-filled and centred. A transparent image shows black, as on the Mac.
                var scale = Math.Max(width / (double)_backgroundImageWidth, height / (double)_backgroundImageHeight);
                var drawWidth = _backgroundImageWidth * scale;
                var drawHeight = _backgroundImageHeight * scale;
                _context.Clear(Black);
                _context.DrawBitmap(
                    image,
                    new RawRectF((float)((width - drawWidth) / 2), (float)((height - drawHeight) / 2), (float)((width + drawWidth) / 2), (float)((height + drawHeight) / 2)),
                    1f,
                    D2DInterpolationMode.HighQualityCubic,
                    null,
                    null);
                return;

            default:
                // A solid colour, and what an image background falls back to when its file is missing or unreadable.
                _context.Clear(OverBlack(ParseColor(background.Primary, Black)));
                return;
        }
    }

    /// <summary>
    /// A number that changes when the image file does, or 0 when there is no file. The file is
    /// looked at twice a second at most, so a replaced image shows up within that time.
    /// </summary>
    private long BackgroundImageStamp(string path)
    {
        var now = Environment.TickCount64;
        if (string.Equals(path, _stampPath, StringComparison.OrdinalIgnoreCase) && now - _stampCheckedAt < BackgroundImageCheckInterval)
        {
            return _stamp;
        }

        _stampPath = path;
        _stampCheckedAt = now;
        try
        {
            var file = new FileInfo(path);
            _stamp = file.Exists ? Math.Max(1, unchecked((file.LastWriteTimeUtc.Ticks * 31) + file.Length) & long.MaxValue) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _stamp = 0;
        }

        return _stamp;
    }

    private ID2D1Bitmap1? GetBackgroundImage(string path, long stamp)
    {
        if (string.Equals(_backgroundImagePath, path, StringComparison.OrdinalIgnoreCase) && _backgroundImageStamp == stamp)
        {
            // Null for a file that would not decode: it is not tried again until it changes.
            return _backgroundImage;
        }

        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _backgroundImagePath = path;
        _backgroundImageStamp = stamp;
        if (StudioBackgroundImage.TryDecode(path, BackgroundImageMaxSide, out var pixels, out var width, out var height))
        {
            BackgroundImageDecodeCount++;
            _backgroundImage = CreateBitmap(pixels, width, height, D2DAlphaMode.Ignore);
            _backgroundImageWidth = width;
            _backgroundImageHeight = height;
        }

        return _backgroundImage;
    }

    /// <summary>
    /// The layer's shape, blurred, in black at the shadow's opacity, in a bitmap with a margin
    /// around the shape. Kept by size, so a layer that moves reuses it.
    /// </summary>
    private CachedShadow? EnsureShadow(CachedShadow cache, double width, double height, double radius, StudioCameraShape shape, StudioResolvedShadow shadow)
    {
        if (!(shadow.Opacity > 0) || !double.IsFinite(shadow.Blur) || !double.IsFinite(shadow.OffsetY))
        {
            return null;
        }

        var key = new ShadowKey(width, height, radius, shape, shadow);
        if (cache.Bitmap is not null && cache.Key == key)
        {
            return cache;
        }

        cache.Dispose();
        var blur = Math.Clamp(shadow.Blur, 0, MaxShadowBlur);
        var margin = (float)Math.Ceiling((blur * 3) + Math.Abs(shadow.OffsetY) + 2);
        var bitmapWidth = (int)Math.Ceiling(width + (2 * margin));
        var bitmapHeight = (int)Math.Ceiling(height + (2 * margin));
        var limit = _context.MaximumBitmapSize;
        if (bitmapWidth > limit || bitmapHeight > limit)
        {
            return null;
        }

        var bitmap = CreateLayerBitmap(bitmapWidth, bitmapHeight);
        try
        {
            using var outline = RecordShape(margin, width, height, radius, shape);
            DrawInto(bitmap, () =>
            {
                _context.Clear(Transparent);
                using var effect = new Shadow(_context);
                effect.SetInput(0, outline, true);
                effect.BlurStandardDeviation = (float)blur;
                effect.Optimization = ShadowOptimization.Quality;
                effect.Color = new Vector4(0, 0, 0, (float)Math.Clamp(shadow.Opacity, 0, 1));
                _context.DrawImage(effect, new Vector2(0, (float)shadow.OffsetY), D2DInterpolationMode.Linear, CompositeMode.SourceOver);
            });
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        cache.Bitmap = bitmap;
        cache.Key = key;
        cache.Margin = margin;
        return cache;
    }

    private ID2D1CommandList RecordShape(float margin, double width, double height, double radius, StudioCameraShape shape)
    {
        var list = _context.CreateCommandList();
        try
        {
            DrawInto(list, () =>
            {
                _brush.Color = Black;
                _context.Transform = Matrix3x2.CreateTranslation(margin, margin);
                FillShape(width, height, radius, shape, _brush);
            });
            list.Close().CheckError();
            return list;
        }
        catch
        {
            list.Dispose();
            throw;
        }
    }

    private void DrawShadow(CachedShadow shadow, StudioFrameRect rect)
    {
        var size = shadow.Bitmap!.PixelSize;
        var x = (float)rect.X - shadow.Margin;
        var y = (float)rect.Y - shadow.Margin;
        _context.DrawBitmap(shadow.Bitmap, new RawRectF(x, y, x + size.Width, y + size.Height), 1f, BitmapInterpolationMode.Linear, null);
    }

    // Layers

    /// <summary>
    /// Fills the layer's shape with the part of the picture <paramref name="crop"/> names. The
    /// shape is drawn at the origin and moved into place by the world transform, which the
    /// brush follows.
    /// </summary>
    private void DrawLayer(ID2D1BitmapBrush1 brush, int pictureWidth, int pictureHeight, StudioFrameRect destination, StudioFrameRect crop, double radius, StudioCameraShape shape, bool mirror, StudioRenderQuality quality)
    {
        var cropX = (float)(crop.X * pictureWidth);
        var cropY = (float)(crop.Y * pictureHeight);
        var cropWidth = crop.Width * pictureWidth;
        var cropHeight = crop.Height * pictureHeight;
        var scaleX = (float)(destination.Width / cropWidth);
        var scaleY = (float)(destination.Height / cropHeight);

        brush.InterpolationMode1 = quality == StudioRenderQuality.Export ? D2DInterpolationMode.HighQualityCubic : D2DInterpolationMode.Linear;
        brush.Transform = mirror
            ? Matrix3x2.CreateTranslation(-cropX - (float)cropWidth, -cropY) * Matrix3x2.CreateScale(-scaleX, scaleY)
            : Matrix3x2.CreateTranslation(-cropX, -cropY) * Matrix3x2.CreateScale(scaleX, scaleY);

        _context.Transform = Matrix3x2.CreateTranslation((float)destination.X, (float)destination.Y);
        FillShape(destination.Width, destination.Height, radius, shape, brush);
        _context.Transform = Matrix3x2.Identity;
    }

    /// <summary>Fills a shape whose bounding box is (0, 0, width, height).</summary>
    private void FillShape(double width, double height, double radius, StudioCameraShape shape, ID2D1Brush brush)
    {
        var w = (float)width;
        var h = (float)height;
        switch (shape)
        {
            case StudioCameraShape.Circle:
                _context.FillEllipse(new Ellipse(new Vector2(w / 2, h / 2), w / 2, h / 2), brush);
                break;
            case StudioCameraShape.Squircle:
                _context.FillGeometry(GetSquircle(w, h), brush);
                break;
            case StudioCameraShape.RoundedRectangle when radius > 0:
                var r = (float)Math.Min(radius, Math.Min(width, height) / 2);
                _context.FillRoundedRectangle(new RoundedRectangle(new RawRectF(0, 0, w, h), r, r), brush);
                break;
            default:
                _context.FillRectangle(new RawRectF(0, 0, w, h), brush);
                break;
        }
    }

    /// <summary>
    /// A stroke of the border's width (at least one pixel) just inside the camera's shape: the
    /// shape drawn smaller by half the stroke on every side, as the Mac compositor does.
    /// </summary>
    private void DrawCameraBorder(string borderColor, StudioResolvedCamera camera, StudioFrameRect rect)
    {
        if (!(camera.BorderWidth > 0))
        {
            return;
        }

        var stroke = (float)Math.Max(1, camera.BorderWidth);
        var inset = stroke / 2;
        var w = Math.Max(0, (float)rect.Width - stroke);
        var h = Math.Max(0, (float)rect.Height - stroke);
        _brush.Color = ParseColor(borderColor, White);
        _context.Transform = Matrix3x2.CreateTranslation((float)rect.X + inset, (float)rect.Y + inset);
        switch (camera.Shape)
        {
            case StudioCameraShape.Circle:
                _context.DrawEllipse(new Ellipse(new Vector2(w / 2, h / 2), w / 2, h / 2), _brush, stroke);
                break;
            case StudioCameraShape.Squircle:
                _context.DrawGeometry(GetSquircle(w, h), _brush, stroke);
                break;
            case StudioCameraShape.RoundedRectangle when camera.CornerRadius - inset > 0:
                var r = (float)Math.Min(camera.CornerRadius - inset, Math.Min(w, h) / 2);
                _context.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(0, 0, w, h), r, r), _brush, stroke);
                break;
            default:
                _context.DrawRectangle(new RawRectF(0, 0, w, h), _brush, stroke, null!);
                break;
        }

        _context.Transform = Matrix3x2.Identity;
    }

    /// <summary>The superellipse |x/a|^5 + |y/b|^5 = 1 inscribed in (0, 0, width, height), as the 96-point polygon the Mac draws.</summary>
    private ID2D1PathGeometry GetSquircle(float width, float height)
    {
        if (_squircles.TryGetValue((width, height), out var cached))
        {
            return cached;
        }

        if (_squircles.Count >= MaxCachedSquircles)
        {
            foreach (var old in _squircles.Values)
            {
                old.Dispose();
            }

            _squircles.Clear();
        }

        var geometry = _factory.CreatePathGeometry();
        try
        {
            using (var sink = geometry.Open())
            {
                var a = width / 2.0;
                var b = height / 2.0;
                for (var index = 0; index < SquirclePointCount; index++)
                {
                    var theta = Math.PI * 2 * index / SquirclePointCount;
                    var cos = Math.Cos(theta);
                    var sin = Math.Sin(theta);
                    var point = new Vector2(
                        (float)(a + (a * Math.Sign(cos) * Math.Pow(Math.Abs(cos), 2.0 / 5.0))),
                        (float)(b + (b * Math.Sign(sin) * Math.Pow(Math.Abs(sin), 2.0 / 5.0))));
                    if (index == 0)
                    {
                        sink.BeginFigure(point, FigureBegin.Filled);
                    }
                    else
                    {
                        sink.AddLine(point);
                    }
                }

                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }

            _squircles[(width, height)] = geometry;
            return geometry;
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
    }

    // Overlays

    /// <summary>
    /// The rings of the clicks showing at <paramref name="time"/>, clipped to the screen card.
    /// A clip is set up only when a ring reaches the card's edge, and the costly kind, a layer
    /// with the card's rounded outline as its mask, only when the card has round corners.
    /// </summary>
    private void DrawClickRings(StudioProject project, StudioEvents events, double time, StudioResolvedScreen screen, StudioFrameRect card)
    {
        var overlay = project.Overlays.Clicks;
        if (!overlay.Enabled || events.Clicks is not { Length: > 0 } clicks)
        {
            return;
        }

        _rings.Clear();
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (var click in clicks)
        {
            if (!StudioRenderingMath.TryComputeClickRing(click, time, events, project, screen, out var ring))
            {
                continue;
            }

            _rings.Add(ring);

            // One spare pixel for the antialiased edge.
            var reach = (float)(ring.Radius + (ring.StrokeWidth / 2) + 1);
            left = Math.Min(left, (float)ring.CenterX - reach);
            top = Math.Min(top, (float)ring.CenterY - reach);
            right = Math.Max(right, (float)ring.CenterX + reach);
            bottom = Math.Max(bottom, (float)ring.CenterY + reach);
        }

        if (_rings.Count == 0)
        {
            return;
        }

        var cardRect = new RawRectF((float)card.X, (float)card.Y, (float)(card.X + card.Width), (float)(card.Y + card.Height));
        var radius = (float)Math.Min(screen.CornerRadius, Math.Min(card.Width, card.Height) / 2);

        // Inside the card and clear of its rounded corners, a ring needs no clip at all.
        var inset = Math.Max(0, radius);
        var clear = left >= cardRect.Left + inset && top >= cardRect.Top + inset && right <= cardRect.Right - inset && bottom <= cardRect.Bottom - inset;
        ID2D1RoundedRectangleGeometry? mask = null;
        var clipped = false;
        try
        {
            if (!clear && radius > 0)
            {
                mask = _factory.CreateRoundedRectangleGeometry(new RoundedRectangle(cardRect, radius, radius));
                var parameters = new LayerParameters1
                {
                    ContentBounds = new RawRectF(Math.Max(left, cardRect.Left), Math.Max(top, cardRect.Top), Math.Min(right, cardRect.Right), Math.Min(bottom, cardRect.Bottom)),
                    GeometricMask = mask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions1.None,
                };
                _context.PushLayer(parameters, null!);
            }
            else if (!clear)
            {
                _context.PushAxisAlignedClip(cardRect, AntialiasMode.Aliased);
                clipped = true;
            }

            var color = ParseColor(overlay.Color, White);
            foreach (var ring in _rings)
            {
                _brush.Color = new Color4(color.R, color.G, color.B, color.A * (float)ring.Alpha);
                _context.DrawEllipse(new Ellipse(new Vector2((float)ring.CenterX, (float)ring.CenterY), (float)ring.Radius, (float)ring.Radius), _brush, (float)ring.StrokeWidth);
            }
        }
        finally
        {
            if (mask is not null)
            {
                _context.PopLayer();
                mask.Dispose();
            }
            else if (clipped)
            {
                _context.PopAxisAlignedClip();
            }
        }
    }

    /// <summary>The badge normal recordings get, in the same corner.</summary>
    private void DrawBranding(int width, int height)
    {
        if (_badgeBuiltForHeight != height)
        {
            _badge?.Dispose();
            _badge = null;
            _badgeBuiltForHeight = height;
            if (_branding.TryGetBadge(height, out var straight, out var badgeWidth, out var badgeHeight, out var margin))
            {
                // The compositor keeps the array it returns, so premultiply a copy.
                var premultiplied = (byte[])straight.Clone();
                for (var index = 0; index + 3 < premultiplied.Length; index += 4)
                {
                    var alpha = premultiplied[index + 3];
                    premultiplied[index] = (byte)(premultiplied[index] * alpha / 255);
                    premultiplied[index + 1] = (byte)(premultiplied[index + 1] * alpha / 255);
                    premultiplied[index + 2] = (byte)(premultiplied[index + 2] * alpha / 255);
                }

                _badge = CreateBitmap(premultiplied, badgeWidth, badgeHeight, D2DAlphaMode.Premultiplied);
                _badgeWidth = badgeWidth;
                _badgeHeight = badgeHeight;
                _badgeMargin = margin;
            }
        }

        if (_badge is not null)
        {
            _context.DrawBitmap(
                _badge,
                new RawRectF(width - _badgeWidth - _badgeMargin, height - _badgeHeight - _badgeMargin, width - _badgeMargin, height - _badgeMargin),
                1f,
                BitmapInterpolationMode.Linear,
                null);
        }
    }

    // Helpers

    /// <summary>Runs <paramref name="draw"/> between BeginDraw and EndDraw on <paramref name="target"/>. For caches, not for every frame.</summary>
    private void DrawInto(ID2D1Image target, Action draw)
    {
        _context.Target = target;
        var began = false;
        try
        {
            _context.BeginDraw();
            began = true;
            _context.Transform = Matrix3x2.Identity;
            draw();
            _context.Transform = Matrix3x2.Identity;
            _context.EndDraw().CheckError();
            began = false;
        }
        finally
        {
            if (began)
            {
                EndDrawQuietly();
            }

            _context.Target = null;
        }
    }

    private void EndDrawQuietly()
    {
        try
        {
            _context.Transform = Matrix3x2.Identity;
            _context.EndDraw();
        }
        catch (SharpGenException)
        {
            // The failure that brought us here is the one to report.
        }
    }

    private ID2D1Bitmap1 CreateLayerBitmap(int width, int height) =>
        _context.CreateBitmap(new SizeI(width, height), nint.Zero, 0, new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));

    private unsafe ID2D1Bitmap1 CreateBitmap(byte[] bgra, int width, int height, D2DAlphaMode alpha)
    {
        fixed (byte* pixels = bgra)
        {
            return _context.CreateBitmap(new SizeI(width, height), (nint)pixels, (uint)(width * 4), new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, alpha), 96, 96, BitmapOptions.None));
        }
    }

    private static bool IsDrawable(StudioFrameRect rect, StudioFrameRect source) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && rect.Width > 0 && rect.Height > 0
        && double.IsFinite(source.X) && double.IsFinite(source.Y) && source.Width > 0 && source.Height > 0
        && double.IsFinite(source.Width) && double.IsFinite(source.Height);

    /// <summary>The rectangle with its edges moved to the nearest whole pixel, and at least one pixel in size.</summary>
    internal static StudioFrameRect PixelAlign(StudioFrameRect rect)
    {
        var left = Math.Round(rect.X, MidpointRounding.AwayFromZero);
        var top = Math.Round(rect.Y, MidpointRounding.AwayFromZero);
        var right = Math.Round(rect.X + rect.Width, MidpointRounding.AwayFromZero);
        var bottom = Math.Round(rect.Y + rect.Height, MidpointRounding.AwayFromZero);
        return new StudioFrameRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c> to a colour with straight alpha.</summary>
    internal static Color4 ParseColor(string? hex, Color4 fallback)
    {
        var text = hex.AsSpan().Trim();
        if (text.StartsWith("#"))
        {
            text = text[1..];
        }

        if (text.Length is not (6 or 8) || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        if (text.Length == 6)
        {
            value = (value << 8) | 0xFF;
        }

        return new Color4(((value >> 24) & 0xFF) / 255f, ((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
    }

    /// <summary>A background colour with alpha is that colour over black: the video itself is opaque.</summary>
    private static Color4 OverBlack(Color4 color) => new(color.R * color.A, color.G * color.A, color.B * color.A, 1);

    private static StudioDeviceLostException Lost(Exception ex) =>
        new("The Studio rendering device was lost and must be rebuilt.", ex);

    private readonly record struct Card(StudioFrameRect Rect, double Radius, StudioResolvedShadow Shadow);

    private readonly record struct UnderlayKey(int Width, int Height, StudioBackgroundStyle Style, string? Primary, string? Secondary, string? ImagePath, long ImageStamp, Card? Card);

    private readonly record struct ShadowKey(double Width, double Height, double Radius, StudioCameraShape Shape, StudioResolvedShadow Shadow);

    private sealed class CachedShadow : IDisposable
    {
        public ID2D1Bitmap1? Bitmap { get; set; }

        public ShadowKey Key { get; set; }

        public float Margin { get; set; }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }
    }

    /// <summary>A texture the renderer samples: someone else's, or its own copy of a padded picture.</summary>
    private sealed class Source(int width, int height, Format format, bool isArray) : IDisposable
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public Format Format { get; } = format;

        public bool IsArray { get; } = isArray;

        public ID3D11Texture2D? OwnTexture { get; init; }

        public ID2D1Bitmap1? Bitmap { get; set; }

        public ID2D1BitmapBrush1? Brush { get; set; }

        public void Dispose()
        {
            Brush?.Dispose();
            Bitmap?.Dispose();
            OwnTexture?.Dispose();
        }
    }
}
