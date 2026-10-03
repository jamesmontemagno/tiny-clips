using StudioEngineSpike.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;

namespace StudioEngineSpike.Engine;

/// <summary>
/// The ONE Direct3D 11 device every part of the spike shares: MediaPlayer frame copies, source
/// reader decode, Direct2D compositing, the sink writer's encoder, and the swap chains.
/// Created the same way as <c>WgcInterop.GetSharedDevice</c> in Core (hardware, BGRA + video
/// support, multithread-protected immediate context).
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    private readonly ID3D11Query _eventQuery;
    private ID3D11Texture2D? _regionStaging;
    private int _regionStagingWidth;
    private int _regionStagingHeight;
    private Format _regionStagingFormat = Format.Unknown;
    private ID3D11Texture2D? _fullStaging;
    private int _fullStagingWidth;
    private int _fullStagingHeight;
    private Format _fullStagingFormat = Format.Unknown;
    private ID3D11Texture2D? _packStaging;
    private int _packStagingWidth;
    private int _packStagingHeight;
    private Format _packStagingFormat = Format.Unknown;

    private GraphicsDevice(ID3D11Device device, bool videoSupport)
    {
        Device = device;
        Context = device.ImmediateContext;
        VideoSupport = videoSupport;
        WinRTDevice = Direct3DInterop.CreateDirect3DDevice(device);
        _eventQuery = device.CreateQuery(QueryType.Event, QueryFlags.None);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var description = adapter.Description;
        AdapterName = description.Description;
        AdapterVendorId = description.VendorId;
        DedicatedVideoMemory = (long)(ulong)description.DedicatedVideoMemory;
        SharedSystemMemory = (long)(ulong)description.SharedSystemMemory;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    /// <summary>WinRT view of <see cref="Device"/> (MediaPlayer surfaces, Win2D, WGC).</summary>
    public IDirect3DDevice WinRTDevice { get; }

    public bool VideoSupport { get; }

    public string AdapterName { get; }

    public uint AdapterVendorId { get; }

    public long DedicatedVideoMemory { get; }

    public long SharedSystemMemory { get; }

    /// <summary>
    /// Serializes every use of the immediate context and of Direct2D across the spike's threads
    /// (MediaPlayer callbacks, render loop, encoder pump). D3D11's own multithread protection only
    /// makes single calls atomic; Direct2D sets pipeline state across several calls.
    /// </summary>
    public object Gate { get; } = new();

    public static GraphicsDevice Create()
    {
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var flagSets = new[]
        {
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            DeviceCreationFlags.BgraSupport,
        };

        foreach (var flags in flagSets)
        {
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, featureLevels, out ID3D11Device? device);
            if (result.Failure || device is null)
            {
                continue;
            }

            using (var multithread = device.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true);
            }

            return new GraphicsDevice(device, flags.HasFlag(DeviceCreationFlags.VideoSupport));
        }

        throw new InvalidOperationException("Could not create a hardware Direct3D 11 device.");
    }

    /// <summary>A BGRA texture Direct2D can draw into and sample from, and the encoder can read.</summary>
    public ID3D11Texture2D CreateRenderTexture(int width, int height) => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    public IDirect3DSurface CreateSurface(ID3D11Texture2D texture) => Direct3DInterop.CreateDirect3DSurface(texture);

    /// <summary>
    /// Blocks until the GPU has executed everything queued so far. Used only to measure the real
    /// cost of a composite (CPU-side timings stop at command submission).
    /// </summary>
    public void WaitForGpu()
    {
        Context.End(_eventQuery);
        Context.Flush();
        var spins = 0;
        while (!Context.IsDataAvailable(_eventQuery, AsyncGetDataFlags.None))
        {
            if (++spins > 64)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    /// <summary>
    /// Reads a small region of a 32-bit texture (BGRA or BGRX, any subresource) into
    /// <paramref name="destination"/> as tightly packed rows. This forces a GPU sync, so only
    /// measurement code calls it.
    /// </summary>
    public unsafe void ReadRegion(ID3D11Texture2D texture, uint subresource, int x, int y, int width, int height, Span<byte> destination)
    {
        if (destination.Length < width * height * 4)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        // CopySubresourceRegion needs the same format family on both sides: a BGRX frame from the
        // video processor cannot be copied into a BGRA staging texture.
        var format = texture.Description.Format;
        if (_regionStaging is null || _regionStagingWidth < width || _regionStagingHeight < height || _regionStagingFormat != format)
        {
            _regionStaging?.Dispose();
            _regionStagingWidth = Math.Max(width, _regionStagingFormat == format ? _regionStagingWidth : 0);
            _regionStagingHeight = Math.Max(height, _regionStagingFormat == format ? _regionStagingHeight : 0);
            _regionStagingFormat = format;
            _regionStaging = CreateStaging(_regionStagingWidth, _regionStagingHeight, format);
        }

        Context.CopySubresourceRegion(_regionStaging, 0, 0, 0, 0, texture, subresource, new Box(x, y, 0, x + width, y + height, 1));
        var mapped = Context.Map(_regionStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)mapped.DataPointer;
            fixed (byte* target = destination)
            {
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(source + ((long)row * mapped.RowPitch), target + ((long)row * width * 4), width * 4, width * 4);
                }
            }
        }
        finally
        {
            Context.Unmap(_regionStaging, 0);
        }
    }

    /// <summary>
    /// Reads several small regions of one 32-bit texture with a single GPU sync: they are packed
    /// one under the other into a staging texture and mapped once. Each result is tightly packed.
    /// </summary>
    public unsafe byte[][] ReadRegions(ID3D11Texture2D texture, uint subresource, ReadOnlySpan<(int X, int Y, int Width, int Height)> regions)
    {
        var width = 1;
        var height = 0;
        foreach (var region in regions)
        {
            width = Math.Max(width, region.Width);
            height += region.Height;
        }

        var format = texture.Description.Format;
        if (_packStaging is null || _packStagingWidth < width || _packStagingHeight < height || _packStagingFormat != format)
        {
            _packStaging?.Dispose();
            _packStagingWidth = Math.Max(width, _packStagingFormat == format ? _packStagingWidth : 0);
            _packStagingHeight = Math.Max(height, _packStagingFormat == format ? _packStagingHeight : 0);
            _packStagingFormat = format;
            _packStaging = CreateStaging(_packStagingWidth, _packStagingHeight, format);
        }

        var top = 0;
        foreach (var region in regions)
        {
            Context.CopySubresourceRegion(_packStaging, 0, 0, (uint)top, 0, texture, subresource, new Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1));
            top += region.Height;
        }

        var results = new byte[regions.Length][];
        var mapped = Context.Map(_packStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)mapped.DataPointer;
            top = 0;
            for (var index = 0; index < regions.Length; index++)
            {
                var region = regions[index];
                var pixels = new byte[region.Width * region.Height * 4];
                fixed (byte* target = pixels)
                {
                    for (var row = 0; row < region.Height; row++)
                    {
                        Buffer.MemoryCopy(source + ((long)(top + row) * mapped.RowPitch), target + ((long)row * region.Width * 4), region.Width * 4, region.Width * 4);
                    }
                }

                results[index] = pixels;
                top += region.Height;
            }
        }
        finally
        {
            Context.Unmap(_packStaging, 0);
        }

        return results;
    }

    /// <summary>Reads a whole BGRA texture back as tightly packed top-down BGRA.</summary>
    public unsafe byte[] ReadTexture(ID3D11Texture2D texture, uint subresource = 0)
    {
        var description = texture.Description;
        var width = (int)description.Width;
        var height = (int)description.Height;
        if (_fullStaging is null || _fullStagingWidth != width || _fullStagingHeight != height || _fullStagingFormat != description.Format)
        {
            _fullStaging?.Dispose();
            _fullStaging = CreateStaging(width, height, description.Format);
            _fullStagingWidth = width;
            _fullStagingHeight = height;
            _fullStagingFormat = description.Format;
        }

        Context.CopySubresourceRegion(_fullStaging, 0, 0, 0, 0, texture, subresource, null);
        var pixels = new byte[width * height * 4];
        var mapped = Context.Map(_fullStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)mapped.DataPointer;
            fixed (byte* target = pixels)
            {
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(source + ((long)row * mapped.RowPitch), target + ((long)row * width * 4), width * 4, width * 4);
                }
            }
        }
        finally
        {
            Context.Unmap(_fullStaging, 0);
        }

        return pixels;
    }

    private ID3D11Texture2D CreateStaging(int width, int height, Format format) => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Staging,
        BindFlags = BindFlags.None,
        CPUAccessFlags = CpuAccessFlags.Read,
        MiscFlags = ResourceOptionFlags.None,
    });

    /// <summary>A sampleable texture with the given format (for copies of decoder output).</summary>
    public ID3D11Texture2D CreateSourceTexture(int width, int height, Format format) => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    public void Dispose()
    {
        _regionStaging?.Dispose();
        _fullStaging?.Dispose();
        _packStaging?.Dispose();
        _eventQuery.Dispose();
        (WinRTDevice as IDisposable)?.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
