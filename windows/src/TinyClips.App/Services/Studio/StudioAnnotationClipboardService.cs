using TinyClips.Core.Studio;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace TinyClips.App.Services.Studio;

public interface IStudioAnnotationClipboardService : IDisposable
{
    event EventHandler? ContentChanged;
    bool HasAnnotation { get; }
    Task WriteAsync(byte[] payload);
    Task<byte[]?> ReadAsync();
}

/// <summary>Raw UTF-8 bytes in the custom format only; no text fallback or automation access.</summary>
public sealed class StudioAnnotationClipboardService : IStudioAnnotationClipboardService
{
    private bool _isDisposed;

    public StudioAnnotationClipboardService() => Clipboard.ContentChanged += OnContentChanged;

    public event EventHandler? ContentChanged;

    public bool HasAnnotation => !_isDisposed && Clipboard.GetContent().Contains(StudioAnnotationClipboard.Format);

    public async Task WriteAsync(byte[] payload)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (payload.Length > StudioAnnotationClipboard.MaximumPayloadBytes)
        {
            throw new InvalidDataException("The annotation clipboard payload exceeds 1 MiB.");
        }
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        stream.Seek(0);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetData(StudioAnnotationClipboard.Format, stream);
        Clipboard.SetContent(package);
        // Keep the bytes available after the source editor (and this stream) closes.
        Clipboard.Flush();
    }

    public async Task<byte[]?> ReadAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var content = Clipboard.GetContent();
        if (!content.Contains(StudioAnnotationClipboard.Format))
        {
            return null;
        }
        var data = await content.GetDataAsync(StudioAnnotationClipboard.Format);
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (data is not IRandomAccessStream stream)
        {
            throw new InvalidDataException("Invalid Tiny Clips annotation clipboard data.");
        }
        using (stream)
        {
            var size = stream.Size;
            if (size > StudioAnnotationClipboard.MaximumPayloadBytes)
            {
                throw new InvalidDataException("The annotation clipboard payload exceeds 1 MiB.");
            }
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync((uint)size);
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (loaded != size)
            {
                throw new InvalidDataException("Incomplete Tiny Clips annotation clipboard data.");
            }
            var payload = new byte[loaded];
            reader.ReadBytes(payload);
            return payload;
        }
    }

    private void OnContentChanged(object? sender, object args)
    {
        if (!_isDisposed)
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        Clipboard.ContentChanged -= OnContentChanged;
    }
}
