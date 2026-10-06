using TinyClips.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TinyClips.App;

internal static class ClipboardService
{
    public static async Task CopySavedClipAsync(string path, CaptureType type)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

        package.SetStorageItems(new[] { file });

        if (type == CaptureType.Screenshot)
        {
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }

        SetContent(package);
    }

    public static async Task CopyFilesAsync(IReadOnlyList<string> paths)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        var files = new List<IStorageItem>(paths.Count);
        foreach (var path in paths)
        {
            files.Add(await StorageFile.GetFileFromPathAsync(path));
        }

        package.SetStorageItems(files);
        SetContent(package);
    }

    public static Task CopyTextAsync(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        SetContent(package);
        return Task.CompletedTask;
    }

    public static async Task CopyBitmapAsync(SoftwareBitmap bitmap)
    {
        using var stream = await EncodeBitmapAsync(bitmap);
        SetBitmapContent(stream);
    }

    /// <summary>Encodes PNG on a worker; the caller retains the bitmap until this task completes.</summary>
    public static Task<InMemoryRandomAccessStream> EncodeBitmapAsync(
        SoftwareBitmap bitmap, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new InMemoryRandomAccessStream();
        try
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();
            stream.Seek(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }, cancellationToken);

    /// <summary>UI-thread publication, separate from encoding so callers can reject stale results.</summary>
    public static void SetBitmapContent(IRandomAccessStream stream)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));

        SetContent(package);
    }

    private static void SetContent(DataPackage package)
    {
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
