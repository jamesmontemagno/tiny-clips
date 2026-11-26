using TinyClips.App;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace TinyClips.App.Tests;

public sealed class ScreenshotClipboardFormatTests
{
    [Fact]
    public async Task TemporaryPackageContainsOnlyBitmapBytesThatSurviveFileDeletion()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.png");
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            await ClipboardService.CopyTemporaryScreenshotAsync(path, async package =>
            {
                File.Delete(path);
                var content = package.GetView();
                Assert.Contains(StandardDataFormats.Bitmap, content.AvailableFormats);
                Assert.DoesNotContain(StandardDataFormats.StorageItems, content.AvailableFormats);
                Assert.All(content.AvailableFormats, format =>
                    Assert.True(format == StandardDataFormats.Bitmap || format == "Preferred DropEffect"));
                using var stream = await (await content.GetBitmapAsync()).OpenReadAsync();
                using var reader = new DataReader(stream);
                await reader.LoadAsync((uint)bytes.Length);
                var actual = new byte[bytes.Length];
                reader.ReadBytes(actual);
                Assert.Equal(bytes, actual);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
