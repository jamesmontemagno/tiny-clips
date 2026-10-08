using TinyClips.Core.Models;

namespace TinyClips.Core.Studio;

/// <summary>The sizes Studio renders at. The editor shows the export size, so both use these.</summary>
public static class StudioExportLimits
{
    /// <summary>The longest side of an exported video, in pixels. Larger canvases are scaled down.</summary>
    public const double LongSide = 3840;

    /// <summary>The longest side of a project's poster image, in pixels.</summary>
    public const double PosterLongSide = 640;

    /// <summary>The pixel size <paramref name="project"/> exports at.</summary>
    public static StudioSize GetExportSize(StudioProject project) =>
        StudioCanvasMath.ExportSize(StudioCanvasMath.NaturalSize(project), LongSide);
}

/// <summary>Renders Studio projects to files. Calls may come from any thread.</summary>
public interface IStudioExportService
{
    /// <summary>
    /// Renders the project to an MP4 at <paramref name="outputPath"/>, replacing a file that is
    /// already there once the new one is complete. <paramref name="progress"/> receives values
    /// from 0 to 1, and reaches 1 only when the file is in place.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The export was cancelled. A cancelled or failed export leaves nothing new at
    /// <paramref name="outputPath"/>, and does not touch a file that was there before.
    /// </exception>
    Task ExportAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        string outputPath,
        VideoCodec codec,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the project's poster image to <see cref="StudioProjectPaths.PosterPath"/>: the first
    /// frame of the exported video, no larger than <see cref="StudioExportLimits.PosterLongSide"/>.
    /// </summary>
    Task WritePosterAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken);
}
