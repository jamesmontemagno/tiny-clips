using System;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Settings.Sections;

/// <summary>Teleprompter overlay enable toggle, transcript text, and scroll speed.</summary>
public sealed partial class TeleprompterSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

    public SettingsViewModel ViewModel { get; }

    public TeleprompterSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }
}
