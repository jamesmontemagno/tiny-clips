using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace StudioEngineSpike.Interop;

/// <summary>
/// WinRT ⇄ Direct3D 11 interop, copied from <c>TinyClips.Core.Capture.WgcInterop</c> (the spike is
/// standalone and cannot reference Core). Uses source-generated COM interop so it works under
/// CsWinRT's ComWrappers and NativeAOT.
/// </summary>
internal static partial class Direct3DInterop
{
    private static readonly Guid Direct3DDxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid D3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [GeneratedComInterface]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    internal partial interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out nint ppvObject);
    }

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint graphicsSurface);

    /// <summary>Wraps a D3D11 device as the WinRT <see cref="IDirect3DDevice"/> MediaPlayer, WGC and Win2D take.</summary>
    internal static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        ThrowIfFailed(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable), nameof(CreateDirect3D11DeviceFromDXGIDevice));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Wraps a D3D11 texture as a WinRT <see cref="IDirect3DSurface"/> (no copy).</summary>
    internal static IDirect3DSurface CreateDirect3DSurface(ID3D11Texture2D texture)
    {
        using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
        ThrowIfFailed(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var inspectable), nameof(CreateDirect3D11SurfaceFromDXGISurface));
        try
        {
            return MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Gets the D3D11 texture behind a WinRT surface. The caller owns the returned reference.</summary>
    internal static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
        nint accessPtr = 0;
        nint texturePtr = 0;
        try
        {
            ThrowIfFailed(Marshal.QueryInterface(surfacePtr, in Direct3DDxgiInterfaceAccessGuid, out accessPtr), "QueryInterface(IDirect3DDxgiInterfaceAccess)");
            var access = ComInterfaceMarshaller<IDirect3DDxgiInterfaceAccess>.ConvertToManaged((void*)accessPtr)!;
            ThrowIfFailed(access.GetInterface(in D3D11Texture2DGuid, out texturePtr), "IDirect3DDxgiInterfaceAccess.GetInterface");
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

    internal static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"{operation} failed with HRESULT 0x{hr:X8}.", hr);
        }
    }
}
