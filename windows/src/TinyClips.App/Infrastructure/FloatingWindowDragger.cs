using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace TinyClips.App;

/// <summary>
/// Encapsulates drag-anywhere behavior for borderless floating overlay windows.
/// Window movement is anchored to the absolute cursor position rather than a
/// pointer-relative offset to avoid feedback jitter as the window moves under the pointer.
/// </summary>
/// <remarks>
/// <para>
/// Wire the public methods to <see cref="UIElement.PointerPressed"/>,
/// <see cref="UIElement.PointerMoved"/>, <see cref="UIElement.PointerReleased"/>,
/// <see cref="UIElement.PointerCanceled"/>, and <see cref="UIElement.PointerCaptureLost"/>
/// on the drag surface. Buttons and other interactive controls should mark their own pointer
/// events handled so a drag is only initiated on the bar background.
/// </para>
/// <para>
/// Windows whose pointer-release handler performs additional work (e.g.
/// <c>TeleprompterWindow</c> position persistence or <c>WebcamPreviewWindow</c> corner
/// snapping) keep their own drag state rather than using this helper, as the instruction
/// specifies that differing behaviors should not be forced into a shared abstraction.
/// </para>
/// </remarks>
internal sealed class FloatingWindowDragger
{
    private readonly AppWindow _appWindow;
    private bool _dragging;
    private POINT _cursorStart;
    private PointInt32 _windowStart;

    /// <param name="appWindow">
    /// The <see cref="AppWindow"/> whose position this dragger controls.
    /// Typically obtained from <c>window.AppWindow</c> in the host window's constructor.
    /// </param>
    public FloatingWindowDragger(AppWindow appWindow)
    {
        _appWindow = appWindow;
    }

    /// <summary>
    /// Call from <see cref="UIElement.PointerPressed"/> on the drag surface.
    /// Captures the pointer so drag events are received even when the pointer leaves the element.
    /// </summary>
    public void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        GetCursorPos(out _cursorStart);
        _windowStart = _appWindow.Position;
        _dragging = element.CapturePointer(e.Pointer);
    }

    /// <summary>
    /// Call from <see cref="UIElement.PointerMoved"/> on the drag surface.
    /// Moves the window by the delta between the current and start cursor positions.
    /// </summary>
    public void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        GetCursorPos(out var current);
        var dx = current.X - _cursorStart.X;
        var dy = current.Y - _cursorStart.Y;

        if (dx == 0 && dy == 0)
        {
            return;
        }

        _appWindow.Move(new PointInt32(_windowStart.X + dx, _windowStart.Y + dy));
    }

    /// <summary>
    /// Call from <see cref="UIElement.PointerReleased"/> on the drag surface.
    /// Clears the drag flag and releases pointer capture.
    /// </summary>
    public void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        _dragging = false;
        element.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>
    /// Clears drag state when WinUI cancels the interaction or ends pointer capture.
    /// </summary>
    public void OnPointerCaptureEnded(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
