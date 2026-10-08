using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using WinRT;

namespace StudioEngineSpike.Interop;

/// <summary>
/// COM interfaces behind the two presenters and the self-screenshot, all source-generated
/// (<c>[GeneratedComInterface]</c>) so they work under CsWinRT's ComWrappers and NativeAOT.
/// </summary>
internal static partial class PanelInterop
{
    /// <summary>WinUI 3's ISwapChainPanelNative (microsoft.ui.xaml.media.dxinterop.h). Not the UWP IID.</summary>
    [GeneratedComInterface]
    [Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
    internal partial interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(nint swapChain);
    }

    /// <summary>Win2D's way out to the DXGI object behind a wrapper (Microsoft.Graphics.Canvas.native.h).</summary>
    [GeneratedComInterface]
    [Guid("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A")]
    internal partial interface ICanvasResourceWrapperNative
    {
        [PreserveSig]
        int GetNativeResource(nint device, float dpi, in Guid iid, out nint resource);
    }

    [GeneratedComInterface]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    internal partial interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetWindowText(nint window, char* text, int maxCount);

    /// <summary>
    /// Is the given window the one on top, or is another application's full-screen window in front
    /// of it? In the second case DWM does not put the spike's swap chains on the display at all
    /// and DXGI frame statistics stay at zero; screenshots still work.
    /// </summary>
    internal static unsafe (bool IsOurs, bool CoversMonitor, string Title) DescribeForeground(nint ourWindow)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return (false, false, "(none)");
        }

        var buffer = stackalloc char[128];
        var length = GetWindowText(foreground, buffer, 128);
        var title = length > 0 ? new string(buffer, 0, length) : "(untitled)";
        var covers = false;
        var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
        if (GetWindowRect(foreground, out var rect) && GetMonitorInfo(MonitorFromWindow(foreground, 2), ref info))
        {
            covers = rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
        }

        return (foreground == ourWindow, covers, title);
    }

    private static readonly Guid SwapChainPanelNativeGuid = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");
    private static readonly Guid CanvasResourceWrapperNativeGuid = new("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A");
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiSwapChain1Guid = new("790A45F7-0D42-4876-983A-0A55CFE6F4AA");

    /// <summary>Attaches (or with null detaches) a DXGI composition swap chain to a XAML SwapChainPanel. UI thread only.</summary>
    internal static unsafe void SetSwapChain(Microsoft.UI.Xaml.Controls.SwapChainPanel panel, IDXGISwapChain1? swapChain)
    {
        var panelPointer = ((IWinRTObject)panel).NativeObject.ThisPtr;
        Direct3DInterop.ThrowIfFailed(Marshal.QueryInterface(panelPointer, in SwapChainPanelNativeGuid, out var nativePointer), "QueryInterface(ISwapChainPanelNative)");
        try
        {
            var native = ComInterfaceMarshaller<ISwapChainPanelNative>.ConvertToManaged((void*)nativePointer)!;
            Direct3DInterop.ThrowIfFailed(native.SetSwapChain(swapChain?.NativePointer ?? 0), "ISwapChainPanelNative.SetSwapChain");
        }
        finally
        {
            ComInterfaceMarshaller<ISwapChainPanelNative>.Free((void*)nativePointer);
        }
    }

    /// <summary>The IDXGISwapChain1 inside a Win2D CanvasSwapChain (for frame statistics). The caller owns the result.</summary>
    internal static unsafe IDXGISwapChain1 GetSwapChain(Microsoft.Graphics.Canvas.CanvasSwapChain swapChain)
    {
        var wrapperPointer = ((IWinRTObject)swapChain).NativeObject.ThisPtr;
        Direct3DInterop.ThrowIfFailed(Marshal.QueryInterface(wrapperPointer, in CanvasResourceWrapperNativeGuid, out var nativePointer), "QueryInterface(ICanvasResourceWrapperNative)");
        try
        {
            var native = ComInterfaceMarshaller<ICanvasResourceWrapperNative>.ConvertToManaged((void*)nativePointer)!;
            Direct3DInterop.ThrowIfFailed(native.GetNativeResource(0, 0f, in DxgiSwapChain1Guid, out var resource), "ICanvasResourceWrapperNative.GetNativeResource");
            return new IDXGISwapChain1(resource);
        }
        finally
        {
            ComInterfaceMarshaller<ICanvasResourceWrapperNative>.Free((void*)nativePointer);
        }
    }

    /// <summary>A Windows.Graphics.Capture item for a top-level window (used to screenshot the spike's own window).</summary>
    internal static unsafe GraphicsCaptureItem CreateCaptureItemForWindow(nint hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        nint interopPointer = 0;
        nint itemPointer = 0;
        try
        {
            Direct3DInterop.ThrowIfFailed(Marshal.QueryInterface(factory.ThisPtr, in GraphicsCaptureItemInteropGuid, out interopPointer), "QueryInterface(IGraphicsCaptureItemInterop)");
            var interop = ComInterfaceMarshaller<IGraphicsCaptureItemInterop>.ConvertToManaged((void*)interopPointer)!;
            Direct3DInterop.ThrowIfFailed(interop.CreateForWindow(hwnd, in GraphicsCaptureItemGuid, out itemPointer), "IGraphicsCaptureItemInterop.CreateForWindow");
            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
            return item;
        }
        finally
        {
            if (itemPointer != 0)
            {
                Marshal.Release(itemPointer);
            }

            if (interopPointer != 0)
            {
                ComInterfaceMarshaller<IGraphicsCaptureItemInterop>.Free((void*)interopPointer);
            }
        }
    }
}
