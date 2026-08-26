using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels.ClipsLibrary;

namespace TinyClips.App.Settings.Sections;

public sealed partial class ClipsLibrarySettingsSection : UserControl
{
    public ClipsLibrarySettingsViewModel ViewModel { get; }

    public ClipsLibrarySettingsSection(ClipsLibrarySettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnFirstLoaded;
    }

    private void OnFirstLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        // Let the realization-time TwoWay write-backs settle before re-enabling persistence.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ViewModel.CompleteRealization);
    }
}
