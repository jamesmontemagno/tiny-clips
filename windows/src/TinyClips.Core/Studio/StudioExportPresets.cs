using System.Globalization;
using TinyClips.Core.Models;

namespace TinyClips.Core.Studio;

/// <summary>How large an exported video is. A video is never made larger than its canvas.</summary>
public enum StudioExportResolution
{
    /// <summary>The canvas at its own size, however large that is.</summary>
    Source,
    P2160,
    P1440,
    P1080,
    P720,
    P480,
}

/// <summary>How many bits an exported video spends on each frame, from most to fewest.</summary>
public enum StudioExportQuality
{
    Best,
    High,
    Standard,
    Small,
}

/// <summary>What the Export popup asks, and what an agent may pass to <c>studio.export</c>.</summary>
public readonly record struct StudioExportChoice(StudioExportResolution Resolution, StudioExportQuality Quality)
{
    /// <summary>What Export offers until another choice has been made.</summary>
    public static StudioExportChoice Default { get; } = new(StudioExportResolution.P1080, StudioExportQuality.High);
}

/// <summary>A resolution offered for a canvas, and the size it gives that canvas.</summary>
public sealed record StudioExportResolutionOption(StudioExportResolution Resolution, string Id, string Label, StudioSize Size);

/// <summary>A quality offered at one size, and what it comes to there.</summary>
public sealed record StudioExportQualityOption(StudioExportQuality Quality, string Id, string Label, uint VideoBitrate, long EstimatedBytes);

/// <summary>
/// The sizes, bitrates and estimates behind the Export popup. The numbers are the same on macOS;
/// <c>shared/studio/fixtures/export</c> holds what both have to come to.
/// </summary>
public static class StudioExportPresets
{
    private const uint SoundBitrate = 192_000;

    private static readonly StudioExportResolution[] Resolutions = Enum.GetValues<StudioExportResolution>();

    private static readonly StudioExportQuality[] Qualities = Enum.GetValues<StudioExportQuality>();

    public static string Id(StudioExportResolution resolution) => resolution switch
    {
        StudioExportResolution.P2160 => "2160p",
        StudioExportResolution.P1440 => "1440p",
        StudioExportResolution.P1080 => "1080p",
        StudioExportResolution.P720 => "720p",
        StudioExportResolution.P480 => "480p",
        _ => "source",
    };

    public static string Id(StudioExportQuality quality) => quality switch
    {
        StudioExportQuality.Best => "best",
        StudioExportQuality.Standard => "standard",
        StudioExportQuality.Small => "small",
        _ => "high",
    };

    public static string Label(StudioExportResolution resolution) => resolution switch
    {
        StudioExportResolution.P2160 => "4K",
        StudioExportResolution.Source => "Source",
        _ => Id(resolution),
    };

    public static string Label(StudioExportQuality quality) => quality switch
    {
        StudioExportQuality.Best => "Best",
        StudioExportQuality.Standard => "Standard",
        StudioExportQuality.Small => "Small file",
        _ => "High",
    };

    public static IReadOnlyList<string> ResolutionIds { get; } = Array.ConvertAll(Resolutions, Id);

    public static IReadOnlyList<string> QualityIds { get; } = Array.ConvertAll(Qualities, Id);

    public static bool TryParseResolution(string? id, out StudioExportResolution resolution)
    {
        foreach (var candidate in Resolutions)
        {
            if (string.Equals(id, Id(candidate), StringComparison.Ordinal))
            {
                resolution = candidate;
                return true;
            }
        }

        resolution = default;
        return false;
    }

    public static bool TryParseQuality(string? id, out StudioExportQuality quality)
    {
        foreach (var candidate in Qualities)
        {
            if (string.Equals(id, Id(candidate), StringComparison.Ordinal))
            {
                quality = candidate;
                return true;
            }
        }

        quality = default;
        return false;
    }

    /// <summary>
    /// The size a canvas exports at. A named resolution fits the canvas inside a frame of that
    /// resolution, turned to match the canvas: 1080p is 1920 × 1080 for a wide canvas and
    /// 1080 × 1920 for a tall one. Source is the canvas's own size. The shape never changes, and
    /// nothing is scaled up.
    /// </summary>
    public static StudioSize GetSize(StudioSize natural, StudioExportResolution resolution)
    {
        if (resolution == StudioExportResolution.Source)
        {
            return StudioCanvasMath.ExportSize(natural, 0);
        }

        var (frameLong, frameShort) = FrameOf(resolution);
        var canvasLong = Math.Max(natural.Width, natural.Height);
        var canvasShort = Math.Min(natural.Width, natural.Height);
        var scale = Math.Min(1, Math.Min(frameLong / canvasLong, frameShort / canvasShort));
        return new StudioSize(StudioCanvasMath.Even(natural.Width * scale), StudioCanvasMath.Even(natural.Height * scale));
    }

    /// <summary>
    /// Whether a size fits inside the 4K frame, turned to match it. Past it, the largest video an
    /// encoder can make depends on the PC and on the shape of the frame.
    /// </summary>
    public static bool FitsFourK(double width, double height) =>
        Math.Max(width, height) <= 3840 && Math.Min(width, height) <= 2160;

    /// <summary>
    /// The resolutions worth offering for a canvas, largest first. One that would give the same
    /// size as a larger one is left out, so a 720p recording offers Source, not a 1080p that is
    /// not there, and a 720p that says the same thing.
    /// </summary>
    public static IReadOnlyList<StudioExportResolutionOption> GetResolutionOptions(StudioSize natural)
    {
        var options = new List<StudioExportResolutionOption>();
        foreach (var resolution in Resolutions)
        {
            var size = GetSize(natural, resolution);
            if (!options.Exists(option => option.Size == size))
            {
                options.Add(new StudioExportResolutionOption(resolution, Id(resolution), Label(resolution), size));
            }
        }

        return options;
    }

    /// <summary>The offered resolution that gives what <paramref name="resolution"/> gives this canvas.</summary>
    public static StudioExportResolution Effective(StudioSize natural, StudioExportResolution resolution)
    {
        var size = GetSize(natural, resolution);
        return GetResolutionOptions(natural).First(option => option.Size == size).Resolution;
    }

    /// <summary>
    /// The video bitrate of an export. Up to 4K, Standard is the recorder's own rule, so that an
    /// export at that quality weighs what a recording of its size does; past 4K its ceiling grows
    /// with the pixels, so a Source export of a larger canvas is not starved. The other qualities
    /// are multiples of Standard.
    /// </summary>
    public static uint GetVideoBitrate(double width, double height, double framesPerSecond, VideoCodec codec, StudioExportQuality quality)
    {
        var fps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? Math.Clamp(framesPerSecond, 1, 240) : 30;
        var ceiling = (long)(24_000_000 * Math.Max(1, width * height / (3840.0 * 2160)));
        var standard = Math.Clamp((long)(width * height * fps / 10), 2_000_000, ceiling);
        var multiple = quality switch
        {
            StudioExportQuality.Best => 2.5,
            StudioExportQuality.High => 1.5,
            StudioExportQuality.Small => 0.5,
            _ => 1.0,
        };
        var forCodec = codec == VideoCodec.Hevc ? 0.6 : 1.0;
        return (uint)(standard * multiple * forCodec);
    }

    /// <summary>About how large the file comes to: the video and sound bitrates over its length.</summary>
    public static long EstimateBytes(uint videoBitrate, double durationSeconds, bool hasSound)
    {
        var seconds = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
        return (long)((videoBitrate + (hasSound ? SoundBitrate : 0)) * seconds / 8);
    }

    public static IReadOnlyList<StudioExportQualityOption> GetQualityOptions(StudioSize size, double framesPerSecond, VideoCodec codec, double durationSeconds, bool hasSound)
    {
        var options = new StudioExportQualityOption[Qualities.Length];
        for (var index = 0; index < options.Length; index++)
        {
            var quality = Qualities[index];
            var bitrate = GetVideoBitrate(size.Width, size.Height, framesPerSecond, codec, quality);
            options[index] = new StudioExportQualityOption(quality, Id(quality), Label(quality), bitrate, EstimateBytes(bitrate, durationSeconds, hasSound));
        }

        return options;
    }

    /// <summary>What the Export popup offers for a project, starting from <paramref name="saved"/>.</summary>
    public static StudioExportOptionsModel CreateOptions(StudioProject project, VideoCodec codec, StudioExportChoice saved)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new StudioExportOptionsModel(
            StudioCanvasMath.NaturalSize(project),
            project.Sources.Screen.FrameRate,
            codec,
            StudioTimeMap.FromProject(project).OutputDuration,
            !project.Audio.Muted,
            saved);
    }

    /// <summary>For example <c>1080p – 1920 × 1080</c>.</summary>
    public static string GetOptionText(StudioExportResolutionOption option) =>
        string.Create(CultureInfo.InvariantCulture, $"{option.Label} – {SizeText(option.Size)}");

    /// <summary>For example <c>1920 × 1080</c>.</summary>
    public static string SizeText(StudioSize size) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)size.Width} × {(long)size.Height}");

    /// <summary>For example <c>About 68 MB</c> or <c>About 1.2 GB</c>.</summary>
    public static string GetEstimateText(long bytes)
    {
        const double Megabyte = 1024 * 1024;
        var megabytes = bytes / Megabyte;
        return megabytes switch
        {
            < 1 => "Less than 1 MB",
            < 1024 => string.Create(CultureInfo.InvariantCulture, $"About {Math.Round(megabytes, MidpointRounding.AwayFromZero):0} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"About {megabytes / 1024:0.0} GB"),
        };
    }

    private static (double Long, double Short) FrameOf(StudioExportResolution resolution) => resolution switch
    {
        StudioExportResolution.P2160 => (3840, 2160),
        StudioExportResolution.P1440 => (2560, 1440),
        StudioExportResolution.P1080 => (1920, 1080),
        StudioExportResolution.P720 => (1280, 720),
        StudioExportResolution.P480 => (854, 480),
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Source has no frame."),
    };
}

/// <summary>
/// What the Export popup shows and holds while it is open, without the controls that show it.
/// The choice that was offered is kept apart from the one that was saved: a small project offers
/// Source where 1080p was saved, and that alone should not change what larger projects offer.
/// </summary>
public sealed class StudioExportOptionsModel
{
    private readonly StudioSize _natural;
    private readonly double _framesPerSecond;
    private readonly VideoCodec _codec;
    private readonly double _durationSeconds;
    private readonly bool _hasSound;
    private readonly StudioExportChoice _saved;
    private readonly StudioExportChoice _offered;

    public StudioExportOptionsModel(StudioSize natural, double framesPerSecond, VideoCodec codec, double durationSeconds, bool hasSound, StudioExportChoice saved)
    {
        _natural = natural;
        _framesPerSecond = framesPerSecond;
        _codec = codec;
        _durationSeconds = durationSeconds;
        _hasSound = hasSound;
        _saved = saved;
        _offered = saved with { Resolution = StudioExportPresets.Effective(natural, saved.Resolution) };
        Choice = _offered;
        Resolutions = StudioExportPresets.GetResolutionOptions(natural);
    }

    public IReadOnlyList<StudioExportResolutionOption> Resolutions { get; }

    public StudioExportChoice Choice { get; set; }

    public StudioSize Size => StudioExportPresets.GetSize(_natural, Choice.Resolution);

    public IReadOnlyList<StudioExportQualityOption> Qualities =>
        StudioExportPresets.GetQualityOptions(Size, _framesPerSecond, _codec, _durationSeconds, _hasSound);

    /// <summary>For example <c>1920 × 1080 · 30 fps · H.264</c>.</summary>
    public string DetailsText
    {
        get
        {
            var fps = double.IsFinite(_framesPerSecond) && _framesPerSecond > 0 ? _framesPerSecond : 30;
            var codec = _codec == VideoCodec.Hevc ? "HEVC" : "H.264";
            return string.Create(CultureInfo.InvariantCulture, $"{StudioExportPresets.SizeText(Size)} · {fps:0.##} fps · {codec}");
        }
    }

    public string EstimateText => StudioExportPresets.GetEstimateText(Qualities.First(option => option.Quality == Choice.Quality).EstimatedBytes);

    /// <summary>The choice to keep for next time: the saved one, with what was changed here.</summary>
    public StudioExportChoice ChoiceToRemember => new(
        Choice.Resolution == _offered.Resolution ? _saved.Resolution : Choice.Resolution,
        Choice.Quality);
}
