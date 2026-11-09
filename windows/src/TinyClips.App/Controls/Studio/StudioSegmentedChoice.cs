using System.Reflection;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace TinyClips.App.Controls.Studio;

/// <summary>Supplies the ListViewBase automation peer that the toolkit's Segmented control lacks.</summary>
public sealed partial class StudioSegmented : Segmented
{
    protected override AutomationPeer OnCreateAutomationPeer() => new StudioSegmentedAutomationPeer(this);
}

public sealed partial class StudioSegmentedAutomationPeer(StudioSegmented owner) : ListViewBaseAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(Segmented);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override ItemAutomationPeer OnCreateItemAutomationPeer(object item) => new ListViewItemDataAutomationPeer(item, this);
}

/// <summary>
/// Keeps a <see cref="Segmented"/> showing the choice it was last given when its template is
/// applied late. The control remembers the first choice it is given before its template is
/// applied and puts that one back when the template is applied: a control that is collapsed
/// until the user opens it (in an expander, or for another selection) would hand that old choice
/// back to the view model through its two-way binding. This keeps what it remembers up to date.
/// </summary>
public static class StudioSegmentedChoice
{
    private static readonly FieldInfo? RememberedIndex =
        typeof(Segmented).GetField("_internalSelectedIndex", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? HasLoaded =
        typeof(Segmented).GetField("_hasLoaded", BindingFlags.Instance | BindingFlags.NonPublic);

    // These are CommunityToolkit.WinUI.Controls.Segmented internals (8.x). A toolkit update that
    // renames them makes this a no-op and brings the stale choice back, so say so in Debug builds.
    static StudioSegmentedChoice() =>
        System.Diagnostics.Debug.Assert(
            RememberedIndex is not null && HasLoaded is not null,
            "Segmented no longer has _internalSelectedIndex/_hasLoaded; StudioSegmentedChoice needs updating.");

    public static readonly DependencyProperty KeepsLatestChoiceProperty = DependencyProperty.RegisterAttached(
        "KeepsLatestChoice",
        typeof(bool),
        typeof(StudioSegmentedChoice),
        new PropertyMetadata(false, OnKeepsLatestChoiceChanged));

    public static bool GetKeepsLatestChoice(DependencyObject element) => (bool)element.GetValue(KeepsLatestChoiceProperty);

    public static void SetKeepsLatestChoice(DependencyObject element, bool value) => element.SetValue(KeepsLatestChoiceProperty, value);

    private static void OnKeepsLatestChoiceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Segmented segmented && e.NewValue is true)
        {
            Remember(segmented);
            segmented.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Primitives.Selector.SelectedIndexProperty, (sender, _) => Remember((Segmented)sender));
        }
    }

    private static void Remember(Segmented segmented)
    {
        // Once the template is applied the control no longer looks at what it remembered.
        if (RememberedIndex is null || HasLoaded?.GetValue(segmented) is true || segmented.SelectedIndex < 0)
        {
            return;
        }

        RememberedIndex.SetValue(segmented, segmented.SelectedIndex);
    }
}
