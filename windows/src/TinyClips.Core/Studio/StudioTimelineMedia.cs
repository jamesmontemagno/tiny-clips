using System.Security.Cryptography;

namespace TinyClips.Core.Studio;

/// <summary>A selected input's resolved path and handle identity, captured before authorization.</summary>
public sealed record StudioMediaFileIdentity
{
    private StudioMediaFileIdentity(string path, StudioExportTarget.FileVersion version)
    {
        Path = path;
        Version = version;
    }

    public string Path { get; }
    private StudioExportTarget.FileVersion Version { get; }

    public static StudioMediaFileIdentity Capture(string path)
    {
        using var stream = Open(path, out var identity);
        return identity;
    }

    internal static FileStream Open(string path, out StudioMediaFileIdentity identity)
    {
        var resolved = StudioExportTarget.ResolvePath(path);
        var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);
        try
        {
            var version = StudioExportTarget.ReadFileVersion(stream.SafeFileHandle)
                ?? throw new IOException("The selected media file's identity could not be read.");
            identity = new(resolved, version);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Recording is a single-recording editor snapshot; its layers are used instead of VideoPath's export.
/// IsRecordedTake marks a Record another segment take, whose imported source names the take draft as takeProjectId.
/// </summary>
public sealed record StudioTimelineImport(
    string VideoPath,
    StudioProject? Recording = null,
    StudioMediaFileIdentity? ExpectedVideoIdentity = null,
    bool IsRecordedTake = false);

public interface IStudioTimelineMediaStore
{
    /// <summary>
    /// Copies and probes an ordered batch without publishing a manifest. Empty imports prepare
    /// v1 conversion/adoption only. Validate the live editor and authorization again before Commit.
    /// </summary>
    Task<StudioPreparedTimelineEdit> StageTimelineAsync(
        StudioProject destination,
        IReadOnlyList<StudioTimelineImport> imports,
        CancellationToken cancellationToken = default);
}

public sealed class StudioProjectStaleException()
    : IOException("The Studio project changed while preparing this edit. Reload it and try again.");

public sealed class StudioMediaFileChangedException(Exception? innerException = null)
    : IOException("The selected input media changed after it was approved.", innerException);

/// <summary>
/// A single-use media transaction. Dispose abandons it. Commit is synchronous so live-editor
/// validation and publication can run together on the editor thread, without an intervening await.
/// </summary>
public sealed class StudioPreparedTimelineEdit : IDisposable
{
    private readonly object _sync = new();
    private readonly string _baseJson;
    private readonly string _proposedJson;
    private readonly int _baseSourceCount;
    private readonly int _baseClipCount;
    private readonly Func<StudioProject, CancellationToken, StudioProject> _commit;
    private Action? _cleanup;

    internal StudioPreparedTimelineEdit(StudioProject basis, StudioProject proposed, string revision,
        Func<StudioProject, CancellationToken, StudioProject> commit, Action cleanup)
    {
        _baseJson = StudioProjectJson.WriteProject(basis);
        _proposedJson = StudioProjectJson.WriteProject(proposed);
        _baseSourceCount = basis.MediaSources!.Length;
        _baseClipCount = basis.Timeline!.Clips.Length;
        ExpectedDiskRevision = revision;
        _commit = commit;
        _cleanup = cleanup;
    }

    // Return detached graphs: records contain mutable arrays and extension dictionaries.
    public StudioProject BaseProject => StudioProjectJson.ReadProject(_baseJson);
    public StudioProject ProposedProject => StudioProjectJson.ReadProject(_proposedJson);
    public IReadOnlyList<StudioMediaSource> ImportedSources => ProposedProject.MediaSources![_baseSourceCount..];
    public IReadOnlyList<StudioTimelineClip> ImportedClips => ProposedProject.Timeline!.Clips[_baseClipCount..];
    public string ExpectedDiskRevision { get; }

    public StudioProject Commit(StudioProject proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_cleanup is null, this);
            try { return _commit(proposal, cancellationToken); }
            finally { Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            var cleanup = _cleanup;
            _cleanup = null;
            cleanup?.Invoke();
        }
    }

    internal static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
