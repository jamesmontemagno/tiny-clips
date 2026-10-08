namespace TinyClips.Core.Studio.Preview;

/// <summary>Opens <see cref="StudioPreviewEngine"/> previews. Stateless; one instance can serve every editor window.</summary>
public sealed class StudioPreviewFactory : IStudioPreviewFactory
{
    private readonly StudioPreviewOptions _options;

    public StudioPreviewFactory()
        : this(new StudioPreviewOptions())
    {
    }

    internal StudioPreviewFactory(StudioPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public async Task<IStudioPreview> OpenAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken = default) =>
        await OpenEngineAsync(project, events, paths, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The same as <see cref="OpenAsync"/>, returning the engine itself, which is what a surface
    /// attaches to. May be called from any thread; the work is done on a worker thread.
    /// </summary>
    /// <exception cref="FileNotFoundException">The screen file is missing, or the project has a camera track and its file is missing.</exception>
    /// <exception cref="InvalidDataException">A file could not be decoded.</exception>
    /// <exception cref="InvalidOperationException">No graphics device could be created.</exception>
    /// <exception cref="TimeoutException">A file did not open or show its first frame in time.</exception>
    public Task<StudioPreviewEngine> OpenEngineAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(paths);
        var options = _options;
        return Task.Run(() => StudioPreviewEngine.Open(project, events, paths, options, cancellationToken), cancellationToken);
    }
}
