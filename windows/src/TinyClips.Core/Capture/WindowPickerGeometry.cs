namespace TinyClips.Core.Capture;

/// <summary>
/// One on-screen window in the window picker's hit-test list. <see cref="Bounds"/> is the
/// visible frame in virtual-desktop physical pixels. Non-selectable targets (such as the
/// taskbar) still block the windows behind them.
/// </summary>
public readonly record struct WindowPickerTarget(nint Handle, PixelRect Bounds, bool IsSelectable);

/// <summary>
/// Pure geometry for the hover-to-highlight window picker. Every rectangle here is in
/// physical pixels; DIP conversion happens in the overlay windows.
/// </summary>
public static class WindowPickerGeometry
{
    /// <summary>
    /// Returns the index of the window the user is pointing at, or -1. <paramref name="frontToBack"/>
    /// must be in z-order (topmost first), as produced by <c>EnumWindows</c>. The first target that
    /// contains the point wins; when it is not selectable the point is treated as blocked so a
    /// window hidden behind it is never picked.
    /// </summary>
    public static int HitTest(IReadOnlyList<WindowPickerTarget> frontToBack, int x, int y)
    {
        for (var i = 0; i < frontToBack.Count; i++)
        {
            var target = frontToBack[i];
            if (Contains(target.Bounds, x, y))
            {
                return target.IsSelectable ? i : -1;
            }
        }

        return -1;
    }

    /// <summary>Half-open containment: the right and bottom edges belong to the neighbour.</summary>
    public static bool Contains(PixelRect rect, int x, int y) =>
        rect.Width > 0 && rect.Height > 0
        && x >= rect.X && y >= rect.Y
        && x < rect.X + rect.Width && y < rect.Y + rect.Height;

    /// <summary>Returns the overlap of two rectangles, or null when they do not overlap.</summary>
    public static PixelRect? Intersect(PixelRect a, PixelRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top
            ? new PixelRect(left, top, right - left, bottom - top)
            : null;
    }

    /// <summary>
    /// Clips a window to a monitor and returns it relative to the monitor's top-left corner, or
    /// null when the window is not on that monitor.
    /// </summary>
    public static PixelRect? ToMonitorLocal(PixelRect window, PixelRect monitor) =>
        Intersect(window, monitor) is { } clipped
            ? clipped with { X = clipped.X - monitor.X, Y = clipped.Y - monitor.Y }
            : null;

    /// <summary>
    /// Chooses the single monitor that shows a window's label: the monitor containing the
    /// window's centre, otherwise the one with the largest overlap. Returns -1 when the window
    /// is on no monitor.
    /// </summary>
    public static int LabelMonitorIndex(PixelRect window, IReadOnlyList<PixelRect> monitors)
    {
        var centerX = window.X + (window.Width / 2);
        var centerY = window.Y + (window.Height / 2);
        for (var i = 0; i < monitors.Count; i++)
        {
            if (Contains(monitors[i], centerX, centerY))
            {
                return i;
            }
        }

        var best = -1;
        long bestArea = 0;
        for (var i = 0; i < monitors.Count; i++)
        {
            if (Intersect(window, monitors[i]) is { } overlap)
            {
                var area = (long)overlap.Width * overlap.Height;
                if (area > bestArea)
                {
                    best = i;
                    bestArea = area;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Moves keyboard focus to the next (or previous) selectable target in z-order, wrapping at
    /// the ends. Starts at the topmost (or bottommost) window when nothing is highlighted, and
    /// returns -1 when there is nothing to select.
    /// </summary>
    public static int Cycle(IReadOnlyList<WindowPickerTarget> frontToBack, int current, bool forward)
    {
        var count = frontToBack.Count;
        if (count == 0)
        {
            return -1;
        }

        var start = current >= 0 && current < count
            ? current
            : forward ? count - 1 : 0;
        var step = forward ? 1 : -1;
        for (var offset = 1; offset <= count; offset++)
        {
            var index = (((start + (step * offset)) % count) + count) % count;
            if (frontToBack[index].IsSelectable)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Formats a window's pixel size for the picker label, e.g. "1920 × 1080".</summary>
    public static string FormatSize(PixelRect bounds) => $"{bounds.Width} × {bounds.Height}";
}
