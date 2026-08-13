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
public sealed partial class GeneralSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

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
    }

    private void OnBrowseScreenshotSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Screenshot);

    private void OnBrowseVideoSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Video);

    private void OnBrowseGifSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(CaptureType.Gif);

    private void OnOpenTempFolder(object sender, RoutedEventArgs e) => ViewModel.OpenTempFolder();

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
