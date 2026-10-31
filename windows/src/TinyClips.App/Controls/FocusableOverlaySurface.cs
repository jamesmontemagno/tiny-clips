using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App;

/// <summary>
/// A full-window surface that can hold the keyboard focus, for capture overlays whose content is
/// purely visual (a <see cref="Grid"/> or <see cref="Canvas"/> cannot be focused, so key events
/// would never reach the window). It draws no focus rectangle: the overlay itself is the focus cue.
/// </summary>
public sealed partial class FocusableOverlaySurface : ContentControl
{
    public FocusableOverlaySurface()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FocusableOverlaySurfaceAutomationPeer(this);
}

/// <summary>Presents a <see cref="FocusableOverlaySurface"/> to screen readers as one named pane.</summary>
public sealed partial class FocusableOverlaySurfaceAutomationPeer(FocusableOverlaySurface owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

    protected override string GetClassNameCore() => nameof(FocusableOverlaySurface);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsKeyboardFocusableCore() => true;
}
