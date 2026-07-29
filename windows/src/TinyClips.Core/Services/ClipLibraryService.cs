using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed class ClipLibraryService : IClipLibraryService
{
    // Extensions that identify each capture type. Keep in sync with FileNameService.
    private static readonly IReadOnlyDictionary<string, CaptureType> ExtensionMap =
        new Dictionary<string, CaptureType>(StringComparer.OrdinalIgnoreCase)
        {
            ["png"]  = CaptureType.Screenshot,
            ["jpg"]  = CaptureType.Screenshot,
            ["jpeg"] = CaptureType.Screenshot,
            ["mp4"]  = CaptureType.Video,
            ["gif"]  = CaptureType.Gif,
        };

    private readonly IClipStorageService _storage;
    private readonly IFileSystem _fileSystem;

    public ClipLibraryService(IClipStorageService storage, IFileSystem fileSystem)
    {
        _storage = storage;
        _fileSystem = fileSystem;
    }

    public Task<IReadOnlyList<ClipEntry>> GetClipsAsync()
    {
        // Collect unique output directories (custom dir collapses all types to one path).
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in Enum.GetValues<CaptureType>())
        {
            dirs.Add(_storage.OutputDirectory(type));
        }

        var clips = new List<ClipEntry>();
        foreach (var dir in dirs)
        {
            if (!_fileSystem.DirectoryExists(dir))
            {
                continue;
            }

            foreach (var file in _fileSystem.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file).TrimStart('.');
                if (!ExtensionMap.TryGetValue(ext, out var type))
                {
                    continue;
                }

                var capturedAt = _fileSystem.GetFileLastWriteTime(file);
                var sizeBytes = _fileSystem.GetFileSizeBytes(file);
                clips.Add(new ClipEntry(file, type, Path.GetFileName(file), capturedAt, sizeBytes));
            }
        }

        IReadOnlyList<ClipEntry> sorted = clips
            .OrderByDescending(c => c.CapturedAt)
            .ToList();

        return Task.FromResult(sorted);
    }

    public void Delete(string path)
    {
        _fileSystem.DeleteFile(path);
    }
}
