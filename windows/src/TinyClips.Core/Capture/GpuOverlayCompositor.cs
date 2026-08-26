using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TinyClips.Core.Models;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace TinyClips.Core.Capture;

/// <summary>
/// Draws the recording overlays (mouse-click pulses, branding badge, webcam picture-in-picture)
/// directly onto a D3D11 BGRA render-target texture with Direct2D, so the GPU pipeline never
/// touches frame pixels on the CPU. Geometry and placement are shared with the CPU compositors
/// (<see cref="MouseClickOverlayCompositor.TryComputeRing"/>, <see cref="WebcamOverlayLayout"/>,
/// <see cref="BrandingOverlayCompositor.TryGetBadge"/>) so both pipelines render the same picture.
///
/// The only CPU→GPU traffic is the webcam frame upload (a small ~1 MP bitmap, uploaded only
/// when a new camera frame arrives) and the one-time badge upload.
///
/// Not thread-safe: the owner serializes calls (the capture pump is single-threaded).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GpuOverlayCompositor : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _context;
    private readonly Dictionary<nint, ID2D1Bitmap1> _targets = new();

    private ID2D1SolidColorBrush? _clickBrush;
    private (byte R, byte G, byte B) _clickColor;
    private string? _clickColorHex;

    private BrandingOverlayCompositor? _branding;
    private ID2D1Bitmap1? _badge;
    private int _badgeWidth;
    private int _badgeHeight;
    private int _badgeMargin;
    private int _badgeBuiltForHeight = -1;

    private ID2D1Bitmap1? _webcamBitmap;
    private ID2D1BitmapBrush? _webcamBrush;
    private WebcamFrame? _uploadedWebcamFrame;
    private int _webcamBitmapWidth;
    private int _webcamBitmapHeight;

    public GpuOverlayCompositor(ID3D11Device d3dDevice)
    {
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        try
        {
            using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
            _device = _factory.CreateDevice(dxgiDevice);
            _context = _device.CreateDeviceContext(DeviceContextOptions.None);
            _context.AntialiasMode = AntialiasMode.PerPrimitive;
            _context.UnitMode = UnitMode.Pixels;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Enables branding-badge drawing, reusing the CPU compositor's rasterized badge.</summary>
    public void EnableBranding(BrandingOverlayCompositor branding) => _branding = branding;

    /// <summary>Begins a draw pass onto <paramref name="target"/>. Must be paired with <see cref="EndFrame"/>.</summary>
    public void BeginFrame(ID3D11Texture2D target)
    {
        _context.Target = GetOrCreateTarget(target);
        _context.BeginDraw();
    }

    public void EndFrame()
    {
        // D2DERR_RECREATE_TARGET is surfaced as an exception by Vortice; the owner tears the
        // compositor down and falls back on the next frame rather than failing the recording.
        _context.EndDraw();
        _context.Target = null;
    }

    public void DrawClicks(
        double frameSeconds,
        IReadOnlyList<MouseClickSample> clicks,
        int originX,
        int originY,
        in MouseClickOverlayStyle style)
    {
        if (clicks.Count == 0 || style.DurationSeconds <= 0 || style.Opacity <= 0)
        {
            return;
        }

        EnsureClickBrush(style.ColorHex);
        foreach (var click in clicks)
        {
            if (!MouseClickOverlayCompositor.TryComputeRing(click, frameSeconds, originX, originY, style, out var ring))
            {
                continue;
            }

            _clickBrush!.Color = new Color4(_clickColor.R / 255f, _clickColor.G / 255f, _clickColor.B / 255f, (float)ring.Alpha);
            var ellipse = new Ellipse(new Vector2((float)ring.CenterX, (float)ring.CenterY), (float)ring.Radius, (float)ring.Radius);
            _context.DrawEllipse(ellipse, _clickBrush, (float)(ring.HalfStroke * 2));
        }
    }

    public void DrawBranding(int frameWidth, int frameHeight)
    {
        if (_branding is null)
        {
            return;
        }

        if (_badge is null || _badgeBuiltForHeight != frameHeight)
        {
            _badgeBuiltForHeight = frameHeight;
            _badge?.Dispose();
            _badge = null;
            if (_branding.TryGetBadge(frameHeight, out var straightBgra, out var width, out var height, out var margin))
            {
                _badge = CreatePremultipliedBitmap(straightBgra, width, height);
                _badgeWidth = width;
                _badgeHeight = height;
                _badgeMargin = margin;
            }
        }

        if (_badge is null)
        {
            return;
        }

        var dest = new RectangleF(
            frameWidth - _badgeWidth - _badgeMargin,
            frameHeight - _badgeHeight - _badgeMargin,
            _badgeWidth,
            _badgeHeight);
        _context.DrawBitmap(_badge, dest, 1f, BitmapInterpolationMode.Linear, null);
    }

    public void DrawWebcam(
        int frameWidth,
        int frameHeight,
        WebcamFrame frame,
        WebcamCornerPosition corner,
        WebcamSizePreset sizePreset,
        WebcamShape shape,
        double? cornerRadius)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.BgraPixels.Length < frame.Width * frame.Height * 4)
        {
            return;
        }

        var layout = WebcamOverlayLayout.Compute(frameWidth, frameHeight, frame.Width, frame.Height, corner, sizePreset, shape, cornerRadius);
        if (layout.IsEmpty)
        {
            return;
        }

        EnsureWebcamBitmap(frame);
        var brush = _webcamBrush!;

        // Map the source crop rectangle onto the overlay rectangle: translate crop origin to 0,
        // scale to the overlay size, then translate into place.
        var scaleX = layout.OverlayWidth / (float)layout.CropWidth;
        var scaleY = layout.OverlayHeight / (float)layout.CropHeight;
        brush.Transform =
            Matrix3x2.CreateTranslation(-layout.CropX, -layout.CropY) *
            Matrix3x2.CreateScale(scaleX, scaleY) *
            Matrix3x2.CreateTranslation(layout.OverlayX, layout.OverlayY);

        var rect = new RectangleF(layout.OverlayX, layout.OverlayY, layout.OverlayWidth, layout.OverlayHeight);
        switch (shape)
        {
            case WebcamShape.Circle:
                _context.FillEllipse(
                    new Ellipse(
                        new Vector2(rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f)),
                        rect.Width / 2f,
                        rect.Height / 2f),
                    brush);
                break;
            case WebcamShape.RoundedRectangle when layout.CornerRadiusPx > 0:
                _context.FillRoundedRectangle(new RoundedRectangle(rect, layout.CornerRadiusPx, layout.CornerRadiusPx), brush);
                break;
            default:
                _context.FillRectangle(rect, brush);
                break;
        }
    }

    private ID2D1Bitmap1 GetOrCreateTarget(ID3D11Texture2D texture)
    {
        var key = texture.NativePointer;
        if (_targets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        using var surface = texture.QueryInterface<IDXGISurface>();
        var props = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            96f,
            96f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        var bitmap = _context.CreateBitmapFromDxgiSurface(surface, props);
        _targets[key] = bitmap;
        return bitmap;
    }

    /// <summary>Drops the cached render target for a texture that left the pool.</summary>
    public void ForgetTarget(ID3D11Texture2D texture)
    {
        if (_targets.Remove(texture.NativePointer, out var bitmap))
        {
            bitmap.Dispose();
        }
    }

    private void EnsureClickBrush(string colorHex)
    {
        if (_clickBrush is not null && string.Equals(_clickColorHex, colorHex, StringComparison.Ordinal))
        {
            return;
        }

        _clickColorHex = colorHex;
        _clickColor = MouseClickOverlayCompositor.ParseColor(colorHex);
        _clickBrush ??= _context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
    }

    private unsafe ID2D1Bitmap1 CreatePremultipliedBitmap(byte[] straightBgra, int width, int height)
    {
        var premultiplied = new byte[straightBgra.Length];
        for (var i = 0; i + 3 < straightBgra.Length; i += 4)
        {
            var a = straightBgra[i + 3];
            premultiplied[i] = (byte)(straightBgra[i] * a / 255);
            premultiplied[i + 1] = (byte)(straightBgra[i + 1] * a / 255);
            premultiplied[i + 2] = (byte)(straightBgra[i + 2] * a / 255);
            premultiplied[i + 3] = a;
        }

        var props = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            96f,
            96f,
            BitmapOptions.None);
        fixed (byte* p = premultiplied)
        {
            return _context.CreateBitmap(new SizeI(width, height), (nint)p, (uint)(width * 4), props);
        }
    }

    private unsafe void EnsureWebcamBitmap(WebcamFrame frame)
    {
        if (_webcamBitmap is null || _webcamBitmapWidth != frame.Width || _webcamBitmapHeight != frame.Height)
        {
            _webcamBrush?.Dispose();
            _webcamBrush = null;
            _webcamBitmap?.Dispose();

            // Camera drivers leave BGRA alpha undefined; Ignore treats every pixel as opaque.
            var props = new BitmapProperties1(
                new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore),
                96f,
                96f,
                BitmapOptions.None);
            _webcamBitmap = _context.CreateBitmap(new SizeI(frame.Width, frame.Height), nint.Zero, 0, props);
            _webcamBitmapWidth = frame.Width;
            _webcamBitmapHeight = frame.Height;
            _uploadedWebcamFrame = null;
        }

        if (!ReferenceEquals(_uploadedWebcamFrame, frame))
        {
            using var handle = frame.BgraPixels.Pin();
            _webcamBitmap.CopyFromMemory((nint)handle.Pointer, (uint)(frame.Width * 4));
            _uploadedWebcamFrame = frame;
        }

        _webcamBrush ??= _context.CreateBitmapBrush(
            _webcamBitmap,
            new BitmapBrushProperties(ExtendMode.Clamp, ExtendMode.Clamp, BitmapInterpolationMode.Linear));
    }

    public void Dispose()
    {
        foreach (var target in _targets.Values)
        {
            target.Dispose();
        }

        _targets.Clear();
        _webcamBrush?.Dispose();
        _webcamBitmap?.Dispose();
        _badge?.Dispose();
        _clickBrush?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _factory?.Dispose();
    }
}
