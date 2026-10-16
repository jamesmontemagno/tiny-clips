using TinyClips.Core.Models;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// The editor's export service: <see cref="StudioExporter"/> with the sizes the app uses. Every
/// call renders on a device of its own, so an export never shares a device, or its lock, with
/// the preview.
/// </summary>
public sealed class StudioExportService : IStudioExportService
{
    private readonly StudioExporter _exporter = new();

    public Task ExportAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        string outputPath,
        VideoCodec codec,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        _exporter.ExportAsync(
            project,
            events,
            paths,
            outputPath,
            new StudioExportOptions(StudioExportLimits.LongSide, codec),
            progress,
            cancellationToken);

    public Task WritePosterAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return _exporter.WritePosterAsync(project, events, paths, paths.PosterPath, StudioExportLimits.PosterLongSide, 0, cancellationToken);
    }
}
