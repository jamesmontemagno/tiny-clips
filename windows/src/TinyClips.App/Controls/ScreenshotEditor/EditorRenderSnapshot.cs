using TinyClips.Core.Editing;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;

namespace TinyClips.App.ScreenshotEditor;

/// <summary>Owns one image lease, never the controller's original bitmap or any XAML object.</summary>
internal sealed class EditorRenderSnapshot(
    SharedResource<EditorImage>.Lease image,
    ScreenshotRenderState state,
    DocumentRevision revision,
    CancellationToken documentCancellation) : IDisposable
{
    public EditorImage Image => image.Value;
    public ScreenshotRenderState State { get; } = state;
    public DocumentRevision Revision { get; } = revision;
    public CancellationToken DocumentCancellation { get; } = documentCancellation;

    public void Dispose() => image.Dispose();
}

/// <summary>
/// Immutable document pixels plus a lazily uploaded Win2D source. Source access is serialized
/// by the controller's render gate; uploads and device-change recreation happen on workers.
/// </summary>
internal sealed class EditorImage(SoftwareBitmap bitmap) : IDisposable
{
    private CanvasDevice? _device;
    private CanvasBitmap? _source;
    public SoftwareBitmap Bitmap { get; } = bitmap;

    public CanvasBitmap GetCanvasSource(CanvasDevice device)
    {
        if (_source is null || !ReferenceEquals(_device, device))
        {
            var source = CanvasBitmap.CreateFromSoftwareBitmap(device, Bitmap);
            _source?.Dispose();
            _source = source;
            _device = device;
        }
        return _source;
    }

    public void Dispose()
    {
        try
        {
            _source?.Dispose();
        }
        finally
        {
            Bitmap.Dispose();
        }
    }
}
