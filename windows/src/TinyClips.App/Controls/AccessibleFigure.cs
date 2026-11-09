using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App;

/// <summary>
/// A diagram built from ordinary elements that a screen reader reads as one picture. Set its
/// description with <c>AutomationProperties.Name</c>; the labels inside are not read separately.
/// </summary>
public sealed partial class AccessibleFigure : ContentControl
{
    public AccessibleFigure()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new AccessibleFigureAutomationPeer(this);
}

/// <summary>Presents an <see cref="AccessibleFigure"/> as a single image with no children.</summary>
public sealed partial class AccessibleFigureAutomationPeer(AccessibleFigure owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    protected override string GetClassNameCore() => nameof(AccessibleFigure);

    protected override IList<AutomationPeer> GetChildrenCore() => [];

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
