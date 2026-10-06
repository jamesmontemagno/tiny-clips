using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
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
    private readonly IStudioProjectStore _studioProjects;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // MF_MT_MPEG2_PROFILE attribute + eAVEncH264VProfile values.
    // Baseline (66) disables B-frames for max compatibility; High (100) is the default.
    private static readonly Guid Mpeg2ProfileAttribute = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private const int MfTransformTypeNotSet = unchecked((int)0xC00D6D60);
    private const uint AvcBaselineProfile = 66;
    private const uint AvcHighProfile = 100;

    private ContinuousCaptureSession? _capture;
    private GpuCaptureSession? _gpuCapture;
    private GpuOverlayCompositor? _gpuOverlay;
    private Channel<TimestampedFrame>? _channel;
    private Channel<GpuFrame>? _gpuChannel;
    private MfSinkWriterEncoder? _sinkWriter;
    private Thread? _audioMuxThread;
    private volatile bool _audioMuxStop;
    private RecordingPerformanceMonitor? _perf;
    private Task? _transcodeTask;
    private FileStream? _fileStream;
    private MediaStreamSource? _mediaStreamSource;
    private string? _outputPath;
    private int _frameRate;
    private TimeSpan _frameDuration;
    private Timer? _limitTimer;
    private int _stopping;
    private int _discardRequested;
    private PreparedPipeline? _prepared;
    private VideoRecordingOptions _activeOptions = VideoRecordingOptions.Default;
    private StudioProjectPaths? _studioPaths;
    private StudioCameraRecorder? _studioCameraRecorder;
    private readonly object _studioCursorGate = new();
    private readonly List<StudioPointSample> _studioCursorSamples = [];
    private IReadOnlyList<MouseClickSample> _studioClickSamples = [];
    private int _studioCaptureOriginX;
    private int _studioCaptureOriginY;
    private double _studioCaptureScale = 1;
    private WebcamCornerPosition _studioInitialCorner;
    private bool _studioRecordPointerEvents;
    private CaptureTarget? _activeTarget;
    private PixelRect? _activeRegion;

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
        IWebcamCaptureService webcamCapture,
        IStudioProjectStore studioProjects)
    {
        _monitors = monitors;
        _storage = storage;
        _settings = settings;
        _analytics = analytics;
        _webcamCapture = webcamCapture;
        _studioProjects = studioProjects;
    }

    public bool IsRecording { get; private set; }

    public bool IsPaused { get; private set; }

    public bool CanMuteSystemAudio => _audio?.CanMuteSystemAudio == true;

    public bool CanMuteMicrophone => _audio?.CanMuteMicrophone == true;

    public bool IsSystemAudioMuted => _audio?.IsSystemAudioMuted == true;

    public bool IsMicrophoneMuted => _audio?.IsMicrophoneMuted == true;

    public event EventHandler<string?>? RecordingCompleted;

    public event EventHandler<string>? StudioRecordingCompleted;

    public string? ActiveStudioProjectId => Volatile.Read(ref _studioPaths)?.ProjectId;

    public event EventHandler<string>? WebcamCaptureFailed;

    public RecordingPerformanceReport? LastPerformanceReport { get; private set; }

    /// <summary>"gpu" or "cpu" for the pipeline actually in use (after any fallback); null when idle.</summary>
    public string? ActivePipeline => _perf?.Pipeline;

    public async Task PrepareAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default)
    {
        await PrepareAsync(target, region, VideoRecordingOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareAsync(CaptureTarget? target, PixelRect? region, VideoRecordingOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            options ??= VideoRecordingOptions.Default;
            if (IsRecording)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            var captureTarget = ResolveTarget(target);
            if (_prepared is { } existing)
            {
                if (existing.Matches(captureTarget, region, options))
                {
                    return;
                }

                await CleanupFailedStartAsync().ConfigureAwait(false);
            }

            try
            {
                await PrepareCoreAsync(captureTarget, region, options, cancellationToken).ConfigureAwait(false);
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
        await StartAsync(target, region, timeLimitMinutesOverride, VideoRecordingOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CaptureTarget? target, PixelRect? region, double? timeLimitMinutesOverride, VideoRecordingOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            options ??= VideoRecordingOptions.Default;
            if (IsRecording)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            var captureTarget = ResolveTarget(target);
            try
            {
                if (_prepared is null || !_prepared.Matches(captureTarget, region, options))
                {
                    if (_prepared is not null)
                    {
                        await CleanupFailedStartAsync().ConfigureAwait(false);
                    }

                    await PrepareCoreAsync(captureTarget, region, options, cancellationToken).ConfigureAwait(false);
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
    /// emitting), prepared branding, webcam, audio devices, output file and a prepared transcoder. Safe to run during
    /// the countdown because nothing is written to the timeline until <see cref="BeginPreparedAsync"/>.
    /// </summary>
    private async Task PrepareCoreAsync(CaptureTarget captureTarget, PixelRect? region, VideoRecordingOptions options, CancellationToken cancellationToken)
    {
        // Until here this is the video of the recording before, kept for a discard that arrives
        // after its stop. This start has no file yet, and if it fails before it has one, the
        // cleanup must find nothing to delete.
        _outputPath = null;
        cancellationToken.ThrowIfCancellationRequested();
        WebcamDiagnostics.BeginRecording();
        Interlocked.Exchange(ref _discardRequested, 0);
        _activeOptions = options;
        _activeTarget = captureTarget;
        _activeRegion = region;
        _studioInitialCorner = _settings.WebcamCornerPosition;
        _studioClickSamples = [];
        lock (_studioCursorGate)
        {
            _studioCursorSamples.Clear();
        }

        var fps = Math.Clamp(_settings.VideoFrameRate, 1, 60);
        _frameRate = fps;
        _frameDuration = TimeSpan.FromSeconds(1.0 / fps);
        Interlocked.Exchange(ref _videoFramesDropped, 0);
        LastPerformanceReport = null;
        _perf = new RecordingPerformanceMonitor("cpu", 0, 0, fps)
        {
            RequestedPipeline = _settings.UseGpuRecordingPipeline ? "gpu" : "cpu",
            RequestedEncoderBackend = _settings.VideoEncoderBackend.ToString(),
        };
        _perf.BeginPreparation();

        int width;
        int height;
        if (_settings.UseGpuRecordingPipeline && TryStartGpuCapture(captureTarget, region, fps, out width, out height))
        {
            CaptureFlowTrace.Mark("video: GPU capture session initialized");
        }
        else
        {
            (width, height) = StartCpuCapture(captureTarget, region, fps);
        }

        // A recording for Studio has no badge in it: the editor draws one when it is asked for.
        _branding = !options.RecordForStudio && _settings.ShowBrandingOverlay ? new BrandingOverlayCompositor() : null;
        await PrepareBrandingAsync(height, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (_gpuCapture is { } gpu)
        {
            try
            {
                gpu.Start();
                _perf.Pipeline = "gpu";
                CaptureFlowTrace.Mark("video: GPU capture session started");
            }
            catch (Exception ex)
            {
                WebcamDiagnostics.Log($"GPU capture start unavailable (0x{(uint)ex.HResult:X8} {ex.GetType().Name}); falling back to CPU pipeline.");
                DisposeGpuPipeline();
                (width, height) = StartCpuCapture(captureTarget, region, fps);
                await PrepareBrandingAsync(height, cancellationToken).ConfigureAwait(false);
            }
        }

        // After the pipeline is settled: a GPU session that could not start has by now been
        // replaced by the CPU one, with a geometry of its own.
        InitializeStudioCaptureGeometry(captureTarget, region);
        StartMouseClickOverlay(captureTarget);
        await StartWebcamOverlayAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        CaptureFlowTrace.Mark("video: webcam overlay started");

        _outputPath = options.RecordForStudio
            ? BeginStudioRecording().ScreenPath
            : _storage.GenerateFilePath(CaptureType.Video);
        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _audioEnding = false;
        _audioDraining = false;
        _audioFramesRead = 0;
        _audioSamplesRequested = 0;
        _audioNonSilentChunks = 0;
        _audioStarvedChunks = 0;
        _drainFramesServed = 0;
        _loggedFirstAudioChunk = false;
        StartAudioCapture();
        CaptureFlowTrace.Mark("video: audio capture started");

        var includeAudio = _hasAudio;
        var codec = _settings.VideoCodec;

        if (_settings.VideoEncoderBackend == VideoEncoderBackend.SinkWriter &&
            TryCreateSinkWriter(width, height, fps, codec, includeAudio))
        {
            CaptureFlowTrace.Mark("video: sink writer prepared");
            _prepared = new PreparedPipeline(captureTarget, region, _activeOptions, null);
            _perf.Prepared();
            return;
        }

        await PrepareTranscoderAsync(captureTarget, region, width, height, fps, codec, includeAudio, cancellationToken).ConfigureAwait(false);
        _perf.Prepared();
    }

    private (int Width, int Height) StartCpuCapture(CaptureTarget captureTarget, PixelRect? region, int fps)
    {
        var perf = _perf ?? throw new InvalidOperationException("Performance monitor has not been initialized.");
        perf.Pipeline = "cpu";
        _capture = new ContinuousCaptureSession(captureTarget, region, fps, includeCursor: true, perf, processBorrowedFrame: OnFrameReady);
        _capture.Start();
        perf.Width = _capture.OutputWidth;
        perf.Height = _capture.OutputHeight;
        CaptureFlowTrace.Mark("video: capture session started");
        return (_capture.OutputWidth, _capture.OutputHeight);
    }

    private async Task PrepareBrandingAsync(int height, CancellationToken cancellationToken)
    {
        if (_branding is { } branding)
        {
            await branding.PrepareAsync(height, cancellationToken).ConfigureAwait(false);
            if (_gpuOverlay is { } overlay)
            {
                // WGC callbacks and the pump have not started, so this context has one owner.
                // Await even on cancellation before cleanup can dispose the compositor.
                await Task.Run(
                    () => overlay.PrepareBranding(branding, height, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            CaptureFlowTrace.Mark("video: branding preparation finished");
        }
        else
        {
            WebcamDiagnostics.Log("Branding preparation: disabled.");
        }
    }

    /// <summary>
    /// Sets up the <c>MediaTranscoder</c> + <c>MediaStreamSource</c> encoder (the original pull-model
    /// backend) and the plumbing that feeds it: a bounded channel of CPU frames, or a texture pool +
    /// channel of GPU frames.
    /// </summary>
    private async Task PrepareTranscoderAsync(
        CaptureTarget captureTarget,
        PixelRect? region,
        int width,
        int height,
        int fps,
        VideoCodec codec,
        bool includeAudio,
        CancellationToken cancellationToken)
    {
        if (_gpuCapture is { } gpu)
        {
            AttachTranscoderGpuPlumbing(gpu, fps);
        }
        else
        {
            // DropWrite makes TryWrite report success even when the item is discarded, so drops are
            // counted through the item-dropped callback rather than the TryWrite result.
            _channel = Channel.CreateBounded<TimestampedFrame>(
                new BoundedChannelOptions(fps * 4)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = true,
                },
                _ =>
                {
                    Interlocked.Increment(ref _videoFramesDropped);
                    _perf?.QueueDropped();
                });
        }

        _fileStream = new FileStream(_outputPath!, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var randomAccessStream = _fileStream.AsRandomAccessStream();

        var profile = CreateEncodingProfile(width, height, fps, includeAudio, codec);
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

        _encoderPath = usedFallbackProfile
            ? "H.264 Baseline via MediaTranscoder (fallback; hardware disabled; transform unverified)"
            : $"{(codec == VideoCodec.Hevc ? "HEVC" : "H.264 High")} via MediaTranscoder (hardware requested; transform unverified)";
        if (_perf is not null)
        {
            _perf.EncoderPath = _encoderPath;
            _perf.EncoderBackend = "Transcoder";
            _perf.HardwareEncodingRequested = !usedFallbackProfile;
        }

        CaptureFlowTrace.Mark("video: transcoder prepared");
        _prepared = new PreparedPipeline(captureTarget, region, _activeOptions, prepare);
    }

    /// <summary>
    /// Creates the <c>IMFSinkWriter</c> backend. GPU frames come from Media Foundation's own sample
    /// allocator (auto-recycled), CPU frames are pushed as memory buffers, and audio is pushed by
    /// <see cref="AudioMuxLoop"/>. Returns false (after cleanup) to fall back to the transcoder.
    /// </summary>
    private bool TryCreateSinkWriter(int width, int height, int fps, VideoCodec codec, bool includeAudio)
    {
        MfSinkWriterEncoder? encoder = null;
        try
        {
            var device = _gpuCapture?.D3DDevice ?? WgcInterop.GetSharedDevice().D3D;
            var bitrate = (uint)Math.Clamp((long)width * height * fps / 10, 2_000_000, 24_000_000);
            if (codec == VideoCodec.Hevc)
            {
                bitrate = (uint)(bitrate * 0.6);
            }

            encoder = MfSinkWriterEncoder.Create(
                _outputPath!,
                device,
                width,
                height,
                fps,
                bitrate,
                codec,
                includeAudio,
                AudioCaptureService.SampleRate,
                AudioCaptureService.Channels,
                AudioCaptureService.BitsPerSample,
                192_000);

            if (_gpuCapture is { } gpu)
            {
                gpu.AttachAllocator(encoder.CreateFrameAllocator(4, GpuPoolMaxCapacity(fps)));
            }

            _sinkWriter = encoder;
            _encoderPath = encoder.Description;
            if (_perf is not null)
            {
                _perf.EncoderPath = _encoderPath;
                _perf.EncoderBackend = "SinkWriter";
                _perf.HardwareEncodingRequested = true;
            }

            WebcamDiagnostics.Log($"Sink writer encoder ready: {encoder.Description}, audio={includeAudio}.");
            return true;
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"Sink writer encoder unavailable (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}); falling back to MediaTranscoder.");
            encoder?.Dispose();
            _sinkWriter = null;
            DeleteOutputFileIfPresent(_outputPath);
            return false;
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        _recordingTimeline = RecordingTimeline.StartNow();
        _studioInitialCorner = _settings.WebcamCornerPosition;
        _webcamPlacements = new WebcamPlacementTimeline(_studioInitialCorner);
        _audio?.BeginTimeline(_recordingTimeline);
        if (_activeOptions.RecordForStudio && _webcamCapture.IsRunning && _studioPaths?.CameraPath is { } cameraPath)
        {
            _studioCameraRecorder = new StudioCameraRecorder(cameraPath, _recordingTimeline, Math.Min(30, Math.Clamp(_settings.VideoFrameRate, 1, 60)));
            _webcamCapture.FrameArrived += _studioCameraRecorder.OnFrameArrived;
        }

        _perf?.Start();
        _capture?.BeginEmitting(_recordingTimeline);
        _gpuCapture?.BeginEmitting(_recordingTimeline);

        if (prepared.Transcode is { } transcode)
        {
            _transcodeTask = transcode.TranscodeAsync().AsTask();
        }
        else if (_sinkWriter is not null && _hasAudio)
        {
            StartAudioMux();
        }

        IsRecording = true;
        CaptureFlowTrace.Mark($"video: recording started (emitting, pipeline={_perf?.Pipeline ?? "cpu"}, encoder={_encoderPath})");

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

    private sealed record PreparedPipeline(CaptureTarget Target, PixelRect? Region, VideoRecordingOptions Options, PrepareTranscodeResult? Transcode)
    {
        public bool Matches(CaptureTarget target, PixelRect? region, VideoRecordingOptions options) =>
            Target.HMonitor == target.HMonitor &&
            Target.Hwnd == target.Hwnd &&
            Region == region &&
            Options.RecordForStudio == options.RecordForStudio;
    }

    private void OnFrameReady(CapturedFrame frame, TimeSpan pts)
    {
        if (IsPaused)
        {
            _perf?.FrameNotSubmitted();
            return;
        }

        var compose = RecordingPerformanceMonitor.Begin();
        if (_activeOptions.RecordForStudio)
        {
            CollectStudioCursorSample(pts);
            _perf?.End(RecordingStage.Composite, compose);
            WriteCpuFrame(frame, pts);
            return;
        }

        var clicks = RecordingPerformanceMonitor.Begin();
        DrawClickOverlay(frame, pts);
        _perf?.End(RecordingStage.OverlayClicks, clicks);

        if (_branding is { } brandingOverlay)
        {
            var branding = RecordingPerformanceMonitor.Begin();
            brandingOverlay.DrawPrepared(frame.BgraPixels, frame.Width, frame.Height);
            _perf?.End(RecordingStage.OverlayBranding, branding);
        }

        if (_webcamOverlay is not null)
        {
            var webcam = RecordingPerformanceMonitor.Begin();
            var webcamFrame = ResolveWebcamFrame(pts);
            if (webcamFrame is not null)
            {
                var corner = _webcamPlacements?.CornerAt(pts) ?? _settings.WebcamCornerPosition;
                _webcamOverlay.Draw(frame.BgraPixels, frame.Width, frame.Height, webcamFrame, corner);
            }

            _perf?.End(RecordingStage.OverlayWebcam, webcam);
        }
        else if (_settings.WebcamEnabled)
        {
            Interlocked.Increment(ref _webcamOverlayNullFrames);
        }

        _perf?.End(RecordingStage.Composite, compose);

        WriteCpuFrame(frame, pts);
    }

    private void WriteCpuFrame(CapturedFrame frame, TimeSpan pts)
    {
        // Encoder back-pressure: the frame is dropped (counted by the channel's item-dropped
        // callback) but PTS stays wall-clock, so the video simply has a lower effective frame rate
        // here and never slides against audio.
        var prepare = RecordingPerformanceMonitor.Begin();
        if (_sinkWriter is { } sink)
        {
            // Push model: the sink writer throttles internally if the encoder falls behind.
            try
            {
                _perf?.SubmissionAttempt();
                if (sink.WriteVideo(frame, pts, _frameDuration)) { _perf?.FrameEncoded(); }
                else { _perf?.FrameNotSubmitted(); }
            }
            catch (Exception ex)
            {
                _perf?.SubmissionFailed();
                LogEncoderWriteFailure(ex);
            }

            _perf?.End(RecordingStage.SamplePrepare, prepare);
            return;
        }

        var channel = _channel;
        if (channel is not null)
        {
            var buffer = CpuVideoBuffer.Create(frame);
            if (!channel.Writer.TryWrite(new TimestampedFrame(buffer, pts))) { _perf?.FrameNotSubmitted(); }
        }
        else
        {
            _perf?.FrameNotSubmitted();
        }

        _perf?.End(RecordingStage.SamplePrepare, prepare);
    }

    private int _encoderWriteFailuresLogged;

    private void LogEncoderWriteFailure(Exception ex)
    {
        if (Interlocked.Increment(ref _encoderWriteFailuresLogged) <= 3)
        {
            WebcamDiagnostics.Log($"Sink writer WriteSample failed: 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Picks the webcam frame to composite at <paramref name="pts"/> (shared by both pipelines) and
    /// maintains the webcam diagnostics counters.
    /// </summary>
    private WebcamFrame? ResolveWebcamFrame(TimeSpan pts)
    {
        if (_webcamCapture.TryGetLatestFrame(out WebcamFrame? webcamFrame) &&
            webcamFrame is not null &&
            IsWebcamFrameReady(webcamFrame, pts))
        {
            _lastTimelineWebcamFrame = webcamFrame;
        }

        if (_lastTimelineWebcamFrame is not null)
        {
            Interlocked.Increment(ref _webcamCompositedFrames);
            return _lastTimelineWebcamFrame;
        }

        Interlocked.Increment(ref _webcamNoFrameFrames);
        return null;
    }

    /// <summary>
    /// Initializes the GPU-resident capture session and its Direct2D overlay compositor without
    /// starting callbacks; branding is prepared before Start. Returns false
    /// (after cleaning up) when anything in the GPU path is unavailable so the caller can fall back
    /// to the CPU pipeline — the recording must never fail just because the fast path did.
    /// </summary>
    private bool TryStartGpuCapture(CaptureTarget captureTarget, PixelRect? region, int fps, out int width, out int height)
    {
        width = 0;
        height = 0;
        var perf = _perf ?? throw new InvalidOperationException("Performance monitor has not been initialized.");
        GpuCaptureSession? session = null;
        GpuOverlayCompositor? overlay = null;
        try
        {
            session = new GpuCaptureSession(captureTarget, region, fps, includeCursor: true, perf);
            session.Initialize();
            overlay = new GpuOverlayCompositor(session.D3DDevice);

            session.Compose += OnGpuCompose;
            session.FrameReady += OnGpuFrameReady;
            session.ScaledBlit = request =>
            {
                var compositor = _gpuOverlay;
                if (compositor is null)
                {
                    return false;
                }

                try
                {
                    return compositor.BlitLetterboxed(request);
                }
                catch (Exception ex)
                {
                    WebcamDiagnostics.Log($"GPU letterbox blit failed (0x{(uint)ex.HResult:X8}); cropping instead.");
                    return false;
                }
            };

            width = session.OutputWidth;
            height = session.OutputHeight;
            perf.Width = width;
            perf.Height = height;
            _gpuCapture = session;
            _gpuOverlay = overlay;
            _perf = perf;
            WebcamDiagnostics.Log($"GPU recording pipeline initialized: {width}x{height}@{fps} (capture not started).");
            return true;
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"GPU recording pipeline unavailable (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}); falling back to CPU pipeline.");
            session?.Dispose();
            overlay?.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Transcoder backend on the GPU pipeline: a hand-rolled texture pool (frames return on
    /// <c>MediaStreamSample.Processed</c>) and a bounded channel the <c>SampleRequested</c> pull loop drains.
    /// </summary>
    private void AttachTranscoderGpuPlumbing(GpuCaptureSession session, int fps)
    {
        var max = GpuPoolMaxCapacity(fps);
        session.AttachAllocator(new GpuFrameTexturePool(session.D3DDevice, session.OutputWidth, session.OutputHeight, 4, max));

        // The channel can hold as many frames as the pool; when the encoder falls behind,
        // the pool runs dry first and the pump drops at the source (no texture churn).
        var perf = _perf;
        _gpuChannel = Channel.CreateBounded<GpuFrame>(
            new BoundedChannelOptions(max)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true,
            },
            dropped =>
            {
                dropped.Release();
                Interlocked.Increment(ref _videoFramesDropped);
                perf?.QueueDropped();
            });
        WebcamDiagnostics.Log($"GPU pipeline feeding MediaTranscoder; texture pool up to {max}.");
    }

    private void OnGpuCompose(GpuFrame frame)
    {
        if (_activeOptions.RecordForStudio)
        {
            return;
        }

        var overlay = _gpuOverlay;
        if (overlay is null)
        {
            return;
        }

        var clicks = _clickMonitor?.GetClicks();
        var drawClicks = clicks is { Count: > 0 };
        var webcamFrame = _webcamOverlay is not null ? ResolveWebcamFrame(frame.Pts) : null;
        if (_webcamOverlay is null && _settings.WebcamEnabled)
        {
            Interlocked.Increment(ref _webcamOverlayNullFrames);
        }

        if (!drawClicks && _branding is null && webcamFrame is null)
        {
            return;
        }

        try
        {
            overlay.BeginFrame(frame.Texture);
            try
            {
                if (drawClicks)
                {
                    var t = RecordingPerformanceMonitor.Begin();
                    overlay.DrawClicks(frame.Pts.TotalSeconds, clicks!, _clickOriginX, _clickOriginY, _clickStyle);
                    _perf?.End(RecordingStage.OverlayClicks, t);
                }

                if (_branding is not null)
                {
                    var t = RecordingPerformanceMonitor.Begin();
                    overlay.DrawBranding(frame.Width, frame.Height);
                    _perf?.End(RecordingStage.OverlayBranding, t);
                }

                if (webcamFrame is not null)
                {
                    var t = RecordingPerformanceMonitor.Begin();
                    var corner = _webcamPlacements?.CornerAt(frame.Pts) ?? _settings.WebcamCornerPosition;
                    overlay.DrawWebcam(
                        frame.Width,
                        frame.Height,
                        webcamFrame,
                        corner,
                        _settings.WebcamSizePreset,
                        _settings.WebcamShape,
                        _settings.WebcamCornerRadius);
                    _perf?.End(RecordingStage.OverlayWebcam, t);
                }
            }
            finally
            {
                overlay.EndFrame();
            }
        }
        catch (Exception ex)
        {
            // Typically D2DERR_RECREATE_TARGET after a device reset. Drop overlays for the rest of
            // this recording rather than losing the screen content.
            WebcamDiagnostics.Log($"GPU overlay compositing failed (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}); overlays disabled for the rest of this recording.");
            _gpuOverlay = null;
            overlay.Dispose();
        }
    }

    private void OnGpuFrameReady(GpuFrame frame)
    {
        if (IsPaused)
        {
            _perf?.FrameNotSubmitted();
            frame.Release();
            return;
        }

        if (_activeOptions.RecordForStudio)
        {
            CollectStudioCursorSample(frame.Pts);
        }

        if (_sinkWriter is { } sink)
        {
            // Push model: write on the pump thread, then drop our reference — Media Foundation
            // recycles the texture into its allocator once the encoder has consumed it.
            var write = RecordingPerformanceMonitor.Begin();
            try
            {
                _perf?.SubmissionAttempt();
                if (sink.WriteVideo(frame, _frameDuration)) { _perf?.FrameEncoded(); }
                else { _perf?.FrameNotSubmitted(); }
            }
            catch (Exception ex)
            {
                _perf?.SubmissionFailed();
                LogEncoderWriteFailure(ex);
            }
            finally
            {
                _perf?.End(RecordingStage.SamplePrepare, write);
                frame.Release();
            }

            return;
        }

        // Transcoder path: queue for the SampleRequested pull loop, which stamps HandedOffTimestamp
        // when it actually hands the sample to the encoder.
        var channel = _gpuChannel;
        if (channel is null || !channel.Writer.TryWrite(frame))
        {
            // Writer completed (stopping) — DropWrite handles the "full" case via the callback.
            _perf?.FrameNotSubmitted();
            frame.Release();
        }
    }

    /// <summary>
    /// Upper bound on encoder-ready textures. Hardware encoders hold roughly 200–400 ms of input
    /// for look-ahead, so cover half a second at the target rate (bounded to keep 4K VRAM under
    /// ~1 GB: 30 × 33 MB).
    /// </summary>
    private static int GpuPoolMaxCapacity(int fps) => Math.Clamp(fps / 2, 8, 30);

    private StudioProjectPaths BeginStudioRecording()
    {
        _studioPaths = _studioProjects.BeginRecording();
        return _studioPaths;
    }

    private void InitializeStudioCaptureGeometry(CaptureTarget target, PixelRect? region)
    {
        _studioCaptureOriginX = 0;
        _studioCaptureOriginY = 0;
        _studioCaptureScale = 1;
        _studioRecordPointerEvents = !target.IsWindow;

        if (target.IsWindow)
        {
            return;
        }

        var monitor = _monitors.GetMonitors().FirstOrDefault(m => m.HMonitor == target.HMonitor)
            ?? _monitors.GetPrimaryMonitor();
        if (monitor is null)
        {
            return;
        }

        // Where the recorded picture starts on the desktop. The capture session knows: it is the
        // region after it was clipped to the display, which is also where the click overlay
        // counts from.
        if (_perf?.Geometry is { } geometry)
        {
            (_studioCaptureOriginX, _studioCaptureOriginY) = geometry.GetDesktopOrigin(monitor.X, monitor.Y);
        }
        else
        {
            _studioCaptureOriginX = monitor.X + (region?.X ?? 0);
            _studioCaptureOriginY = monitor.Y + (region?.Y ?? 0);
        }

        _studioCaptureScale = monitor.ScaleFactor;
    }

    private void StartMouseClickOverlay(CaptureTarget target)
    {
        // Mouse-click visuals only map reliably onto a (possibly cropped) monitor;
        // window targets move/resize, so skip them — matching the mac restriction.
        if (target.IsWindow || (!_activeOptions.RecordForStudio && !_settings.ShouldShowMouseClickVisuals(CaptureType.Video)))
        {
            return;
        }

        var monitor = _monitors.GetMonitors().FirstOrDefault(m => m.HMonitor == target.HMonitor)
            ?? _monitors.GetPrimaryMonitor();
        if (monitor == null)
        {
            return;
        }

        var geometry = _perf?.Geometry ?? throw new InvalidOperationException("Capture geometry has not been initialized.");
        (_clickOriginX, _clickOriginY) = geometry.GetDesktopOrigin(monitor.X, monitor.Y);
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

    private MediaEncodingProfile CreateEncodingProfile(
        int width,
        int height,
        int fps,
        bool includeAudio,
        VideoCodec codec,
        bool useBaselineProfile = false)
    {
        // The Baseline retry is an H.264-only recovery path, so it overrides an HEVC request.
        var useHevc = codec == VideoCodec.Hevc && !useBaselineProfile;
        var profile = useHevc
            ? MediaEncodingProfile.CreateHevc(VideoEncodingQuality.HD1080p)
            : MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
        profile.Audio = includeAudio
            ? AudioEncodingProperties.CreateAac(AudioCaptureService.SampleRate, AudioCaptureService.Channels, 192_000)
            : null;
        profile.Video.Subtype = useHevc ? MediaEncodingSubtypes.Hevc : MediaEncodingSubtypes.H264;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        var bitrate = (uint)Math.Clamp((long)width * height * fps / 10, 2_000_000, 24_000_000);
        profile.Video.Bitrate = useHevc ? (uint)(bitrate * 0.6) : bitrate;

        // High is the normal recording profile. Baseline is reserved for the recovery path when
        // the system encoder cannot initialize with High. HEVC keeps the profile's own default.
        if (!useHevc)
        {
            profile.Video.Properties[Mpeg2ProfileAttribute] =
                useBaselineProfile ? AvcBaselineProfile : AvcHighProfile;
        }

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
            VideoCodec.H264,
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
        DisposeGpuPipeline();
        StopAudioMux();
        DisposeSinkWriter();
        DisposeAudio();
        DisposeCpuChannel();
        _perf = null;
        _transcodeTask = null;
        _fileStream?.Dispose();
        _fileStream = null;
        CleanupStudioRecording(deleteProject: true);

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
        _activeOptions = VideoRecordingOptions.Default;
        _activeTarget = null;
        _activeRegion = null;
        IsRecording = false;
        WebcamDiagnostics.EndRecording();
    }

    private void StopAudioMux()
    {
        var thread = _audioMuxThread;
        _audioMuxThread = null;
        if (thread is null)
        {
            return;
        }

        _audioMuxStop = true;
        if (thread.IsAlive && Thread.CurrentThread != thread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void DisposeSinkWriter()
    {
        var sink = _sinkWriter;
        _sinkWriter = null;
        sink?.Dispose();
    }

    private void DisposeGpuPipeline()
    {
        var gpuChannel = _gpuChannel;
        _gpuChannel = null;
        gpuChannel?.Writer.TryComplete();
        if (gpuChannel is not null)
        {
            // Frames still queued never reached the encoder; hand their textures back before the
            // pool is torn down so nothing is double-released later.
            while (gpuChannel.Reader.TryRead(out var frame))
            {
                frame.Release();
            }
        }

        if (_gpuCapture is { } session)
        {
            session.Compose -= OnGpuCompose;
            session.FrameReady -= OnGpuFrameReady;
            session.Dispose();
            _gpuCapture = null;
        }

        _gpuOverlay?.Dispose();
        _gpuOverlay = null;
    }

    private void DisposeCpuChannel()
    {
        var channel = _channel;
        _channel = null;
        channel?.Writer.TryComplete();
        if (channel is not null)
        {
            // Unconsumed owned buffers have no lease to return; dropping the references releases
            // them independently of the capture cache or any samples already owned by the encoder.
            while (channel.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task StartWebcamOverlayAsync(CancellationToken cancellationToken)
    {
        _webcamOverlay = null;
        _lastTimelineWebcamFrame = null;
        Interlocked.Exchange(ref _webcamCompositedFrames, 0);
        Interlocked.Exchange(ref _webcamOverlayNullFrames, 0);
        Interlocked.Exchange(ref _webcamNoFrameFrames, 0);

        WebcamDiagnostics.Log($"StartWebcamOverlay: WebcamEnabled={_settings.WebcamEnabled} deviceId='{(string.IsNullOrWhiteSpace(_settings.SelectedWebcamId) ? "(default)" : _settings.SelectedWebcamId)}' shape={_settings.WebcamShape} size={_settings.WebcamSizePreset} corner={_settings.WebcamCornerPosition}");

        if (!_settings.WebcamEnabled)
        {
            WebcamDiagnostics.Log("Webcam is disabled in settings; no overlay will be composited.");
            return;
        }

        if (!_activeOptions.RecordForStudio)
        {
            _webcamOverlay = new WebcamOverlayCompositor(
                _settings.WebcamCornerPosition,
                _settings.WebcamSizePreset,
                _settings.WebcamShape,
                _settings.WebcamCornerRadius);
        }

        // On the GPU pipeline the camera frames stay in video memory and the Direct2D compositor
        // draws them directly; the CPU pipeline needs pixel buffers.
        // A Studio recording encodes the camera to its own file from pixel buffers, at the camera's
        // own shape instead of the overlay preset's.
        _webcamCapture.SetPreferredDirect3DDevice(!_activeOptions.RecordForStudio && _gpuCapture is not null ? WgcInterop.GetSharedDevice().WinRT : null);
        _webcamCapture.SetPreserveSourceAspect(_activeOptions.RecordForStudio);

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
                .StartAsync(_settings.SelectedWebcamId, _activeOptions.RecordForStudio ? StudioWebcamSize() : ResolveRequestedWebcamSize(_settings.WebcamSizePreset), cancellationToken)
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
        // Only relevant when the camera is drawn or recorded and capture actually started. A Studio
        // recording has no overlay but still wants the camera track to start with the screen.
        if ((_webcamOverlay is null && !_activeOptions.RecordForStudio) || !_webcamCapture.IsRunning)
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

    /// <summary>
    /// Stops the camera of a Studio recording and finalizes its track. Called once the screen
    /// track has taken its last frame: the camera's frames come until the device has stopped, so
    /// its track ends after the screen's. Does nothing for an ordinary recording. Never throws.
    /// </summary>
    private async Task StopStudioCameraAsync(StudioCameraRecorder? cameraRecorder)
    {
        if (cameraRecorder is null)
        {
            return;
        }

        await StopWebcamOverlayAsync().ConfigureAwait(false);
        _webcamCapture.FrameArrived -= cameraRecorder.OnFrameArrived;
        await Task.Run(cameraRecorder.Finish).ConfigureAwait(false);
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

    private static BitmapSize StudioWebcamSize() => new() { Width = 1920, Height = 1080 };

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

        if (_gpuChannel is { } gpuChannel)
        {
            await HandleGpuVideoRequestAsync(args, gpuChannel).ConfigureAwait(false);
            return;
        }

        var channel = _channel;
        if (channel is null)
        {
            return;
        }

        var deferral = args.Request.GetDeferral();
        var wait = RecordingPerformanceMonitor.Begin();
        var submissionAttempted = false;
        try
        {
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (channel.Reader.TryRead(out var frame))
                {
                    _perf?.End(RecordingStage.EncoderWait, wait);
                    submissionAttempted = true;
                    _perf?.SubmissionAttempt();
                    var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels, frame.Pts);
                    sample.Duration = _frameDuration;
                    args.Request.Sample = sample;
                    _perf?.FrameEncoded();
                    return;
                }
            }

            // Channel completed and drained -> signal end of stream.
            args.Request.Sample = null;
        }
        catch (Exception ex)
        {
            if (submissionAttempted) { _perf?.SubmissionFailed(); }
            WebcamDiagnostics.Log($"CPU video sample request failed: {ex.GetType().Name}: {ex.Message}");
            args.Request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task HandleGpuVideoRequestAsync(MediaStreamSourceSampleRequestedEventArgs args, Channel<GpuFrame> channel)
    {
        var deferral = args.Request.GetDeferral();
        var wait = RecordingPerformanceMonitor.Begin();
        var submissionAttempted = false;
        try
        {
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (channel.Reader.TryRead(out var frame))
                {
                    _perf?.End(RecordingStage.EncoderWait, wait);
                    var prepare = RecordingPerformanceMonitor.Begin();

                    // The sample references the pooled texture directly; the encoder's colour
                    // converter reads it on the GPU. Processed fires when the pipeline is done with
                    // it, which is when the texture can be reused. Until ownership has transferred
                    // to the sample (Processed hooked and the sample accepted), a failure must
                    // return the frame to the pool ourselves.
                    var handedOff = false;
                    submissionAttempted = true;
                    _perf?.SubmissionAttempt();
                    try
                    {
                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface!, frame.Pts);
                        sample.Duration = _frameDuration;
                        var perf = _perf;
                        // Hold time is measured from the actual hand-off, not from when the frame
                        // entered the channel, so a backlog cannot inflate EncoderHold.
                        frame.HandedOffTimestamp = RecordingPerformanceMonitor.Begin();
                        sample.Processed += (_, _) =>
                        {
                            perf?.Record(RecordingStage.EncoderHold, RecordingPerformanceMonitor.Begin() - frame.HandedOffTimestamp);
                            frame.Release();
                        };
                        args.Request.Sample = sample;
                        handedOff = true;
                    }
                    finally
                    {
                        if (!handedOff)
                        {
                            frame.Release();
                        }
                    }

                    _perf?.End(RecordingStage.SamplePrepare, prepare);
                    _perf?.FrameEncoded();
                    return;
                }
            }

            args.Request.Sample = null;
        }
        catch (Exception ex)
        {
            if (submissionAttempted) { _perf?.SubmissionFailed(); }
            LogEncoderWriteFailure(ex);
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

        var (pts, duration) = AccountAudioChunk(data);
        var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), pts);
        sample.Duration = duration;
        args.Request.Sample = sample;
    }

    /// <summary>
    /// Assigns the next contiguous audio PTS/duration to a PCM chunk and maintains the muxer
    /// diagnostics counters. Shared by the transcoder pull path and the sink-writer push loop.
    /// </summary>
    private (TimeSpan Pts, TimeSpan Duration) AccountAudioChunk(byte[] data)
    {
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

        return (pts, duration);
    }

    /// <summary>
    /// Sink-writer backend: pushes captured audio into the muxer from a dedicated thread, gated on
    /// real captured frames (same back-pressure rule as the pull path) so audio never runs ahead of
    /// the capture clock. After Stop it drains what was captured before the devices stopped.
    /// </summary>
    private void StartAudioMux()
    {
        _audioMuxStop = false;
        _audioMuxThread = new Thread(AudioMuxLoop)
        {
            IsBackground = true,
            Name = "TinyClips.AudioMux",
            Priority = ThreadPriority.AboveNormal,
        };
        _audioMuxThread.Start();
    }

    private void AudioMuxLoop()
    {
        const int frameCount = AudioCaptureService.SampleRate / 50;
        const int pollMs = 4;
        var audio = _audio;
        var sink = _sinkWriter;
        if (audio is null || sink is null)
        {
            return;
        }

        try
        {
            // Same back-pressure rule as HandleAudioRequestAsync: wait for real captured frames so
            // audio never runs ahead of the capture clock; wait without limit only while paused. A
            // stalled/disconnected device must not leave the track short, so after the same 2 s cap
            // the chunk is filled with silence and counted as starved.
            const int maxWaitMs = 2000;
            var waitedMs = 0;
            while (!_audioMuxStop)
            {
                if (IsPaused)
                {
                    waitedMs = 0;
                    Thread.Sleep(pollMs);
                    continue;
                }

                if (audio.AvailableFrames < frameCount)
                {
                    if (waitedMs < maxWaitMs)
                    {
                        Thread.Sleep(pollMs);
                        waitedMs += pollMs;
                        continue;
                    }

                    WriteStarvedAudioChunk(sink, frameCount);
                    waitedMs = 0;
                    continue;
                }

                waitedMs = 0;
                WriteAudioChunk(sink, audio, frameCount);
            }

            // Drain: devices are stopped; hand the muxer what was captured before Stop so the audio
            // track ends where the video does. Bounded so a misbehaving source cannot hold us here.
            var budget = MaxDrainFrames;
            while (budget > 0 && audio.AvailableFrames > 0)
            {
                var n = Math.Min(frameCount, Math.Min(audio.AvailableFrames, budget));
                if (!WriteAudioChunk(sink, audio, n))
                {
                    break;
                }

                budget -= n;
                _drainFramesServed += n;
            }
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"Audio mux loop ended with {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool WriteAudioChunk(MfSinkWriterEncoder sink, AudioCaptureService audio, int frameCount)
    {
        var data = audio.ReadChunk(frameCount);
        if (data is null || data.Length == 0)
        {
            return false;
        }

        return WriteAudioData(sink, data);
    }

    /// <summary>Emits a silent chunk in place of audio a stalled source never delivered.</summary>
    private void WriteStarvedAudioChunk(MfSinkWriterEncoder sink, int frameCount)
    {
        _audioStarvedChunks++;
        if (_audioStarvedChunks == 1 || _audioStarvedChunks % 50 == 0)
        {
            WebcamDiagnostics.Log($"Audio muxer starved: no captured audio for 2 s; filled chunk with silence (starvedChunks={_audioStarvedChunks}).");
        }

        WriteAudioData(sink, new byte[frameCount * AudioCaptureService.Channels * (AudioCaptureService.BitsPerSample / 8)]);
    }

    private bool WriteAudioData(MfSinkWriterEncoder sink, byte[] data)
    {
        var (pts, duration) = AccountAudioChunk(data);
        try
        {
            sink.WriteAudio(data, pts, duration);
            return true;
        }
        catch (Exception ex)
        {
            LogEncoderWriteFailure(ex);
            return false;
        }
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
            _perf?.Pause();
            _capture?.PauseEmitting();
            _gpuCapture?.PauseEmitting();
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
            _perf?.Resume();
            _audio?.Resume();
            _capture?.ResumeEmitting();
            _gpuCapture?.ResumeEmitting();
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

            if (_activeOptions.RecordForStudio)
            {
                _studioClickSamples = _clickMonitor?.GetClicks() ?? [];
            }

            _clickMonitor?.Dispose();
            _clickMonitor = null;
            if (_settings.WebcamEnabled)
            {
                WebcamDiagnostics.Log($"Recording stopping — webcam composite summary: composited={Interlocked.Read(ref _webcamCompositedFrames)} noFrameYet={Interlocked.Read(ref _webcamNoFrameFrames)} overlayDisabled={Interlocked.Read(ref _webcamOverlayNullFrames)}");
            }

            // A Studio recording's camera goes on until its screen track is finished. Outside the
            // camera track's own time the camera is not drawn, so a camera that stopped first, as
            // it does in an ordinary recording, would be missing from the last moments of every
            // Studio recording, for as long as the camera and its file took to stop.
            var studioCamera = _studioCameraRecorder;
            if (studioCamera is null)
            {
                await StopWebcamOverlayAsync().ConfigureAwait(false);
            }

            // Stop the audio devices first, then let the muxer drain the audio already captured
            // (so the track ends where the video does) before it ends the stream. Only then stop
            // new video frames and let the encoder drain what's buffered.
            _audio?.Stop();
            _audioDraining = true;

            _capture?.Stop();
            _channel?.Writer.TryComplete();
            _gpuCapture?.Stop();
            _gpuChannel?.Writer.TryComplete();
            _perf?.BeginFinalization();
            var finalized = true;

            var cpuPipeline = _capture is not null;
            try
            {
                if (_transcodeTask is not null)
                {
                    try
                    {
                        await _transcodeTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        finalized = false;
                        if (_capture is not null)
                        {
                            WebcamDiagnostics.Log($"CPU transcode failed: {ex.GetType().Name}: {ex.Message}");
                        }
                        else
                        {
                            WebcamDiagnostics.Log($"Transcoder finalization failed: 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                if (_sinkWriter is { } sink)
                {
                    // Capture pumps are stopped (no more video writes). Let the audio mux drain what
                    // was captured before Stop, then finalize the MP4 (blocking until the encoder flushes).
                    StopAudioMux();
                    try
                    {
                        await Task.Run(sink.Finish).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (_perf is not null) { _perf.FinalizationSucceeded = false; }
                        LogSyncReport();

                        // Also when the screen track cannot be finalized: the camera must not stay on.
                        await StopStudioCameraAsync(studioCamera).ConfigureAwait(false);
                        throw;
                    }
                }

                await StopStudioCameraAsync(studioCamera).ConfigureAwait(false);
                if (_perf is not null) { _perf.FinalizationSucceeded = finalized; }
                LogSyncReport();
            }
            catch when (cpuPipeline)
            {
                DisposeRecordingPipeline();

                // A Studio recording that could not be finished keeps what was written. Its folder
                // has no project in it, and the storage cleanup removes such a folder after a day.
                CleanupStudioRecording(deleteProject: false);
                _activeOptions = VideoRecordingOptions.Default;
                _activeTarget = null;
                _activeRegion = null;
                throw;
            }

            // What a Studio project is made from. Letting go of the pipeline forgets both, and the
            // project is made after that, when the files are closed.
            var studioTimeline = _recordingTimeline;
            var studioCameraCorners = BuildStudioCameraCorners();
            DisposeRecordingPipeline();
            var path = _outputPath;
            var shouldDiscard = ConsumeDiscardRequested(discard);
            var wasStudioRecording = _activeOptions.RecordForStudio;
            string? studioProjectId = null;
            if (wasStudioRecording)
            {
                // A finished project replaces the output path. If the project cannot be saved, the
                // path becomes the screen track kept as an ordinary recording, or null.
                studioProjectId = FinishStudioRecording(path, shouldDiscard, studioTimeline, studioCameraCorners, out path);
            }

            _activeOptions = VideoRecordingOptions.Default;
            _activeTarget = null;
            _activeRegion = null;

            if (studioProjectId is not null)
            {
                _outputPath = null;
                _analytics.RecordCapture(CaptureType.Video);
                RecordingCompleted?.Invoke(this, null);
                StudioRecordingCompleted?.Invoke(this, studioProjectId);
                return null;
            }

            if (shouldDiscard && (wasStudioRecording || !string.IsNullOrEmpty(path)))
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

    private void DisposeRecordingPipeline()
    {
        StopAudioMux();
        _capture?.Dispose();
        _capture = null;
        DisposeCpuChannel();
        DisposeGpuPipeline();
        _branding = null;
        DisposeSinkWriter();
        DisposeAudio();
        _fileStream?.Dispose();
        _fileStream = null;
        _perf = null;
        _transcodeTask = null;
        _recordingTimeline = null;
        _webcamPlacements = null;
        _lastTimelineWebcamFrame = null;
        DetachMediaStreamSource();
        IsRecording = false;
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
            var videoPts = _gpuCapture?.LastEmittedPts ?? _capture?.LastEmittedPts ?? TimeSpan.MinValue;
            var videoEmitted = _gpuCapture?.EmittedFrameCount ?? _capture?.EmittedFrameCount ?? 0;
            var videoDropped = Interlocked.Read(ref _videoFramesDropped) + (_gpuCapture?.PoolExhaustedDrops ?? 0);
            var elapsed = timeline?.Elapsed ?? TimeSpan.Zero;
            var pauses = timeline?.PauseCount ?? 0;
            var paused = timeline?.PausedDuration ?? TimeSpan.Zero;

            if (_perf is { } perf)
            {
                // Every drop site (channel DropWrite, pool exhaustion, frame-production failure)
                // reports to the monitor directly, so its count is authoritative; the two counters
                // below are kept for the sync line and cross-checked here.
                var report = perf.Complete();
                videoDropped = Math.Max(videoDropped, report.FramesDropped);
                LastPerformanceReport = report;
                foreach (var line in report.ToTable().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                {
                    WebcamDiagnostics.Log($"Perf report: {line}");
                }

                if (_gpuCapture is { } gpu)
                {
                    WebcamDiagnostics.Log($"Perf report: GPU frames highWater={gpu.PoolHighWaterMark}/{gpu.PoolMaxCapacity} exhaustedDrops={gpu.PoolExhaustedDrops} rawPacerSkippedSlotsIncludingPause={gpu.PumpOverruns} contentResizes={gpu.ContentResizes}.");
                }

                if (_sinkWriter is { } sink)
                {
                    WebcamDiagnostics.Log($"Perf report: sink writer videoSamples={sink.VideoSamplesWritten} audioSamples={sink.AudioSamplesWritten}.");
                }
            }

            WebcamDiagnostics.Log($"Sync report: encoder='{_encoderPath}' elapsed={elapsed.TotalSeconds:F3}s pauses={pauses} pausedTotal={paused.TotalSeconds:F3}s.");
            WebcamDiagnostics.Log($"Sync report: video lastPts={(videoPts == TimeSpan.MinValue ? "none" : $"{videoPts.TotalSeconds:F3}s")} framesEmitted={videoEmitted} framesDroppedQueuePoolProduction={videoDropped}.");

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

    /// <summary>
    /// Turns the finished screen and camera tracks into a Studio project and returns its id. Returns
    /// null when the recording was discarded or the project could not be saved; in the second case
    /// <paramref name="fallbackPath"/> is the screen track moved into the normal save folder, so
    /// the capture is kept as an ordinary recording. When it cannot be moved there either, it is
    /// the screen track where it lies, in its project folder, which is then left alone: it holds
    /// the only copy of the recording. Storage cleanup removes such a folder after a day.
    /// </summary>
    private string? FinishStudioRecording(
        string? screenPath,
        bool discard,
        RecordingTimeline? timeline,
        StudioCameraCornerEvent[] cameraCorners,
        out string? fallbackPath)
    {
        fallbackPath = null;
        var paths = _studioPaths;
        if (paths is null)
        {
            CleanupStudioRecording(deleteProject: false);
            return null;
        }

        if (discard || !HasNonEmptyOutputFile(screenPath))
        {
            CleanupStudioRecording(deleteProject: true);
            return null;
        }

        try
        {
            // The project gets the frame rate the recording was made at. The file's own figure is
            // its frames divided by its length, which is lower whenever a frame was dropped.
            var screen = MediaFileProbe.Probe(paths.ScreenPath) with { FrameRate = _frameRate };
            var camera = BuildStudioCameraSource(paths);
            var clickStyle = _settings.MouseClickOverlayStyleFor(CaptureType.Video);
            var request = StudioRecordingBuilder.BuildCreationRequest(
                Path.GetFileNameWithoutExtension(_storage.GenerateFilePath(CaptureType.Video)),
                screen,
                camera,
                _studioInitialCorner,
                clickStyle,
                _settings.ShouldShowMouseClickVisuals(CaptureType.Video),
                _settings.ShowBrandingOverlay,
                _activeOptions.AppVersion ?? typeof(VideoRecordingService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                _activeOptions.Look,
                cameraCorners);

            _studioProjects.CompleteRecording(paths.ProjectId, request);
            SaveStudioEvents(paths.ProjectId, screen, timeline, cameraCorners);
            CleanupStudioRecording(deleteProject: false);
            return paths.ProjectId;
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"Studio project could not be saved; keeping the screen track as a regular recording (0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}).");
            fallbackPath = TryKeepScreenTrackAsRecording(paths.ScreenPath);
            var isOnlyCopy = fallbackPath is null && HasNonEmptyOutputFile(paths.ScreenPath);
            if (isOnlyCopy)
            {
                // Deleting the project now would delete the recording with it.
                fallbackPath = paths.ScreenPath;
                WebcamDiagnostics.Log($"The screen recording stays in its Studio project folder, for a day: {paths.ScreenPath}");
            }

            CleanupStudioRecording(deleteProject: !isOnlyCopy);
            return null;
        }
    }

    private void SaveStudioEvents(
        string projectId,
        StudioRecordingSourceInfo screen,
        RecordingTimeline? timeline,
        StudioCameraCornerEvent[] cameraCorners)
    {
        if (timeline is null)
        {
            return;
        }

        try
        {
            _studioProjects.SaveEvents(projectId, BuildStudioEvents(screen, timeline, cameraCorners));
        }
        catch (Exception ex)
        {
            // Events are optional: the project opens without them, it only loses click and cursor data.
            WebcamDiagnostics.Log($"Studio events could not be saved: 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string? TryKeepScreenTrackAsRecording(string screenPath)
    {
        try
        {
            if (!HasNonEmptyOutputFile(screenPath))
            {
                return null;
            }

            var destination = _storage.GenerateFilePath(CaptureType.Video);
            File.Move(screenPath, destination);
            return destination;
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"Studio screen track could not be kept as a recording: 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private StudioCameraSourceInfo? BuildStudioCameraSource(StudioProjectPaths paths)
    {
        var recorder = _studioCameraRecorder;
        if (recorder is null || !recorder.HasFrames || paths.CameraPath is null || !HasNonEmptyOutputFile(paths.CameraPath))
        {
            if (recorder is not null)
            {
                // The camera was on, in its bubble, and nothing of it is in the project. Say so.
                WebcamDiagnostics.Log($"Studio camera: no camera track ({recorder.FramesWritten} frames written); the project has the screen only.");
                try
                {
                    WebcamCaptureFailed?.Invoke(this, "The camera could not be recorded. The Studio project has the screen only.");
                }
                catch
                {
                    // This runs while the project is being saved: a listener's failure must not
                    // be taken for a failure to save it.
                }
            }

            DeleteOutputFileIfPresent(paths.CameraPath);
            return null;
        }

        try
        {
            var probed = MediaFileProbe.Probe(paths.CameraPath, 30);
            return new StudioCameraSourceInfo(
                probed.Width,
                probed.Height,
                probed.Duration > 0 ? probed.Duration : recorder.DurationSeconds,
                recorder.StartOffset?.TotalSeconds ?? 0);
        }
        catch (Exception ex)
        {
            // A camera track that cannot be read is left out; the project is still a screen recording.
            WebcamDiagnostics.Log($"Studio camera track is unreadable and was left out of the project: 0x{(uint)ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            DeleteOutputFileIfPresent(paths.CameraPath);
            return null;
        }
    }

    private StudioEvents BuildStudioEvents(
        StudioRecordingSourceInfo screen,
        RecordingTimeline timeline,
        StudioCameraCornerEvent[] cameraCorners)
    {
        var pointerEventsEnabled = _studioRecordPointerEvents;
        StudioPointSample[] cursorSamples;
        lock (_studioCursorGate)
        {
            cursorSamples = _studioCursorSamples.ToArray();
        }

        return new StudioEvents
        {
            Capture = new StudioCaptureInfo
            {
                Width = screen.Width,
                Height = screen.Height,
                Scale = _studioCaptureScale,
                Kind = _activeTarget is null ? StudioCaptureKind.Display : StudioRecordingBuilder.ToCaptureKind(_activeTarget, _activeRegion),
            },
            Clicks = pointerEventsEnabled
                ? StudioRecordingBuilder.BuildClickEvents(_studioClickSamples, timeline, _studioCaptureOriginX, _studioCaptureOriginY, screen.Width, screen.Height)
                : [],
            Cursor = pointerEventsEnabled
                ? StudioRecordingBuilder.BuildCursorSamples(cursorSamples, _studioCaptureOriginX, _studioCaptureOriginY, screen.Width, screen.Height)
                : [],
            CameraCorners = cameraCorners,
        };
    }

    // The corners the camera was in while recording. They go into the events file, and the
    // moves from one to another become the scenes of the project.
    private StudioCameraCornerEvent[] BuildStudioCameraCorners() =>
        _webcamPlacements is null
            ? [new StudioCameraCornerEvent { T = 0, Corner = StudioRecordingBuilder.ToStudioAnchor(_studioInitialCorner) }]
            : StudioRecordingBuilder.BuildCameraCornerEvents(_webcamPlacements.Events);

    private void CleanupStudioRecording(bool deleteProject)
    {
        if (_studioCameraRecorder is { } cameraRecorder)
        {
            _webcamCapture.FrameArrived -= cameraRecorder.OnFrameArrived;
            cameraRecorder.Dispose();
            _studioCameraRecorder = null;
        }

        if (deleteProject && _studioPaths is { } paths)
        {
            try
            {
                _studioProjects.Delete(paths.ProjectId);
            }
            catch
            {
                // Best-effort cleanup of abandoned Studio source files.
            }
        }

        _studioPaths = null;
        lock (_studioCursorGate)
        {
            _studioCursorSamples.Clear();
        }

        _studioClickSamples = [];
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

    private void CollectStudioCursorSample(TimeSpan pts)
    {
        if (!_activeOptions.RecordForStudio || !_studioRecordPointerEvents || pts < TimeSpan.Zero)
        {
            return;
        }

        if (!GetCursorPos(out var point))
        {
            return;
        }

        lock (_studioCursorGate)
        {
            // A resting pointer adds nothing: samples are steps, so the last one still applies.
            if (_studioCursorSamples.Count > 0 &&
                _studioCursorSamples[^1] is var last &&
                last.X == point.X &&
                last.Y == point.Y)
            {
                return;
            }

            _studioCursorSamples.Add(new StudioPointSample(pts, point.X, point.Y));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    private readonly record struct TimestampedFrame(IBuffer Pixels, TimeSpan Pts);
}
