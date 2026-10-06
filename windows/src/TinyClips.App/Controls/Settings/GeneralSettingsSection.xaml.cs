using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;

namespace TinyClips.App.Settings.Sections;

/// <summary>
/// General settings: theme, save location, file naming, launch-at-login, and capture behavior
/// toggles, and the switch for Tiny Clips Studio. While Studio is switched on it also shows Studio
/// project storage and the recordings that only their project holds.
/// </summary>
public sealed partial class GeneralSettingsSection : UserControl, ISettingsSectionLifecycle
{
    private readonly IDisposable _realizationScope;
    private bool _closed;

    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Raised when the user clicks Browse. The folder picker must be owned by the shell window
    /// (it needs an HWND via <c>WinRT.Interop.WindowNative</c>), so this section only requests it
    /// rather than showing a picker itself.
    /// </summary>
    public event Action<CaptureType>? BrowseSaveDirectoryRequested;

    public GeneralSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization(SettingsSectionKind.General);
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);

        // Never faults. With Studio switched off it reads only what the line under the switch says.
        _ = viewModel.EnsureStudioStorageInitializedAsync();
    }

    public void NotifyWindowClosed() => _closed = true;

    private void OnBrowseScreenshotSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Screenshot);

    private void OnBrowseVideoSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Video);

    private void OnBrowseGifSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Gif);

    private void OnOpenTempFolder(object sender, RoutedEventArgs e) => ViewModel.OpenTempFolder();

    private async void OnCleanUpStudioProjects(object sender, RoutedEventArgs e)
    {
        await ViewModel.CleanUpStudioProjectsAsync();
        if (_closed)
        {
            return;
        }

        // The status line is a live region, so screen readers need to be told its text changed.
        var peer = FrameworkElementAutomationPeer.FromElement(StudioCleanupStatusText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(StudioCleanupStatusText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnOpenStudioDraft(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StudioDraftItem draft } && Application.Current is App app)
        {
            app.OpenStudioWindow(draft.Id);
        }
    }

    /// <summary>
    /// Shows one of this section's dialogs, and returns what was chosen. Returns null when it
    /// could not be shown: the section is no longer on a window, or another dialog is open in
    /// it. WinUI shows one dialog at a time in a window and throws for a second one, and these
    /// handlers are <c>async void</c>, where an exception that gets out ends the app, and with
    /// it a recording that is running. A dialog can be asked for while another is open because
    /// each of these handlers waits for a copy or a delete first.
    /// </summary>
    private async Task<ContentDialogResult?> TryShowAsync(ContentDialog dialog)
    {
        if (_closed || XamlRoot is null)
        {
            return null;
        }

        dialog.XamlRoot = XamlRoot;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"A dialog of General settings could not be shown: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Saves a row's screen recording as an ordinary video. From there it is a saved video like
    /// any other, and the app announces it as one. A failure is said in a dialog, or in a
    /// notification where no dialog can be shown.
    /// </summary>
    /// <remarks>
    /// The button stays as it is while the recording is copied, so that it keeps the keyboard
    /// focus, and a press then saves nothing more. A screen reader is told that the recording
    /// is being saved at the press that starts the copy and at every press while it runs, so
    /// that no press goes without an answer.
    /// </remarks>
    private async void OnSaveStudioDraftRecording(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StudioDraftItem draft } button)
        {
            return;
        }

        var saving = ViewModel.SaveStudioScreenRecordingAsync(draft);
        if (draft.IsSavingRecording && !_closed)
        {
            TellScreenReader(button, StudioScreenRecording.SavingMessage, "StudioDraftRecordingSaving");
        }

        var (path, error) = await saving;
        if (path is not null)
        {
            // Also when Settings was closed while the recording was being copied: the video is there.
            (Application.Current as App)?.AnnounceStudioVideoSaved(path);
            return;
        }

        if (error is null)
        {
            return;
        }

        var failure = new ContentDialog
        {
            Title = "The screen recording was not saved",
            Content = error,
            CloseButtonText = "OK",
        };
        if (await TryShowAsync(failure) is null)
        {
            // Settings was closed while the recording was being copied, or is asking something
            // else. The failure is said all the same.
            App.ShowMessageNotification($"The screen recording was not saved. {error}");
        }
    }

    /// <summary>
    /// Says a sentence through a screen reader, from the control it is about. Never fails: it
    /// is called from handlers that are <c>async void</c>.
    /// </summary>
    private static void TellScreenReader(FrameworkElement element, string message, string activityId)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(element)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent,
                message,
                activityId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"General settings could not tell a screen reader \"{message}\": {ex}");
        }
    }

    private async void OnDeleteStudioDraft(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StudioDraftItem draft })
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            Title = "Delete this draft?",
            Content = $"\"{draft.Name}\" and its recordings will be removed. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        // Not shown, or not answered with Delete: nothing is deleted.
        if (await TryShowAsync(confirmation) != ContentDialogResult.Primary || _closed)
        {
            return;
        }

        var index = ViewModel.StudioDrafts.IndexOf(draft);
        var error = await ViewModel.DeleteStudioDraftAsync(draft);
        if (error is null)
        {
            if (!_closed)
            {
                FocusAfterDraftRemoved(index);
            }

            return;
        }

        var failure = new ContentDialog
        {
            Title = "The draft was not deleted",
            Content = error,
            CloseButtonText = "OK",
        };
        if (await TryShowAsync(failure) is null)
        {
            App.ShowMessageNotification($"The draft was not deleted. {error}");
        }
    }

    /// <summary>
    /// The button that had the keyboard focus went away with its row. Focus goes to the row that
    /// took its place, to the last row when that was the last one, or to Clean up now when no
    /// draft is left, so that it is not lost.
    /// </summary>
    private void FocusAfterDraftRemoved(int index)
    {
        // The list has just been filled again. Its rows are there once it has been laid out.
        StudioDraftsRepeater.UpdateLayout();
        var count = ViewModel.StudioDrafts.Count;
        var row = count > 0 ? StudioDraftsRepeater.TryGetElement(Math.Clamp(index, 0, count - 1)) : null;
        Control target = FindFirstButton(row) ?? StudioCleanUpNowButton;
        target.Focus(FocusState.Programmatic);
    }

    private static Button? FindFirstButton(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            // A row does not show every button: one that cannot be opened has no Open.
            if (child is Button { Visibility: Visibility.Visible, IsEnabled: true } button)
            {
                return button;
            }

            if (FindFirstButton(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private async void OnPurgeTempFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Purge temporary files?",
            Content = $"This deletes {ViewModel.TempFolderSummary} from Tiny Clips' temporary folder. Your saved captures will not be affected.",
            PrimaryButtonText = "Purge",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var result = ViewModel.PurgeTempFiles();
            if (result.SkippedFileCount > 0)
            {
                var skippedDialog = new ContentDialog
                {
                    Title = "Some temporary files are still in use",
                    Content = $"{result.RemovedFileCount} temporary file(s) were removed. {result.SkippedFileCount} active or unavailable file(s) were kept.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot,
                };
                await skippedDialog.ShowAsync();
            }
        }
    }

    private async void OnResetAllSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset all settings to defaults?",
            Content = "This resets every TinyClips setting to its default value. This cannot be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ResetAllSettings();
        }
    }
}
