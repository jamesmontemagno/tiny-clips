namespace TinyClips.Core.Studio.Preview;

/// <summary>Which of the files a preview plays are the engine's to wait for when it closes.</summary>
internal static class StudioPreviewFiles
{
    /// <summary>
    /// Whether <paramref name="path"/> is a file in <paramref name="folder"/> or below it. The
    /// names are compared as Windows compares them: whatever their case, and after <c>.</c> and
    /// <c>..</c> have been worked out. False for a path that cannot be read as one.
    /// </summary>
    public static bool IsInFolder(string? folder, string? path)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(folder);
            if (!Path.EndsInDirectorySeparator(root))
            {
                root += Path.DirectorySeparatorChar;
            }

            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
