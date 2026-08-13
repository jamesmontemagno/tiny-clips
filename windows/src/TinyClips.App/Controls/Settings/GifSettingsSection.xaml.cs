using System;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Settings.Sections;

/// <summary>GIF frame rate, size, countdown, and output settings.</summary>
public sealed partial class GifSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

    public SettingsViewModel ViewModel { get; }

    public GifSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }
}
