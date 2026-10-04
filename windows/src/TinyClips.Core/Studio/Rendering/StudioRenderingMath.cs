using System.Globalization;
using TinyClips.Core.Models;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>One output video frame and the source instant it samples.</summary>
public readonly record struct StudioExportFrame(int Index, double OutputTimeSeconds, double SourceTimeSeconds);

/// <summary>A run of screen-track audio samples copied into an export. The end is exclusive.</summary>
public readonly record struct StudioAudioSampleRange(long SourceStartSample, long SourceEndSample, long OutputStartSample)
{
    public long SampleCount => SourceEndSample - SourceStartSample;
}

/// <summary>
/// The sound of an export: the runs of the screen track it keeps, each with its place in the
/// output, and how many samples the finished track has. Between two runs that do not follow each
/// other in the output, and after the last one up to <paramref name="TotalSamples"/>, the track is
/// silent: that is where the video plays at another speed.
/// </summary>
public sealed record StudioAudioPlan(IReadOnlyList<StudioAudioSampleRange> Ranges, long TotalSamples);

/// <summary>Resolved click-ring geometry in canvas pixels.</summary>
public readonly record struct StudioClickRing(double CenterX, double CenterY, double Radius, double StrokeWidth, double Alpha);

/// <summary>An exact frame rate, as Media Foundation and MP4 store it.</summary>
public readonly record struct StudioFrameRate(int Numerator, int Denominator)
{
    public double FramesPerSecond => Numerator / (double)Denominator;

    /// <summary>The start of frame <paramref name="frameIndex"/> in 100 ns units, rounded to the nearest unit.</summary>
    public long FrameTimeTicks(long frameIndex) =>
        (long)((((Int128)frameIndex * StudioRenderingMath.MediaFoundationTicksPerSecond * Denominator) + (Numerator / 2)) / Numerator);
}

/// <summary>Small deterministic helpers shared by the Studio renderer, exporter, tests and check tool.</summary>
public static class StudioRenderingMath
{
    public const int MediaFoundationTicksPerSecond = 10_000_000;

    /// <summary>Samples in one AAC frame. An AAC track always ends on a multiple of this.</summary>
    public const int AacFrameSamples = 1024;

    /// <summary>
    /// The exact rate an export uses for a source frame rate: whole rates stay whole, NTSC rates
    /// (29.97, 59.94, 23.976) become n/1001, anything else is kept to a thousandth. A missing or
    /// impossible rate is treated as 30, and rates are limited to 1–240.
    /// </summary>
    public static StudioFrameRate FrameRate(double framesPerSecond)
    {
        var fps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? Math.Clamp(framesPerSecond, 1, 240) : 30;
        var whole = Math.Round(fps, MidpointRounding.AwayFromZero);
        if (Math.Abs(fps - whole) < 0.005)
        {
            return new StudioFrameRate((int)whole, 1);
        }

        var ntsc = Math.Round(fps * 1.001, MidpointRounding.AwayFromZero);
        if (Math.Abs((fps * 1.001) - ntsc) < 0.005)
        {
            return new StudioFrameRate((int)ntsc * 1000, 1001);
        }

        var thousandths = (int)Math.Round(fps * 1000, MidpointRounding.AwayFromZero);
        var divisor = GreatestCommonDivisor(thousandths, 1000);
        return new StudioFrameRate(thousandths / divisor, 1000 / divisor);
    }

    /// <summary>
    /// The frames of an export at <paramref name="frameRate"/>. Frame <c>i</c> shows the source at
    /// the middle of its own duration, <c>(i + 0.5) / frameRate</c> of output time, which is the
    /// instant a paused preview shows for that frame. A last partial frame is dropped, so no frame
    /// ever shows a moment outside the kept range.
    /// </summary>
    public static IReadOnlyList<StudioExportFrame> BuildFramePlan(StudioProject project, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!double.IsFinite(frameRate) || frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate), "Frame rate must be positive.");
        }

        var map = StudioTimeMap.FromProject(project);
        var frameCount = CheckedFrameCount(map.OutputDuration, frameRate);
        var frames = new StudioExportFrame[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var outputTime = (index + 0.5) / frameRate;
            frames[index] = new StudioExportFrame(index, outputTime, map.OutputToSource(outputTime));
        }

        return frames;
    }

    public static int CheckedFrameCount(double outputDurationSeconds, double frameRate)
    {
        if (!double.IsFinite(outputDurationSeconds) || outputDurationSeconds <= 0)
        {
            throw new InvalidOperationException("Nothing is left to export after trimming.");
        }

        var exact = outputDurationSeconds * frameRate;
        if (exact >= int.MaxValue)
        {
            throw new InvalidOperationException("The video is too long to export.");
        }

        // The small allowance keeps a duration that is a whole number of frames, such as 2.7 s at
        // 30 fps, from losing its last frame to floating-point error.
        var count = (int)Math.Floor(exact + 1e-9);
        if (count < 1)
        {
            throw new InvalidOperationException("What is left after trimming is shorter than one output frame.");
        }

        return count;
    }

    /// <summary>The camera's own time for a source time. It may be negative or past the camera's end.</summary>
    public static double CameraSourceTime(StudioProject project, double sourceTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(project);
        return sourceTimeSeconds - (project.Sources.Camera?.StartOffset ?? 0);
    }

    public static bool CameraVisible(StudioProject project, double sourceTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(project);
        var camera = project.Sources.Camera;
        if (camera is null)
        {
            return false;
        }

        var time = sourceTimeSeconds - camera.StartOffset;
        return time >= 0 && time <= camera.Duration;
    }

    /// <summary>
    /// The audio an export keeps, as sample ranges of the screen track: one range for each piece
    /// of the time map that plays at the recording's own speed, each starting and ending on the
    /// sample nearest the piece's edge. A piece at another speed has no sound (section 7 of the
    /// project format): the output moves on by the time it takes, and the track is silent there.
    /// A muted project has no ranges and no samples.
    /// </summary>
    public static StudioAudioPlan BuildAudioPlan(StudioProject project, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (project.Audio.Muted)
        {
            return new StudioAudioPlan([], 0);
        }

        var map = StudioTimeMap.FromProject(project);
        var ranges = new List<StudioAudioSampleRange>(map.Pieces.Count);
        long outputCursor = 0;
        foreach (var piece in map.Pieces)
        {
            if (piece.Rate != 1)
            {
                outputCursor += SecondsToSamples(piece.OutputDuration, sampleRate);
                continue;
            }

            var start = SecondsToSamples(piece.Start, sampleRate);
            var end = SecondsToSamples(piece.End, sampleRate);
            if (end > start)
            {
                ranges.Add(new StudioAudioSampleRange(start, end, outputCursor));
                outputCursor += end - start;
            }
        }

        return new StudioAudioPlan(ranges, outputCursor);
    }

    /// <summary>The ranges of <see cref="BuildAudioPlan"/>.</summary>
    public static IReadOnlyList<StudioAudioSampleRange> BuildAudioRanges(StudioProject project, int sampleRate) =>
        BuildAudioPlan(project, sampleRate).Ranges;

    /// <summary>
    /// How many audio samples to write next to <paramref name="frameCount"/> video frames. An AAC
    /// track is padded to a whole number of 1024-sample frames, so the count is chosen to put that
    /// padded end as close to the end of the video as it can be: at most half an AAC frame (512
    /// samples, 10.7 ms at 48 kHz) before or after it. Never more than <paramref name="keptSamples"/>.
    /// </summary>
    public static long AudioSampleCount(long keptSamples, long frameCount, StudioFrameRate frameRate, int sampleRate)
    {
        if (keptSamples <= 0 || frameCount <= 0 || sampleRate <= 0)
        {
            return 0;
        }

        var videoEnd = (long)((((Int128)frameCount * frameRate.Denominator * sampleRate) + (frameRate.Numerator / 2)) / frameRate.Numerator);
        var wholeFrames = videoEnd - (videoEnd % AacFrameSamples);
        var target = wholeFrames > 0 && (videoEnd - wholeFrames) * 2 <= AacFrameSamples ? wholeFrames : videoEnd;
        return Math.Min(keptSamples, target);
    }

    /// <summary>Cuts <paramref name="ranges"/> off after <paramref name="sampleCount"/> output samples.</summary>
    public static IReadOnlyList<StudioAudioSampleRange> LimitAudioRanges(IReadOnlyList<StudioAudioSampleRange> ranges, long sampleCount)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var limited = new List<StudioAudioSampleRange>(ranges.Count);
        foreach (var range in ranges)
        {
            var room = sampleCount - range.OutputStartSample;
            if (room <= 0)
            {
                break;
            }

            limited.Add(range.SampleCount <= room ? range : range with { SourceEndSample = range.SourceStartSample + room });
        }

        return limited;
    }

    public static long SecondsToSamples(double seconds, int sampleRate) =>
        (long)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);

    public static long SecondsToMfTicks(double seconds) =>
        (long)Math.Round(seconds * MediaFoundationTicksPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>The recorder's bitrate rule, so an export weighs what a recording of that size does.</summary>
    public static uint VideoBitrate(int width, int height, double framesPerSecond, VideoCodec codec)
    {
        var bitrate = Math.Clamp((long)(width * (double)height * framesPerSecond / 10), 2_000_000, 24_000_000);
        return codec == VideoCodec.Hevc ? (uint)(bitrate * 0.6) : (uint)bitrate;
    }

    public static (double StartX, double StartY, double EndX, double EndY) GradientEndpoints(double width, double height) =>
        (0, 0, width, height);

    /// <summary>
    /// The name an export or poster is written under until it is complete: next to the final file,
    /// so the move into place stays on one volume, and with an extension the Clips Library does
    /// not scan.
    /// </summary>
    public static string TemporaryOutputPath(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var directory = Path.GetDirectoryName(outputPath);
        var name = string.Create(CultureInfo.InvariantCulture, $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}{TemporaryExtension}");
        return Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, name);
    }

    public const string TemporaryExtension = ".tcexport";

    /// <summary>
    /// Deletes what an export that was killed left behind in <paramref name="folder"/>: files
    /// named exactly as <see cref="TemporaryOutputPath"/> names them that were last written to,
    /// and created, at least <paramref name="olderThan"/> ago. Nothing else is touched, folders
    /// inside it are not looked into, and a file that anything has open, as an export that is
    /// still running has its own, is left whatever its age. Never throws.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    public static int DeleteStaleTemporaryFiles(string folder, TimeSpan olderThan)
    {
        var deleted = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return 0;
            }

            var newest = DateTime.UtcNow - (olderThan > TimeSpan.Zero ? olderThan : TimeSpan.Zero);
            foreach (var path in Directory.EnumerateFiles(folder, "*" + TemporaryExtension, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var file = new FileInfo(path);
                    if (IsTemporaryName(file.Name) && file.LastWriteTimeUtc <= newest && file.CreationTimeUtc <= newest)
                    {
                        // Media Foundation lets the file it is writing be deleted under it, so
                        // a plain delete would take a running export's file. Opening it for
                        // nobody else fails while anyone has it open, and deletes it otherwise.
                        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                        {
                        }

                        deleted++;
                    }
                }
                catch (Exception)
                {
                    // In use, protected, or gone in the meantime: not this call's to remove.
                }
            }
        }
        catch (Exception)
        {
            // The folder cannot be listed. There is nothing to clean up that can be reached.
        }

        return deleted;
    }

    /// <summary>
    /// Whether a file name is one <see cref="TemporaryOutputPath"/> makes: a dot, the output's
    /// name, a dot, 32 lower-case hexadecimal digits, and <see cref="TemporaryExtension"/>.
    /// </summary>
    internal static bool IsTemporaryName(string fileName)
    {
        const int idLength = 32;
        var idStart = fileName.Length - TemporaryExtension.Length - idLength;
        if (idStart < 2 || fileName[0] != '.' || fileName[idStart - 1] != '.' || !fileName.EndsWith(TemporaryExtension, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var digit in fileName.AsSpan(idStart, idLength))
        {
            if (digit is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The ring a click draws at <paramref name="sourceTimeSeconds"/>, following section 6.7 of the format.</summary>
    public static bool TryComputeClickRing(
        StudioClickEvent click,
        double sourceTimeSeconds,
        StudioEvents events,
        StudioProject project,
        StudioResolvedScreen screen,
        out StudioClickRing ring)
    {
        ArgumentNullException.ThrowIfNull(click);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(project);
        ring = default;
        var overlay = project.Overlays.Clicks;
        if (!overlay.Enabled || !(overlay.Duration > 0) || !(overlay.Opacity > 0))
        {
            return false;
        }

        var elapsed = sourceTimeSeconds - click.T;
        if (!(elapsed >= 0) || elapsed > overlay.Duration)
        {
            return false;
        }

        var source = screen.Source;
        if (!(source.Width > 0) || !(source.Height > 0) || project.Sources.Screen.Width <= 0)
        {
            return false;
        }

        if (click.X < source.X || click.Y < source.Y || click.X > source.X + source.Width || click.Y > source.Y + source.Height)
        {
            return false;
        }

        var captureScale = events.Capture.Scale > 0 ? events.Capture.Scale : 1;
        var captureRatio = events.Capture.Width > 0 ? project.Sources.Screen.Width / (double)events.Capture.Width : 1;
        var k = captureScale * captureRatio * (screen.Rect.Width / (source.Width * project.Sources.Screen.Width));
        var diameter = Math.Max(0, overlay.Size) * k;
        var progress = elapsed / overlay.Duration;
        var radius = (diameter / 2) + (diameter * 0.58 * progress);
        var stroke = Math.Max(0, overlay.StrokeWidth) * k;
        var alpha = (1 - progress) * Math.Clamp(overlay.Opacity, 0, 1);
        var centerX = screen.Rect.X + (((click.X - source.X) / source.Width) * screen.Rect.Width);
        var centerY = screen.Rect.Y + (((click.Y - source.Y) / source.Height) * screen.Rect.Height);

        ring = new StudioClickRing(centerX, centerY, radius, stroke, alpha);
        return alpha > 1e-12 && stroke > 0 && double.IsFinite(radius) && double.IsFinite(centerX) && double.IsFinite(centerY);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Max(1, Math.Abs(a));
    }
}
