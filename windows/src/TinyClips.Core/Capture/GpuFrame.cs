using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Core.Capture;

/// <summary>
/// A frame that lives entirely on the GPU: a BGRA render-target texture the encoder can consume
/// directly. Depending on the encoder backend the texture is either pool-owned (transcoder path,
/// exposed as an <see cref="IDirect3DSurface"/> for <c>MediaStreamSample.CreateFromDirect3D11Surface</c>)
/// or owned by a Media Foundation sample allocator (sink-writer path, see <see cref="MfSinkWriterEncoder"/>).
/// Call <see cref="Release"/> exactly once when the encoder no longer needs it.
/// </summary>
internal sealed class GpuFrame
{
    private readonly Action<GpuFrame> _onRelease;
    private int _released;

    internal GpuFrame(Action<GpuFrame> onRelease, ID3D11Texture2D texture, int width, int height)
    {
        _onRelease = onRelease;
        Texture = texture;
        Width = width;
        Height = height;
    }

    public ID3D11Texture2D Texture { get; }

    /// <summary>WinRT surface wrapper (transcoder path only).</summary>
    public IDirect3DSurface? Surface { get; internal set; }

    /// <summary>Backend-specific owner handle (the sink-writer path stores its <c>IMFSample</c> here).</summary>
    public IDisposable? BackendSample { get; internal set; }

    public int Width { get; }

    public int Height { get; }

    public TimeSpan Pts { get; internal set; }

    /// <summary>Stopwatch timestamp when the frame was handed to the encoder (for hold-time stats).</summary>
    public long HandedOffTimestamp { get; internal set; }

    internal void Rented()
    {
        Volatile.Write(ref _released, 0);
    }

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _onRelease(this);
        }
    }
}

/// <summary>Hands out encoder-ready GPU frames; implementations bound how many are in flight.</summary>
internal interface IGpuFrameAllocator : IDisposable
{
    /// <summary>Frames allocated so far (high-water mark of concurrent in-flight frames).</summary>
    int Allocated { get; }

    int MaxCapacity { get; }

    bool TryAcquire(out GpuFrame frame);
}

/// <summary>
/// Pool of encoder-ready textures that grows on demand up to a hard cap. Bounding the pool (not
/// just the channel) bounds VRAM: at 4K each BGRA frame is ~33 MB. The encoder pipeline holds
/// input surfaces for its look-ahead / B-frame window (measured 50–200 ms on AMD VCN), so the pool
/// must cover that latency at the target frame rate or frames are dropped at the source.
/// </summary>
internal sealed class GpuFrameTexturePool : IGpuFrameAllocator
{
    private readonly Stack<GpuFrame> _free = new();
    private readonly List<GpuFrame> _all = new();
    private readonly object _gate = new();
    private readonly ID3D11Device _device;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public GpuFrameTexturePool(ID3D11Device device, int width, int height, int initialCapacity, int maxCapacity)
    {
        _device = device;
        _width = width;
        _height = height;
        MaxCapacity = Math.Max(initialCapacity, maxCapacity);
        for (var i = 0; i < initialCapacity; i++)
        {
            _free.Push(CreateFrame());
        }
    }

    public int MaxCapacity { get; }

    public int Allocated
    {
        get
        {
            lock (_gate)
            {
                return _all.Count;
            }
        }
    }

    public int Available
    {
        get
        {
            lock (_gate)
            {
                return _free.Count;
            }
        }
    }

    public bool TryAcquire(out GpuFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }

            if (_free.Count == 0)
            {
                if (_all.Count >= MaxCapacity)
                {
                    frame = null!;
                    return false;
                }

                _free.Push(CreateFrame());
            }

            frame = _free.Pop();
            frame.Rented();
            return true;
        }
    }

    private GpuFrame CreateFrame()
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // RenderTarget for Direct2D compositing; ShaderResource for the encoder's
            // colour-space converter. No CPU access so the driver keeps it in VRAM.
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        var frame = new GpuFrame(Return, texture, _width, _height)
        {
            Surface = WgcInterop.CreateDirect3DSurface(texture),
        };
        _all.Add(frame);
        return frame;
    }

    private void Return(GpuFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _free.Push(frame);
        }
    }

    public void Dispose()
    {
        List<GpuFrame> all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _free.Clear();
            all = new List<GpuFrame>(_all);
            _all.Clear();
        }

        // Textures may still be referenced by in-flight MediaStreamSamples; releasing our COM
        // reference is safe because the sample holds its own on the IDirect3DSurface.
        foreach (var frame in all)
        {
            frame.Surface?.Dispose();
            frame.Texture.Dispose();
        }
    }
}
