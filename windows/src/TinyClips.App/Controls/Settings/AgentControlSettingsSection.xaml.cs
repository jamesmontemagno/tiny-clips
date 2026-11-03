using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels;

namespace TinyClips.App.Settings.Sections;

public sealed partial class AgentControlSettingsSection : UserControl, ISettingsSectionLifecycle
{
    public AgentControlSettingsViewModel ViewModel { get; }

    public AgentControlSettingsSection(AgentControlSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public void NotifyWindowClosed() => ViewModel.Detach();
}
