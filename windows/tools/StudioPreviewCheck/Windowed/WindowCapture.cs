using System.Diagnostics;
using TinyClips.Core.Capture;
using TinyClips.Core.Studio.Rendering;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Tools.StudioPreviewCheck.Windowed;

/// <summary>A screenshot of the tool's window: tightly packed top-down BGRA.</summary>
/// <param name="At">Stopwatch timestamp of the readback.</param>
internal sealed record CapturedImage(byte[] Pixels, int Width, int Height, long At);

/// <summary>
/// Screenshots of the tool's own window through Windows.Graphics.Capture, which returns what the
/// system composes for the window (the XAML content and the panel's swap chain) whatever else is
/// on the desktop, also when the window is behind others. It reads back on a device of its own, so
/// a screenshot never waits for the engine's device.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private readonly StudioGraphicsDevice _graphics;
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;

    /// <param name="maxWidth">
    /// The frame pool is made this large so that a window being resized always fits: frames keep
    /// the pool's buffer size and report the window's size as their content size.
    /// </param>
    public WindowCapture(nint window, int maxWidth, int maxHeight)
    {
        _graphics = StudioGraphicsDevice.CreateHardware();
        _device = WgcInterop.CreateDirect3DDevice(_graphics.Device) ?? throw new InvalidOperationException("Could not wrap the capture device for Windows.Graphics.Capture.");
        _item = WgcInterop.CreateCaptureItemForWindow(window);
        var size = _item.Size;
        _poolSize = new SizeInt32(Math.Max(size.Width, maxWidth), Math.Max(size.Height, maxHeight));
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, _poolSize);
        _session = _pool.CreateCaptureSession(_item);
        WgcInterop.TryConfigureSession(_session, includeCursor: false);
        _session.StartCapture();
    }

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
                Thread.Sleep(1);
                continue;
            }

            var content = frame.ContentSize;
            if (content.Width > _poolSize.Width || content.Height > _poolSize.Height)
            {
                _poolSize = new SizeInt32(Math.Max(content.Width, _poolSize.Width), Math.Max(content.Height, _poolSize.Height));
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, _poolSize);
                continue;
            }

            if (content.Width <= 0 || content.Height <= 0)
            {
                continue;
            }

            using var texture = WgcInterop.GetTextureFromSurface(frame.Surface);
            byte[] whole;
            int stride;
            lock (_graphics.Gate)
            {
                whole = _graphics.ReadTexture(texture);
                stride = (int)texture.Description.Width * 4;
            }

            var at = Stopwatch.GetTimestamp();
            var pixels = new byte[content.Width * content.Height * 4];
            for (var row = 0; row < content.Height; row++)
            {
                whole.AsSpan(row * stride, content.Width * 4).CopyTo(pixels.AsSpan(row * content.Width * 4));
            }

            return new CapturedImage(pixels, content.Width, content.Height, at);
        }

        return null;
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
        _device.Dispose();
        _graphics.Dispose();
    }
}
