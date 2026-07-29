using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    public event EventHandler? BrowseSaveDirectoryRequested;

    public GeneralSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }

    private void OnBrowseSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseSaveDirectoryRequested?.Invoke(this, EventArgs.Empty);

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
