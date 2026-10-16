using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// One Direct3D 11 device for Studio rendering: decoding, drawing and encoding all happen on it,
/// so frames never leave the GPU.
/// </summary>
/// <remarks>
/// The immediate context is shared by everything on the device. Direct3D's multithread protection
/// makes single calls safe, but not a renderer's sequence of them, so every user of
/// <see cref="Context"/> and of a <see cref="StudioSceneRenderer"/> on this device takes
/// <see cref="Gate"/> while it draws or reads back.
/// </remarks>
public sealed class StudioGraphicsDevice : IDisposable
{
    private const uint MicrosoftVendorId = 0x1414;
    private const uint BasicRenderDeviceId = 0x8C;

    private ID3D11Texture2D? _readback;
    private Texture2DDescription _readbackDescription;

    private StudioGraphicsDevice(ID3D11Device device, bool videoSupport, bool software, string adapterName)
    {
        Device = device;
        Context = device.ImmediateContext;
        VideoSupport = videoSupport;
        IsSoftware = software;
        AdapterName = adapterName;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    /// <summary>Whether the device was created with Direct3D video support (hardware decode and conversion).</summary>
    public bool VideoSupport { get; }

    /// <summary>
    /// True when the device is WARP, Direct3D's software rasterizer: either asked for, or all a PC
    /// without usable graphics hardware has.
    /// </summary>
    public bool IsSoftware { get; }

    public string AdapterName { get; }

    /// <summary>Serializes use of <see cref="Context"/> and of renderers on this device.</summary>
    public object Gate { get; } = new();

    /// <summary>The graphics hardware if there is any, otherwise WARP.</summary>
    public static StudioGraphicsDevice CreateHardware() => Create(preferWarp: false);

    public static StudioGraphicsDevice CreateWarp() => Create(preferWarp: true);

    public static StudioGraphicsDevice Create(bool preferWarp = false)
    {
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var flagSets = new[]
        {
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            DeviceCreationFlags.BgraSupport,
        };

        Result lastResult = default;
        foreach (var driver in preferWarp ? new[] { DriverType.Warp } : new[] { DriverType.Hardware, DriverType.Warp })
        {
            foreach (var flags in flagSets)
            {
                lastResult = D3D11.D3D11CreateDevice(null, driver, flags, featureLevels, out ID3D11Device? device);
                if (lastResult.Failure || device is null)
                {
                    continue;
                }

                using (var multithread = device.QueryInterface<ID3D11Multithread>())
                {
                    multithread.SetMultithreadProtected(true);
                }

                var (software, name) = DescribeAdapter(device, driver);
                return new StudioGraphicsDevice(device, flags.HasFlag(DeviceCreationFlags.VideoSupport), software, name);
            }
        }

        throw new StudioGraphicsDeviceUnavailableException($"Could not create a Direct3D 11 device for Studio rendering (0x{lastResult.Code:X8}).");
    }

    /// <summary>A texture a <see cref="StudioSceneRenderer"/> can draw into and a later draw can sample.</summary>
    public ID3D11Texture2D CreateRenderTexture(int width, int height, Format format = Format.B8G8R8A8_UNorm) => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    /// <summary>
    /// Copies a four-bytes-per-pixel texture to system memory, top row first, tightly packed. This
    /// waits for the GPU. Call it with <see cref="Gate"/> held.
    /// </summary>
    public byte[] ReadTexture(ID3D11Texture2D texture, uint subresource = 0)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var description = texture.Description;
        var pixels = new byte[checked((int)description.Width * (int)description.Height * 4)];
        ReadTexture(texture, subresource, pixels);
        return pixels;
    }

    /// <summary>As <see cref="ReadTexture(ID3D11Texture2D, uint)"/>, into a buffer the caller owns.</summary>
    public unsafe void ReadTexture(ID3D11Texture2D texture, uint subresource, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var description = texture.Description;
        if (description.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.R8G8B8A8_UNorm))
        {
            throw new ArgumentException($"Only 32-bit colour textures can be read back; this one is {description.Format}.", nameof(texture));
        }

        var width = (int)description.Width;
        var height = (int)description.Height;
        var rowBytes = width * 4;
        if (destination.Length < rowBytes * height)
        {
            throw new ArgumentException("The buffer is smaller than the texture.", nameof(destination));
        }

        try
        {
            if (_readback is null
                || _readbackDescription.Width != description.Width
                || _readbackDescription.Height != description.Height
                || _readbackDescription.Format != description.Format)
            {
                _readback?.Dispose();
                _readback = null;
                _readbackDescription = new Texture2DDescription
                {
                    Width = description.Width,
                    Height = description.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = description.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _readback = Device.CreateTexture2D(_readbackDescription);
            }

            Context.CopySubresourceRegion(_readback, 0, 0, 0, 0, texture, subresource, null);
            var mapped = Context.Map(_readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var source = (byte*)mapped.DataPointer;
                fixed (byte* target = destination)
                {
                    for (var row = 0; row < height; row++)
                    {
                        Buffer.MemoryCopy(source + ((long)row * mapped.RowPitch), target + ((long)row * rowBytes), rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                Context.Unmap(_readback, 0);
            }
        }
        catch (SharpGenException ex) when (StudioMediaFoundation.IsDeviceLost(ex))
        {
            throw new StudioDeviceLostException("The Studio rendering device was lost and must be rebuilt.", ex);
        }
    }

    public void Dispose()
    {
        _readback?.Dispose();
        _readback = null;
        Context.Dispose();
        Device.Dispose();
    }

    private static (bool Software, string Name) DescribeAdapter(ID3D11Device device, DriverType driver)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var description = adapter.Description;

            // A PC with no graphics hardware answers a request for a hardware device with the
            // Microsoft Basic Render Driver, which is WARP under another name.
            var basicRender = description.VendorId == MicrosoftVendorId && description.DeviceId == BasicRenderDeviceId;
            return (driver == DriverType.Warp || basicRender, description.Description.TrimEnd('\0', ' '));
        }
        catch (SharpGenException)
        {
            return (driver == DriverType.Warp, driver == DriverType.Warp ? "WARP" : "unknown adapter");
        }
    }
}

/// <summary>Raised when Direct2D or Direct3D reports that the rendering device must be rebuilt.</summary>
public sealed class StudioDeviceLostException : Exception
{
    public StudioDeviceLostException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when Studio cannot create any Direct3D device for rendering or export.</summary>
public sealed class StudioGraphicsDeviceUnavailableException : Exception
{
    public StudioGraphicsDeviceUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
