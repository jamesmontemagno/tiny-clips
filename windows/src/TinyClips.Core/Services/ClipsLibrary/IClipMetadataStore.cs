using TinyClips.Core.Models.ClipsLibrary;

namespace TinyClips.Core.Services.ClipsLibrary;

/// <summary>
/// Persists user metadata (favorites, names, tags, notes, collections, upload links) keyed by
/// clip path. Implementations must be safe to call from any thread.
/// </summary>
public interface IClipMetadataStore
{
    /// <summary>Raised after any mutation. May fire on a background thread.</summary>
    event EventHandler? Changed;

    ClipMetadata Get(string path);

    IReadOnlyCollection<ClipMetadata> GetAll();

    void Upsert(ClipMetadata metadata);

    void Remove(string path);

    /// <summary>Re-keys a record after the underlying file was renamed or moved.</summary>
    void RenamePath(string oldPath, string newPath);

    /// <summary>Drops records whose paths are not in <paramref name="existingPaths"/>.</summary>
    int Prune(IEnumerable<string> existingPaths);

    /// <summary>Writes any pending changes to disk immediately.</summary>
    void Flush();
}
