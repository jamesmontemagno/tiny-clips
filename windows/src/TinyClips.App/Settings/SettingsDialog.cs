using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Settings;

/// <summary>
/// Shows the dialogs of the Settings window. WinUI shows one dialog at a time in a window and
/// throws for a second one. The handlers that show one are <c>async void</c>, where an exception
/// that gets out ends the app, and with it a recording that is running. A second dialog can be
/// asked for because some handlers wait before they show theirs: for a Studio draft to be
/// copied or deleted, for a file picker, for a file to be read. One of those can then come up
/// against a dialog that was opened in the meantime, so every dialog of the window goes through
/// here.
/// </summary>
internal static class SettingsDialog
{
    /// <summary>
    /// Shows a dialog and returns what was chosen. Returns null when it could not be shown:
    /// there is nothing to show it on, or another dialog is open there. A dialog that was not
    /// shown was not answered, so the caller does nothing, or says what it had to say another way.
    /// </summary>
    /// <param name="xamlRoot">What the dialog is shown on. Null once its element has left the window.</param>
    public static async Task<ContentDialogResult?> TryShowAsync(ContentDialog dialog, XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
        {
            return null;
        }

        try
        {
            dialog.XamlRoot = xamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"A dialog of Settings could not be shown: {ex}");
            return null;
        }
    }
}
