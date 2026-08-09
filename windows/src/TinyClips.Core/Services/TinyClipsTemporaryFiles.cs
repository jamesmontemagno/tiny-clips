namespace TinyClips.Core.Services;

/// <summary>
/// Manages Tiny Clips-owned temporary files outside the user's capture library.
/// </summary>
public static class TinyClipsTemporaryFiles
{
    private const string ApplicationFolderName = "TinyClips";
    private const string TemporaryFolderName = "Temp";

    /// <summary>The directory reserved for temporary Tiny Clips artifacts.</summary>
    public static string DirectoryPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var rootDirectory = string.IsNullOrWhiteSpace(localAppData)
                ? Path.GetTempPath()
                : localAppData;

            return Path.Combine(rootDirectory, ApplicationFolderName, TemporaryFolderName);
        }
    }

    /// <summary>Creates the temporary directory when it does not already exist.</summary>
    public static string EnsureDirectoryExists()
    {
        Directory.CreateDirectory(DirectoryPath);
        return DirectoryPath;
    }

    /// <summary>Returns the number and total size of files managed by Tiny Clips.</summary>
    public static TemporaryFilesSummary GetSummary() => GetSummary(DirectoryPath);

    /// <summary>Returns the number and total size of files in a temporary directory.</summary>
    public static TemporaryFilesSummary GetSummary(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            return new TemporaryFilesSummary(0, 0);
        }

        var files = new DirectoryInfo(directoryPath).EnumerateFiles();
        var fileCount = 0;
        long totalSize = 0;
        foreach (var file in files)
        {
            fileCount++;
            totalSize += file.Length;
        }

        return new TemporaryFilesSummary(fileCount, totalSize);
    }

    /// <summary>Deletes temporary files that are not actively being used by Tiny Clips.</summary>
    public static TemporaryFilesPurgeResult Purge(IEnumerable<string>? activeFilePaths = null) =>
        Purge(DirectoryPath, activeFilePaths);

    /// <summary>Deletes temporary files in a directory, excluding active files.</summary>
    public static TemporaryFilesPurgeResult Purge(string directoryPath, IEnumerable<string>? activeFilePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            return new TemporaryFilesPurgeResult(0, 0);
        }

        var activePaths = activeFilePaths is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(activeFilePaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var removedFileCount = 0;
        var skippedFileCount = 0;

        foreach (var file in new DirectoryInfo(directoryPath).EnumerateFiles())
        {
            if (activePaths.Contains(file.FullName))
            {
                skippedFileCount++;
                continue;
            }

            try
            {
                file.Delete();
                removedFileCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skippedFileCount++;
            }
        }

        return new TemporaryFilesPurgeResult(removedFileCount, skippedFileCount);
    }
}

/// <summary>A point-in-time summary of files in the Tiny Clips temporary directory.</summary>
public readonly record struct TemporaryFilesSummary(int FileCount, long TotalSize);

/// <summary>The outcome of deleting files from the Tiny Clips temporary directory.</summary>
public readonly record struct TemporaryFilesPurgeResult(int RemovedFileCount, int SkippedFileCount);
