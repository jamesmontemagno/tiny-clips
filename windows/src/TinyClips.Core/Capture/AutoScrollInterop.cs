using System.Runtime.InteropServices;

namespace TinyClips.Core.Capture;

/// <summary>
/// Public Win32 APIs used by auto-scroll: <c>PrintWindow</c> to capture a window even when it is
/// covered, and posted <c>WM_MOUSEWHEEL</c> messages to scroll it without moving the real mouse
/// or changing focus.
/// </summary>
internal static unsafe partial class AutoScrollNative
{
    internal const uint WmMouseWheel = 0x020A;
    internal const uint GaRoot = 2;
    internal const uint CwpSkipInvisible = 0x0001;
    internal const uint CwpSkipTransparent = 0x0004;
    internal const uint PwRenderFullContent = 0x00000002;
    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint DibRgbColors = 0;
    internal const uint CoInitMultithreaded = 0x0;
    internal const uint ClsCtxInprocServer = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    internal static partial int IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    internal static partial int ScreenToClient(nint hwnd, ref POINT point);

    [LibraryImport("user32.dll")]
    internal static partial nint ChildWindowFromPointEx(nint hwnd, POINT point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    internal static partial int PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial int PrintWindow(nint hwnd, nint hdc, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint hdc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteDC(nint hdc);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, RECT* value, uint size);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    /// <summary>
    /// Deepest visible child of <paramref name="root"/> at a screen point, even if other windows
    /// cover it. WinUI 3 receives input through a hidden <c>InputSiteWindowClass</c> child, so that
    /// is preferred when present.
    /// </summary>
    internal static nint DeepestChild(nint root, int screenX, int screenY)
    {
        var current = root;
        for (var i = 0; i < 32; i++)
        {
            var point = new POINT(screenX, screenY);
            ScreenToClient(current, ref point);
            var child = ChildWindowFromPointEx(current, point, CwpSkipInvisible | CwpSkipTransparent);
            if (child == 0 || child == current)
            {
                break;
            }

            current = child;
        }

        var inputSite = FindWindowEx(current, 0, "InputSiteWindowClass", null);
        return inputSite != 0 ? inputSite : current;
    }

    /// <summary>
    /// Posts one wheel message per notch (positive scrolls down). Posting, not injecting, leaves
    /// the real mouse and focus untouched. Some apps scroll a fixed step per message regardless of
    /// the delta, so notches are never combined into one message.
    /// </summary>
    internal static void PostWheel(nint target, int screenX, int screenY, int notches)
    {
        var delta = notches > 0 ? -120 : 120;
        var wParam = (nuint)(uint)((delta & 0xFFFF) << 16);
        var lParam = (nint)(((screenY & 0xFFFF) << 16) | (screenX & 0xFFFF));
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            PostMessage(target, WmMouseWheel, wParam, lParam);
        }
    }

    /// <summary>Visible bounds of a window in screen pixels (excludes the invisible resize border).</summary>
    internal static RECT VisibleBounds(nint hwnd, out RECT windowRect)
    {
        GetWindowRect(hwnd, out windowRect);
        RECT frame;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, &frame, (uint)sizeof(RECT)) < 0)
        {
            frame = windowRect;
        }

        return frame;
    }
}

/// <summary>
/// Auto-scroll surface for a real window: captures with <c>PrintWindow(PW_RENDERFULLCONTENT)</c>
/// (the window's own content, without overlapping windows; apps that stop painting while covered
/// return stale frames), cropped to the selected region, and scrolls with posted wheel messages.
/// Must be created and used on an MTA thread.
/// </summary>
internal sealed unsafe class WindowAutoScrollSurface : IAutoScrollSurface, IDisposable
{
    private readonly nint _window;
    private readonly nint _wheelTarget;
    private readonly int _pointX;
    private readonly int _pointY;
    private readonly PixelRect? _cropInWindow;
    private readonly UiaScrollPane? _pane;
    private int _frameOriginX;

    public WindowAutoScrollSurface(AutoScrollRequest request)
    {
        _window = request.Window;
        _pointX = request.PointX;
        _pointY = request.PointY;
        _pane = UiaScrollPane.TryFind(_pointX, _pointY, _window);
        _wheelTarget = AutoScrollNative.DeepestChild(_window, _pointX, _pointY);

        if (request.ScreenRegion is { } region)
        {
            // Fixed relative to the window, so moving the window mid-capture doesn't shift the crop.
            var visible = AutoScrollNative.VisibleBounds(_window, out _);
            var left = Math.Max(region.X, visible.Left);
            var top = Math.Max(region.Y, visible.Top);
            var right = Math.Min(region.X + region.Width, visible.Right);
            var bottom = Math.Min(region.Y + region.Height, visible.Bottom);
            if (right - left < 16 || bottom - top < 16)
            {
                _pane?.Dispose();
                throw new InvalidOperationException("The selected area doesn't cover enough of the window.");
            }

            _cropInWindow = new PixelRect(left - visible.Left, top - visible.Top, right - left, bottom - top);
        }
    }

    public double ScrollPercent => _pane?.VerticalPercent ?? -1;

    public int PaneColumn => _pointX - _frameOriginX;

    public bool HasScrollPosition => _pane is not null;

    public void Wheel(int notches) => AutoScrollNative.PostWheel(_wheelTarget, _pointX, _pointY, notches);

    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);

    public CapturedFrame Capture()
    {
        var visible = AutoScrollNative.VisibleBounds(_window, out var windowRect);
        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;
        if (windowWidth <= 0 || windowHeight <= 0 || AutoScrollNative.IsIconic(_window) != 0)
        {
            throw new InvalidOperationException("The window was closed or minimized.");
        }

        // Visible part of the window inside the PrintWindow bitmap.
        var visibleLeft = Math.Max(0, visible.Left - windowRect.Left);
        var visibleTop = Math.Max(0, visible.Top - windowRect.Top);
        var visibleWidth = Math.Min(windowWidth, visible.Right - windowRect.Left) - visibleLeft;
        var visibleHeight = Math.Min(windowHeight, visible.Bottom - windowRect.Top) - visibleTop;

        var cropX = 0;
        var cropY = 0;
        var cropWidth = visibleWidth;
        var cropHeight = visibleHeight;
        if (_cropInWindow is { } crop)
        {
            cropX = Math.Clamp(crop.X, 0, Math.Max(0, visibleWidth - 1));
            cropY = Math.Clamp(crop.Y, 0, Math.Max(0, visibleHeight - 1));
            cropWidth = Math.Min(crop.Width, visibleWidth - cropX);
            cropHeight = Math.Min(crop.Height, visibleHeight - cropY);
        }

        if (cropWidth <= 0 || cropHeight <= 0)
        {
            throw new InvalidOperationException("The window is too small to capture.");
        }

        var screenDc = AutoScrollNative.GetDC(0);
        var memoryDc = AutoScrollNative.CreateCompatibleDC(screenDc);
        var header = new AutoScrollNative.BITMAPINFOHEADER
        {
            Size = (uint)sizeof(AutoScrollNative.BITMAPINFOHEADER),
            Width = windowWidth,
            Height = -windowHeight, // top-down
            Planes = 1,
            BitCount = 32,
        };
        void* bits;
        var dib = AutoScrollNative.CreateDIBSection(memoryDc, &header, AutoScrollNative.DibRgbColors, &bits, 0, 0);
        try
        {
            if (dib == 0 || bits == null)
            {
                throw new InvalidOperationException("Couldn't allocate a capture bitmap.");
            }

            var previousObject = AutoScrollNative.SelectObject(memoryDc, dib);
            try
            {
                AutoScrollNative.PrintWindow(_window, memoryDc, AutoScrollNative.PwRenderFullContent);
            }
            finally
            {
                AutoScrollNative.SelectObject(memoryDc, previousObject);
            }

            var pixels = new byte[cropWidth * cropHeight * 4];
            var target = MemoryMarshal.Cast<byte, int>(pixels.AsSpan());
            var source = (int*)bits;
            for (var y = 0; y < cropHeight; y++)
            {
                var row = new ReadOnlySpan<int>(source + ((long)(visibleTop + cropY + y) * windowWidth) + visibleLeft + cropX, cropWidth);
                row.CopyTo(target.Slice(y * cropWidth, cropWidth));
            }

            // GDI leaves alpha undefined; the result is opaque.
            for (var i = 0; i < target.Length; i++)
            {
                target[i] |= unchecked((int)0xFF000000);
            }

            _frameOriginX = windowRect.Left + visibleLeft + cropX;
            return new CapturedFrame(pixels, cropWidth, cropHeight);
        }
        finally
        {
            if (dib != 0)
            {
                AutoScrollNative.DeleteObject(dib);
            }

            AutoScrollNative.DeleteDC(memoryDc);
            AutoScrollNative.ReleaseDC(0, screenDc);
        }
    }

    public void Dispose() => _pane?.Dispose();
}
