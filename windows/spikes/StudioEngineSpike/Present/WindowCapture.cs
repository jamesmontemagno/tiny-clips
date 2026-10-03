using System.Diagnostics;
using StudioEngineSpike.Engine;
using StudioEngineSpike.Interop;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace StudioEngineSpike.Present;

/// <summary>A screenshot: tightly packed BGRA rows.</summary>
/// <param name="Timestamp">When it was read back (Stopwatch ticks).</param>
/// <param name="ComposedAt">When DWM composed it (the frame's SystemRelativeTime).</param>
internal sealed record CapturedImage(byte[] Pixels, int Width, int Height, long Timestamp, TimeSpan ComposedAt)
{
    public byte[] Crop(int x, int y, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Pixels.AsSpan((((y + row) * Width) + x) * 4, width * 4).CopyTo(result.AsSpan(row * width * 4));
        }

        return result;
    }
}

/// <summary>
/// Screenshots of the spike's own window through Windows.Graphics.Capture, which returns what DWM
/// composes for the window (XAML content and both swap chains), whatever else is on the desktop.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;

    /// <param name="maxWidth">
    /// The frame pool is created this large so that a window that is being resized always fits:
    /// frames keep the pool's buffer size and report the window's size as their content size.
    /// </param>
    /// <param name="minUpdateIntervalMs">Shortest time between frames; 0 leaves the system default (about 1/60 s).</param>
    public WindowCapture(GraphicsDevice graphics, nint hwnd, int maxWidth, int maxHeight, double minUpdateIntervalMs)
    {
        _graphics = graphics;
        _item = PanelInterop.CreateCaptureItemForWindow(hwnd);
        var size = _item.Size;
        _poolSize = new SizeInt32(Math.Max(size.Width, maxWidth), Math.Max(size.Height, maxHeight));
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(graphics.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, _poolSize);
        _session = _pool.CreateCaptureSession(_item);
        try
        {
            _session.IsCursorCaptureEnabled = false;
        }
        catch (Exception ex)
        {
            Notes.Add($"IsCursorCaptureEnabled could not be set: 0x{ex.HResult:X8}");
        }

        try
        {
            _session.IsBorderRequired = false;
        }
        catch (Exception ex)
        {
            Notes.Add($"IsBorderRequired could not be set: 0x{ex.HResult:X8}");
        }

        if (minUpdateIntervalMs > 0)
        {
            try
            {
                // The default lets a frame through about every 1/60 s, which on a 100 Hz display is
                // every other refresh. The pacing checks want every composition.
                _session.MinUpdateInterval = TimeSpan.FromMilliseconds(minUpdateIntervalMs);
            }
            catch (Exception ex)
            {
                Notes.Add($"MinUpdateInterval could not be set: 0x{ex.HResult:X8} (screenshots come at most every 1/60 s)");
            }
        }

        _session.StartCapture();
    }

    public List<string> Notes { get; } = new();

    /// <summary>Throws away the frames already queued, so the next one was composed after this call.</summary>
    public void Drain()
    {
        while (_pool.TryGetNextFrame() is { } stale)
        {
            stale.Dispose();
        }
    }

    /// <summary>The next frame the system delivers, or null after <paramref name="timeout"/>.</summary>
    public CapturedImage? Next(TimeSpan timeout, bool fresh = false)
    {
        if (fresh)
        {
            Drain();
        }

        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            using var frame = _pool.TryGetNextFrame();
            if (frame is null)
            {
                PreciseTimer.Sleep(1);
                continue;
            }

            var content = frame.ContentSize;
            if (content.Width > _poolSize.Width || content.Height > _poolSize.Height)
            {
                _poolSize = new SizeInt32(Math.Max(content.Width, _poolSize.Width), Math.Max(content.Height, _poolSize.Height));
                _pool.Recreate(_graphics.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, _poolSize);
                continue;
            }

            if (content.Width <= 0 || content.Height <= 0)
            {
                continue;
            }

            var timestamp = Stopwatch.GetTimestamp();
            using var texture = Direct3DInterop.GetTexture(frame.Surface);
            var pixels = new byte[content.Width * content.Height * 4];
            lock (_graphics.Gate)
            {
                _graphics.ReadRegion(texture, 0, 0, 0, content.Width, content.Height, pixels);
            }

            return new CapturedImage(pixels, content.Width, content.Height, timestamp, frame.SystemRelativeTime);
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="Next"/>, but reads only the given rectangles of the next frame (one GPU
    /// sync, a few kilobytes), so that watching playback does not hold the device for the time a
    /// whole-window readback takes. Returns null on timeout or when the window is not
    /// <paramref name="expectedWidth"/> x <paramref name="expectedHeight"/>.
    /// </summary>
    public (TimeSpan ComposedAt, byte[][] Regions)? NextRegions(TimeSpan timeout, int expectedWidth, int expectedHeight, (int X, int Y, int Width, int Height)[] regions)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            using var frame = _pool.TryGetNextFrame();
            if (frame is null)
            {
                PreciseTimer.Sleep(1);
                continue;
            }

            var content = frame.ContentSize;
            if (content.Width != expectedWidth || content.Height != expectedHeight || content.Width > _poolSize.Width || content.Height > _poolSize.Height)
            {
                return null;
            }

            using var texture = Direct3DInterop.GetTexture(frame.Surface);
            lock (_graphics.Gate)
            {
                return (frame.SystemRelativeTime, _graphics.ReadRegions(texture, 0, regions));
            }
        }

        return null;
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
    }
}
