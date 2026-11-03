namespace TinyClips.Core.Editing;

/// <summary>Stages an encoded snapshot beside its destination, then commits the complete file.</summary>
public static class ScreenshotExportFile
{
    private static readonly SemaphoreSlim NewExportGate = new(1, 1);

    public static async Task<string> WriteNewAsync(
        string path, Func<string, CancellationToken, Task> encode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(encode);
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var temporaryPath = TemporaryPath(path);
        try
        {
            await encode(temporaryPath, cancellationToken).ConfigureAwait(false);
            await NewExportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                for (var suffix = 1; ; suffix++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = suffix == 1 ? path : Path.Combine(directory, $"{stem} {suffix}{extension}");
                    try
                    {
                        File.Move(temporaryPath, candidate, overwrite: false);
                        return candidate;
                    }
                    catch (IOException) when (File.Exists(candidate))
                    {
                    }
                }
            }
            finally
            {
                NewExportGate.Release();
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static async Task WriteAsync(
        string path, Func<string, CancellationToken, Task> encode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(encode);
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = TemporaryPath(path);
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

    private static string TemporaryPath(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".tinyclips-export-{Guid.NewGuid():N}.tmp");
}
