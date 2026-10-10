namespace TinyClips.Core.Editing;

/// <summary>Stages an encoded snapshot beside its destination, then commits the complete file.</summary>
public static class ScreenshotExportFile
{
    public static async Task WriteAsync(
        string path, Func<string, CancellationToken, Task> encode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(encode);
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
            $".tinyclips-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await encode(temporaryPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
