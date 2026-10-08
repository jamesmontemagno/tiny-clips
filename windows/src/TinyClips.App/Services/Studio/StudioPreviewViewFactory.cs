using Microsoft.UI.Xaml;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.App.Services.Studio;

/// <summary>
/// Shows a preview opened by <see cref="StudioPreviewFactory"/> in a <see cref="StudioPreviewPanel"/>.
/// The two are registered together: the panel draws with the engine's own device.
/// </summary>
public sealed class StudioPreviewViewFactory : IStudioPreviewViewFactory
{
    /// <inheritdoc/>
    public FrameworkElement Create(IStudioPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview is not StudioPreviewEngine engine)
        {
            throw new InvalidOperationException("The preview is of a kind this window cannot show.");
        }

        // The panel stops drawing when it leaves the window, which the editor makes it do before
        // it disposes the preview.
        var panel = new StudioPreviewPanel();
        panel.Attach(engine);
        return panel;
    }
}
