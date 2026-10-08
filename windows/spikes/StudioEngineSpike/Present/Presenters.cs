using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StudioEngineSpike.Engine;
using StudioEngineSpike.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace StudioEngineSpike.Present;

/// <summary>Draws one frame into a render target of the given pixel size. Takes <see cref="GraphicsDevice.Gate"/> itself.</summary>
internal delegate void DrawCallback(ID3D11Texture2D target, int pixelWidth, int pixelHeight);

/// <param name="GateWaitMs">Waiting for the device gate (a MediaPlayer frame copy holds it for several milliseconds).</param>
/// <param name="DrawMs">Scene drawn (and, for Win2D, copied into the swap chain), once the gate was held.</param>
/// <param name="PresentMs">The Present call itself.</param>
internal readonly record struct PresentTiming(double GateWaitMs, double DrawMs, double PresentMs);

/// <summary>What the two ways of getting a composited frame into a WinUI 3 window have in common.</summary>
internal interface IPanelPresenter : IDisposable
{
    string Name { get; }

    int PixelWidth { get; }

    int PixelHeight { get; }

    /// <summary>Presents that came back as DXGI_STATUS_OCCLUDED (only known for the DXGI path).</summary>
    int OccludedPresents { get; }

    /// <summary>
    /// 1: every presented frame is shown for at least one refresh, and Present blocks once the
    /// queue is full. 0: Present never waits, and of several frames presented before one refresh
    /// only the newest is shown. Neither tears: DWM composes these swap chains.
    /// </summary>
    int SyncInterval { get; set; }

    /// <summary>New panel size in DIPs and composition scale. Not concurrent with <see cref="Present"/>.</summary>
    void Resize(double widthDip, double heightDip, double scale);

    /// <param name="waitForGpu">Wait for the GPU to finish the drawing before presenting, so DrawMs is the whole cost.</param>
    PresentTiming Present(DrawCallback draw, bool waitForGpu);

    /// <summary>DXGI frame statistics of the underlying swap chain, when they can be had.</summary>
    bool TryGetFrameStatistics(out FrameStatistics statistics, out uint lastPresentCount);

    /// <summary>The swap chain as DXGI describes it (null when it cannot be reached).</summary>
    SwapChainDescription1? Describe();
}

/// <summary>
/// (a) Win2D: a <see cref="CanvasSwapChainPanel"/> whose <see cref="CanvasDevice"/> is created on
/// the shared Direct3D device. The scene is drawn by the shared renderer into a texture of the
/// panel's pixel size; Win2D wraps that texture as a <see cref="CanvasBitmap"/> (no copy) and
/// draws it into its swap chain.
/// </summary>
internal sealed class Win2DPresenter : IPanelPresenter
{
    private readonly GraphicsDevice _graphics;
    private readonly SceneRenderer _renderer;
    private readonly CanvasDevice _canvasDevice;
    private CanvasSwapChain? _swapChain;
    private IDXGISwapChain1? _dxgiSwapChain;
    private ID3D11Texture2D? _composite;
    private IDirect3DSurface? _compositeSurface;
    private CanvasBitmap? _bitmap;
    private float _dpi;

    public Win2DPresenter(GraphicsDevice graphics, SceneRenderer renderer)
    {
        _graphics = graphics;
        _renderer = renderer;
        _canvasDevice = CanvasDevice.CreateFromDirect3D11Device(graphics.WinRTDevice);
    }

    public string Name => "(a) Win2D CanvasSwapChainPanel";

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public int OccludedPresents => 0;

    public int SyncInterval { get; set; } = 1;

    /// <summary>Why the DXGI swap chain behind the CanvasSwapChain could not be reached, if it could not.</summary>
    public string? InteropFailure { get; private set; }

    /// <summary>Creates the swap chain and hands it to the panel. UI thread.</summary>
    public void Attach(CanvasSwapChainPanel panel, double widthDip, double heightDip, double scale)
    {
        _dpi = (float)(96 * scale);
        _swapChain = new CanvasSwapChain(_canvasDevice, (float)widthDip, (float)heightDip, _dpi);
        panel.SwapChain = _swapChain;
        CreateComposite();
        try
        {
            _dxgiSwapChain = PanelInterop.GetSwapChain(_swapChain);
        }
        catch (Exception ex)
        {
            InteropFailure = $"0x{ex.HResult:X8} {ex.Message.Trim()}";
        }
    }

    public void Resize(double widthDip, double heightDip, double scale)
    {
        if (_swapChain is null)
        {
            return;
        }

        lock (_graphics.Gate)
        {
            _dpi = (float)(96 * scale);
            ReleaseComposite();
            _swapChain.ResizeBuffers((float)widthDip, (float)heightDip, _dpi);
            CreateComposite();
        }
    }

    public PresentTiming Present(DrawCallback draw, bool waitForGpu)
    {
        if (_swapChain is null || _composite is null || _bitmap is null)
        {
            return default;
        }

        var start = Stopwatch.GetTimestamp();
        long acquired;

        // Win2D has its own Direct2D factory and context on the shared device, so its drawing is
        // serialized with everything else that touches the immediate context. One hold of the
        // gate covers the scene and Win2D's copy, so no frame copy can slip in between them.
        lock (_graphics.Gate)
        {
            acquired = Stopwatch.GetTimestamp();
            draw(_composite, PixelWidth, PixelHeight);
            using (var session = _swapChain.CreateDrawingSession(Microsoft.UI.Colors.Black))
            {
                session.DrawImage(_bitmap);
            }

            if (waitForGpu)
            {
                _graphics.WaitForGpu();
            }
        }

        var drawn = Stopwatch.GetTimestamp();
        _swapChain.Present(SyncInterval);
        return new PresentTiming(
            Stopwatch.GetElapsedTime(start, acquired).TotalMilliseconds,
            Stopwatch.GetElapsedTime(acquired, drawn).TotalMilliseconds,
            Stopwatch.GetElapsedTime(drawn).TotalMilliseconds);
    }

    public bool TryGetFrameStatistics(out FrameStatistics statistics, out uint lastPresentCount)
    {
        statistics = default;
        lastPresentCount = 0;
        if (_dxgiSwapChain is null || _dxgiSwapChain.GetFrameStatistics(out statistics).Failure)
        {
            return false;
        }

        lastPresentCount = _dxgiSwapChain.LastPresentCount;
        return true;
    }

    public SwapChainDescription1? Describe() => _dxgiSwapChain?.Description1;

    private void CreateComposite()
    {
        var size = _swapChain!.SizeInPixels;
        PixelWidth = (int)size.Width;
        PixelHeight = (int)size.Height;
        _composite = _graphics.CreateRenderTexture(PixelWidth, PixelHeight);
        _compositeSurface = _graphics.CreateSurface(_composite);

        // Same DPI as the swap chain, so one bitmap pixel is one swap chain pixel.
        _bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_canvasDevice, _compositeSurface, _dpi, CanvasAlphaMode.Ignore);
    }

    private void ReleaseComposite()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        (_compositeSurface as IDisposable)?.Dispose();
        _compositeSurface = null;
        if (_composite is not null)
        {
            _renderer.ForgetTarget(_composite);
            _composite.Dispose();
            _composite = null;
        }
    }

    public void Dispose()
    {
        lock (_graphics.Gate)
        {
            ReleaseComposite();
            _dxgiSwapChain?.Dispose();
            _swapChain?.Dispose();
            _canvasDevice.Dispose();
        }
    }
}

/// <summary>
/// (b) A plain XAML <see cref="SwapChainPanel"/> with a DXGI composition swap chain attached
/// through <c>ISwapChainPanelNative</c>. The shared renderer draws straight into the back buffer.
/// </summary>
internal sealed class NativePresenter : IPanelPresenter
{
    private const int DxgiStatusOccluded = 0x087A0001;

    private readonly GraphicsDevice _graphics;
    private readonly SceneRenderer _renderer;
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private int _occluded;

    public NativePresenter(GraphicsDevice graphics, SceneRenderer renderer)
    {
        _graphics = graphics;
        _renderer = renderer;
    }

    public string Name => "(b) SwapChainPanel + ISwapChainPanelNative";

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public int OccludedPresents => _occluded;

    public int SyncInterval { get; set; } = 1;

    /// <summary>Creates the swap chain and attaches it to the panel. UI thread.</summary>
    public void Attach(SwapChainPanel panel, double widthDip, double heightDip, double scale)
    {
        PixelWidth = Math.Max(1, (int)Math.Round(widthDip * scale));
        PixelHeight = Math.Max(1, (int)Math.Round(heightDip * scale));
        using var dxgiDevice = _graphics.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)PixelWidth,
            Height = (uint)PixelHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _swapChain = factory.CreateSwapChainForComposition(_graphics.Device, description, null!);
        _swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        ApplyScale(scale);
        PanelInterop.SetSwapChain(panel, _swapChain);
    }

    public void Resize(double widthDip, double heightDip, double scale)
    {
        if (_swapChain is null)
        {
            return;
        }

        var width = Math.Max(1, (int)Math.Round(widthDip * scale));
        var height = Math.Max(1, (int)Math.Round(heightDip * scale));
        lock (_graphics.Gate)
        {
            if (width != PixelWidth || height != PixelHeight)
            {
                // Every reference to the back buffer has to go before ResizeBuffers: the renderer's
                // cached Direct2D target, and anything Direct2D left queued on the immediate context.
                using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
                {
                    _renderer.ForgetTarget(backBuffer);
                }

                _graphics.Context.ClearState();
                _graphics.Context.Flush();
                _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
                PixelWidth = width;
                PixelHeight = height;
            }

            ApplyScale(scale);
        }
    }

    public PresentTiming Present(DrawCallback draw, bool waitForGpu)
    {
        if (_swapChain is null)
        {
            return default;
        }

        var start = Stopwatch.GetTimestamp();
        long acquired;
        lock (_graphics.Gate)
        {
            acquired = Stopwatch.GetTimestamp();
            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                draw(backBuffer, PixelWidth, PixelHeight);
            }

            if (waitForGpu)
            {
                _graphics.WaitForGpu();
            }
        }

        var drawn = Stopwatch.GetTimestamp();
        var result = _swapChain.Present((uint)SyncInterval, PresentFlags.None);
        result.CheckError();
        if (result.Code == DxgiStatusOccluded)
        {
            _occluded++;
        }

        return new PresentTiming(
            Stopwatch.GetElapsedTime(start, acquired).TotalMilliseconds,
            Stopwatch.GetElapsedTime(acquired, drawn).TotalMilliseconds,
            Stopwatch.GetElapsedTime(drawn).TotalMilliseconds);
    }

    public bool TryGetFrameStatistics(out FrameStatistics statistics, out uint lastPresentCount)
    {
        statistics = default;
        lastPresentCount = 0;
        if (_swapChain is null || _swapChain.GetFrameStatistics(out statistics).Failure)
        {
            return false;
        }

        lastPresentCount = _swapChain.LastPresentCount;
        return true;
    }

    public SwapChainDescription1? Describe() => _swapChain?.Description1;

    /// <summary>
    /// The panel lays a swap chain out as if one buffer pixel were one DIP. The inverse of the
    /// composition scale brings it back to one buffer pixel per physical pixel.
    /// </summary>
    private void ApplyScale(double scale)
    {
        if (_swapChain2 is not null)
        {
            _swapChain2.MatrixTransform = Matrix3x2.CreateScale((float)(1.0 / scale));
        }
    }

    /// <summary>Detaches from the panel. UI thread.</summary>
    public void Detach(SwapChainPanel panel) => PanelInterop.SetSwapChain(panel, null);

    public void Dispose()
    {
        lock (_graphics.Gate)
        {
            _renderer.ForgetAllTargets();
            _swapChain2?.Dispose();
            _swapChain?.Dispose();
        }
    }
}
