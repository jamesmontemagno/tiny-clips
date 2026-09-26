using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Shared "delete the capture I'm editing" flow for the trimmer windows: confirm, delete the
/// source file, and drop it from the recent captures list. Callers close their window right after
/// a successful delete so the discarded capture is never saved or announced.
/// </summary>
internal static class EditorSourceDeletion
{
    public static async Task<bool> ConfirmAsync(FrameworkElement owner, string filePath, CaptureType type)
    {
        var noun = type == CaptureType.Gif ? "GIF" : "recording";
        var dialog = new ContentDialog
        {
            Title = $"Delete this {noun}?",
            Content = $"This permanently deletes {Path.GetFileName(filePath)} and closes the editor without saving. This cannot be undone.",
            PrimaryButtonText = $"Delete {noun}",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.RequestedTheme,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "EditorDeleteSourceDialog");
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Deletes the source file and removes it from recent captures. Returns the failure, or null on
    /// success. A missing file counts as success. Playback/decode handles are released asynchronously
    /// by WinRT, so a locked file is retried briefly before giving up.
    /// </summary>
    public static async Task<Exception?> TryDeleteAsync(string filePath)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                App.Services.GetRequiredService<IRecentCaptureService>().Remove(filePath);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(120);
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        return lastError;
    }

    public static async Task ShowFailureAsync(FrameworkElement owner, string filePath, Exception error)
    {
        var dialog = new ContentDialog
        {
            Title = "Couldn't delete this capture",
            Content = $"Tiny Clips couldn't delete {Path.GetFileName(filePath)}. Check that the file is not in use and that you have permission, then try again. Details: {error.Message}",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.RequestedTheme,
        };

        await dialog.ShowAsync();
    }
}
