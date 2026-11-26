using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Settings.Sections;

/// <summary>Screenshot format, quality, countdown, and editor settings.</summary>
public sealed partial class ScreenshotSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

    public SettingsViewModel ViewModel { get; }

    public ScreenshotSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization(SettingsSectionKind.Screenshot);
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }

    private void OnScreenshotOutputToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        var requested = toggle.IsOn;
        bool actual;
        if (ReferenceEquals(toggle, SaveScreenshotToDiskToggle))
        {
            ViewModel.SaveScreenshotToDisk = requested;
            actual = ViewModel.SaveScreenshotToDisk;
        }
        else if (ReferenceEquals(toggle, CopyScreenshotToggle))
        {
            ViewModel.CopyScreenshotToClipboard = requested;
            actual = ViewModel.CopyScreenshotToClipboard;
        }
        else if (ReferenceEquals(toggle, ShowScreenshotEditorToggle))
        {
            ViewModel.ShowScreenshotEditor = requested;
            actual = ViewModel.ShowScreenshotEditor;
        }
        else
        {
            return;
        }

        ScreenshotOutputToggleFeedback.Reconcile(toggle, requested, actual, ScreenshotOutputStatus);
    }
}
