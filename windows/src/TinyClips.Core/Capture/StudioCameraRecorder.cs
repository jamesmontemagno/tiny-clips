using TinyClips.Core.Models;

namespace TinyClips.Core.Capture;

/// <summary>
/// Encodes webcam frames into their own video-only MP4 for a Studio recording. Frames are stamped on
/// the shared recording timeline with the first written frame at zero, so a pause leaves no gap and
/// <see cref="StartOffset"/> says where the track sits against the screen track.
/// </summary>
internal sealed class StudioCameraRecorder : IDisposable
{
    private readonly string _path;
    private readonly RecordingTimeline _timeline;
    private readonly int _targetFps;
    private readonly TimeSpan _frameDuration;
    private readonly TimeSpan _minFrameSpacing;
    private readonly object _gate = new();
    private MfSinkWriterEncoder? _encoder;
    private byte[]? _flipBuffer;
    private int _encodedWidth;
    private int _encodedHeight;
    private TimeSpan? _firstTimelineTime;
    private TimeSpan _lastPts;
    private long _framesWritten;
    private bool _finished;
    private bool _failed;

    public StudioCameraRecorder(string path, RecordingTimeline timeline, int targetFps)
    {
        _path = path;
        _timeline = timeline;
        _targetFps = Math.Clamp(targetFps, 1, 60);
        _frameDuration = TimeSpan.FromSeconds(1.0 / _targetFps);
        // Three quarters of a frame absorbs timestamp jitter at the target rate while still dropping
        // every other frame of a camera that runs twice as fast.
        _minFrameSpacing = TimeSpan.FromTicks(_frameDuration.Ticks * 3 / 4);
    }

    /// <summary>Recording timeline time of the first written frame, or null when none was written.</summary>
    public TimeSpan? StartOffset
    {
        get
        {
            lock (_gate)
            {
                return _firstTimelineTime;
            }
        }
    }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public bool HasFrames => FramesWritten > 0;

    public double DurationSeconds
    {
        get
        {
            lock (_gate)
            {
                return _framesWritten > 0 ? (_lastPts + _frameDuration).TotalSeconds : 0;
            }
        }
    }

    public void OnFrameArrived(object? sender, WebcamFrameArrivedEventArgs args)
    {
        var frame = args.Frame;
        if (frame.IsGpuFrame ||
            frame.Timestamp == TimeSpan.Zero ||
            frame.Width < 2 ||
            frame.Height < 2 ||
            frame.BgraPixels.Length < (long)frame.Width * frame.Height * 4 ||
            !_timeline.TryNormalizeActive(frame.Timestamp, out var timelineTime))
        {
            return;
        }

        lock (_gate)
        {
            if (_finished || _failed)
            {
                return;
            }

            try
            {
                var pts = timelineTime - (_firstTimelineTime ?? timelineTime);
                if (_framesWritten > 0 && pts - _lastPts < _minFrameSpacing)
                {
                    // Out of order, a duplicate, or faster than the track: the encoder needs
                    // timestamps that only move forward.
                    return;
                }

                var width = frame.Width - (frame.Width % 2);
                var height = frame.Height - (frame.Height % 2);
                if (_encoder is null)
                {
                    _encoder = CreateEncoder(width, height);
                    _encodedWidth = width;
                    _encodedHeight = height;
                }
                else if (width != _encodedWidth || height != _encodedHeight)
                {
                    // The track has one size. A camera that changes format mid-recording is ignored
                    // from then on rather than corrupting the stream.
                    return;
                }

                _encoder.WriteVideo(CopyBottomUp(frame, width, height), pts, _frameDuration);
                _firstTimelineTime ??= timelineTime;
                _lastPts = pts;
                Interlocked.Increment(ref _framesWritten);
            }
            catch (Exception ex)
            {
                _failed = true;
                WebcamDiagnostics.Log($"Studio camera encoder failed; the camera track ends here (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}).");
            }
        }
    }

    /// <summary>
    /// Finalizes the file and releases it so it can be read, moved, or deleted. Never throws: a
    /// camera track that cannot be finalized must not stop the screen recording from being saved.
    /// </summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            var encoder = _encoder;
            _encoder = null;
            _flipBuffer = null;
            if (encoder is null)
            {
                return;
            }

            try
            {
                encoder.Finish();
            }
            catch (Exception ex)
            {
                _failed = true;
                WebcamDiagnostics.Log($"Studio camera track could not be finalized (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}).");
            }

            try
            {
                encoder.Dispose();
            }
            catch (Exception ex)
            {
                WebcamDiagnostics.Log($"Studio camera encoder teardown failed (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}).");
            }
        }
    }

    public void Dispose() => Finish();

    private MfSinkWriterEncoder CreateEncoder(int width, int height)
    {
        var bitrate = (uint)Math.Clamp((long)width * height * _targetFps / 10, 2_000_000, 18_000_000);
        var device = WgcInterop.GetSharedDevice().D3D;
        try
        {
            return CreateEncoder(width, height, bitrate, device, enableHardwareTransforms: true);
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"Studio camera hardware encoder unavailable (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}); retrying with the software encoder.");
            return CreateEncoder(width, height, bitrate, device, enableHardwareTransforms: false);
        }
    }

    private MfSinkWriterEncoder CreateEncoder(int width, int height, uint bitrate, Vortice.Direct3D11.ID3D11Device device, bool enableHardwareTransforms) =>
        MfSinkWriterEncoder.Create(
            _path,
            device,
            width,
            height,
            _targetFps,
            bitrate,
            VideoCodec.H264,
            includeAudio: false,
            AudioCaptureService.SampleRate,
            AudioCaptureService.Channels,
            AudioCaptureService.BitsPerSample,
            0,
            enableHardwareTransforms);

    /// <summary>
    /// Media Foundation BGRA samples are bottom-up; camera frames arrive top-down. The buffer is
    /// reused because the encoder copies it before <c>WriteVideo</c> returns.
    /// </summary>
    private byte[] CopyBottomUp(WebcamFrame frame, int width, int height)
    {
        var sourceStride = frame.Width * 4;
        var stride = width * 4;
        var length = stride * height;
        if (_flipBuffer is null || _flipBuffer.Length != length)
        {
            _flipBuffer = new byte[length];
        }

        var source = frame.BgraPixels.Span;
        var destination = _flipBuffer.AsSpan();
        for (var y = 0; y < height; y++)
        {
            source.Slice((height - 1 - y) * sourceStride, stride).CopyTo(destination.Slice(y * stride, stride));
        }

        return _flipBuffer;
    }
}