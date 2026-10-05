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

    /// <summary>For StudioRenderCheck: go straight to the software encoder.</summary>
    internal bool ForceSoftwareEncoder { get; init; }

    /// <summary>For StudioRenderCheck: the encoder the track is written with, once the first frame has come.</summary>
    internal string? EncoderDescription { get; private set; }

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
            frame.Width < 2 ||
            frame.Height < 2 ||
            frame.BgraPixels.Length < (long)frame.Width * frame.Height * 4)
        {
            return;
        }

        // A camera that gives its frames no time arrives here with zero. Such a frame gets the
        // time it arrived, which is a little late and still a time; left out, a camera like that
        // would be live in its bubble throughout and missing from the project.
        var stamp = frame.Timestamp == TimeSpan.Zero ? RecordingTimeline.SystemRelativeNow() : frame.Timestamp;
        if (!_timeline.TryNormalizeActive(stamp, out var timelineTime))
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
                    EncoderDescription = _encoder.Description;
                    _encodedWidth = width;
                    _encodedHeight = height;
                }
                else if (width != _encodedWidth || height != _encodedHeight)
                {
                    // The track has one size. A camera that changes format mid-recording is ignored
                    // from then on rather than corrupting the stream.
                    return;
                }

                _encoder.WriteVideoTopDown(frame.BgraPixels.Span, frame.Width * 4, pts, _frameDuration);
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
        if (ForceSoftwareEncoder)
        {
            return CreateEncoder(width, height, bitrate, device, enableHardwareTransforms: false);
        }

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
            enableHardwareTransforms,
            // Camera frames arrive top-down. Saying so makes the track upright whichever
            // component reads the frame: left unsaid, an encoder that takes BGRA itself reads
            // the rows top-down and a converter in front of one that does not reads them bottom-up.
            topDownMemoryFrames: true,
            // Frames carry the time they arrived, and a camera's frames are not evenly spaced.
            keepFrameTimes: true);
}