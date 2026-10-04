using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The bottom of the Studio window: play and pause, the two frame steps, the time, the two buttons
/// that trim at the playhead, and under them the trim bar.
/// </summary>
public sealed partial class StudioTimeline : UserControl
{
    public StudioTimeline(StudioViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public StudioViewModel ViewModel { get; }

    /// <summary>Puts keyboard focus on Play, where Space and the arrow keys are closest to hand.</summary>
    public bool FocusPlayButton() => PlayPauseButton.Focus(FocusState.Programmatic);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTrimBar();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            UpdateTrimBar();
        }
        else if (e.PropertyName == nameof(StudioViewModel.PlayheadText))
        {
            // Raised with the playhead, after it: both are current by now.
            TrimBar.UpdatePlayhead(ViewModel.Playhead, ViewModel.PlayheadText);
        }
    }

    private void UpdateTrimBar() =>
        TrimBar.Update(
            ViewModel.SourceDuration,
            ViewModel.TrimStart,
            ViewModel.TrimEnd,
            ViewModel.Playhead,
            ViewModel.FrameDuration,
            ViewModel.TrimStep,
            ViewModel.TrimStartText,
            ViewModel.TrimEndText,
            ViewModel.PlayheadText);

    private void OnTrimGestureStarted(object? sender, EventArgs e) => ViewModel.BeginGesture();

    private void OnTrimGestureCompleted(object? sender, EventArgs e) => ViewModel.EndGesture();

    private void OnTrimStartRequested(object? sender, double sourceTime) => ViewModel.SetTrimStart(sourceTime);

    private void OnTrimEndRequested(object? sender, double sourceTime) => ViewModel.SetTrimEnd(sourceTime);

    private void OnScrubRequested(object? sender, double sourceTime) => ViewModel.Scrub(sourceTime);
}
