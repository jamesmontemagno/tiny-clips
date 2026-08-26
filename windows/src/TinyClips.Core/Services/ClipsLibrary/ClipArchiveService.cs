using TinyClips.Core.Models;

namespace TinyClips.Core.Services.ClipsLibrary;

public interface IClipArchiveService
{
    /// <summary>Sub-folder name (relative to each capture directory) that receives archived clips.</summary>
    string ArchiveFolderName { get; }

    /// <summary>
    /// Moves clips captured more than <paramref name="olderThanDays"/> days before <paramref name="now"/>
    /// into an <c>Archive</c> sub-folder next to them. Returns (oldPath, newPath) for each moved clip.
    /// </summary>
    IReadOnlyList<(string OldPath, string NewPath)> ArchiveOlderThan(IEnumerable<ClipEntry> clips, int olderThanDays, DateTimeOffset now);

    /// <summary>Moves a single clip into its directory's archive folder and returns the new path.</summary>
    string Archive(ClipEntry clip);
}

public sealed class ClipArchiveService : IClipArchiveService
{
    private readonly IFileSystem _fileSystem;
    private readonly IClipFileOperations _fileOperations;

    public ClipArchiveService(IFileSystem fileSystem, IClipFileOperations fileOperations)
    {
        _fileSystem = fileSystem;
        _fileOperations = fileOperations;
    }

    public string ArchiveFolderName => "Archive";

    public IReadOnlyList<(string OldPath, string NewPath)> ArchiveOlderThan(IEnumerable<ClipEntry> clips, int olderThanDays, DateTimeOffset now)
    {
        if (olderThanDays <= 0)
        {
            return [];
        }

        var cutoff = now - TimeSpan.FromDays(olderThanDays);
        var moved = new List<(string, string)>();
        foreach (var clip in clips.Where(clip => clip.CapturedAt < cutoff).ToList())
        {
            try
            {
                moved.Add((clip.Path, Archive(clip)));
            }
            catch
            {
                // Skip clips that are locked or unreadable; the rest still archive.
            }
        }

        return moved;
    }

    public string Archive(ClipEntry clip)
    {
        var directory = Path.GetDirectoryName(clip.Path) ?? string.Empty;
        var archiveDirectory = Path.Combine(directory, ArchiveFolderName);
        _fileSystem.CreateDirectory(archiveDirectory);

        var destination = Path.Combine(archiveDirectory, clip.FileName);
        var counter = 1;
        while (_fileSystem.FileExists(destination))
        {
            var stem = Path.GetFileNameWithoutExtension(clip.FileName);
            destination = Path.Combine(archiveDirectory, $"{stem} ({counter++}){Path.GetExtension(clip.FileName)}");
        }

        _fileOperations.MoveFile(clip.Path, destination);
        return destination;
    }
}
