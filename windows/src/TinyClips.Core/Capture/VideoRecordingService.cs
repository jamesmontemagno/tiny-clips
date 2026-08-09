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

    private AudioCaptureService? _audio;
    private AudioStreamDescriptor? _audioDescriptor;
    private bool _hasAudio;
    private long _audioFramesRead;
    private long _audioSamplesRequested;
    private long _audioNonSilentChunks;
    private bool _loggedFirstAudioChunk;
    private volatile bool _audioEnding;
    private RecordingTimeline? _recordingTimeline;

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

    public event EventHandler<string?>? RecordingCompleted;

    public event EventHandler<string>? WebcamCaptureFailed;

    public async Task StartAsync(CaptureTarget? target = null, PixelRect? region = null, double? timeLimitMinutesOverride = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            Interlocked.Exchange(ref _discardRequested, 0);

            var captureTarget = target ?? CaptureTarget.Monitor(
                (_monitors.GetPrimaryMonitor()
                    ?? throw new InvalidOperationException("No monitor was found to record.")).HMonitor);

            var fps = Math.Clamp(_settings.VideoFrameRate, 1, 60);
            _frameDuration = TimeSpan.FromSeconds(1.0 / fps);

            try
            {
                _channel = Channel.CreateBounded<TimestampedFrame>(new BoundedChannelOptions(fps * 4)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = true,
                });

                _capture = new ContinuousCaptureSession(captureTarget, region, fps, includeCursor: true);
                _capture.FrameReady += OnFrameReady;
                _capture.Start();

                StartMouseClickOverlay(captureTarget, region);
                _branding = _settings.ShowBrandingOverlay ? new BrandingOverlayCompositor() : null;
                await StartWebcamOverlayAsync(cancellationToken).ConfigureAwait(false);

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
                _audioFramesRead = 0;
                _audioSamplesRequested = 0;
                _audioNonSilentChunks = 0;
                _loggedFirstAudioChunk = false;
                StartAudioCapture();

                var includeAudio = _hasAudio;
                var requestedEncoderProfile = _settings.VideoEncoderProfile;
                var profile = CreateEncodingProfile(width, height, fps, requestedEncoderProfile, includeAudio);
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
                }

                if (!prepare.CanTranscode)
                {
                    throw new InvalidOperationException($"Cannot encode video: {prepare.FailureReason}.");
                }

                // The encoder is now ready to consume frames. Wait briefly for the first webcam
                // frame, then give screen, webcam, loopback, and microphone one shared QPC origin.
                // Audio packets retain their source timestamp offsets rather than being flattened
                // by independent buffers. This anchors the recorded timeline to the real start
                // moment — without it, the capture clock,
                // encoder prep and camera warm-up were baked in as several seconds of dead pre-roll
                // (frozen screen, no webcam) at the front of every clip, and that pre-roll saturated
                // the bounded frame channel so real frames near the end were dropped.
                await WaitForFirstWebcamFrameAsync(cancellationToken).ConfigureAwait(false);
                _recordingTimeline = RecordingTimeline.StartNow();
                _audio?.BeginTimeline(_recordingTimeline);
                _capture.BeginEmitting(_recordingTimeline);

                _transcodeTask = prepare.TranscodeAsync().AsTask();
                IsRecording = true;

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
                _webcamOverlay.Draw(frame.BgraPixels, frame.Width, frame.Height, _lastTimelineWebcamFrame);
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
        VideoEncoderProfile encoderProfile,
        bool includeAudio)
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

        // H.264 profile is configurable. High (default) enables B-frames + CABAC for the
        // best quality/size. Baseline disables B-frames for maximum playback compatibility.
        // (eAVEncH264VProfile_Base = 66, eAVEncH264VProfile_High = 100.)
        profile.Video.Properties[Mpeg2ProfileAttribute] =
            encoderProfile == VideoEncoderProfile.Baseline
                ? AvcBaselineProfile
                : AvcHighProfile;

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
            VideoEncoderProfile.Baseline,
            includeAudio);
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
            // so there is no silence-splicing crackle. A generous cap prevents a stalled capture
            // from hanging the transcode pull.
            var audio = _audio;
            var waited = 0;
            const int maxWaitMs = 2000;
            const int pollMs = 4;
            while (audio is not null &&
                   !_audioEnding &&
                   audio.AvailableFrames < frameCount &&
                   waited < maxWaitMs)
            {
                await Task.Delay(pollMs).ConfigureAwait(false);
                waited += pollMs;
            }

            FillAudioRequest(args, frameCount);
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

    private void FillAudioRequest(MediaStreamSourceSampleRequestedEventArgs args, int frameCount)
    {
        var audio = _audio;
        if (audio is null || _audioEnding)
        {
            // End the audio stream so the transcode can terminate.
            args.Request.Sample = null;
            return;
        }

        var data = audio.ReadChunk(frameCount);
        if (data is null || data.Length == 0)
        {
            args.Request.Sample = null;
            return;
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
        WebcamDiagnostics.Log($"StartAudioCapture: RecordAudio={wantSystem} RecordMicrophone={wantMic} micDeviceId='{(string.IsNullOrWhiteSpace(_settings.SelectedMicrophoneId) ? "(default)" : _settings.SelectedMicrophoneId)}'");
        if (!wantSystem && !wantMic)
        {
            WebcamDiagnostics.Log("StartAudioCapture: no audio sources requested; recording will have no audio track.");
            return;
        }

        try
        {
            var audio = new AudioCaptureService(wantSystem, wantMic, _settings.SelectedMicrophoneId);
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

            // Signal audio end-of-stream and stop the device before draining the encoder,
            // otherwise the continuous silence source would prevent EOS.
            _audioEnding = true;
            _audio?.Stop();

            // Stop new frames, then let the encoder drain what's buffered.
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

            _capture?.Dispose();
            _capture = null;
            DisposeAudio();
            _fileStream?.Dispose();
            _fileStream = null;
            _channel = null;
            _transcodeTask = null;
            _recordingTimeline = null;
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
