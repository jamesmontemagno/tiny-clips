using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace TinyClips.Core.Capture;

/// <summary>
/// Productionized video recorder: a continuous WGC capture session pumps BGRA frames
/// into a bounded channel; a <see cref="MediaStreamSource"/> drains that channel on
/// demand and a hardware-accelerated <see cref="MediaTranscoder"/> writes H.264 MP4.
/// </summary>
public sealed class VideoRecordingService : IVideoRecordingService
{
    private readonly IMonitorService _monitors;
    private readonly IClipStorageService _storage;
    private readonly ICaptureSettings _settings;
    private readonly IClipAnalyticsService _analytics;
    private readonly IWebcamCaptureService _webcamCapture;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // MF_MT_MPEG2_PROFILE attribute + eAVEncH264VProfile values.
    // Baseline (66) disables B-frames for max compatibility; High (100) is the default.
    private static readonly Guid Mpeg2ProfileAttribute = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private const int MfTransformTypeNotSet = unchecked((int)0xC00D6D60);
    private const uint AvcBaselineProfile = 66;
    private const uint AvcHighProfile = 100;

    private ContinuousCaptureSession? _capture;
    private Channel<TimestampedFrame>? _channel;
    private Task? _transcodeTask;
    private FileStream? _fileStream;
    private MediaStreamSource? _mediaStreamSource;
    private string? _outputPath;
    private TimeSpan _frameDuration;
    private Timer? _limitTimer;
    private int _stopping;
    private int _discardRequested;
    private PreparedPipeline? _prepared;

    private MouseClickMonitor? _clickMonitor;
    private MouseClickOverlayStyle _clickStyle;
    private int _clickOriginX;
    private int _clickOriginY;
    private BrandingOverlayCompositor? _branding;
    private WebcamOverlayCompositor? _webcamOverlay;
    private bool _webcamCaptureSubscribed;
    private long _webcamCompositedFrames;
    private long _webcamOverlayNullFrames;
    private long _webcamNoFrameFrames;
    private WebcamFrame? _lastTimelineWebcamFrame;
    private WebcamPlacementTimeline? _webcamPlacements;

    private AudioCaptureService? _audio;
    private AudioStreamDescriptor? _audioDescriptor;
    private bool _hasAudio;
    private long _audioFramesRead;
    private long _audioSamplesRequested;
    private long _audioNonSilentChunks;
    private long _audioStarvedChunks;
    private long _videoFramesDropped;
    private long _drainFramesServed;
    private bool _loggedFirstAudioChunk;
    private volatile bool _audioEnding;
    private volatile bool _audioDraining;
    private RecordingTimeline? _recordingTimeline;
    private string _encoderPath = "unknown";
    private TimeSpan _activeUserOffset;

    // Remaining captured audio handed to the muxer after Stop before the track is ended. Bounds the
    // tail so a source that somehow keeps producing cannot hold the transcode open.
    private const int MaxDrainFrames = AudioCaptureService.SampleRate / 2;

    public VideoRecordingService(
        IMonitorService monitors,
        IClipStorageService storage,
        ICaptureSettings settings,
        IClipAnalyticsService analytics,
        IWebcamCaptureService webcamCapture)
    {
        _monitors = monitors;
        _storage = storage;
        _settings = settings;
        _analytics = analytics;
        _webcamCapture = webcamCapture;
    }

    public bool IsRecording { get; private set; }

    public bool IsPaused { get; private set; }

    public bool CanMuteSystemAudio => _audio?.CanMuteSystemAudio == true;

    public bool CanMuteMicrophone => _audio?.CanMuteMicrophone == true;

    public bool IsSystemAudioMuted => _audio?.IsSystemAudioMuted == true;

    public bool IsMicrophoneMuted => _audio?.IsMicrophoneMuted == true;

    public event EventHandler<string?>? RecordingCompleted;

    public event EventHandler<string>? WebcamCaptureFailed;

    public async Task PrepareAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            var captureTarget = ResolveTarget(target);
            if (_prepared is { } existing)
            {
                if (existing.Matches(captureTarget, region))
                {
                    return;
                }

                await CleanupFailedStartAsync().ConfigureAwait(false);
            }

            try
            {
                await PrepareCoreAsync(captureTarget, region, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CleanupFailedStartAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DiscardPreparedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording && _prepared is not null)
            {
                await CleanupFailedStartAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CaptureTarget? target = null, PixelRect? region = null, double? timeLimitMinutesOverride = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            var captureTarget = ResolveTarget(target);
            try
            {
                if (_prepared is null || !_prepared.Matches(captureTarget, region))
                {
                    if (_prepared is not null)
                    {
                        await CleanupFailedStartAsync().ConfigureAwait(false);
                    }

                    await PrepareCoreAsync(captureTarget, region, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    CaptureFlowTrace.Mark("video: using pre-warmed pipeline");
                }

                await BeginPreparedAsync(timeLimitMinutesOverride, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CleanupFailedStartAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private CaptureTarget ResolveTarget(CaptureTarget? target) => target ?? CaptureTarget.Monitor(
        (_monitors.GetPrimaryMonitor()
            ?? throw new InvalidOperationException("No monitor was found to record.")).HMonitor);

    /// <summary>
    /// Builds the whole pipeline up to "encoder ready": capture session (capturing but not
    /// emitting), webcam, audio devices, output file and a prepared transcoder. Safe to run during
    /// the countdown because nothing is written to the timeline until <see cref="BeginPreparedAsync"/>.
    /// </summary>
    private async Task PrepareCoreAsync(CaptureTarget captureTarget, PixelRect? region, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _discardRequested, 0);

        var fps = Math.Clamp(_settings.VideoFrameRate, 1, 60);
        _frameDuration = TimeSpan.FromSeconds(1.0 / fps);

        // DropWrite makes TryWrite report success even when the item is discarded, so drops are
        // counted through the item-dropped callback rather than the TryWrite result.
        _channel = Channel.CreateBounded<TimestampedFrame>(
            new BoundedChannelOptions(fps * 4)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true,
            },
            _ => Interlocked.Increment(ref _videoFramesDropped));

        _capture = new ContinuousCaptureSession(captureTarget, region, fps, includeCursor: true);
        _capture.FrameReady += OnFrameReady;
        _capture.Start();
        CaptureFlowTrace.Mark("video: capture session started");

        StartMouseClickOverlay(captureTarget, region);
        _branding = _settings.ShowBrandingOverlay ? new BrandingOverlayCompositor() : null;
        await StartWebcamOverlayAsync(cancellationToken).ConfigureAwait(false);
        CaptureFlowTrace.Mark("video: webcam overlay started");

        var width = _capture.OutputWidth;
        var height = _capture.OutputHeight;

        _outputPath = _storage.GenerateFilePath(CaptureType.Video);
        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _fileStream = new FileStream(_outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var randomAccessStream = _fileStream.AsRandomAccessStream();

        _audioEnding = false;
        _audioDraining = false;
        _audioFramesRead = 0;
        _audioSamplesRequested = 0;
        _audioNonSilentChunks = 0;
        _audioStarvedChunks = 0;
        _drainFramesServed = 0;
        Interlocked.Exchange(ref _videoFramesDropped, 0);
        _loggedFirstAudioChunk = false;
        StartAudioCapture();
        CaptureFlowTrace.Mark("video: audio capture started");

        var includeAudio = _hasAudio;
        var profile = CreateEncodingProfile(width, height, fps, includeAudio);
        var mediaStreamSource = CreateMediaStreamSource(width, height, fps);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        PrepareTranscodeResult prepare;
        var usedFallbackProfile = false;
        try
        {
            prepare = await PrepareTranscodeAsync(
                transcoder,
                mediaStreamSource,
                randomAccessStream,
                profile,
                cancellationToken).ConfigureAwait(false);
        }
        catch (COMException ex) when (ex.HResult == MfTransformTypeNotSet)
        {
            prepare = await RetryPrepareWithBaselineAsync(
                randomAccessStream,
                width,
                height,
                fps,
                includeAudio,
                cancellationToken,
                ex).ConfigureAwait(false);
            usedFallbackProfile = true;
        }

        if (!prepare.CanTranscode && !usedFallbackProfile)
        {
            prepare = await RetryPrepareWithBaselineAsync(
                randomAccessStream,
                width,
                height,
                fps,
                includeAudio,
                cancellationToken,
                null).ConfigureAwait(false);
            usedFallbackProfile = true;
        }

        if (!prepare.CanTranscode)
        {
            throw new InvalidOperationException($"Cannot encode video: {prepare.FailureReason}.");
        }

        _encoderPath = usedFallbackProfile ? "software H.264 Baseline (fallback)" : "H.264 High (hardware-accelerated)";
        CaptureFlowTrace.Mark("video: transcoder prepared");
        _prepared = new PreparedPipeline(captureTarget, region, prepare);
    }

    private async Task BeginPreparedAsync(double? timeLimitMinutesOverride, CancellationToken cancellationToken)
    {
        var prepared = _prepared ?? throw new InvalidOperationException("Pipeline has not been prepared.");
        _prepared = null;

        // The encoder is ready to consume frames. Wait briefly for the first webcam frame, then
        // give screen, webcam, loopback, and microphone one shared QPC origin. Audio packets retain
        // their source timestamp offsets rather than being flattened by independent buffers. This
        // anchors the recorded timeline to the real start moment — without it, the capture clock,
        // encoder prep and camera warm-up were baked in as several seconds of dead pre-roll
        // (frozen screen, no webcam) at the front of every clip, and that pre-roll saturated the
        // bounded frame channel so real frames near the end were dropped.
        await WaitForFirstWebcamFrameAsync(cancellationToken).ConfigureAwait(false);
        _recordingTimeline = RecordingTimeline.StartNow();
        _webcamPlacements = new WebcamPlacementTimeline(_settings.WebcamCornerPosition);
        _audio?.BeginTimeline(_recordingTimeline);
        _capture!.BeginEmitting(_recordingTimeline);

        _transcodeTask = prepared.Transcode.TranscodeAsync().AsTask();
        IsRecording = true;
        CaptureFlowTrace.Mark("video: recording started (emitting)");

        var limitMinutes = timeLimitMinutesOverride ?? _settings.VideoRecordingTimeLimitMinutes;
        if (limitMinutes > 0)
        {
            _limitTimer = new Timer(
                _ => _ = StopAsync(),
                null,
                TimeSpan.FromMinutes(limitMinutes),
                Timeout.InfiniteTimeSpan);
        }
    }

    private sealed record PreparedPipeline(CaptureTarget Target, PixelRect? Region, PrepareTranscodeResult Transcode)
    {
        public bool Matches(CaptureTarget target, PixelRect? region) =>
            Target.HMonitor == target.HMonitor && Target.Hwnd == target.Hwnd && Region == region;
    }

    private void OnFrameReady(CapturedFrame frame, TimeSpan pts)
    {
        if (IsPaused)
        {
            return;
        }

        DrawClickOverlay(frame, pts);
        _branding?.Draw(frame.BgraPixels, frame.Width, frame.Height);

        if (_webcamOverlay is not null)
        {
            if (_webcamCapture.TryGetLatestFrame(out WebcamFrame? webcamFrame) &&
                webcamFrame is not null &&
                IsWebcamFrameReady(webcamFrame, pts))
            {
                _lastTimelineWebcamFrame = webcamFrame;
            }

            if (_lastTimelineWebcamFrame is not null)
            {
                var corner = _webcamPlacements?.CornerAt(pts) ?? _settings.WebcamCornerPosition;
                _webcamOverlay.Draw(frame.BgraPixels, frame.Width, frame.Height, _lastTimelineWebcamFrame, corner);
                Interlocked.Increment(ref _webcamCompositedFrames);
            }
            else
            {
                Interlocked.Increment(ref _webcamNoFrameFrames);
            }
        }
        else if (_settings.WebcamEnabled)
        {
            Interlocked.Increment(ref _webcamOverlayNullFrames);
        }

        // Encoder back-pressure: the frame is dropped (counted by the channel's item-dropped
        // callback) but PTS stays wall-clock, so the video simply has a lower effective frame rate
        // here and never slides against audio.
        _channel?.Writer.TryWrite(new TimestampedFrame(CreateBottomUpVideoBuffer(frame), pts));
    }

    private void StartMouseClickOverlay(CaptureTarget target, PixelRect? region)
    {
        // Mouse-click visuals only map reliably onto a (possibly cropped) monitor;
        // window targets move/resize, so skip them — matching the mac restriction.
        if (target.IsWindow || !_settings.ShouldShowMouseClickVisuals(CaptureType.Video))
        {
            return;
        }

        var monitor = _monitors.GetMonitors().FirstOrDefault(m => m.HMonitor == target.HMonitor)
            ?? _monitors.GetPrimaryMonitor();
        if (monitor == null)
        {
            return;
        }

        _clickOriginX = monitor.X + (region?.X ?? 0);
        _clickOriginY = monitor.Y + (region?.Y ?? 0);
        _clickStyle = _settings.MouseClickOverlayStyleFor(CaptureType.Video);
        _clickMonitor = new MouseClickMonitor();
        _clickMonitor.Start();
    }

    private void DrawClickOverlay(CapturedFrame frame, TimeSpan pts)
    {
        var monitor = _clickMonitor;
        if (monitor == null)
        {
            return;
        }

        MouseClickOverlayCompositor.Draw(
            frame.BgraPixels,
            frame.Width,
            frame.Height,
            pts.TotalSeconds,
            monitor.GetClicks(),
            _clickOriginX,
            _clickOriginY,
            _clickStyle);
    }

    private static byte[] CreateBottomUpVideoBuffer(CapturedFrame frame)
    {
        var rowStride = frame.Width * 4;
        var pixels = new byte[frame.BgraPixels.Length];

        // Media Foundation BGRA samples are bottom-up; WGC frames arrive top-down.
        for (var y = 0; y < frame.Height; y++)
        {
            System.Buffer.BlockCopy(
                frame.BgraPixels,
                (frame.Height - 1 - y) * rowStride,
                pixels,
                y * rowStride,
                rowStride);
        }

        return pixels;
    }

    private MediaEncodingProfile CreateEncodingProfile(
        int width,
        int height,
        int fps,
        bool includeAudio,
        bool useBaselineProfile = false)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
        profile.Audio = includeAudio
            ? AudioEncodingProperties.CreateAac(AudioCaptureService.SampleRate, AudioCaptureService.Channels, 192_000)
            : null;
        profile.Video.Subtype = MediaEncodingSubtypes.H264;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp((long)width * height * fps / 10, 2_000_000, 24_000_000);

        // High is the normal recording profile. Baseline is reserved for the recovery path when
        // the system encoder cannot initialize with High.
        profile.Video.Properties[Mpeg2ProfileAttribute] =
            useBaselineProfile ? AvcBaselineProfile : AvcHighProfile;

        return profile;
    }

    private MediaStreamSource CreateMediaStreamSource(int width, int height, int fps)
    {
        DetachMediaStreamSource();

        // WinRT stream descriptors are single-use: once attached to a MediaStreamSource they
        // cannot be reused for another one ("This object has already been initialized"). Build
        // fresh descriptors on every call so the Baseline retry path gets its own instances.
        var videoProps = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        videoProps.FrameRate.Numerator = (uint)fps;
        videoProps.FrameRate.Denominator = 1;
        var videoDescriptor = new VideoStreamDescriptor(videoProps);

        MediaStreamSource mediaStreamSource;
        if (_hasAudio)
        {
            _audioDescriptor = new AudioStreamDescriptor(
                AudioEncodingProperties.CreatePcm(AudioCaptureService.SampleRate, AudioCaptureService.Channels, AudioCaptureService.BitsPerSample));
            mediaStreamSource = new MediaStreamSource(videoDescriptor, _audioDescriptor);
        }
        else
        {
            _audioDescriptor = null;
            mediaStreamSource = new MediaStreamSource(videoDescriptor);
        }

        mediaStreamSource.BufferTime = TimeSpan.Zero;
        mediaStreamSource.Starting += OnMediaStreamSourceStarting;
        mediaStreamSource.SampleRequested += OnSampleRequested;
        _mediaStreamSource = mediaStreamSource;
        return mediaStreamSource;
    }

    private void DetachMediaStreamSource()
    {
        var mediaStreamSource = _mediaStreamSource;
        if (mediaStreamSource is null)
        {
            return;
        }

        mediaStreamSource.Starting -= OnMediaStreamSourceStarting;
        mediaStreamSource.SampleRequested -= OnSampleRequested;
        _mediaStreamSource = null;
    }

    private static async Task<PrepareTranscodeResult> PrepareTranscodeAsync(
        MediaTranscoder transcoder,
        MediaStreamSource mediaStreamSource,
        IRandomAccessStream outputStream,
        MediaEncodingProfile profile,
        CancellationToken cancellationToken)
    {
        return await transcoder
            .PrepareMediaStreamSourceTranscodeAsync(mediaStreamSource, outputStream, profile)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PrepareTranscodeResult> RetryPrepareWithBaselineAsync(
        IRandomAccessStream outputStream,
        int width,
        int height,
        int fps,
        bool includeAudio,
        CancellationToken cancellationToken,
        COMException? originalException)
    {
        WebcamDiagnostics.Log(originalException is null
            ? "Transcode prepare failed with the requested H.264 profile; retrying with software Baseline profile."
            : $"Transcode prepare failed with 0x{(uint)originalException.HResult:X8}; retrying with software Baseline profile.");

        DetachMediaStreamSource();
        _fileStream?.SetLength(0);
        outputStream.Seek(0);

        var fallbackProfile = CreateEncodingProfile(
            width,
            height,
            fps,
            includeAudio,
            useBaselineProfile: true);
        var fallbackSource = CreateMediaStreamSource(width, height, fps);
        var fallbackTranscoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
        return await PrepareTranscodeAsync(
            fallbackTranscoder,
            fallbackSource,
            outputStream,
            fallbackProfile,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupFailedStartAsync()
    {
        _prepared = null;
        DetachMediaStreamSource();
        _limitTimer?.Dispose();
        _limitTimer = null;
        _clickMonitor?.Dispose();
        _clickMonitor = null;
        _branding = null;
        await StopWebcamOverlayAsync().ConfigureAwait(false);
        _capture?.Dispose();
        _capture = null;
        DisposeAudio();
        _channel?.Writer.TryComplete();
        _channel = null;
        _transcodeTask = null;
        _fileStream?.Dispose();
        _fileStream = null;

        if (!string.IsNullOrEmpty(_outputPath))
        {
            try
            {
                File.Delete(_outputPath);
            }
            catch
            {
                // Best-effort cleanup of the partial file.
            }
        }

        _outputPath = null;
        _recordingTimeline = null;
        _webcamPlacements = null;
        _lastTimelineWebcamFrame = null;
        IsRecording = false;
        WebcamDiagnostics.EndRecording();
    }

    private async Task StartWebcamOverlayAsync(CancellationToken cancellationToken)
    {
        _webcamOverlay = null;
        _lastTimelineWebcamFrame = null;
        Interlocked.Exchange(ref _webcamCompositedFrames, 0);
        Interlocked.Exchange(ref _webcamOverlayNullFrames, 0);
        Interlocked.Exchange(ref _webcamNoFrameFrames, 0);

        WebcamDiagnostics.BeginRecording();
        WebcamDiagnostics.Log($"StartWebcamOverlay: WebcamEnabled={_settings.WebcamEnabled} deviceId='{(string.IsNullOrWhiteSpace(_settings.SelectedWebcamId) ? "(default)" : _settings.SelectedWebcamId)}' shape={_settings.WebcamShape} size={_settings.WebcamSizePreset} corner={_settings.WebcamCornerPosition}");

        if (!_settings.WebcamEnabled)
        {
            WebcamDiagnostics.Log("Webcam is disabled in settings; no overlay will be composited.");
            return;
        }

        _webcamOverlay = new WebcamOverlayCompositor(
            _settings.WebcamCornerPosition,
            _settings.WebcamSizePreset,
            _settings.WebcamShape,
            _settings.WebcamCornerRadius);

        if (_webcamCapture.IsRunning)
        {
            try
            {
                await _webcamCapture.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort; we'll attempt a fresh start below.
            }
        }

        try
        {
            _webcamCapture.CaptureFailed += OnWebcamCaptureFailed;
            _webcamCaptureSubscribed = true;
            await _webcamCapture
                .StartAsync(_settings.SelectedWebcamId, ResolveRequestedWebcamSize(_settings.WebcamSizePreset), cancellationToken)
                .ConfigureAwait(false);
            WebcamDiagnostics.Log($"StartWebcamOverlay: webcam capture start returned, IsRunning={_webcamCapture.IsRunning}");
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"StartWebcamOverlay: start threw 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            await StopWebcamOverlayAsync().ConfigureAwait(false);
            WebcamCaptureFailed?.Invoke(this, DescribeWebcamFailure(ex));
        }
    }

    private async Task WaitForFirstWebcamFrameAsync(CancellationToken cancellationToken)
    {
        // Only relevant when a webcam overlay is active and capture actually started.
        if (_webcamOverlay is null || !_webcamCapture.IsRunning)
        {
            return;
        }

        // Cap the wait so a slow or unavailable camera can never block the recording start.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_webcamCapture.TryGetLatestFrame(out var frame) && frame is not null)
            {
                WebcamDiagnostics.Log("First webcam frame ready; beginning emission with overlay present.");
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        WebcamDiagnostics.Log("Webcam first frame not ready within budget; beginning emission anyway.");
    }

    private async Task StopWebcamOverlayAsync()
    {
        _webcamOverlay = null;

        if (_webcamCaptureSubscribed)
        {
            _webcamCapture.CaptureFailed -= OnWebcamCaptureFailed;
            _webcamCaptureSubscribed = false;
        }

        if (!_webcamCapture.IsRunning)
        {
            return;
        }

        try
        {
            await _webcamCapture.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore webcam teardown errors so screen recording can complete cleanly.
        }
    }

    private void OnWebcamCaptureFailed(object? sender, WebcamCaptureFailedEventArgs args)
    {
        WebcamDiagnostics.Log($"OnWebcamCaptureFailed (mid-recording): code={args.Code} message='{args.Message}' — overlay disabled for the rest of this recording.");
        _webcamOverlay = null;
        var detail = string.IsNullOrWhiteSpace(args.Message) ? null : args.Message;
        WebcamCaptureFailed?.Invoke(this, detail is null
            ? "The webcam stopped during recording. The screen recording continued without it."
            : $"The webcam stopped during recording ({detail}). The screen recording continued without it.");
    }

    private static string DescribeWebcamFailure(Exception ex)
    {
        // 0x80070005 (E_ACCESSDENIED) surfaces when camera access is blocked in Privacy settings.
        if (ex is UnauthorizedAccessException || (uint)ex.HResult == 0x80070005)
        {
            return "Camera access is blocked. Enable it in Settings > Privacy & security > Camera, then record again. The screen recording continued without the webcam.";
        }

        return $"The webcam couldn't start ({ex.Message}). The screen recording continued without it.";
    }

    private static BitmapSize ResolveRequestedWebcamSize(WebcamSizePreset preset) => preset switch
    {
        WebcamSizePreset.Small => new BitmapSize { Width = 640, Height = 360 },
        WebcamSizePreset.Large => new BitmapSize { Width = 1280, Height = 720 },
        _ => new BitmapSize { Width = 960, Height = 540 },
    };

    private bool IsWebcamFrameReady(WebcamFrame frame, TimeSpan screenPts)
    {
        var timeline = _recordingTimeline;
        if (timeline is null || frame.Timestamp == TimeSpan.Zero)
        {
            return true;
        }

        // A cached pre-origin frame is intentionally allowed at frame zero so an already-warmed
        // camera is visible immediately. Never composite a frame from ahead of the screen clock.
        var webcamPts = timeline.Normalize(frame.Timestamp);
        return webcamPts <= TimeSpan.Zero || webcamPts <= screenPts;
    }

    private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        args.Request.SetActualStartPosition(TimeSpan.Zero);
    }

    private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (_audioDescriptor is not null && ReferenceEquals(args.Request.StreamDescriptor, _audioDescriptor))
        {
            await HandleAudioRequestAsync(args).ConfigureAwait(false);
            return;
        }

        var channel = _channel;
        if (channel is null)
        {
            return;
        }

        var deferral = args.Request.GetDeferral();
        try
        {
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (channel.Reader.TryRead(out var frame))
                {
                    var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(), frame.Pts);
                    sample.Duration = _frameDuration;
                    args.Request.Sample = sample;
                    return;
                }
            }

            // Channel completed and drained -> signal end of stream.
            args.Request.Sample = null;
        }
        catch
        {
            args.Request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task HandleAudioRequestAsync(MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            const int frameCount = AudioCaptureService.SampleRate / 50;

            // Back-pressure: only hand the muxer a chunk once that many real frames have actually
            // been captured. The audio source pads silence on demand (ReadFully), so without this
            // the transcoder drains audio far faster than real time and the whole audio track
            // races ~1s ahead of the video. Gating on captured-frame availability (not the wall
            // clock) keeps audio locked to real capture progress AND never reads an empty buffer,
            // so there is no silence-splicing crackle.
            //
            // While paused there is no cap on the wait: no new audio arrives by design, and ending
            // the wait would hand the muxer a sample it must not have (or, worse, end the track).
            // When not paused, a generous cap prevents a stalled device from hanging the transcode;
            // the starved chunk is then filled with silence rather than ending the stream.
            var audio = _audio;
            var waited = 0;
            var starved = false;
            const int maxWaitMs = 2000;
            const int pollMs = 4;
            while (audio is not null && !_audioEnding && !_audioDraining && audio.AvailableFrames < frameCount)
            {
                if (IsPaused)
                {
                    await Task.Delay(pollMs).ConfigureAwait(false);
                    continue;
                }

                if (waited >= maxWaitMs)
                {
                    starved = true;
                    break;
                }

                await Task.Delay(pollMs).ConfigureAwait(false);
                waited += pollMs;
            }

            FillAudioRequest(args, frameCount, starved);
        }
        catch
        {
            args.Request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void FillAudioRequest(MediaStreamSourceSampleRequestedEventArgs args, int frameCount, bool starved)
    {
        var audio = _audio;
        if (audio is null || _audioEnding)
        {
            // End the audio stream so the transcode can terminate.
            args.Request.Sample = null;
            return;
        }

        if (_audioDraining)
        {
            // Devices are stopped; serve whatever was captured before Stop so the audio track
            // reaches the same point as the video, then end the stream.
            var remaining = Math.Min(audio.AvailableFrames, (int)Math.Max(0, MaxDrainFrames - _drainFramesServed));
            if (remaining <= 0)
            {
                _audioEnding = true;
                args.Request.Sample = null;
                return;
            }

            frameCount = Math.Min(frameCount, remaining);
            _drainFramesServed += frameCount;
        }

        var data = audio.ReadChunk(frameCount);
        if (data is null || data.Length == 0)
        {
            if (_audioDraining)
            {
                _audioEnding = true;
                args.Request.Sample = null;
                return;
            }

            // The mixer should always return a full (silence-padded) chunk; if it somehow did not,
            // substitute silence rather than ending the track mid-recording.
            data = new byte[frameCount * AudioCaptureService.Channels * (AudioCaptureService.BitsPerSample / 8)];
        }

        if (starved)
        {
            _audioStarvedChunks++;
            if (_audioStarvedChunks == 1 || _audioStarvedChunks % 50 == 0)
            {
                WebcamDiagnostics.Log($"Audio muxer starved: no captured audio for 2 s; filled chunk with silence (starvedChunks={_audioStarvedChunks}).");
            }
        }

        var producedFrames = data.Length / (AudioCaptureService.Channels * (AudioCaptureService.BitsPerSample / 8));
        var pts = TimeSpan.FromTicks((long)(_audioFramesRead * TimeSpan.TicksPerSecond / AudioCaptureService.SampleRate));
        var duration = TimeSpan.FromTicks((long)(producedFrames * TimeSpan.TicksPerSecond / AudioCaptureService.SampleRate));
        _audioFramesRead += producedFrames;

        _audioSamplesRequested++;
        if (ContainsNonSilence(data))
        {
            _audioNonSilentChunks++;
            if (!_loggedFirstAudioChunk)
            {
                _loggedFirstAudioChunk = true;
                WebcamDiagnostics.Log($"Audio muxer received first NON-SILENT chunk after {_audioSamplesRequested} request(s) ({producedFrames} frames).");
            }
        }

        if (_audioSamplesRequested % 250 == 0)
        {
            WebcamDiagnostics.Log($"Audio muxer progress: requests={_audioSamplesRequested} nonSilentChunks={_audioNonSilentChunks} framesRead={_audioFramesRead}.");
        }

        var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), pts);
        sample.Duration = duration;
        args.Request.Sample = sample;
    }

    private static bool ContainsNonSilence(byte[] pcm16)
    {
        // Scan interleaved 16-bit PCM for any sample above a small noise floor.
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            if (Math.Abs(sample) > 16)
            {
                return true;
            }
        }

        return false;
    }

    private void StartAudioCapture()
    {
        var wantSystem = _settings.RecordAudio;
        var wantMic = _settings.RecordMicrophone;
        var limitMic = _settings.MicrophoneLimiterEnabled;
        var userOffset = TimeSpan.FromMilliseconds(_settings.AudioOffsetMilliseconds);
        _activeUserOffset = userOffset;
        WebcamDiagnostics.Log($"StartAudioCapture: RecordAudio={wantSystem} RecordMicrophone={wantMic} MicrophoneLimiter={limitMic} audioOffsetMs={userOffset.TotalMilliseconds:F0} micDeviceId='{(string.IsNullOrWhiteSpace(_settings.SelectedMicrophoneId) ? "(default)" : _settings.SelectedMicrophoneId)}'");
        if (!wantSystem && !wantMic)
        {
            WebcamDiagnostics.Log("StartAudioCapture: no audio sources requested; recording will have no audio track.");
            return;
        }

        try
        {
            var audio = new AudioCaptureService(wantSystem, wantMic, _settings.SelectedMicrophoneId, limitMic, userOffset);
            if (audio.TryStart())
            {
                _audio = audio;
                // The AudioStreamDescriptor itself is built per-MediaStreamSource in
                // CreateMediaStreamSource; here we only record that audio is available.
                _hasAudio = true;
                WebcamDiagnostics.Log("StartAudioCapture: audio capture started; audio track will be muxed.");
            }
            else
            {
                WebcamDiagnostics.Log("StartAudioCapture: AudioCaptureService.TryStart returned false; NO audio source started.");
                audio.Dispose();
            }
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"StartAudioCapture: exception starting audio capture: {ex.GetType().Name}: {ex.Message}");
            _audio = null;
            _hasAudio = false;
            _audioDescriptor = null;
        }
    }

    private void DisposeAudio()
    {
        _audioEnding = true;
        _audioDraining = false;
        _audio?.Dispose();
        _audio = null;
        _hasAudio = false;
        _audioDescriptor = null;
    }


    public Task<string?> StopAsync() => StopAsync(discard: false);

    public async Task PauseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording || IsPaused)
            {
                return;
            }

            _recordingTimeline?.Pause();
            _capture?.PauseEmitting();
            _audio?.Pause();
            IsPaused = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording || !IsPaused)
            {
                return;
            }

            _recordingTimeline?.Resume();
            _audio?.Resume();
            _capture?.ResumeEmitting();
            IsPaused = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetWebcamCorner(WebcamCornerPosition corner)
    {
        _settings.WebcamCornerPosition = corner;
        var timeline = _recordingTimeline;
        _webcamPlacements?.Add(timeline?.Elapsed ?? TimeSpan.Zero, corner);
    }

    public void SetSystemAudioMuted(bool muted)
    {
        _audio?.SetSystemAudioMuted(muted);
    }

    public void SetMicrophoneMuted(bool muted)
    {
        _audio?.SetMicrophoneMuted(muted);
    }

    public async Task CancelAsync()
    {
        await StopAsync(discard: true).ConfigureAwait(false);
    }

    private bool ConsumeDiscardRequested(bool discard)
    {
        var latched = Interlocked.Exchange(ref _discardRequested, 0) == 1;
        return discard || latched;
    }

    private async Task<string?> StopAsync(bool discard)
    {
        if (discard)
        {
            Interlocked.Exchange(ref _discardRequested, 1);
        }

        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            return discard ? null : _outputPath;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording)
            {
                IsPaused = false;

                if (_prepared is not null)
                {
                    // A pre-warmed pipeline that never started (e.g. countdown cancelled).
                    await CleanupFailedStartAsync().ConfigureAwait(false);
                    return null;
                }

                if (ConsumeDiscardRequested(discard))
                {
                    DeleteOutputFileIfPresent(_outputPath);
                    _outputPath = null;
                }

                return null;
            }

            _limitTimer?.Dispose();
            _limitTimer = null;
            IsPaused = false;

            _clickMonitor?.Dispose();
            _clickMonitor = null;
            if (_settings.WebcamEnabled)
            {
                WebcamDiagnostics.Log($"Recording stopping — webcam composite summary: composited={Interlocked.Read(ref _webcamCompositedFrames)} noFrameYet={Interlocked.Read(ref _webcamNoFrameFrames)} overlayDisabled={Interlocked.Read(ref _webcamOverlayNullFrames)}");
            }
            await StopWebcamOverlayAsync().ConfigureAwait(false);

            // Stop the audio devices first, then let the muxer drain the audio already captured
            // (so the track ends where the video does) before it ends the stream. Only then stop
            // new video frames and let the encoder drain what's buffered.
            _audio?.Stop();
            _audioDraining = true;

            _capture?.Stop();
            _channel?.Writer.TryComplete();

            if (_transcodeTask is not null)
            {
                try
                {
                    await _transcodeTask.ConfigureAwait(false);
                }
                catch
                {
                    // Surface nothing here; the file may still be partially valid.
                }
            }

            LogSyncReport();

            _capture?.Dispose();
            _capture = null;
            DisposeAudio();
            _fileStream?.Dispose();
            _fileStream = null;
            _channel = null;
            _transcodeTask = null;
            _recordingTimeline = null;
            _webcamPlacements = null;
            _lastTimelineWebcamFrame = null;
            DetachMediaStreamSource();

            IsRecording = false;
            var path = _outputPath;
            var shouldDiscard = ConsumeDiscardRequested(discard);
            if (shouldDiscard && !string.IsNullOrEmpty(path))
            {
                DeleteOutputFileIfPresent(path);
                path = null;
            }
            else
            {
                if (HasNonEmptyOutputFile(path))
                {
                    _analytics.RecordCapture(CaptureType.Video);
                }

                RecordingCompleted?.Invoke(this, path);
            }

            _outputPath = path;
            return path;
        }
        finally
        {
            WebcamDiagnostics.EndRecording();
            Interlocked.Exchange(ref _stopping, 0);
            _gate.Release();
        }
    }

    /// <summary>
    /// One-shot end-of-recording summary so A/V sync can be verified from the diagnostics log
    /// without a listen test. Healthy: |delta| well under 30 ms, zero corrections, no starvation.
    /// </summary>
    private void LogSyncReport()
    {
        try
        {
            var timeline = _recordingTimeline;
            var videoPts = _capture?.LastEmittedPts ?? TimeSpan.MinValue;
            var videoEmitted = _capture?.EmittedFrameCount ?? 0;
            var videoDropped = Interlocked.Read(ref _videoFramesDropped);
            var elapsed = timeline?.Elapsed ?? TimeSpan.Zero;
            var pauses = timeline?.PauseCount ?? 0;
            var paused = timeline?.PausedDuration ?? TimeSpan.Zero;

            WebcamDiagnostics.Log($"Sync report: encoder='{_encoderPath}' elapsed={elapsed.TotalSeconds:F3}s pauses={pauses} pausedTotal={paused.TotalSeconds:F3}s.");
            WebcamDiagnostics.Log($"Sync report: video lastPts={(videoPts == TimeSpan.MinValue ? "none" : $"{videoPts.TotalSeconds:F3}s")} framesEmitted={videoEmitted} framesDroppedByEncoderBackpressure={videoDropped}.");

            if (!_hasAudio || _audio is null)
            {
                WebcamDiagnostics.Log("Sync report: no audio track.");
                return;
            }

            var audioPts = TimeSpan.FromSeconds(_audioFramesRead / (double)AudioCaptureService.SampleRate);

            // The audio cursor is an end position, so compare it with the END of the last video
            // sample (its PTS plus one frame), not its start. A non-zero user offset deliberately
            // shifts the audio endpoint by that amount, so grade the offset-compensated delta.
            var videoEnd = videoPts == TimeSpan.MinValue ? TimeSpan.MinValue : videoPts + _frameDuration;
            var rawDelta = videoEnd == TimeSpan.MinValue ? TimeSpan.Zero : audioPts - videoEnd;
            var delta = rawDelta - _activeUserOffset;
            WebcamDiagnostics.Log($"Sync report: audio pts={audioPts.TotalSeconds:F3}s chunks={_audioSamplesRequested} nonSilent={_audioNonSilentChunks} starvedChunks={_audioStarvedChunks} drainedFrames={_drainFramesServed} userOffsetMs={_activeUserOffset.TotalMilliseconds:F0} driverDiscontinuities={_audio.DriverDiscontinuityCount}.");
            WebcamDiagnostics.Log($"Sync report: audio-video end delta={delta.TotalMilliseconds:F1}ms offset-compensated (raw={rawDelta.TotalMilliseconds:F1}ms vs videoEnd={(videoEnd == TimeSpan.MinValue ? "none" : $"{videoEnd.TotalSeconds:F3}s")}; audio {(delta >= TimeSpan.Zero ? "longer" : "shorter")}; |delta| < 30 ms is healthy).");
            foreach (var stats in _audio.GetSyncStats())
            {
                WebcamDiagnostics.Log($"Sync report: {stats}");
            }
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"Sync report failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DeleteOutputFileIfPresent(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of discarded output.
        }
    }

    private static bool HasNonEmptyOutputFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists && fileInfo.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private readonly record struct TimestampedFrame(byte[] Pixels, TimeSpan Pts);
}
