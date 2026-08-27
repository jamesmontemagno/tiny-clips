using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using WinRT;

namespace TinyClips.Core.Capture;

public sealed partial class WebcamCaptureService : IWebcamCaptureService
{
    /// <summary>
    /// Direct access to a SoftwareBitmap's pixel memory. Classic <c>[ComImport]</c> fails to QI this
    /// under CsWinRT ("Specified cast is not valid"); source-generated COM interop works, the same
    /// fix <see cref="WgcInterop"/> uses for the capture interfaces.
    /// </summary>
    [GeneratedComInterface]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    internal partial interface IMemoryBufferByteAccess
    {
        [PreserveSig]
        int GetBuffer(out nint value, out uint capacity);
    }

    private static readonly Guid MemoryBufferByteAccessGuid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    // Frames are copied into a small ring of reusable buffers instead of a fresh 2 MB byte[] per
    // frame: at 30 fps the old path allocated ~55 MB/s straight onto the Large Object Heap, which
    // cost a Gen2 collection every few frames. Consumers only ever hold the latest frame or the one
    // before it, so a ring this deep cannot be overwritten while still being composited.
    private const int FrameRingDepth = 6;
    private readonly byte[]?[] _frameRing = new byte[FrameRingDepth][];
    private int _frameRingIndex;
    private int _frameRingPixelBytes;
    private Windows.Storage.Streams.Buffer? _fallbackCopyBuffer;
    private int _directAccessUnavailable =
        string.Equals(Environment.GetEnvironmentVariable("TINYCLIPS_WEBCAM_DIRECT_COPY"), "0", StringComparison.Ordinal) ? 1 : 0;

    // GPU delivery: frames are copied by Media Foundation (VideoFrame.CopyToAsync) straight from
    // the camera pipeline's device into BGRA surfaces on the recorder's device. Nothing touches
    // system memory and no managed buffers are allocated per frame. A ring of three is enough:
    // GPU work on one device is ordered, so a surface cannot be overwritten while a queued
    // Direct2D draw still reads it.
    private const int GpuRingDepth = 3;
    private IDirect3DDevice? _preferredDevice;
    private IDirect3DDevice? _activeGpuDevice;
    private readonly VideoFrame?[] _gpuRing = new VideoFrame?[GpuRingDepth];
    private int _gpuRingIndex;
    private int _gpuRingWidth;
    private int _gpuRingHeight;
    private int _gpuDeliveryFailed;
    private long _gpuFramesDelivered;

    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly object _latestFrameGate = new();

    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private int _nextMediaCaptureId;
    private int _activeMediaCaptureId;
    private bool _stopRequested;
    private bool _isDisposed;

    private WebcamFrame? _latestFrame;
    private long _framesArrived;
    private int _firstFrameLogged;
    private int _firstCacheLogged;
    private int _frameErrorLogged;

    public bool IsRunning { get; private set; }

    public event EventHandler<WebcamCaptureFailedEventArgs>? CaptureFailed;

    public void SetPreferredDirect3DDevice(IDirect3DDevice? device) => _preferredDevice = device;

    public async Task StartAsync(string? deviceId, BitmapSize bitmapSize, CancellationToken cancellationToken = default)
    {
        if (bitmapSize.Width == 0 || bitmapSize.Height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitmapSize), "Bitmap size must be greater than 0.");
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (IsRunning)
            {
                throw new InvalidOperationException("Webcam capture is already running.");
            }

            _stopRequested = false;
            Interlocked.Exchange(ref _framesArrived, 0);
            Interlocked.Exchange(ref _firstFrameLogged, 0);
            Interlocked.Exchange(ref _firstCacheLogged, 0);
            Interlocked.Exchange(ref _frameErrorLogged, 0);
            lock (_latestFrameGate)
            {
                _latestFrame = null;
            }

            _activeGpuDevice = _preferredDevice;
            Interlocked.Exchange(ref _gpuDeliveryFailed, 0);
            Interlocked.Exchange(ref _gpuFramesDelivered, 0);
            ClearGpuRing();

            WebcamDiagnostics.Log($"WebcamCaptureService.StartAsync deviceId='{(string.IsNullOrWhiteSpace(deviceId) ? "(default)" : deviceId)}' size={bitmapSize.Width}x{bitmapSize.Height} delivery={(_activeGpuDevice is null ? "cpu" : "gpu")}");

            var mediaCapture = new MediaCapture();
            var captureId = Interlocked.Increment(ref _nextMediaCaptureId);
            mediaCapture.Failed += (sender, args) => OnMediaCaptureFailed(sender, args, captureId);

            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    // GPU delivery lets the camera pipeline keep frames in video memory; CPU
                    // delivery forces SoftwareBitmaps so the CPU compositor can read them.
                    MemoryPreference = _activeGpuDevice is null ? MediaCaptureMemoryPreference.Cpu : MediaCaptureMemoryPreference.Auto,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    VideoDeviceId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId,
                };

                await mediaCapture.InitializeAsync(settings).AsTask(cancellationToken).ConfigureAwait(false);
                WebcamDiagnostics.Log("InitializeAsync succeeded.");

                if (_stopRequested)
                {
                    mediaCapture.Dispose();
                    return;
                }

                var source = SelectPreferredSource(mediaCapture.FrameSources)
                    ?? throw new InvalidOperationException("No color webcam frame source was available.");
                WebcamDiagnostics.Log($"Selected frame source kind={source.Info.SourceKind} streamType={source.Info.MediaStreamType} id={source.Info.Id}");

                var frameReader = await mediaCapture
                    .CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, bitmapSize)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

                frameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                frameReader.FrameArrived += OnFrameArrived;

                var startStatus = await frameReader.StartAsync().AsTask(cancellationToken).ConfigureAwait(false);
                WebcamDiagnostics.Log($"frameReader.StartAsync status={startStatus}");
                if (startStatus != MediaFrameReaderStartStatus.Success)
                {
                    frameReader.FrameArrived -= OnFrameArrived;
                    frameReader.Dispose();
                    throw new InvalidOperationException($"Failed to start webcam frame reader: {startStatus}.");
                }

                if (_stopRequested)
                {
                    frameReader.FrameArrived -= OnFrameArrived;
                    await frameReader.StopAsync().AsTask().ConfigureAwait(false);
                    frameReader.Dispose();
                    mediaCapture.Dispose();
                    return;
                }

                _mediaCapture = mediaCapture;
                _frameReader = frameReader;
                Volatile.Write(ref _activeMediaCaptureId, captureId);
                IsRunning = true;
                WebcamDiagnostics.Log("Webcam capture is now running (IsRunning=true).");
            }
            catch (Exception ex)
            {
                WebcamDiagnostics.Log($"StartAsync FAILED: 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
                mediaCapture.Dispose();
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task StopAsync()
    {
        _stopRequested = true;

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var frameReader = _frameReader;
            var mediaCapture = _mediaCapture;

            _frameReader = null;
            _mediaCapture = null;
            Volatile.Write(ref _activeMediaCaptureId, 0);
            IsRunning = false;

            if (mediaCapture is not null)
            {
                WebcamDiagnostics.Log($"WebcamCaptureService.StopAsync — total frames arrived={Interlocked.Read(ref _framesArrived)} gpuDelivered={Interlocked.Read(ref _gpuFramesDelivered)}");
            }

            if (frameReader is not null)
            {
                frameReader.FrameArrived -= OnFrameArrived;
            }

            if (frameReader is not null)
            {
                try
                {
                    await frameReader.StopAsync().AsTask().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore stop errors while tearing down.
                }
                finally
                {
                    frameReader.Dispose();
                }
            }

            mediaCapture?.Dispose();

            lock (_latestFrameGate)
            {
                _latestFrame = null;
            }

            ClearGpuRing();
            _activeGpuDevice = null;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public bool TryGetLatestFrame(out WebcamFrame? frame)
    {
        lock (_latestFrameGate)
        {
            frame = _latestFrame;
            return frame is not null;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            using var frameReference = sender.TryAcquireLatestFrame();
            var videoFrame = frameReference?.VideoMediaFrame;
            if (videoFrame is null)
            {
                return;
            }

            if (_activeGpuDevice is { } gpuDevice && Volatile.Read(ref _gpuDeliveryFailed) == 0)
            {
                if (TryDeliverGpuFrame(videoFrame, frameReference!.SystemRelativeTime ?? TimeSpan.Zero, gpuDevice))
                {
                    return;
                }

                // Fall through to the CPU path for this and all later frames.
                Interlocked.Exchange(ref _gpuDeliveryFailed, 1);
            }

            var softwareBitmap = videoFrame.SoftwareBitmap;
            SoftwareBitmap? surfaceCopy = null;
            if (softwareBitmap is null && videoFrame.Direct3DSurface is { } d3dSurface)
            {
                // GPU delivery was requested but failed: under MemoryPreference.Auto the camera
                // pipeline may only provide D3D surfaces, so read one back for the CPU path.
                surfaceCopy = SoftwareBitmap.CreateCopyFromSurfaceAsync(d3dSurface, BitmapAlphaMode.Ignore).AsTask().GetAwaiter().GetResult();
                softwareBitmap = surfaceCopy;
            }

            using var surfaceCopyScope = surfaceCopy;
            if (softwareBitmap is null)
            {
                return;
            }

            var timestamp = frameReference!.SystemRelativeTime ?? TimeSpan.Zero;
            Interlocked.Increment(ref _framesArrived);
            if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
            {
                WebcamDiagnostics.Log($"First frame arrived: {softwareBitmap.PixelWidth}x{softwareBitmap.PixelHeight} format={softwareBitmap.BitmapPixelFormat} alpha={softwareBitmap.BitmapAlphaMode}");
            }

            // The overlay compositor blends BGR using its own shape mask and ignores the
            // webcam's source alpha, so when the camera already delivers Bgra8 we copy the
            // raw bytes directly — avoiding a per-frame SoftwareBitmap.Convert that can fail
            // in the packaged runtime. Only non-Bgra8 formats (e.g. Nv12/Yuy2) need a convert.
            SoftwareBitmap? convertedBitmap = null;
            var preparedBitmap = softwareBitmap;
            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                preparedBitmap = convertedBitmap;
            }

            try
            {
                // Keep the source's absolute system-relative timestamp. The recorder
                // normalizes it against the same QPC origin used by screen and audio.
                var frame = CopyFrame(preparedBitmap, timestamp);
                lock (_latestFrameGate)
                {
                    _latestFrame = frame;
                }

                if (Interlocked.Exchange(ref _firstCacheLogged, 1) == 0)
                {
                    WebcamDiagnostics.Log($"First webcam frame cached for compositing: {frame.Width}x{frame.Height} (converted={(convertedBitmap is not null)})");
                }
            }
            finally
            {
                convertedBitmap?.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _frameErrorLogged, 1) == 0)
            {
                WebcamDiagnostics.Log($"OnFrameArrived FAILED (frame not cached): 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            }
            // A single failed frame should not stop webcam capture.
        }
    }

    private async void OnMediaCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args, int captureId)
    {
        if (captureId != Volatile.Read(ref _activeMediaCaptureId))
        {
            return;
        }

        WebcamDiagnostics.Log($"MediaCapture.Failed fired: code={args.Code} message='{args.Message}' (frames so far={Interlocked.Read(ref _framesArrived)})");
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Failure handling should not throw back into MediaCapture internals.
        }

        CaptureFailed?.Invoke(this, new WebcamCaptureFailedEventArgs(args.Code, args.Message));
    }

    private static MediaFrameSource? SelectPreferredSource(IReadOnlyDictionary<string, MediaFrameSource> frameSources)
    {
        return frameSources.Values
            .Where(source => source.Info.SourceKind == MediaFrameSourceKind.Color)
            .OrderBy(source => source.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 :
                source.Info.MediaStreamType == MediaStreamType.VideoRecord ? 1 : 2)
            .ThenBy(source => source.Info.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private WebcamFrame CopyFrame(SoftwareBitmap bitmap, TimeSpan timestamp)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var packedStride = width * 4;
        var pixelBytes = packedStride * height;
        var destination = RentRingBuffer(pixelBytes);

        if (Volatile.Read(ref _directAccessUnavailable) == 0 && TryCopyDirect(bitmap, destination, width, height))
        {
            return new WebcamFrame(new ReadOnlyMemory<byte>(destination, 0, pixelBytes), width, height, timestamp);
        }

        // Fallback: fully WinRT-projected path (CopyToBuffer + DataReader). Slower (two copies)
        // but never fails; the IBuffer is cached so this path allocates only once per size.
        if (_fallbackCopyBuffer is null || _fallbackCopyBuffer.Capacity != (uint)pixelBytes)
        {
            _fallbackCopyBuffer = new Windows.Storage.Streams.Buffer((uint)pixelBytes);
        }

        bitmap.CopyToBuffer(_fallbackCopyBuffer);
        var length = (int)_fallbackCopyBuffer.Length;
        using (var reader = DataReader.FromBuffer(_fallbackCopyBuffer))
        {
            if (length == pixelBytes)
            {
                // ReadBytes fills the whole array, so hand it a buffer of exactly the right size.
                reader.ReadBytes(destination);
            }
            else
            {
                // A driver reporting padded rows: read into a scratch array and de-stride.
                var raw = new byte[length];
                reader.ReadBytes(raw);
                var stride = height > 0 ? length / height : packedStride;
                for (var row = 0; row < height && stride >= packedStride; row++)
                {
                    System.Buffer.BlockCopy(raw, row * stride, destination, row * packedStride, packedStride);
                }
            }
        }

        return new WebcamFrame(new ReadOnlyMemory<byte>(destination, 0, pixelBytes), width, height, timestamp);
    }

    /// <summary>Copies the bitmap's pixels straight from its locked memory into a packed BGRA buffer.</summary>
    private unsafe bool TryCopyDirect(SoftwareBitmap bitmap, byte[] destination, int width, int height)
    {
        try
        {
            using var locked = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = locked.CreateReference();
            var referencePtr = ((IWinRTObject)reference).NativeObject.ThisPtr;
            nint accessPtr = 0;
            try
            {
                if (Marshal.QueryInterface(referencePtr, in MemoryBufferByteAccessGuid, out accessPtr) < 0)
                {
                    Interlocked.Exchange(ref _directAccessUnavailable, 1);
                    return false;
                }

                var access = ComInterfaceMarshaller<IMemoryBufferByteAccess>.ConvertToManaged((void*)accessPtr)!;
                // ConvertToManaged does not take ownership: the wrapper AddRefs, and our QI
                // reference is released by the finally block.
                if (access.GetBuffer(out var data, out var capacity) < 0 || data == 0)
                {
                    return false;
                }

                var plane = locked.GetPlaneDescription(0);
                var packedStride = width * 4;
                if (plane.Stride < packedStride || (long)plane.StartIndex + ((long)(height - 1) * plane.Stride) + packedStride > capacity)
                {
                    return false;
                }

                var src = (byte*)data + plane.StartIndex;
                fixed (byte* dst = destination)
                {
                    if (plane.Stride == packedStride)
                    {
                        System.Buffer.MemoryCopy(src, dst, destination.Length, (long)packedStride * height);
                    }
                    else
                    {
                        for (var row = 0; row < height; row++)
                        {
                            System.Buffer.MemoryCopy(src + ((long)row * plane.Stride), dst + ((long)row * packedStride), packedStride, packedStride);
                        }
                    }
                }

                return true;
            }
            finally
            {
                if (accessPtr != 0)
                {
                    ComInterfaceMarshaller<IMemoryBufferByteAccess>.Free((void*)accessPtr);
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _directAccessUnavailable, 1);
            return false;
        }
    }

    private byte[] RentRingBuffer(int pixelBytes)
    {
        if (_frameRingPixelBytes != pixelBytes)
        {
            Array.Clear(_frameRing);
            _frameRingPixelBytes = pixelBytes;
            _frameRingIndex = 0;
        }

        var index = _frameRingIndex;
        _frameRingIndex = (index + 1) % FrameRingDepth;
        return _frameRing[index] ??= new byte[pixelBytes];
    }
    /// <summary>
    /// GPU delivery: lets Media Foundation copy (and colour-convert if needed) the camera frame
    /// into a BGRA surface on the recorder's device. Works whether the camera pipeline produced a
    /// GPU surface or a SoftwareBitmap — in the latter case the upload happens once, here, instead of
    /// once per composited output frame.
    /// </summary>
    private bool TryDeliverGpuFrame(VideoMediaFrame videoFrame, TimeSpan timestamp, IDirect3DDevice device)
    {
        try
        {
            int width;
            int height;
            if (videoFrame.Direct3DSurface is { } sourceSurface)
            {
                var desc = sourceSurface.Description;
                width = desc.Width;
                height = desc.Height;
            }
            else if (videoFrame.SoftwareBitmap is { } bitmap)
            {
                width = bitmap.PixelWidth;
                height = bitmap.PixelHeight;
            }
            else
            {
                return false;
            }

            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var destination = RentGpuFrame(width, height, device);
            using var source = videoFrame.GetVideoFrame();
            // Frame-reader callbacks run on an MTA thread, so blocking on the WinRT copy is safe.
            source.CopyToAsync(destination).AsTask().GetAwaiter().GetResult();

            var frame = new WebcamFrame(destination.Direct3DSurface, width, height, timestamp);
            lock (_latestFrameGate)
            {
                _latestFrame = frame;
            }

            Interlocked.Increment(ref _framesArrived);
            if (Interlocked.Increment(ref _gpuFramesDelivered) == 1)
            {
                WebcamDiagnostics.Log($"First webcam frame delivered on the GPU: {width}x{height} (source={(videoFrame.Direct3DSurface is not null ? "Direct3DSurface" : "SoftwareBitmap")}).");
            }

            return true;
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"GPU webcam delivery failed (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}); using CPU frames for this recording.");
            return false;
        }
    }

    private VideoFrame RentGpuFrame(int width, int height, IDirect3DDevice device)
    {
        if (_gpuRingWidth != width || _gpuRingHeight != height)
        {
            ClearGpuRing();
            _gpuRingWidth = width;
            _gpuRingHeight = height;
        }

        var index = _gpuRingIndex;
        _gpuRingIndex = (index + 1) % GpuRingDepth;
        return _gpuRing[index] ??= VideoFrame.CreateAsDirect3D11SurfaceBacked(
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            width,
            height,
            device);
    }

    private void ClearGpuRing()
    {
        for (var i = 0; i < _gpuRing.Length; i++)
        {
            _gpuRing[i]?.Dispose();
            _gpuRing[i] = null;
        }

        _gpuRingIndex = 0;
        _gpuRingWidth = 0;
        _gpuRingHeight = 0;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(WebcamCaptureService));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await StopAsync().ConfigureAwait(false);
        _stateGate.Dispose();
    }
}
