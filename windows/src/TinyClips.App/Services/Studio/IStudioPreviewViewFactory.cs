using Microsoft.UI.Xaml;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.App.Services.Studio;

/// <summary>Makes the element that shows a Studio preview inside the editor window.</summary>
public interface IStudioPreviewViewFactory
{
    /// <summary>
    /// Creates the element that shows <paramref name="preview"/>. Called on the UI thread, once for
    /// each editor window, after the preview has opened.
    /// </summary>
    /// <remarks>
    /// The element fills whatever rectangle it is arranged into. The editor gives it the canvas
    /// rectangle: already the shape of the exported video, and with its edges on whole physical
    /// pixels. The editor takes the element out of its tree before it disposes the preview.
    /// </remarks>
    FrameworkElement Create(IStudioPreview preview);
}
