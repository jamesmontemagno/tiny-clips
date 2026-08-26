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
    /// Like <see cref="GetClipsAsync()"/>, optionally restricted to files whose names carry the
    /// Tiny Clips prefix so captures from other tools saved to the same folder are hidden.
    /// </summary>
    Task<IReadOnlyList<ClipEntry>> GetClipsAsync(bool onlyTinyClipsFiles);

    /// <summary>Unique directories the library scans (one per capture type, deduplicated).</summary>
    IReadOnlyList<string> GetLibraryDirectories();

    /// <summary>
    /// Renames the file at <paramref name="path"/> to <paramref name="newFileNameWithoutExtension"/>
    /// (extension preserved) within its current directory and returns the new full path.
    /// </summary>
    string Rename(string path, string newFileNameWithoutExtension);

    /// <summary>
    /// Permanently deletes the file at <paramref name="path"/> from disk.
    /// </summary>
    void Delete(string path);
}
