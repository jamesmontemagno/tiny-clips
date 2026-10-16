using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// Where the keyboard focus goes after a button's own action has switched the button off, or
/// hidden it. Left alone, the focus would go to whatever comes next in the window.
/// </summary>
internal static class StudioFocus
{
    /// <summary>How the control that raised an event has the focus: by the keyboard, by the pointer, or not at all.</summary>
    public static FocusState StateOf(object sender) =>
        sender is Control control ? control.FocusState : FocusState.Unfocused;

    /// <summary>
    /// Gives the focus to the first of the controls that can take it, the way the pressed button
    /// had it. A button that was pressed without having the focus leaves the focus alone.
    /// </summary>
    public static void Move(FocusState state, params Control[] candidates)
    {
        if (state == FocusState.Unfocused)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.IsEnabled && candidate.Visibility == Visibility.Visible && candidate.Focus(state))
            {
                return;
            }
        }
    }
}
