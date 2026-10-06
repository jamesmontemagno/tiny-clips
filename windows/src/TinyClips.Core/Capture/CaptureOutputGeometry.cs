namespace TinyClips.Core.Capture;

/// <summary>Physical-pixel intersection followed by an even-size crop, never an implicit upscale.</summary>
public sealed record CaptureOutputGeometry(PixelRect Requested, PixelRect Clipped, PixelRect Encoded)
{
    public bool WasClipped => Requested != Clipped;
    public bool WasEvenSized => Clipped != Encoded;

    public (int X, int Y) GetDesktopOrigin(int monitorX, int monitorY) =>
        (checked(monitorX + Clipped.X), checked(monitorY + Clipped.Y));

    public static CaptureOutputGeometry Calculate(int contentWidth, int contentHeight, PixelRect? region)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentHeight);
        var requested = region ?? new PixelRect(0, 0, contentWidth, contentHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requested.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requested.Height);
        var x = Math.Clamp(requested.X, 0, contentWidth);
        var y = Math.Clamp(requested.Y, 0, contentHeight);
        var right = Math.Clamp((long)requested.X + requested.Width, 0, contentWidth);
        var bottom = Math.Clamp((long)requested.Y + requested.Height, 0, contentHeight);
        var width = (int)Math.Max(0, right - x);
        var height = (int)Math.Max(0, bottom - y);
        if (width < 2 || height < 2)
        {
            throw new ArgumentException("The capture intersection must contain at least 2x2 physical pixels.", nameof(region));
        }

        return new(requested, new(x, y, width, height), new(x, y, width & ~1, height & ~1));
    }
}
