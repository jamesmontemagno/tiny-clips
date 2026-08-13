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

    public static Task CopyTextAsync(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        SetContent(package);
        return Task.CompletedTask;
    }

    public static async Task CopyBitmapAsync(SoftwareBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        stream.Seek(0);

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
