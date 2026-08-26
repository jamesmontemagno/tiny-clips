namespace TinyClips.Core.Services.ClipsLibrary;

/// <summary>
/// Mutating file operations the library needs beyond <see cref="IFileSystem"/>. Kept separate so
/// existing <see cref="IFileSystem"/> fakes stay source-compatible.
/// </summary>
public interface IClipFileOperations
{
    void MoveFile(string sourcePath, string destinationPath);

    DateTimeOffset GetFileCreationTime(string path);
}

public sealed class ClipFileOperations : IClipFileOperations
{
    public void MoveFile(string sourcePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(sourcePath, destinationPath);
    }

    public DateTimeOffset GetFileCreationTime(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }
}
