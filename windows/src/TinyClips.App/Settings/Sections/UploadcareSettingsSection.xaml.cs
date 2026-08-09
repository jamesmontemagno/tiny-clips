using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Settings.Sections;

public sealed partial class UploadcareSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

    public SettingsViewModel ViewModel { get; }

    public UploadcareSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }

    private void OnSaveSecretKey(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SecretKeyBox.Password))
        {
            return;
        }

        ViewModel.SaveUploadcareSecretKey(SecretKeyBox.Password);
        SecretKeyBox.Password = string.Empty;
    }

    private void OnClearSecretKey(object sender, RoutedEventArgs e) => ViewModel.ClearUploadcareSecretKey();
}
