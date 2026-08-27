using Windows.Graphics.Imaging;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Core.Capture;

/// <summary>
/// A webcam frame with its system-relative source timestamp. Delivered either as tightly-packed
/// BGRA8 pixels (<see cref="BgraPixels"/>) or, when the capture service was given a Direct3D device,
/// as a GPU surface on that device (<see cref="Surface"/>) so the GPU compositor can draw it without
/// any system-memory round trip. Exactly one of the two is populated.
/// </summary>
public sealed class WebcamFrame
{
    public WebcamFrame(ReadOnlyMemory<byte> bgraPixels, int width, int height, TimeSpan timestamp)
    {
        BgraPixels = bgraPixels;
        Width = width;
        Height = height;
        Timestamp = timestamp;
    }

    public WebcamFrame(IDirect3DSurface surface, int width, int height, TimeSpan timestamp)
    {
        Surface = surface;
        Width = width;
        Height = height;
        Timestamp = timestamp;
    }

    public ReadOnlyMemory<byte> BgraPixels { get; }

    /// <summary>BGRA8 surface on the recorder's Direct3D device, or null for CPU frames.</summary>
    public IDirect3DSurface? Surface { get; }

    public bool IsGpuFrame => Surface is not null;

    public int Width { get; }

    public int Height { get; }

    public TimeSpan Timestamp { get; }
}

/// <summary>
/// Provides lifecycle management and frame access for webcam capture.
/// </summary>
public interface IWebcamCaptureService : IAsyncDisposable
{
    bool IsRunning { get; }

    event EventHandler<WebcamCaptureFailedEventArgs>? CaptureFailed;

    Task StartAsync(string? deviceId, BitmapSize bitmapSize, CancellationToken cancellationToken = default);

    Task StopAsync();

    bool TryGetLatestFrame(out WebcamFrame? frame);

    /// <summary>
    /// Asks the next <see cref="StartAsync"/> to deliver frames as GPU surfaces on <paramref name="device"/>
    /// (see <see cref="WebcamFrame.Surface"/>) instead of CPU pixel buffers. Pass null to return to
    /// CPU delivery. Implementations may ignore this and keep delivering CPU frames.
    /// </summary>
    void SetPreferredDirect3DDevice(IDirect3DDevice? device)
    {
    }
}

/// <summary>
/// Failure details raised from the webcam capture pipeline.
/// </summary>
public sealed class WebcamCaptureFailedEventArgs : EventArgs
{
    public WebcamCaptureFailedEventArgs(uint code, string message)
    {
        Code = code;
        Message = message;
    }

    public uint Code { get; }

    public string Message { get; }
}
