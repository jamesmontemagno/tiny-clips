using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public interface IClipLibraryService
{
    /// <summary>
    /// Scans the configured capture output directories and returns all recognised clip files,
    /// sorted newest-first.
    /// </summary>
    Task<IReadOnlyList<ClipEntry>> GetClipsAsync();

    /// <summary>
    /// Permanently deletes the file at <paramref name="path"/> from disk.
    /// </summary>
    void Delete(string path);
}
