using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TinyClips.Core.Models;
using TinyClips.Core.Services.ClipsLibrary;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TinyClips.App.Services.ClipsLibrary;

public interface IThumbnailCache
{
    /// <summary>
    /// Returns the path of a cached JPEG thumbnail for <paramref name="entry"/>, generating and
    /// persisting it on first request. Returns null when the source cannot be decoded.
    /// </summary>
    Task<string?> GetThumbnailPathAsync(ClipEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Drops cached thumbnails whose source clip no longer exists.</summary>
    Task PruneAsync(IEnumerable<ClipEntry> liveClips);
}

/// <summary>
/// Disk-backed thumbnail cache. Keys incorporate path, modified time and size so an edited or
/// re-trimmed clip automatically gets a fresh thumbnail while unchanged clips never re-decode.
/// Generation is throttled so scrolling a large library doesn't saturate the media pipeline.
/// </summary>
public sealed class ThumbnailCacheService : IThumbnailCache
{
    public const int ThumbnailWidth = 480;
    public const int ThumbnailHeight = 270;

    private readonly string _directory;
    private readonly SemaphoreSlim _throttle = new(3, 3);
    private readonly Dictionary<string, Task<string?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ThumbnailCacheService()
        : this(ClipsLibraryPaths.ThumbnailCacheDirectory())
    {
    }

    public ThumbnailCacheService(string directory)
    {
        _directory = directory;
    }

    public static string CacheKey(ClipEntry entry)
    {
        var material = $"{entry.Path.ToUpperInvariant()}|{entry.CapturedAt.UtcTicks}|{entry.FileSizeBytes}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    public Task<string?> GetThumbnailPathAsync(ClipEntry entry, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(_directory, CacheKey(entry) + ".jpg");
        if (File.Exists(target))
        {
            return Task.FromResult<string?>(target);
        }

        lock (_gate)
        {
            if (_inFlight.TryGetValue(target, out var pending))
            {
                return pending;
            }

            var task = GenerateAsync(entry, target, cancellationToken);
            _inFlight[target] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    _inFlight.Remove(target);
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    public Task PruneAsync(IEnumerable<ClipEntry> liveClips) => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var keep = new HashSet<string>(liveClips.Select(clip => CacheKey(clip) + ".jpg"), StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_directory, "*.jpg"))
            {
                if (!keep.Contains(Path.GetFileName(file)))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ThumbnailCacheService: prune failed: {ex}");
        }
    });

    private async Task<string?> GenerateAsync(ClipEntry entry, string target, CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            Directory.CreateDirectory(_directory);
            var file = await StorageFile.GetFileFromPathAsync(entry.Path);
            using var frame = entry.Type == CaptureType.Video
                ? await DecodeVideoFrameAsync(file)
                : await DecodeImageAsync(file);
            if (frame is null)
            {
                return null;
            }

            var temporary = target + ".tmp";
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var random = output.AsRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, random);
                encoder.SetSoftwareBitmap(frame);
                await encoder.FlushAsync();
            }

            File.Move(temporary, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"ThumbnailCacheService: failed for '{entry.Path}': {ex.Message}");
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static async Task<SoftwareBitmap?> DecodeVideoFrameAsync(StorageFile file)
    {
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        // Sample a little past the first frame so fade-in starts don't produce a black tile.
        var position = clip.OriginalDuration > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
        using var stream = await composition.GetThumbnailAsync(position, ThumbnailWidth, ThumbnailHeight, VideoFramePrecision.NearestKeyFrame);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
    }

    private static async Task<SoftwareBitmap?> DecodeImageAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1.0, Math.Min(
            (double)ThumbnailWidth / Math.Max(1, decoder.PixelWidth),
            (double)ThumbnailHeight / Math.Max(1, decoder.PixelHeight)));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
    }
}
