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
    public static TemporaryFilesSummary GetSummary()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return new TemporaryFilesSummary(0, 0);
        }

        var files = new DirectoryInfo(DirectoryPath).EnumerateFiles();
        var fileCount = 0;
        long totalSize = 0;
        foreach (var file in files)
        {
            fileCount++;
            totalSize += file.Length;
        }

        return new TemporaryFilesSummary(fileCount, totalSize);
    }

    /// <summary>Deletes all files owned by Tiny Clips in its temporary directory.</summary>
    public static int Purge()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return 0;
        }

        var removedCount = 0;
        foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles())
        {
            file.Delete();
            removedCount++;
        }

        return removedCount;
    }
}

/// <summary>A point-in-time summary of files in the Tiny Clips temporary directory.</summary>
public readonly record struct TemporaryFilesSummary(int FileCount, long TotalSize);
