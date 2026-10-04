using TinyClips.Core.Capture;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Tools.StudioWindowCheck.Capture;

/// <summary>A screenshot of one of the tool's windows: tightly packed top-down BGRA.</summary>
/// <param name="ScreenX">The screen position, in pixels, of the screenshot's left edge.</param>
/// <param name="ScreenY">The screen position of its top edge.</param>
/// <param name="Serial">Counts the frames the system has delivered for the window.</param>
internal sealed record Shot(byte[] Bgra, int Width, int Height, int ScreenX, int ScreenY, long Serial)
{
    /// <summary>The average colour around a point of the screenshot, or (-1,-1,-1) outside it.</summary>
    public Rgb Color(double x, double y, int radius = 1) => FrameCode.Color(Bgra, Width, Height, x, y, radius);

    public void Save(string path) => PngWriter.WriteBgra(path, Bgra, Width, Height);
}

/// <summary>
/// Screenshots of one window of the tool through Windows.Graphics.Capture, which returns what the
/// system composes for the window, also while it is behind other windows. The system delivers a
/// frame whenever the window's picture changes; the newest one is kept, and read back on a device
/// of its own only when a check asks for it.
/// </summary>
internal sealed class WindowCamera : IDisposable
{
    private const int Buffers = 3;

    private readonly object _sync = new();
    private readonly nint _window;
    private readonly StudioGraphicsDevice _graphics;
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Direct3D11CaptureFrame? _latest;
    private SizeInt32 _poolSize;
    private long _serial;
    private bool _disposed;

    public WindowCamera(nint window)
    {
        _window = window;
        _graphics = StudioGraphicsDevice.CreateHardware();
        _device = WgcInterop.CreateDirect3DDevice(_graphics.Device) ?? throw new InvalidOperationException("Could not wrap the capture device for Windows.Graphics.Capture.");
        _item = WgcInterop.CreateCaptureItemForWindow(window);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        WgcInterop.TryConfigureSession(_session, includeCursor: false);
        _session.StartCapture();
    }

    /// <summary>How many screenshots were read back.</summary>
    public int Taken { get; private set; }

    /// <summary>The newest picture of the window, or null when the system has not delivered one yet.</summary>
    public Shot? Take()
    {
        lock (_sync)
        {
            if (_disposed || _latest is not { } frame)
            {
                return null;
            }

            var content = frame.ContentSize;
            using var texture = WgcInterop.GetTextureFromSurface(frame.Surface);
            byte[] whole;
            int stride;
            lock (_graphics.Gate)
            {
                whole = _graphics.ReadTexture(texture);
                stride = (int)texture.Description.Width * 4;
            }

            var width = Math.Min(content.Width, (int)texture.Description.Width);
            var height = Math.Min(content.Height, (int)texture.Description.Height);

            // The texture is larger than the window only after the window has shrunk.
            var pixels = whole;
            if (width * 4 != stride || whole.Length != width * height * 4)
            {
                pixels = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                {
                    whole.AsSpan(row * stride, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
                }
            }

            var bounds = Native.FrameBounds(_window);
            Taken++;
            return new Shot(pixels, width, height, bounds.Left, bounds.Top, _serial);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pool.FrameArrived -= OnFrameArrived;
            _latest?.Dispose();
            _latest = null;
        }

        _session.Dispose();
        _pool.Dispose();
        _device.Dispose();
        _graphics.Dispose();
    }

    // On a thread of the frame pool.
    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            Direct3D11CaptureFrame? newest = null;
            while (pool.TryGetNextFrame() is { } frame)
            {
                newest?.Dispose();
                newest = frame;
            }

            if (newest is null)
            {
                return;
            }

            _latest?.Dispose();
            _latest = newest;
            _serial++;

            // A window that grew needs larger buffers. The frame at hand stays valid until it is replaced.
            var content = newest.ContentSize;
            if (content.Width > _poolSize.Width || content.Height > _poolSize.Height)
            {
                _poolSize = new SizeInt32(Math.Max(content.Width, _poolSize.Width), Math.Max(content.Height, _poolSize.Height));
                pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, _poolSize);
            }
        }
    }
}
