using TinyClips.Core.Models;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.Core.Services;

public sealed class ClipLibraryService : IClipLibraryService
{
    /// <summary>Prefix Tiny Clips stamps on every generated file name (see FileNameService).</summary>
    public const string TinyClipsFilePrefix = "TinyClips";

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
    private readonly IClipFileOperations _fileOperations;

    public ClipLibraryService(IClipStorageService storage, IFileSystem fileSystem)
        : this(storage, fileSystem, new ClipFileOperations())
    {
    }

    public ClipLibraryService(IClipStorageService storage, IFileSystem fileSystem, IClipFileOperations fileOperations)
    {
        _storage = storage;
        _fileSystem = fileSystem;
        _fileOperations = fileOperations;
    }

    public static bool IsSupportedClipFile(string path) =>
        ExtensionMap.ContainsKey(Path.GetExtension(path).TrimStart('.'));

    public static CaptureType? CaptureTypeFor(string path) =>
        ExtensionMap.TryGetValue(Path.GetExtension(path).TrimStart('.'), out var type) ? type : null;

    public Task<IReadOnlyList<ClipEntry>> GetClipsAsync() => GetClipsAsync(onlyTinyClipsFiles: false);

    public Task<IReadOnlyList<ClipEntry>> GetClipsAsync(bool onlyTinyClipsFiles) => Task.Run(() =>
    {
        var clips = new List<ClipEntry>();
        foreach (var dir in GetLibraryDirectories())
        {
            if (!_fileSystem.DirectoryExists(dir))
            {
                continue;
            }

            foreach (var file in _fileSystem.EnumerateFiles(dir))
            {
                if (CaptureTypeFor(file) is not { } type)
                {
                    continue;
                }

                var fileName = Path.GetFileName(file);
                if (onlyTinyClipsFiles && !fileName.StartsWith(TinyClipsFilePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var capturedAt = _fileSystem.GetFileLastWriteTime(file);
                var sizeBytes = _fileSystem.GetFileSizeBytes(file);
                clips.Add(new ClipEntry(file, type, fileName, capturedAt, sizeBytes));
            }
        }

        IReadOnlyList<ClipEntry> sorted = clips
            .OrderByDescending(c => c.CapturedAt)
            .ToList();

        return sorted;
    });

    public IReadOnlyList<string> GetLibraryDirectories()
    {
        // Collect unique output directories (custom dir collapses all types to one path).
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in Enum.GetValues<CaptureType>())
        {
            var dir = _storage.OutputDirectory(type);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                dirs.Add(dir);
            }
        }

        return dirs.ToList();
    }

    public string Rename(string path, string newFileNameWithoutExtension)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var extension = Path.GetExtension(path);
        var stem = SanitizeFileStem(newFileNameWithoutExtension);
        if (stem.Length == 0)
        {
            throw new ArgumentException("A file name is required.", nameof(newFileNameWithoutExtension));
        }

        var destination = Path.Combine(directory, stem + extension);
        if (string.Equals(destination, path, StringComparison.Ordinal))
        {
            return path;
        }

        if (_fileSystem.FileExists(destination) && !string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"A file named \"{stem}{extension}\" already exists.");
        }

        _fileOperations.MoveFile(path, destination);
        return destination;
    }

    public void Delete(string path)
    {
        _fileSystem.DeleteFile(path);
    }

    public static string SanitizeFileStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Trim().TrimEnd('.');
    }
}
