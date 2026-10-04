using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using SharpGen.Runtime;
using TinyClips.Core.Models;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio.Rendering;

public enum StudioEncoderPreference
{
    /// <summary>A hardware encoder when there is one, the software encoder when it cannot start or fails on the first frames.</summary>
    HardwareWithSoftwareFallback,

    /// <summary>Fail rather than encode in software.</summary>
    HardwareOnly,

    SoftwareOnly,
}

public enum StudioRenderDevicePreference
{
    /// <summary>The graphics hardware, or WARP (Direct3D's software rasterizer) on a PC that has none.</summary>
    HardwareWithWarpFallback,

    /// <summary>Always WARP. For checking the path a PC without graphics hardware takes.</summary>
    WarpOnly,
}

/// <summary>Options for a Studio MP4 export.</summary>
/// <param name="LongSideLimit">The longest side of the video in pixels; larger canvases are scaled down. 0 means no limit.</param>
/// <param name="RenderQuality">
/// How the screen and camera are scaled. Null, the default, means the caller did not ask, and
/// the exporter chooses by the device the frames are drawn on: high-quality sampling on graphics
/// hardware, and linear sampling in software (WARP), where high-quality sampling makes an export
/// slower than the video plays. A value given here is used on either.
/// </param>
public sealed record StudioExportOptions(
    double LongSideLimit = StudioExportLimits.LongSide,
    VideoCodec Codec = VideoCodec.H264,
    StudioEncoderPreference EncoderPreference = StudioEncoderPreference.HardwareWithSoftwareFallback,
    StudioRenderDevicePreference DevicePreference = StudioRenderDevicePreference.HardwareWithWarpFallback,
    StudioRenderQuality? RenderQuality = null);

/// <summary>What a completed export wrote.</summary>
/// <param name="EncoderDescription">The codec and the encoder Media Foundation used, as read back from the writer.</param>
/// <param name="HardwareEncoder">Whether that encoder is a hardware one.</param>
/// <param name="SoftwareRendering">Whether the frames were drawn by WARP instead of graphics hardware.</param>
/// <param name="RenderQuality">How the screen and camera were scaled: what was asked for, or what the exporter chose for the device.</param>
public sealed record StudioExportResult(
    string OutputPath,
    int FrameCount,
    double DurationSeconds,
    string EncoderDescription,
    int Width,
    int Height,
    StudioFrameRate FrameRate,
    bool HasAudio,
    bool HardwareEncoder,
    bool SoftwareRendering,
    StudioRenderQuality RenderQuality);

/// <summary>
/// Renders Studio projects to MP4 files and poster JPEGs. Both methods only check their arguments
/// on the calling thread; the work runs on a thread of its own and may be started from any thread.
/// </summary>
public sealed class StudioExporter
{
    /// <summary>Set by the check tool only: makes an encoder fail on purpose. See <see cref="StudioEncoderFault"/>.</summary>
    internal StudioEncoderFault? Fault { get; init; }

    /// <summary>
    /// Renders the project to an MP4 at <paramref name="outputPath"/>, replacing a file that is
    /// already there. The video is written under a temporary name next to it and moved into place
    /// only when it is complete, so a failed or cancelled export leaves nothing new behind and
    /// does not touch an earlier file at that path.
    /// </summary>
    /// <exception cref="OperationCanceledException">The export was cancelled.</exception>
    /// <exception cref="FileNotFoundException">A source file is missing.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of <paramref name="outputPath"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">Nothing is left to export after trimming.</exception>
    /// <exception cref="StudioExportException">Anything else; the message can be shown to the user.</exception>
    public Task<StudioExportResult> ExportAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        string outputPath,
        StudioExportOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var job = new StudioExportJob(project, events, paths, outputPath, options ?? new StudioExportOptions(), progress, cancellationToken) { Fault = Fault };
        return StudioWorker.Run("Tiny Clips Studio export", job.Run, cancellationToken);
    }

    /// <summary>
    /// Writes one frame of the export as a JPEG no longer than <paramref name="maxLongSide"/>
    /// pixels: the frame showing at <paramref name="outputTimeSeconds"/> of the exported video,
    /// so 0 gives its first frame.
    /// </summary>
    public Task WritePosterAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        string posterPath,
        double maxLongSide,
        double outputTimeSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(posterPath);

        var job = new StudioPosterJob(project, events, paths, posterPath, maxLongSide, outputTimeSeconds, cancellationToken);
        return StudioWorker.Run("Tiny Clips Studio poster", job.Run, cancellationToken);
    }
}

/// <summary>Runs blocking Media Foundation work on a dedicated multithreaded-apartment thread.</summary>
internal static class StudioWorker
{
    public static Task<T> Run<T>(string name, Func<T> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }
}

/// <summary>Where a project's source files are, whichever way the caller built its paths.</summary>
internal static class StudioSourceFiles
{
    public static string Screen(StudioProject project, StudioProjectPaths paths) =>
        project.Sources.Screen.External ? project.Sources.Screen.File : paths.ScreenPath;

    public static string? Camera(StudioProject project, StudioProjectPaths paths) =>
        project.Sources.Camera is { } camera ? paths.CameraPath ?? Path.Combine(paths.ProjectDirectory, camera.File) : null;

    public static void Require(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The {description} is missing: {path}", path);
        }
    }

    public static void DeleteQuietly(string path)
    {
        // A writer that was just released can hold its file for a moment longer.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }
}

/// <summary>
/// Decides, for the check tool, whether an encoder fails at a frame. The arguments are whether
/// the attempt allows a hardware encoder, whether it hands frames over as textures, and the
/// index of the frame about to be encoded. Nothing in the app sets one.
/// </summary>
internal delegate bool StudioEncoderFault(bool allowHardware, bool textureFrames, int frameIndex);

/// <summary>The encoder gave up. Early enough in an export, the next encoder is tried.</summary>
internal sealed class StudioEncoderFailedException : Exception
{
    /// <param name="stage">What the encoder was doing, to follow "the encoder failed while".</param>
    public StudioEncoderFailedException(string stage, long framesWritten, Exception innerException)
        : base($"the encoder failed while {stage} ({StudioMediaFoundation.Describe(innerException)})", innerException)
    {
        FramesWritten = framesWritten;
    }

    public long FramesWritten { get; }
}

/// <summary>One export, start to finish, on the worker thread.</summary>
internal sealed class StudioExportJob
{
    private const int EncoderPoolInitial = 4;
    private const int EncoderPoolMax = 8;

    /// <summary>An encoder that fails within this much video is replaced; later, the export fails.</summary>
    private const double EncoderTrialSeconds = 2;

    private static readonly TimeSpan EncoderStallLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(50);

    private readonly StudioProject _project;
    private readonly StudioEvents _events;
    private readonly StudioProjectPaths _paths;
    private readonly string _outputPath;
    private readonly StudioExportOptions _options;
    private readonly IProgress<double>? _progress;
    private readonly CancellationToken _cancellationToken;
    private int _width;
    private int _height;
    private StudioRenderQuality _quality;
    private double _reported = -1;

    public StudioExportJob(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        string outputPath,
        StudioExportOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        _project = project;
        _events = events;
        _paths = paths;
        _outputPath = outputPath;
        _options = options;
        _progress = progress;
        _cancellationToken = cancellationToken;
    }

    public StudioEncoderFault? Fault { get; init; }

    private readonly record struct Attempt(bool AllowHardware, bool GpuFrames);

    public StudioExportResult Run()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var outputPath = Path.GetFullPath(_outputPath);
        var folder = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"The folder to export to does not exist: {folder}");
        }

        var screenPath = StudioSourceFiles.Screen(_project, _paths);
        var cameraPath = StudioSourceFiles.Camera(_project, _paths);
        StudioSourceFiles.Require(screenPath, "screen recording");
        if (cameraPath is not null)
        {
            StudioSourceFiles.Require(cameraPath, "camera recording");
        }

        var size = StudioCanvasMath.ExportSize(StudioCanvasMath.NaturalSize(_project), _options.LongSideLimit);
        var width = _width = (int)size.Width;
        var height = _height = (int)size.Height;
        var rate = StudioRenderingMath.FrameRate(_project.Sources.Screen.FrameRate);
        var plan = StudioRenderingMath.BuildFramePlan(_project, rate.FramesPerSecond);

        var temporaryPath = StudioRenderingMath.TemporaryOutputPath(outputPath);
        try
        {
            StudioExportResult result;
            using (StudioMediaFoundation.Startup())
            using (var graphics = StudioGraphicsDevice.Create(_options.DevicePreference == StudioRenderDevicePreference.WarpOnly))
            {
                _quality = _options.RenderQuality ?? (graphics.IsSoftware ? StudioRenderQuality.Preview : StudioRenderQuality.Export);
                result = Encode(graphics, screenPath, cameraPath, temporaryPath, outputPath, width, height, rate, plan);
            }

            // A cancel that arrives after the last frame still must not replace the user's file.
            _cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
            Report(1);
            return result;
        }
        catch (Exception ex)
        {
            StudioSourceFiles.DeleteQuietly(temporaryPath);
            if (Translate(ex, outputPath) is { } translated)
            {
                throw translated;
            }

            throw;
        }
    }

    private StudioExportResult Encode(
        StudioGraphicsDevice graphics,
        string screenPath,
        string? cameraPath,
        string temporaryPath,
        string outputPath,
        int width,
        int height,
        StudioFrameRate rate,
        IReadOnlyList<StudioExportFrame> plan)
    {
        var attempts = Attempts(graphics);
        var trialFrames = (long)Math.Ceiling(EncoderTrialSeconds * rate.FramesPerSecond);
        for (var index = 0; ; index++)
        {
            try
            {
                return RunAttempt(graphics, attempts[index], screenPath, cameraPath, temporaryPath, outputPath, width, height, rate, plan);
            }
            catch (StudioEncoderFailedException ex) when (index + 1 < attempts.Count && ex.FramesWritten <= trialFrames)
            {
                // This encoder cannot do the job; start over with the next one.
                StudioSourceFiles.DeleteQuietly(temporaryPath);
            }
        }
    }

    private List<Attempt> Attempts(StudioGraphicsDevice graphics)
    {
        var attempts = new List<Attempt>(3);
        if (_options.EncoderPreference != StudioEncoderPreference.SoftwareOnly && !graphics.IsSoftware)
        {
            attempts.Add(new Attempt(AllowHardware: true, GpuFrames: true));
        }

        if (_options.EncoderPreference == StudioEncoderPreference.HardwareOnly)
        {
            if (attempts.Count == 0)
            {
                throw new StudioExportException("This PC has no graphics hardware, so there is no hardware video encoder to export with.");
            }

            return attempts;
        }

        // The software encoder still takes its frames as textures and lets Media Foundation copy
        // them down. Frames from system memory are the last resort, and what WARP ends up with
        // where it has no video processor.
        attempts.Add(new Attempt(AllowHardware: false, GpuFrames: true));
        attempts.Add(new Attempt(AllowHardware: false, GpuFrames: false));
        return attempts;
    }

    private StudioExportResult RunAttempt(
        StudioGraphicsDevice graphics,
        Attempt attempt,
        string screenPath,
        string? cameraPath,
        string temporaryPath,
        string outputPath,
        int width,
        int height,
        StudioFrameRate rate,
        IReadOnlyList<StudioExportFrame> plan)
    {
        IMFDXGIDeviceManager? readerManager = null;
        StudioSceneRenderer? renderer = null;
        StudioVideoSource? screen = null;
        StudioVideoSource? camera = null;
        StudioAudioSource? audio = null;
        StudioSinkWriterEncoder? encoder = null;
        ID3D11Texture2D? memoryTarget = null;
        try
        {
            if (!graphics.IsSoftware)
            {
                readerManager = MediaFactory.MFCreateDXGIDeviceManager();
                readerManager.ResetDevice(graphics.Device).CheckError();
            }

            renderer = new StudioSceneRenderer(graphics.Device);
            screen = StudioVideoSource.Open(screenPath, "screen recording", graphics, readerManager);
            camera = cameraPath is null ? null : StudioVideoSource.Open(cameraPath, "camera recording", graphics, readerManager);
            audio = _project.Audio.Muted ? null : StudioAudioSource.TryOpen(screenPath, "screen recording");

            var settings = new StudioVideoEncoderSettings(
                width,
                height,
                rate,
                StudioRenderingMath.VideoBitrate(width, height, rate.FramesPerSecond, _options.Codec),
                _options.Codec,
                attempt.AllowHardware,
                attempt.GpuFrames);
            try
            {
                encoder = StudioSinkWriterEncoder.Create(temporaryPath, attempt.GpuFrames ? graphics.Device : null, settings, audio?.Format);
                if (attempt.GpuFrames)
                {
                    encoder.CreateFrameAllocator(EncoderPoolInitial, EncoderPoolMax);
                }
            }
            catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
            {
                throw new StudioEncoderFailedException("starting", 0, ex);
            }

            if (_options.EncoderPreference == StudioEncoderPreference.HardwareOnly && !encoder.IsHardware)
            {
                throw new StudioExportException($"No hardware encoder on this PC can make a {width}×{height} {CodecName(_options.Codec)} video.");
            }

            StudioAudioPump? pump = null;
            var audioFormat = audio?.Format;
            if (audio is not null && audioFormat is not null)
            {
                var audioPlan = StudioRenderingMath.BuildAudioPlan(_project, audioFormat.SampleRate);
                var samples = StudioRenderingMath.AudioSampleCount(audioPlan.TotalSamples, plan.Count, rate, audioFormat.SampleRate);
                var sink = encoder;
                pump = new StudioAudioPump(
                    audio,
                    StudioRenderingMath.LimitAudioRanges(audioPlan.Ranges, samples),
                    audioFormat.BlockAlign,
                    (pcm, start) => WriteAudio(sink, pcm, start, audioFormat),
                    samples);
            }

            byte[]? pixels = null;
            if (!attempt.GpuFrames)
            {
                memoryTarget = graphics.CreateRenderTexture(width, height);
                pixels = new byte[width * height * 4];
            }

            var layout = StudioLayoutPlan.Create(_project, _events);
            var lastProgress = Stopwatch.GetTimestamp();
            for (var index = 0; index < plan.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var sourceTime = plan[index].SourceTimeSeconds;
                var resolved = layout.Resolve(sourceTime, width, height);

                // The reader's frame stays valid until the next request, which is after the draw.
                StudioGpuVideoFrame? screenFrame = resolved.Screen is null
                    ? null
                    : screen.GetFrame(StudioRenderingMath.SecondsToMfTicks(sourceTime), _cancellationToken);
                StudioGpuVideoFrame? cameraFrame = camera is not null && resolved.Camera is { Visible: true } visible
                    ? camera.GetFrame(StudioRenderingMath.SecondsToMfTicks(visible.SourceTime), _cancellationToken)
                    : null;

                if (Fault?.Invoke(attempt.AllowHardware, attempt.GpuFrames, index) == true)
                {
                    throw new StudioEncoderFailedException("encoding a frame", encoder.VideoFramesWritten, new InvalidOperationException("a failure injected by the check tool"));
                }

                var time = rate.FrameTimeTicks(index);
                var duration = rate.FrameTimeTicks(index + 1) - time;
                if (attempt.GpuFrames)
                {
                    var frame = AcquireFrame(encoder);
                    try
                    {
                        lock (graphics.Gate)
                        {
                            renderer.Render(Request(sourceTime, width, height, screenFrame, cameraFrame, frame.Texture, resolved));

                            // Submit before the encoder, which may use its own context, reads the texture.
                            graphics.Context.Flush();
                        }
                    }
                    catch
                    {
                        frame.Dispose();
                        throw;
                    }

                    WriteVideo(encoder, frame, time, duration);
                }
                else
                {
                    lock (graphics.Gate)
                    {
                        renderer.Render(Request(sourceTime, width, height, screenFrame, cameraFrame, memoryTarget!, resolved));
                        graphics.ReadTexture(memoryTarget!, 0, pixels);
                    }

                    WriteVideoMemory(encoder, pixels!, time, duration);
                }

                // Sound goes in about half a second ahead of the picture it belongs to.
                if (pump is not null && audioFormat is not null)
                {
                    var videoEnd = (long)(((Int128)rate.FrameTimeTicks(index + 1) * audioFormat.SampleRate) / StudioMediaFoundation.TicksPerSecond);
                    pump.PumpTo(videoEnd + (audioFormat.SampleRate / 2), _cancellationToken);
                }

                if (_progress is not null && Stopwatch.GetElapsedTime(lastProgress) >= ProgressInterval)
                {
                    // Just short of 1, which is kept for the file being in place.
                    lastProgress = Stopwatch.GetTimestamp();
                    Report((index + 1) / (double)(plan.Count + 1));
                }
            }

            pump?.PumpToEnd(_cancellationToken);
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                encoder.Finish();
            }
            catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
            {
                throw new StudioEncoderFailedException("finishing the file", encoder.VideoFramesWritten, ex);
            }

            return new StudioExportResult(
                outputPath,
                plan.Count,
                plan.Count / rate.FramesPerSecond,
                $"{CodecName(_options.Codec)}, {encoder.EncoderName} ({(encoder.IsHardware ? "hardware" : "software")}{(attempt.GpuFrames ? string.Empty : ", frames from system memory")})",
                width,
                height,
                rate,
                encoder.HasAudio,
                encoder.IsHardware,
                graphics.IsSoftware,
                _quality);
        }
        finally
        {
            // The renderer wraps decoder and encoder textures, so it lets go of them first.
            lock (graphics.Gate)
            {
                renderer?.Dispose();
            }

            memoryTarget?.Dispose();
            encoder?.Dispose();
            audio?.Dispose();
            camera?.Dispose();
            screen?.Dispose();
            readerManager?.Dispose();
        }
    }

    private StudioRenderRequest Request(
        double sourceTime,
        int width,
        int height,
        StudioGpuVideoFrame? screen,
        StudioGpuVideoFrame? camera,
        ID3D11Texture2D target,
        StudioResolvedFrame resolved) =>
        new(_project, _events, _paths.ProjectDirectory, sourceTime, width, height, screen, camera, target, resolved, _quality);

    /// <summary>Waits for a free encoder texture. An offline export waits for the encoder; it never drops a frame.</summary>
    private StudioEncoderFrame AcquireFrame(StudioSinkWriterEncoder encoder)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (encoder.TryAcquireFrame() is { } frame)
                {
                    return frame;
                }
            }
            catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
            {
                throw new StudioEncoderFailedException("taking a frame from the encoder", encoder.VideoFramesWritten, ex);
            }

            if (Stopwatch.GetElapsedTime(started) > EncoderStallLimit)
            {
                throw new StudioEncoderFailedException(
                    "encoding a frame",
                    encoder.VideoFramesWritten,
                    new TimeoutException($"it took no frame for {EncoderStallLimit.TotalSeconds:0} seconds"));
            }

            Thread.Sleep(1);
        }
    }

    private static void WriteVideo(StudioSinkWriterEncoder encoder, StudioEncoderFrame frame, long time, long duration)
    {
        try
        {
            encoder.WriteVideo(frame, time, duration);
        }
        catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
        {
            throw new StudioEncoderFailedException("encoding a frame", encoder.VideoFramesWritten, ex);
        }
    }

    private static void WriteVideoMemory(StudioSinkWriterEncoder encoder, ReadOnlySpan<byte> pixels, long time, long duration)
    {
        try
        {
            encoder.WriteVideoMemory(pixels, time, duration);
        }
        catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
        {
            throw new StudioEncoderFailedException("encoding a frame", encoder.VideoFramesWritten, ex);
        }
    }

    private static void WriteAudio(StudioSinkWriterEncoder encoder, ReadOnlySpan<byte> pcm, long startSample, StudioAudioFormat format)
    {
        try
        {
            encoder.WriteAudio(pcm, startSample, format);
        }
        catch (SharpGenException ex) when (!StudioMediaFoundation.IsDeviceLost(ex))
        {
            throw new StudioEncoderFailedException("encoding the sound", encoder.VideoFramesWritten, ex);
        }
    }

    /// <summary>Progress only moves forward, also when an encoder gave up and the export started over.</summary>
    private void Report(double value)
    {
        if (_progress is not null && value > _reported)
        {
            _reported = value;
            _progress.Report(value);
        }
    }

    private static string CodecName(VideoCodec codec) => codec == VideoCodec.Hevc ? "HEVC" : "H.264";

    /// <summary>A failure as something to show the user, or null to let the exception through as it is.</summary>
    private Exception? Translate(Exception exception, string outputPath)
    {
        if (exception is OperationCanceledException or FileNotFoundException or DirectoryNotFoundException or StudioExportException or InvalidOperationException)
        {
            return null;
        }

        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner.HResult == StudioMediaFoundation.DiskFull)
            {
                return new StudioExportException("There is not enough free space on the disk to finish the export.", exception);
            }
        }

        return exception switch
        {
            StudioGraphicsDeviceUnavailableException => new StudioExportException("Tiny Clips could not start a graphics device to draw the video with.", exception),
            StudioEncoderFailedException when _options.Codec == VideoCodec.Hevc && StudioMediaFoundation.FindEncoder(VideoFormatGuids.Hevc, hardware: true) is null && StudioMediaFoundation.FindEncoder(VideoFormatGuids.Hevc, hardware: false) is null
                => new StudioExportException("This PC has no HEVC encoder. Choose H.264 and export again.", exception),
            StudioEncoderFailedException => new StudioExportException($"The video could not be encoded as {CodecName(_options.Codec)} at {_width}×{_height} on this PC: {exception.Message}.", exception),
            _ when StudioMediaFoundation.IsDeviceLost(exception) => new StudioExportException("The graphics device stopped working during the export. Export again.", exception),
            IOException or UnauthorizedAccessException => new StudioExportException($"The video could not be saved to {outputPath}. {exception.Message}", exception),
            _ => new StudioExportException($"The export failed ({StudioMediaFoundation.Describe(exception)}).", exception),
        };
    }
}

/// <summary>One poster image, start to finish, on the worker thread.</summary>
internal sealed class StudioPosterJob
{
    private const long JpegQuality = 86;

    private readonly StudioProject _project;
    private readonly StudioEvents _events;
    private readonly StudioProjectPaths _paths;
    private readonly string _posterPath;
    private readonly double _maxLongSide;
    private readonly double _outputTimeSeconds;
    private readonly CancellationToken _cancellationToken;

    public StudioPosterJob(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        string posterPath,
        double maxLongSide,
        double outputTimeSeconds,
        CancellationToken cancellationToken)
    {
        _project = project;
        _events = events;
        _paths = paths;
        _posterPath = posterPath;
        _maxLongSide = maxLongSide;
        _outputTimeSeconds = outputTimeSeconds;
        _cancellationToken = cancellationToken;
    }

    public bool Run()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var posterPath = Path.GetFullPath(_posterPath);
        var screenPath = StudioSourceFiles.Screen(_project, _paths);
        var cameraPath = StudioSourceFiles.Camera(_project, _paths);
        StudioSourceFiles.Require(screenPath, "screen recording");

        var size = StudioCanvasMath.ExportSize(StudioCanvasMath.NaturalSize(_project), _maxLongSide);
        var width = (int)size.Width;
        var height = (int)size.Height;

        // The same frame the export shows at that moment: the plan's frame, sampled at its middle.
        var rate = StudioRenderingMath.FrameRate(_project.Sources.Screen.FrameRate);
        var plan = StudioRenderingMath.BuildFramePlan(_project, rate.FramesPerSecond);
        var index = (int)Math.Clamp(Math.Floor((Math.Max(0, _outputTimeSeconds) * rate.FramesPerSecond) + 1e-9), 0, plan.Count - 1);
        var sourceTime = plan[index].SourceTimeSeconds;

        var temporaryPath = StudioRenderingMath.TemporaryOutputPath(posterPath);
        try
        {
            var pixels = Render(screenPath, cameraPath, sourceTime, width, height);
            _cancellationToken.ThrowIfCancellationRequested();

            // Encoding happens after the device and its lock are gone.
            Directory.CreateDirectory(Path.GetDirectoryName(posterPath)!);
            SaveJpeg(pixels, width, height, temporaryPath);
            _cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, posterPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            StudioSourceFiles.DeleteQuietly(temporaryPath);
            if (ex is OperationCanceledException or FileNotFoundException or StudioExportException or InvalidOperationException)
            {
                throw;
            }

            throw new StudioExportException($"The preview image could not be made ({StudioMediaFoundation.Describe(ex)}).", ex);
        }
    }

    private byte[] Render(string screenPath, string? cameraPath, double sourceTime, int width, int height)
    {
        using var mediaFoundation = StudioMediaFoundation.Startup();
        using var graphics = StudioGraphicsDevice.Create();
        IMFDXGIDeviceManager? readerManager = null;
        StudioSceneRenderer? renderer = null;
        StudioVideoSource? screen = null;
        StudioVideoSource? camera = null;
        ID3D11Texture2D? target = null;
        try
        {
            if (!graphics.IsSoftware)
            {
                readerManager = MediaFactory.MFCreateDXGIDeviceManager();
                readerManager.ResetDevice(graphics.Device).CheckError();
            }

            var resolved = StudioLayoutResolver.Resolve(_project, _events, sourceTime, width, height);
            StudioGpuVideoFrame? screenFrame = null;
            if (resolved.Screen is not null)
            {
                screen = StudioVideoSource.Open(screenPath, "screen recording", graphics, readerManager);
                screenFrame = screen.GetFrame(StudioRenderingMath.SecondsToMfTicks(sourceTime), _cancellationToken);
            }

            // A poster is still worth having when the camera file has gone missing.
            StudioGpuVideoFrame? cameraFrame = null;
            if (resolved.Camera is { Visible: true } visible && cameraPath is not null && File.Exists(cameraPath))
            {
                camera = StudioVideoSource.Open(cameraPath, "camera recording", graphics, readerManager);
                cameraFrame = camera.GetFrame(StudioRenderingMath.SecondsToMfTicks(visible.SourceTime), _cancellationToken);
            }

            renderer = new StudioSceneRenderer(graphics.Device);
            target = graphics.CreateRenderTexture(width, height);
            lock (graphics.Gate)
            {
                renderer.Render(new StudioRenderRequest(_project, _events, _paths.ProjectDirectory, sourceTime, width, height, screenFrame, cameraFrame, target, resolved, StudioRenderQuality.Export));
                return graphics.ReadTexture(target);
            }
        }
        finally
        {
            lock (graphics.Gate)
            {
                renderer?.Dispose();
            }

            target?.Dispose();
            camera?.Dispose();
            screen?.Dispose();
            readerManager?.Dispose();
        }
    }

    private static unsafe void SaveJpeg(byte[] bgra, int width, int height, string path)
    {
        ImageCodecInfo? jpeg = null;
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
            {
                jpeg = codec;
                break;
            }
        }

        if (jpeg is null)
        {
            throw new StudioExportException("This PC has no JPEG encoder.");
        }

        fixed (byte* pixels = bgra)
        {
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, (nint)pixels);
            using var parameters = new EncoderParameters(1);
            using var quality = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
            parameters.Param[0] = quality;
            bitmap.Save(path, jpeg, parameters);
        }
    }
}
