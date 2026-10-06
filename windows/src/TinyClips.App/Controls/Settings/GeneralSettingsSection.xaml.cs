using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Models;

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
        _realizationScope = viewModel.BeginSectionRealization();
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
    /// Saves a row's screen recording as an ordinary video. From there it is a saved video like
    /// any other, and the app announces it as one. A failure is said in a dialog.
    /// </summary>
    private async void OnSaveStudioDraftRecording(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StudioDraftItem draft })
        {
            return;
        }

        var (path, error) = await ViewModel.SaveStudioScreenRecordingAsync(draft);
        if (path is not null)
        {
            // Also when Settings was closed while the recording was being copied: the video is there.
            (Application.Current as App)?.AnnounceStudioVideoSaved(path);
            return;
        }

        if (error is null || _closed)
        {
            return;
        }

        var failure = new ContentDialog
        {
            Title = "The screen recording was not saved",
            Content = error,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await failure.ShowAsync();
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
            XamlRoot = XamlRoot,
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary || _closed)
        {
            return;
        }

        var index = ViewModel.StudioDrafts.IndexOf(draft);
        var error = await ViewModel.DeleteStudioDraftAsync(draft);
        if (_closed)
        {
            return;
        }

        if (error is null)
        {
            FocusAfterDraftRemoved(index);
            return;
        }

        var failure = new ContentDialog
        {
            Title = "The draft was not deleted",
            Content = error,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await failure.ShowAsync();
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
