using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// A message that stands in for the whole editor, and can take the keyboard focus. Nothing else in
/// the window can when it shows, so focus goes here, and a screen reader reads the message: it
/// asks for the focused element when a window opens, where a notice sent that early is lost.
/// </summary>
/// <remarks>
/// Its accessible name is the whole message, set with <c>AutomationProperties.Name</c>. The text
/// inside stays in the tree as well, so its heading can still be found as a heading.
/// </remarks>
public sealed partial class StudioMessagePanel : ContentControl
{
    public StudioMessagePanel()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioMessagePanelAutomationPeer(this);
}

/// <summary>Presents a <see cref="StudioMessagePanel"/> to screen readers as one named group.</summary>
public sealed partial class StudioMessagePanelAutomationPeer(StudioMessagePanel owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => nameof(StudioMessagePanel);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
