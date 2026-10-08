using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;

namespace TinyClips.App.Settings.Sections;

/// <summary>
/// General settings: theme, save location, file naming, launch-at-login, and capture behavior
/// toggles.
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
    }

    public void NotifyWindowClosed() => _closed = true;

    private void OnBrowseScreenshotSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Screenshot);

    private void OnBrowseVideoSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Video);

    private void OnBrowseGifSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Gif);

    private void OnOpenTempFolder(object sender, RoutedEventArgs e) => ViewModel.OpenTempFolder();

    /// <summary>
    /// Shows one of this section's dialogs, and returns what was chosen. Returns null when it
    /// could not be shown: the section is no longer on a window, or another dialog is open in
    /// it. See <see cref="SettingsDialog"/> for why that has to be answered and not thrown.
    /// </summary>
    private Task<ContentDialogResult?> TryShowAsync(ContentDialog dialog) =>
        _closed ? Task.FromResult<ContentDialogResult?>(null) : SettingsDialog.TryShowAsync(dialog, XamlRoot);

    private async void OnPurgeTempFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Purge temporary files?",
            Content = $"This deletes {ViewModel.TempFolderSummary} from Tiny Clips' temporary folder. Your saved captures will not be affected.",
            PrimaryButtonText = "Purge",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        // Not shown, or not answered with Purge: nothing is deleted.
        if (await TryShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var result = ViewModel.PurgeTempFiles();
        if (result.SkippedFileCount > 0)
        {
            var kept = $"{result.RemovedFileCount} temporary file(s) were removed. {result.SkippedFileCount} active or unavailable file(s) were kept.";
            var skippedDialog = new ContentDialog
            {
                Title = "Some temporary files are still in use",
                Content = kept,
                CloseButtonText = "OK",
            };
            if (await TryShowAsync(skippedDialog) is null)
            {
                App.ShowMessageNotification($"Some temporary files are still in use. {kept}");
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
        };

        if (await TryShowAsync(dialog) == ContentDialogResult.Primary)
        {
            ViewModel.ResetAllSettings();
        }
    }
}
