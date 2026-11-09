using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels;

namespace TinyClips.App.Settings.Sections;

/// <summary>Optional supporter plans. Only the Microsoft Store build offers this page.</summary>
public sealed partial class SupportSettingsSection : UserControl, ISettingsSectionLifecycle
{
    public SupportSettingsViewModel ViewModel { get; }

    public SupportSettingsSection(SupportSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _ = ViewModel.LoadAsync();
    }

    public void NotifyWindowClosed() => ViewModel.Detach();
}
