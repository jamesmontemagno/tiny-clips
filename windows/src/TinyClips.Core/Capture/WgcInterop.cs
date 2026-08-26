using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace TinyClips.Core.Capture;

/// <summary>
/// Shared Windows.Graphics.Capture (WGC) + Direct3D 11 interop used by both the
/// single-frame screenshot engine and the continuous video/GIF recorders.
///
/// Uses source-generated COM interop (ComWrappers-compatible). Classic
/// <c>[ComImport]</c> + <c>Marshal.GetTypedObjectForIUnknown</c> throws
/// "Specified cast is not valid" under CsWinRT, so these use
/// <c>[GeneratedComInterface]</c> per the winapp CLI sample.
/// </summary>
internal static partial class WgcInterop
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid Direct3DDxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid D3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [GeneratedComInterface]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    internal partial interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [GeneratedComInterface]
    [System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    internal partial interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out nint ppvObject);
    }

    private static readonly object SharedDeviceGate = new();
    private static ID3D11Device? _sharedD3DDevice;
    private static IDirect3DDevice? _sharedDirect3DDevice;

    /// <summary>
    /// Returns a process-wide shared Direct3D 11 device pair for WGC capture. Creating a D3D11
    /// device costs tens of milliseconds (driver load, adapter enumeration), and the capture flow
    /// used to do it three or four times per capture (one per monitor backdrop, one for the
    /// screenshot, one for the recorder). The shared device is multithread-protected so the
    /// immediate context can be used from the WGC frame-pool threads of concurrent sessions, and it
    /// is recreated transparently if the GPU reports device removal. Callers must not dispose it.
    /// </summary>
    internal static (ID3D11Device D3D, IDirect3DDevice WinRT) GetSharedDevice()
    {
        lock (SharedDeviceGate)
        {
            if (_sharedD3DDevice is not null && _sharedDirect3DDevice is not null)
            {
                if (_sharedD3DDevice.DeviceRemovedReason.Success)
                {
                    return (_sharedD3DDevice, _sharedDirect3DDevice);
                }

                _sharedDirect3DDevice.Dispose();
                _sharedD3DDevice.Dispose();
                _sharedDirect3DDevice = null;
                _sharedD3DDevice = null;
            }

            var d3d = CreateD3D11Device()
                ?? throw new InvalidOperationException("Failed to create a Direct3D 11 device.");

            // The device is shared across concurrent WGC sessions whose frame-pool callbacks run
            // on different threads, so multithread protection on the immediate context is
            // required — not best-effort. Without it, sharing would be unsafe; fail instead.
            try
            {
                using var multithread = d3d.QueryInterfaceOrNull<ID3D11Multithread>()
                    ?? throw new NotSupportedException("ID3D11Multithread is not available on this device.");
                multithread.SetMultithreadProtected(true);
                if (!multithread.GetMultithreadProtected())
                {
                    throw new NotSupportedException("Failed to enable multithread protection on the Direct3D 11 device.");
                }
            }
            catch
            {
                d3d.Dispose();
                throw;
            }

            var winrt = CreateDirect3DDevice(d3d);
            if (winrt is null)
            {
                d3d.Dispose();
                throw new InvalidOperationException("Failed to create the WinRT IDirect3DDevice.");
            }

            _sharedD3DDevice = d3d;
            _sharedDirect3DDevice = winrt;
            return (d3d, winrt);
        }
    }

    /// <summary>Creates the shared device ahead of time (e.g. at app launch) so the first capture doesn't pay for it.</summary>
    internal static void WarmUpSharedDevice()
    {
        try
        {
            _ = GetSharedDevice();
        }
        catch
        {
            // Warm-up is opportunistic; the real capture path surfaces errors.
        }
    }

    internal static ID3D11Device? CreateD3D11Device()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        };

        // VideoSupport lets Media Foundation's hardware encoder MFTs bind textures created on this
        // device directly (the GPU recording path hands encoder samples whole D3D11 surfaces).
        // Some drivers/WARP reject the flag, so fall back to a plain BGRA device.
        var flagSets = new[]
        {
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            DeviceCreationFlags.BgraSupport,
        };

        foreach (var driverType in new[] { DriverType.Hardware, DriverType.Warp })
        {
            foreach (var flags in flagSets)
            {
                var result = D3D11.D3D11CreateDevice(
                    null,
                    driverType,
                    flags,
                    featureLevels,
                    out var device);

                if (result.Success)
                {
                    return device;
                }
            }
        }

        return null;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint graphicsSurface);

    /// <summary>
    /// Wraps a D3D11 texture as a WinRT <see cref="IDirect3DSurface"/> so it can be handed to
    /// <c>MediaStreamSample.CreateFromDirect3D11Surface</c> without a CPU round-trip.
    /// </summary>
    internal static IDirect3DSurface CreateDirect3DSurface(ID3D11Texture2D texture)
    {
        using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
        CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var pInspectable)
            .ThrowIfFailed("CreateDirect3D11SurfaceFromDXGISurface");
        var surface = MarshalInterface<IDirect3DSurface>.FromAbi(pInspectable);
        Marshal.Release(pInspectable);
        return surface;
    }

    internal static IDirect3DDevice? CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        if (dxgiDevice is null)
        {
            return null;
        }

        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pInspectable);
        if (hr != 0)
        {
            return null;
        }

        var device = MarshalInterface<IDirect3DDevice>.FromAbi(pInspectable);
        Marshal.Release(pInspectable);
        return device;
    }

    private delegate int CreateItemCall(IGraphicsCaptureItemInterop interop, out nint itemPtr);

    internal static GraphicsCaptureItem CreateCaptureItemForMonitor(nint hMonitor)
        => CreateCaptureItem((IGraphicsCaptureItemInterop interop, out nint p) =>
            interop.CreateForMonitor(hMonitor, in GraphicsCaptureItemGuid, out p));

    internal static GraphicsCaptureItem CreateCaptureItemForWindow(nint hWnd)
        => CreateCaptureItem((IGraphicsCaptureItemInterop interop, out nint p) =>
            interop.CreateForWindow(hWnd, in GraphicsCaptureItemGuid, out p));

    private static unsafe GraphicsCaptureItem CreateCaptureItem(CreateItemCall create)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        nint interopPtr = 0;
        nint itemPtr = 0;
        try
        {
            Marshal.QueryInterface(factory.ThisPtr, in GraphicsCaptureItemInteropGuid, out interopPtr)
                .ThrowIfFailed("QueryInterface(IGraphicsCaptureItemInterop)");

            var interop = ComInterfaceMarshaller<IGraphicsCaptureItemInterop>.ConvertToManaged((void*)interopPtr)!;
            interopPtr = 0;

            create(interop, out itemPtr)
                .ThrowIfFailed("IGraphicsCaptureItemInterop.CreateForMonitor/Window");

            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            itemPtr = 0;
            return item;
        }
        finally
        {
            if (itemPtr != 0)
            {
                Marshal.Release(itemPtr);
            }

            if (interopPtr != 0)
            {
                ComInterfaceMarshaller<IGraphicsCaptureItemInterop>.Free((void*)interopPtr);
            }
        }
    }

    internal static unsafe ID3D11Texture2D GetTextureFromFrame(Direct3D11CaptureFrame frame)
    {
        var surfacePtr = ((IWinRTObject)frame.Surface).NativeObject.ThisPtr;
        nint accessPtr = 0;
        nint texturePtr = 0;
        try
        {
            Marshal.QueryInterface(surfacePtr, in Direct3DDxgiInterfaceAccessGuid, out accessPtr)
                .ThrowIfFailed("QueryInterface(IDirect3DDxgiInterfaceAccess)");

            var access = ComInterfaceMarshaller<IDirect3DDxgiInterfaceAccess>.ConvertToManaged((void*)accessPtr)!;
            accessPtr = 0;

            access.GetInterface(in D3D11Texture2DGuid, out texturePtr)
                .ThrowIfFailed("IDirect3DDxgiInterfaceAccess.GetInterface(ID3D11Texture2D)");

            var texture = new ID3D11Texture2D(texturePtr);
            texturePtr = 0;
            return texture;
        }
        finally
        {
            if (texturePtr != 0)
            {
                Marshal.Release(texturePtr);
            }

            if (accessPtr != 0)
            {
                ComInterfaceMarshaller<IDirect3DDxgiInterfaceAccess>.Free((void*)accessPtr);
            }
        }
    }

    /// <summary>Best-effort toggle of cursor capture and the capture border.</summary>
    internal static void TryConfigureSession(GraphicsCaptureSession session, bool includeCursor)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            {
                session.IsCursorCaptureEnabled = includeCursor;
            }

            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                session.IsBorderRequired = false;
            }
        }
        catch
        {
            // Capability toggles are best-effort; ignore if the runtime rejects them.
        }
    }
}

internal static class HResultExtensions
{
    public static void ThrowIfFailed(this int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"{operation} failed with HRESULT 0x{hr:X8}.", hr);
        }
    }
}
