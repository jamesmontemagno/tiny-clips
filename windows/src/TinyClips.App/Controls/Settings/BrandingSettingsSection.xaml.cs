using System;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Settings.Sections;

/// <summary>The single branding-overlay watermark toggle.</summary>
public sealed partial class BrandingSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

    public SettingsViewModel ViewModel { get; }

    public BrandingSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }
}
