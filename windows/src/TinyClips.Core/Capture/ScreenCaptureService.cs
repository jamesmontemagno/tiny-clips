using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Core.Capture;

/// <summary>
/// Single-frame screen capture via Windows.Graphics.Capture (WGC) + Direct3D 11.
/// Targets a monitor by HMONITOR, grabs a settled frame, copies it to a CPU-readable
/// staging texture and returns tightly-packed BGRA8 pixels (optionally cropped).
/// </summary>
public sealed partial class ScreenCaptureService : IScreenCaptureService
{
    // Window targets can show a brief capture-start transition on the first frame; monitor
    // targets (border disabled via IsBorderRequired=false) are clean on the first frame, so
    // waiting for a second one would only add a vsync of latency.
    private const int SettleFramesForWindow = 2;
    private const int SettleFramesForMonitor = 1;
    private const int CaptureTimeoutMs = 4000;

    public void WarmUp() => WgcInterop.WarmUpSharedDevice();

    public Task<CapturedFrame> CaptureMonitorAsync(
        nint hMonitor,
        PixelRect? region = null,
        bool includeCursor = false,
        CancellationToken cancellationToken = default)
        => CaptureAsync(CaptureTarget.Monitor(hMonitor), region, includeCursor, cancellationToken);

    public async Task<CapturedFrame> CaptureAsync(
        CaptureTarget target,
        PixelRect? region = null,
        bool includeCursor = false,
        CancellationToken cancellationToken = default)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this device.");
        }

        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        ID3D11Texture2D? stagingTexture = null;
        var settleFrames = target.IsWindow ? SettleFramesForWindow : SettleFramesForMonitor;

        try
        {
            var (d3dDevice, device) = WgcInterop.GetSharedDevice();

            var item = target.CreateItem()
                ?? throw new InvalidOperationException("Failed to create a GraphicsCaptureItem for the target.");

            var size = item.Size;
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);

            session = framePool.CreateCaptureSession(item);
            WgcInterop.TryConfigureSession(session, includeCursor);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = d3dDevice.ImmediateContext;
            int frameCount = 0;
            int frameWidth = 0;
            int frameHeight = 0;
            var sync = new object();

            framePool.FrameArrived += (pool, _) =>
            {
                try
                {
                    using var frame = pool.TryGetNextFrame();
                    if (frame is null)
                    {
                        return;
                    }

                    lock (sync)
                    {
                        if (tcs.Task.IsCompleted)
                        {
                            return;
                        }

                        using var frameTexture = WgcInterop.GetTextureFromFrame(frame);

                        var desc = frameTexture.Description;
                        if (stagingTexture is null)
                        {
                            frameWidth = (int)desc.Width;
                            frameHeight = (int)desc.Height;
                            stagingTexture = d3dDevice.CreateTexture2D(new Texture2DDescription
                            {
                                Width = desc.Width,
                                Height = desc.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = desc.Format,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Staging,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.Read,
                                MiscFlags = ResourceOptionFlags.None,
                            });
                        }

                        context.CopyResource(stagingTexture, frameTexture);
                        frameCount++;

                        if (frameCount >= settleFrames)
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            session.StartCapture();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CaptureTimeoutMs);
            using (timeoutCts.Token.Register(() =>
            {
                // On timeout, accept whatever frame we have already copied.
                lock (sync)
                {
                    if (frameCount > 0)
                    {
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        tcs.TrySetException(new TimeoutException("No frames were captured before the timeout elapsed."));
                    }
                }
            }))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            lock (sync)
            {
                if (stagingTexture is null)
                {
                    throw new InvalidOperationException("Capture produced no frame.");
                }

                return ReadStagingTexture(context, stagingTexture, frameWidth, frameHeight, region);
            }
        }
        finally
        {
            session?.Dispose();
            framePool?.Dispose();
            stagingTexture?.Dispose();
            // The D3D device pair is process-shared (see WgcInterop.GetSharedDevice); do not dispose.
        }
    }

    private static unsafe CapturedFrame ReadStagingTexture(
        ID3D11DeviceContext context,
        ID3D11Texture2D stagingTexture,
        int frameWidth,
        int frameHeight,
        PixelRect? region)
    {
        int x = 0, y = 0, width = frameWidth, height = frameHeight;
        if (region is { } r)
        {
            x = Math.Clamp(r.X, 0, frameWidth);
            y = Math.Clamp(r.Y, 0, frameHeight);
            width = Math.Clamp(r.Width, 1, frameWidth - x);
            height = Math.Clamp(r.Height, 1, frameHeight - y);
        }

        var mapped = context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[width * height * 4];
            var src = (byte*)mapped.DataPointer;
            int srcPitch = (int)mapped.RowPitch;
            int rowBytes = width * 4;

            fixed (byte* dst = pixels)
            {
                if (x == 0 && srcPitch == rowBytes)
                {
                    // Tightly packed source: one contiguous copy.
                    Buffer.MemoryCopy(src + ((long)y * srcPitch), dst, pixels.Length, (long)height * rowBytes);
                }
                else
                {
                    for (int row = 0; row < height; row++)
                    {
                        Buffer.MemoryCopy(
                            src + ((long)(y + row) * srcPitch) + (x * 4),
                            dst + ((long)row * rowBytes),
                            rowBytes,
                            rowBytes);
                    }
                }
            }

            return new CapturedFrame(pixels, width, height);
        }
        finally
        {
            context.Unmap(stagingTexture, 0);
        }
    }
}
