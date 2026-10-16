using System.Drawing;
using System.Numerics;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace StudioEngineSpike.Engine;

/// <summary>How the two soft shadows are produced.</summary>
internal enum ShadowMode
{
    None,

    /// <summary>Blur once per layout into bitmaps and blit them every frame (a static scene).</summary>
    Cached,

    /// <summary>Run the Direct2D shadow effect every frame (a layout that animates, e.g. a morph).</summary>
    Live,
}

internal sealed record RenderOptions(ShadowMode Shadows = ShadowMode.Cached, bool HighQualityScaling = false);

/// <summary>A video frame to draw: a BGRA/BGRX texture (or one slice of a texture array) on the shared device.</summary>
internal readonly record struct SceneSource(ID3D11Texture2D Texture, uint Subresource, int Width, int Height);

/// <summary>
/// The Studio scene drawn with Direct2D (through Vortice) on the shared D3D11 device: gradient
/// background, the screen as a rounded card with a soft shadow, the camera as a mirrored circle
/// with its own shadow. The same instance serves preview, export and the swap chain presenters,
/// which is the point of the design: one renderer, three callers.
///
/// Sources are wrapped as Direct2D bitmaps without copying (<c>CreateBitmapFromDxgiSurface</c>)
/// and cached by texture identity. Not thread-safe; callers hold <see cref="GraphicsDevice.Gate"/>.
/// </summary>
internal sealed class SceneRenderer : IDisposable
{
    private const int MaxCachedSources = 64;

    private readonly GraphicsDevice _graphics;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1SolidColorBrush _solidBrush;
    private readonly Dictionary<nint, ID2D1Bitmap1> _targets = new();
    private readonly Dictionary<(nint Texture, uint Subresource), SourceEntry> _sources = new();

    private ID2D1LinearGradientBrush? _gradientBrush;
    private (double Width, double Height, uint Primary, uint Secondary) _gradientKey;

    private ID2D1Bitmap1? _underlay;
    private UnderlayKey _underlayKey;
    private ID2D1Bitmap1? _cameraShadow;
    private CameraShadowKey _cameraShadowKey;
    private float _cameraShadowMargin;

    public SceneRenderer(GraphicsDevice graphics)
    {
        _graphics = graphics;
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        using var dxgiDevice = graphics.Device.QueryInterface<IDXGIDevice>();
        _device = _factory.CreateDevice(dxgiDevice);
        _context = _device.CreateDeviceContext(DeviceContextOptions.None);
        _context.AntialiasMode = AntialiasMode.PerPrimitive;
        _context.UnitMode = UnitMode.Pixels;
        _solidBrush = _context.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 1f));
    }

    /// <summary>Distinct source textures wrapped so far (shows whether a decoder recycles a pool).</summary>
    public int SourcesWrapped { get; private set; }

    /// <summary>Times the cached shadow bitmaps were rebuilt.</summary>
    public int ShadowRebuilds { get; private set; }

    public ID2D1DeviceContext Context => _context;

    /// <summary>
    /// Draws one frame into <paramref name="target"/> (a BGRA render-target texture of any size;
    /// the layout must have been resolved for that size). A null source is simply not drawn.
    /// </summary>
    public void Render(ID3D11Texture2D target, SceneLayout layout, SceneSource? screen, SceneSource? camera, RenderOptions options)
    {
        var screenShadowLive = options.Shadows == ShadowMode.Live && screen is not null && layout.ScreenShadow.Opacity > 0;
        var cameraShadowLive = options.Shadows == ShadowMode.Live && camera is not null && layout.CameraShadow.Opacity > 0;

        // Anything that needs its own BeginDraw/EndDraw pass happens before the frame's pass.
        if (options.Shadows == ShadowMode.Cached)
        {
            EnsureUnderlay(layout, includeShadow: screen is not null);
            if (camera is not null)
            {
                EnsureCameraShadow(layout);
            }
        }

        using var screenShape = screenShadowLive ? RecordShape(layout.ScreenRect, (float)layout.ScreenRadius, ellipse: false) : null;
        using var cameraShape = cameraShadowLive ? RecordShape(layout.CameraRect, 0, ellipse: true) : null;

        _context.Target = GetTarget(target);
        _context.BeginDraw();
        _context.Transform = Matrix3x2.Identity;

        if (options.Shadows == ShadowMode.Cached)
        {
            // Background and screen shadow in one opaque blit.
            _context.DrawImage(_underlay!, null, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
        }
        else
        {
            FillBackground(layout);
            if (screenShape is not null)
            {
                DrawShadow(screenShape, layout.ScreenShadow);
            }
        }

        if (screen is { } screenSource)
        {
            var entry = GetSource(screenSource, options.HighQualityScaling);
            entry.Brush.Transform =
                Matrix3x2.CreateScale((float)(layout.ScreenRect.Width / screenSource.Width), (float)(layout.ScreenRect.Height / screenSource.Height)) *
                Matrix3x2.CreateTranslation((float)layout.ScreenRect.X, (float)layout.ScreenRect.Y);
            var radius = (float)layout.ScreenRadius;
            _context.FillRoundedRectangle(new RoundedRectangle(ToRectangleF(layout.ScreenRect), radius, radius), entry.Brush);
        }

        if (camera is { } cameraSource)
        {
            if (options.Shadows == ShadowMode.Cached && _cameraShadow is not null)
            {
                var size = _cameraShadow.PixelSize;
                var left = (float)layout.CameraRect.X - _cameraShadowMargin;
                var top = (float)layout.CameraRect.Y - _cameraShadowMargin;
                _context.DrawBitmap(_cameraShadow, new RawRectF(left, top, left + size.Width, top + size.Height), 1f, BitmapInterpolationMode.Linear, null);
            }
            else if (cameraShape is not null)
            {
                DrawShadow(cameraShape, layout.CameraShadow);
            }

            var entry = GetSource(cameraSource, options.HighQualityScaling);
            var cropX = (float)(layout.CameraSource.X * cameraSource.Width);
            var cropY = (float)(layout.CameraSource.Y * cameraSource.Height);
            var scaleX = (float)(layout.CameraRect.Width / (layout.CameraSource.Width * cameraSource.Width));
            var scaleY = (float)(layout.CameraRect.Height / (layout.CameraSource.Height * cameraSource.Height));
            entry.Brush.Transform = layout.Mirror
                ? Matrix3x2.CreateTranslation(-cropX, -cropY) * Matrix3x2.CreateScale(-scaleX, scaleY) * Matrix3x2.CreateTranslation((float)layout.CameraRect.Right, (float)layout.CameraRect.Y)
                : Matrix3x2.CreateTranslation(-cropX, -cropY) * Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation((float)layout.CameraRect.X, (float)layout.CameraRect.Y);
            _context.FillEllipse(
                new Ellipse(
                    new Vector2((float)layout.CameraRect.CenterX, (float)layout.CameraRect.CenterY),
                    (float)(layout.CameraRect.Width / 2),
                    (float)(layout.CameraRect.Height / 2)),
                entry.Brush);
        }

        _context.EndDraw().CheckError();
        _context.Target = null;
    }

    /// <summary>
    /// Scales <paramref name="source"/> to fill <paramref name="target"/> (used to show a composited
    /// frame in a swap chain back buffer of a different size).
    /// </summary>
    public void Blit(ID3D11Texture2D target, SceneSource source, bool highQuality)
    {
        var entry = GetSource(source, highQuality);
        var size = target.Description;
        _context.Target = GetTarget(target);
        _context.BeginDraw();
        _context.Transform = Matrix3x2.Identity;
        _context.DrawBitmap(
            entry.Bitmap,
            new RawRectF(0, 0, size.Width, size.Height),
            1f,
            highQuality ? InterpolationMode.HighQualityCubic : InterpolationMode.Linear,
            null,
            null);
        _context.EndDraw().CheckError();
        _context.Target = null;
    }

    /// <summary>Drops the cached wrapper for a render target that is about to be released or resized.</summary>
    public void ForgetTarget(ID3D11Texture2D texture)
    {
        if (_targets.Remove(texture.NativePointer, out var bitmap))
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Drops every cached target (required before <c>IDXGISwapChain::ResizeBuffers</c>).</summary>
    public void ForgetAllTargets()
    {
        _context.Target = null;
        foreach (var bitmap in _targets.Values)
        {
            bitmap.Dispose();
        }

        _targets.Clear();
    }

    public void ForgetSources()
    {
        foreach (var entry in _sources.Values)
        {
            entry.Dispose();
        }

        _sources.Clear();
    }

    /// <summary>
    /// Tries to wrap a texture as a Direct2D source bitmap without drawing it. Returns null on
    /// success or the failure text, so callers can probe what a decoder hands out.
    /// </summary>
    public string? TryWrap(SceneSource source)
    {
        try
        {
            GetSource(source, highQuality: false);
            return null;
        }
        catch (Exception ex)
        {
            return $"0x{ex.HResult:X8} {ex.Message.Trim()}";
        }
    }

    private void FillBackground(SceneLayout layout)
    {
        var key = (layout.Width, layout.Height, layout.Settings.BackgroundPrimary, layout.Settings.BackgroundSecondary);
        if (_gradientBrush is null || _gradientKey != key)
        {
            _gradientBrush?.Dispose();
            using var stops = _context.CreateGradientStopCollection(new[]
            {
                new GradientStop(0f, ToColor(layout.Settings.BackgroundPrimary, 1f)),
                new GradientStop(1f, ToColor(layout.Settings.BackgroundSecondary, 1f)),
            });

            // Top-left corner to bottom-right corner, as the screenshot editors draw it.
            _gradientBrush = _context.CreateLinearGradientBrush(
                new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2((float)layout.Width, (float)layout.Height)),
                stops);
            _gradientKey = key;
        }

        _context.FillRectangle(new RawRectF(0, 0, (float)layout.Width, (float)layout.Height), _gradientBrush);
    }

    /// <summary>Records a filled shape into a command list, the input the shadow effect blurs.</summary>
    private ID2D1CommandList RecordShape(RectD rect, float radius, bool ellipse)
    {
        var list = _context.CreateCommandList();
        _context.Target = list;
        _context.BeginDraw();
        _context.Transform = Matrix3x2.Identity;
        _solidBrush.Color = new Color4(0f, 0f, 0f, 1f);
        if (ellipse)
        {
            _context.FillEllipse(new Ellipse(new Vector2((float)rect.CenterX, (float)rect.CenterY), (float)(rect.Width / 2), (float)(rect.Height / 2)), _solidBrush);
        }
        else
        {
            _context.FillRoundedRectangle(new RoundedRectangle(ToRectangleF(rect), radius, radius), _solidBrush);
        }

        _context.EndDraw().CheckError();
        _context.Target = null;
        list.Close().CheckError();
        return list;
    }

    /// <summary>
    /// Draws the shape's blurred shadow into the current pass. The spec gives the blur as a radius;
    /// the effect takes a standard deviation, taken here as half the radius (the CSS convention).
    /// </summary>
    private void DrawShadow(ID2D1Image shape, ShadowSpec shadow, float offsetX = 0, float extraOffsetY = 0)
    {
        using var effect = new Shadow(_context);
        effect.SetInput(0, shape, true);
        effect.BlurStandardDeviation = (float)(shadow.Blur / 2);
        effect.Color = new Vector4(0f, 0f, 0f, (float)shadow.Opacity);
        _context.DrawImage(effect, new Vector2(offsetX, (float)shadow.OffsetY + extraOffsetY), InterpolationMode.Linear, CompositeMode.SourceOver);
    }

    private void EnsureUnderlay(SceneLayout layout, bool includeShadow)
    {
        var key = new UnderlayKey(layout.Width, layout.Height, layout.ScreenRect, layout.ScreenRadius, layout.ScreenShadow, layout.Settings.BackgroundPrimary, layout.Settings.BackgroundSecondary, includeShadow);
        if (_underlay is not null && _underlayKey == key)
        {
            return;
        }

        _underlay?.Dispose();
        _underlay = CreateLayerBitmap((int)Math.Ceiling(layout.Width), (int)Math.Ceiling(layout.Height));
        _underlayKey = key;
        ShadowRebuilds++;

        using var shape = includeShadow && layout.ScreenShadow.Opacity > 0 ? RecordShape(layout.ScreenRect, (float)layout.ScreenRadius, ellipse: false) : null;
        _context.Target = _underlay;
        _context.BeginDraw();
        _context.Transform = Matrix3x2.Identity;
        FillBackground(layout);
        if (shape is not null)
        {
            DrawShadow(shape, layout.ScreenShadow);
        }

        _context.EndDraw().CheckError();
        _context.Target = null;
    }

    private void EnsureCameraShadow(SceneLayout layout)
    {
        var key = new CameraShadowKey(layout.CameraRect.Width, layout.CameraRect.Height, layout.CameraShadow);
        if (_cameraShadow is not null && _cameraShadowKey == key)
        {
            return;
        }

        _cameraShadow?.Dispose();
        _cameraShadow = null;
        _cameraShadowKey = key;
        if (layout.CameraShadow.Opacity <= 0)
        {
            return;
        }

        // Three standard deviations of blur on every side, plus the downward offset.
        var margin = (float)Math.Ceiling((layout.CameraShadow.Blur * 1.5) + layout.CameraShadow.OffsetY + 2);
        _cameraShadowMargin = margin;
        var width = (int)Math.Ceiling(layout.CameraRect.Width + (2 * margin));
        var height = (int)Math.Ceiling(layout.CameraRect.Height + (2 * margin));
        _cameraShadow = CreateLayerBitmap(width, height);
        ShadowRebuilds++;

        using var shape = RecordShape(new RectD(margin, margin, layout.CameraRect.Width, layout.CameraRect.Height), 0, ellipse: true);
        _context.Target = _cameraShadow;
        _context.BeginDraw();
        _context.Transform = Matrix3x2.Identity;
        _context.Clear(new Color4(0f, 0f, 0f, 0f));
        DrawShadow(shape, layout.CameraShadow);
        _context.EndDraw().CheckError();
        _context.Target = null;
    }

    private ID2D1Bitmap1 CreateLayerBitmap(int width, int height) => _context.CreateBitmap(
        new SizeI(width, height),
        nint.Zero,
        0,
        new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96f, 96f, BitmapOptions.Target));

    private ID2D1Bitmap1 GetTarget(ID3D11Texture2D texture)
    {
        var key = texture.NativePointer;
        if (_targets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        using var surface = texture.QueryInterface<IDXGISurface>();
        var bitmap = _context.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        _targets[key] = bitmap;
        return bitmap;
    }

    private SourceEntry GetSource(SceneSource source, bool highQuality)
    {
        var key = (source.Texture.NativePointer, source.Subresource);
        if (!_sources.TryGetValue(key, out var entry))
        {
            if (_sources.Count >= MaxCachedSources)
            {
                ForgetSources();
            }

            // A plain texture is itself a DXGI surface. One slice of a texture array (what video
            // decoders hand out) needs IDXGIResource1::CreateSubresourceSurface.
            IDXGISurface surface;
            var description = source.Texture.Description;
            if (description.ArraySize > 1)
            {
                using var resource = source.Texture.QueryInterface<IDXGIResource1>();
                surface = resource.CreateSubresourceSurface(source.Subresource);
            }
            else
            {
                surface = source.Texture.QueryInterface<IDXGISurface>();
            }

            using (surface)
            {
                // Video frames carry no meaningful alpha (BGRX from the video processor, undefined
                // alpha from MediaPlayer), so every pixel is treated as opaque.
                var bitmap = _context.CreateBitmapFromDxgiSurface(
                    surface,
                    new BitmapProperties1(new D2DPixelFormat(description.Format, D2DAlphaMode.Ignore), 96f, 96f, BitmapOptions.None));
                var brush = _context.CreateBitmapBrush(
                    bitmap,
                    new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.Linear),
                    null);
                entry = new SourceEntry(bitmap, brush);
                _sources[key] = entry;
                SourcesWrapped++;
            }
        }

        entry.Brush.InterpolationMode1 = highQuality ? InterpolationMode.HighQualityCubic : InterpolationMode.Linear;
        return entry;
    }

    private static RectangleF ToRectangleF(RectD rect) => new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);

    private static Color4 ToColor(uint rgb, float alpha) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public void Dispose()
    {
        ForgetAllTargets();
        ForgetSources();
        _cameraShadow?.Dispose();
        _underlay?.Dispose();
        _gradientBrush?.Dispose();
        _solidBrush.Dispose();
        _context.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }

    private readonly record struct UnderlayKey(double Width, double Height, RectD ScreenRect, double Radius, ShadowSpec Shadow, uint Primary, uint Secondary, bool IncludeShadow);

    private readonly record struct CameraShadowKey(double Width, double Height, ShadowSpec Shadow);

    private sealed record SourceEntry(ID2D1Bitmap1 Bitmap, ID2D1BitmapBrush1 Brush) : IDisposable
    {
        public void Dispose()
        {
            Brush.Dispose();
            Bitmap.Dispose();
        }
    }
}
