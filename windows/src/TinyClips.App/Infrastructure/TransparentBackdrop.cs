using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TinyClips.App;

/// <summary>
/// A <see cref="SystemBackdrop"/> that paints nothing, making the window's own background fully
/// transparent. Borderless overlays (such as the floating webcam preview) use it so that only the
/// XAML content they draw is visible — without it, WinUI paints an opaque theme-colored rectangle
/// behind the content that shows around any rounded or circular surface.
/// </summary>
public sealed partial class TransparentBackdrop : SystemBackdrop
{
    // SystemBackdrop targets take a Windows.UI.Composition brush, so this uses the system
    // compositor (valid here because every WinUI thread already owns a DispatcherQueue).
    private Windows.UI.Composition.Compositor? _compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _compositor ??= new Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        _compositor?.Dispose();
        _compositor = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
