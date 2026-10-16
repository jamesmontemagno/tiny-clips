using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio.Preview;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// Shows the picture of a <see cref="StudioPreviewEngine"/>: a <see cref="SwapChainPanel"/> with a
/// DXGI composition swap chain on the engine's device, which the engine draws straight into.
/// </summary>
/// <remarks>
/// <para>
/// Use it from the UI thread: create it, place it, and call <see cref="Attach"/> with an opened
/// engine. It fills whatever rectangle layout gives it, so the host gives it one with the canvas'
/// aspect, on whole pixels (a swap chain on a fractional pixel is blurred). Children are drawn
/// over the picture.
/// </para>
/// <para>
/// The buffers are the panel's size in physical pixels: its size in effective pixels times the
/// composition scale, with the inverse scale set on the swap chain so one buffer pixel is one
/// screen pixel. The engine's render thread creates, resizes and presents them; the UI thread
/// only notes the size wanted and hands the swap chain to the panel.
/// </para>
/// <para>
/// The swap chain is released when the panel is unloaded, detached, or the engine is disposed,
/// and made again when the panel is loaded while still attached.
/// </para>
/// </remarks>
public sealed partial class StudioPreviewPanel : SwapChainPanel, IStudioPreviewSurface
{
    private const int BufferCount = 2;

    // Present returns at once and the compositor shows the newest frame at its next pass, so the
    // render thread is never held up. Frames arrive at the recording's rate, far below the display's.
    private const uint SyncInterval = 0;

    private readonly DispatcherQueue _dispatcher;
    private readonly object _sync = new();

    // UI thread.
    private StudioPreviewEngine? _engine;

    // What layout wants and what the buffers are. _sync.
    private EventHandler? _invalidated;
    private int _wantedWidth;
    private int _wantedHeight;
    private float _wantedScaleX = 1;
    private float _wantedScaleY = 1;
    private int _bufferWidth;
    private int _bufferHeight;
    private float _bufferScaleX;
    private float _bufferScaleY;
    private bool _hasBuffers;
    private int _generation;

    // Only touched inside the engine's calls, which never overlap.
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private nint _device;

    public StudioPreviewPanel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        CompositionScaleChanged += OnCompositionScaleChanged;
    }

    event EventHandler? IStudioPreviewSurface.Invalidated
    {
        add
        {
            lock (_sync)
            {
                _invalidated += value;
            }
        }

        remove
        {
            lock (_sync)
            {
                _invalidated -= value;
            }
        }
    }

    bool IStudioPreviewSurface.NeedsConfigure
    {
        get
        {
            lock (_sync)
            {
                if (_wantedWidth <= 0 || _wantedHeight <= 0)
                {
                    return _hasBuffers;
                }

                return !_hasBuffers
                    || _wantedWidth != _bufferWidth
                    || _wantedHeight != _bufferHeight
                    || _wantedScaleX != _bufferScaleX
                    || _wantedScaleY != _bufferScaleY;
            }
        }
    }

    /// <summary>The engine whose picture the panel shows, or null.</summary>
    public StudioPreviewEngine? Engine => _engine;

    /// <summary>
    /// Shows <paramref name="engine"/>'s picture in this panel, in place of any other engine's.
    /// If the panel is not in a window yet, the picture appears when it is loaded. UI thread.
    /// </summary>
    public void Attach(StudioPreviewEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (ReferenceEquals(_engine, engine))
        {
            return;
        }

        Detach();
        _engine = engine;
        if (IsLoaded)
        {
            NoteWantedSize();
            engine.AttachSurface(this);
        }
    }

    /// <summary>
    /// Stops showing the engine's picture and releases the swap chain. Also safe after the engine
    /// has been disposed. UI thread.
    /// </summary>
    public void Detach()
    {
        var engine = _engine;
        _engine = null;
        engine?.DetachSurface(this);
    }

    void IStudioPreviewSurface.Configure(ID3D11Device device)
    {
        int width;
        int height;
        float scaleX;
        float scaleY;
        lock (_sync)
        {
            width = _wantedWidth;
            height = _wantedHeight;
            scaleX = _wantedScaleX;
            scaleY = _wantedScaleY;
        }

        if (width <= 0 || height <= 0)
        {
            ReleaseSwapChain();
            return;
        }

        if (_swapChain is not null && _device != device.NativePointer)
        {
            ReleaseSwapChain();
        }

        if (_swapChain is null)
        {
            CreateSwapChain(device, width, height);
        }
        else if (width != _bufferWidth || height != _bufferHeight)
        {
            // The engine has let go of the back buffer. This can wait for queued presents, which is
            // why it happens here, on the render thread.
            _swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }

        // The panel lays a swap chain out as if one buffer pixel were one effective pixel. The
        // inverse of the composition scale brings it back to one buffer pixel per physical pixel.
        if (_swapChain2 is not null)
        {
            _swapChain2.MatrixTransform = new Matrix3x2(1f / scaleX, 0, 0, 1f / scaleY, 0, 0);
        }

        lock (_sync)
        {
            _bufferWidth = width;
            _bufferHeight = height;
            _bufferScaleX = scaleX;
            _bufferScaleY = scaleY;
            _hasBuffers = true;
        }
    }

    ID3D11Texture2D? IStudioPreviewSurface.AcquireTarget(out int pixelWidth, out int pixelHeight)
    {
        lock (_sync)
        {
            pixelWidth = _bufferWidth;
            pixelHeight = _bufferHeight;
        }

        return _swapChain?.GetBuffer<ID3D11Texture2D>(0);
    }

    void IStudioPreviewSurface.Present()
    {
        // A failure (a removed device above all) is the engine's to handle. An occluded window is not one.
        _swapChain?.Present(SyncInterval, PresentFlags.None).CheckError();
    }

    void IStudioPreviewSurface.ReleaseDeviceResources() => ReleaseSwapChain();

    private void CreateSwapChain(ID3D11Device device, int width, int height)
    {
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var swapChain = factory.CreateSwapChainForComposition(device, description, null!);
        _swapChain = swapChain;
        _swapChain2 = swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        _device = device.NativePointer;

        // The panel takes it on the UI thread. That is posted, never waited for: the UI thread may
        // be waiting for the render thread at this moment. The posted call holds its own reference.
        int generation;
        lock (_sync)
        {
            generation = ++_generation;
        }

        var reference = swapChain.QueryInterface<IDXGISwapChain1>();
        var queued = _dispatcher.TryEnqueue(() =>
        {
            var shown = false;
            try
            {
                bool current;
                lock (_sync)
                {
                    current = generation == _generation;
                }

                if (current)
                {
                    StudioPreviewPanelInterop.SetSwapChain(this, reference);
                    shown = true;
                }
            }
            catch (Exception ex)
            {
                // The picture stays empty until the engine makes the buffers again, which it does
                // by itself when the device was lost. An exception that leaves a dispatcher
                // callback would end the app instead.
                Debug.WriteLine($"Studio preview panel could not take its swap chain: {ex}");
            }
            finally
            {
                reference.Dispose();
            }

            // Whatever was presented before the panel took the swap chain is shown again.
            if (shown)
            {
                RaiseInvalidated();
            }
        });
        if (!queued)
        {
            reference.Dispose();
        }
    }

    private void ReleaseSwapChain()
    {
        if (_swapChain is null)
        {
            return;
        }

        int generation;
        lock (_sync)
        {
            generation = ++_generation;
            _hasBuffers = false;
            _bufferWidth = 0;
            _bufferHeight = 0;
        }

        // The panel lets go on the UI thread; until then its own reference keeps the swap chain alive.
        if (_dispatcher.HasThreadAccess)
        {
            ClearPanelSwapChain();
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                bool current;
                lock (_sync)
                {
                    current = generation == _generation;
                }

                if (current)
                {
                    ClearPanelSwapChain();
                }
            });
        }

        _swapChain2?.Dispose();
        _swapChain2 = null;
        _swapChain.Dispose();
        _swapChain = null;
        _device = 0;
    }

    /// <summary>Takes the swap chain away from the panel. UI thread.</summary>
    private void ClearPanelSwapChain()
    {
        try
        {
            StudioPreviewPanelInterop.SetSwapChain(this, null);
        }
        catch (Exception ex)
        {
            // Nothing depends on it: the panel is going away, or is about to be given another
            // swap chain. This runs in a dispatcher callback or an Unloaded handler, and an
            // exception that leaves either would end the app.
            Debug.WriteLine($"Studio preview panel could not let go of its swap chain: {ex}");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && _engine is { } engine)
        {
            NoteWantedSize();
            engine.AttachSurface(this);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            _engine?.DetachSurface(this);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => NoteWantedSize();

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args) => NoteWantedSize();

    /// <summary>Records the size the buffers should have and tells the engine when it changed. UI thread.</summary>
    private void NoteWantedSize()
    {
        var scaleX = CompositionScaleX;
        var scaleY = CompositionScaleY;
        if (!(scaleX > 0) || !(scaleY > 0))
        {
            return;
        }

        var width = (int)Math.Round(ActualWidth * scaleX);
        var height = (int)Math.Round(ActualHeight * scaleY);
        lock (_sync)
        {
            if (width == _wantedWidth && height == _wantedHeight && scaleX == _wantedScaleX && scaleY == _wantedScaleY)
            {
                return;
            }

            _wantedWidth = width;
            _wantedHeight = height;
            _wantedScaleX = scaleX;
            _wantedScaleY = scaleY;
        }

        RaiseInvalidated();
    }

    private void RaiseInvalidated()
    {
        EventHandler? handlers;
        lock (_sync)
        {
            handlers = _invalidated;
        }

        handlers?.Invoke(this, EventArgs.Empty);
    }
}
