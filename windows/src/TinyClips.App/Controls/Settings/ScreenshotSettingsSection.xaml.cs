using System;
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
}
