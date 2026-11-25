namespace TinyClips.Core.Services;

/// <summary>App-private screenshot files, retained only while an operation or editor needs them.</summary>
public sealed class ScreenshotTemporaryFileStore
{
    public const string FolderName = "Screenshots";
    private const string Prefix = "screenshot-";
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);

    public ScreenshotTemporaryFileStore()
        : this(Path.Combine(TinyClipsTemporaryFiles.DirectoryPath, FolderName))
    {
    }

    public ScreenshotTemporaryFileStore(string directoryPath)
    {
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public string DirectoryPath { get; }

    public ScreenshotTemporaryFileLease Reserve(string extension)
    {
        extension = extension.TrimStart('.').ToLowerInvariant();
        if (extension is not ("png" or "jpg" or "jpeg" or "webp"))
            throw new ArgumentException("Unsupported screenshot file extension.", nameof(extension));

        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, $"{Prefix}{Guid.NewGuid():N}.{extension}");
            _leases.Add(path, 1);
            return new ScreenshotTemporaryFileLease(this, path);
        }
    }

    internal ScreenshotTemporaryFileLease Retain(string path)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(path, out var count))
                throw new InvalidOperationException("The temporary screenshot is no longer leased.");
            _leases[path] = checked(count + 1);
            return new ScreenshotTemporaryFileLease(this, path);
        }
    }

    internal void Release(string path)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(path, out var count)) return;
            if (count > 1)
            {
                _leases[path] = count - 1;
                return;
            }
            _leases.Remove(path);
            TryDelete(path);
        }
    }

    internal Stream OpenNewFile(ScreenshotTemporaryFileLease lease)
    {
        lock (_gate)
        {
            if (!lease.IsOwnedBy(this) || !_leases.ContainsKey(lease.Path))
                throw new InvalidOperationException("The temporary screenshot is no longer leased.");
            return new FileStream(lease.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
        }
    }

    public TemporaryFilesPurgeResult Purge(TimeSpan minimumAge, DateTimeOffset now)
    {
        if (minimumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAge));
        lock (_gate)
        {
            if (!Directory.Exists(DirectoryPath)) return new(0, 0);
            var cutoff = now.UtcDateTime - minimumAge;
            var removed = 0;
            var skipped = 0;
            foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles())
            {
                if (!IsOwnedFileName(file.Name)) continue;
                if (_leases.ContainsKey(file.FullName) || file.LastWriteTimeUtc > cutoff)
                {
                    skipped++;
                    continue;
                }
                if (TryDelete(file.FullName)) removed++;
                else skipped++;
            }
            return new(removed, skipped);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashDiagnostics.Log("Screenshot temporary cleanup", ex, handled: true);
            return false;
        }
    }

    public static bool IsOwnedFileName(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name);
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp" &&
            stem.StartsWith(Prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(stem[Prefix.Length..], "N", out _);
    }
}

public sealed class ScreenshotTemporaryFileLease : IDisposable
{
    private ScreenshotTemporaryFileStore? _store;

    internal ScreenshotTemporaryFileLease(ScreenshotTemporaryFileStore store, string path)
    {
        _store = store;
        Path = path;
    }

    public string Path { get; }
    internal bool IsOwnedBy(ScreenshotTemporaryFileStore store) => ReferenceEquals(_store, store);
    public ScreenshotTemporaryFileLease Retain() =>
        (_store ?? throw new ObjectDisposedException(nameof(ScreenshotTemporaryFileLease))).Retain(Path);
    public void Dispose() => Interlocked.Exchange(ref _store, null)?.Release(Path);
}
