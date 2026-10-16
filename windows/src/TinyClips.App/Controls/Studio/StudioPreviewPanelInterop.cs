using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.UI.Xaml.Controls;
using Vortice.DXGI;
using WinRT;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The one COM interface <see cref="StudioPreviewPanel"/> needs that WinUI does not project:
/// the way to hand a DXGI swap chain to a <see cref="SwapChainPanel"/>. Source-generated, so it
/// works under CsWinRT's ComWrappers and NativeAOT.
/// </summary>
internal static partial class StudioPreviewPanelInterop
{
    private static readonly Guid SwapChainPanelNativeId = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    /// <summary>WinUI 3's ISwapChainPanelNative (microsoft.ui.xaml.media.dxinterop.h). The UWP interface has another IID.</summary>
    [GeneratedComInterface]
    [Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
    internal partial interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(nint swapChain);
    }

    /// <summary>Gives the panel a swap chain to show, or with null takes it away. UI thread only.</summary>
    internal static unsafe void SetSwapChain(SwapChainPanel panel, IDXGISwapChain1? swapChain)
    {
        var panelPointer = ((IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(panelPointer, in SwapChainPanelNativeId, out var nativePointer));
        try
        {
            var native = ComInterfaceMarshaller<ISwapChainPanelNative>.ConvertToManaged((void*)nativePointer)!;
            Marshal.ThrowExceptionForHR(native.SetSwapChain(swapChain?.NativePointer ?? 0));
        }
        finally
        {
            ComInterfaceMarshaller<ISwapChainPanelNative>.Free((void*)nativePointer);
        }
    }
}
