using System;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Editing;
using Windows.System;
using Windows.UI.Core;

namespace TinyClips.App;

/// <summary>
/// Shared Esc-to-close pieces for the screenshot editor and the trimmer windows: recognizing a
/// plain Esc press and asking the user to confirm the close.
/// </summary>
internal static class EditorEscapeConfirmation
{
    /// <summary>
    /// True for Esc on its own. Ctrl+Esc, Alt+Esc and Shift+Esc belong to the system or mean
    /// something else, so they never close an editor.
    /// </summary>
    public static bool IsUnmodifiedEscape(KeyRoutedEventArgs e) =>
        e.Key == VirtualKey.Escape &&
        !IsDown(VirtualKey.Control) &&
        !IsDown(VirtualKey.Shift) &&
        !IsDown(VirtualKey.Menu);

    public static async Task<bool> ConfirmAsync(
        FrameworkElement owner, EditorEscapePrompt prompt, EditorEscapeSurface surface)
    {
        var dialog = new ContentDialog
        {
            Title = EditorEscape.Title(prompt, surface),
            Content = EditorEscape.Message(prompt, surface),
            PrimaryButtonText = EditorEscape.ConfirmButtonText(prompt, surface),
            CloseButtonText = "Cancel",
            // Losing work takes a deliberate choice; a close that loses nothing is one Enter away.
            DefaultButton = EditorEscape.IsDestructive(prompt)
                ? ContentDialogButton.Close
                : ContentDialogButton.Primary,
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.RequestedTheme,
        };
        AutomationProperties.SetAutomationId(dialog, "EditorCloseConfirmationDialog");
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
