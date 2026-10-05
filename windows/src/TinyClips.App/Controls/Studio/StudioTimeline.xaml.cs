using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The bottom of the Studio window: play and pause, the two frame steps, the time, the buttons
/// that split the scene, add a zoom, start a cut and change the speed, and the two that trim at
/// the playhead; and under them the scene lane, the zoom lane, the cut lane, the speed lane and
/// the trim bar.
/// </summary>
public sealed partial class StudioTimeline : UserControl
{
    public StudioTimeline(StudioViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        SceneLaneHost.Child = new StudioSceneLane(viewModel);
        ZoomLaneHost.Child = new StudioZoomLane(viewModel);
        CutLaneHost.Child = new StudioCutLane(viewModel);
        SpeedLaneHost.Child = new StudioSpeedLane(viewModel);
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

    // The bar is given what the editor holds after every change, so a handle that was asked for
    // a place it did not get, as it can be once there are cuts, shows where it really is.
    private void UpdateTrimBar() =>
        TrimBar.Update(
            ViewModel.SourceDuration,
            ViewModel.TrimStart,
            ViewModel.TrimEnd,
            ViewModel.KeptSegments,
            ViewModel.Playhead,
            ViewModel.FrameDuration,
            ViewModel.TrimStep,
            ViewModel.TrimStartText,
            ViewModel.TrimEndText,
            ViewModel.PlayheadText);

    // A paused playhead goes on to where the new scene has been entered, and no scene can be
    // split again that close to its end. Split is then switched off by what it did. The focus
    // goes to the next button that adds something, and where none of them works back to Play
    // rather than on to the two buttons that trim.
    private void OnSplitSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.SplitSceneAtPlayhead();
        if (!ViewModel.CanSplitSceneAtPlayhead)
        {
            StudioFocus.Move(focus, AddZoomButton, AddCutButton, AddSpeedButton, PlayPauseButton);
        }
    }

    private void OnTrimGestureStarted(object? sender, EventArgs e) => ViewModel.BeginGesture();

    private void OnTrimGestureCompleted(object? sender, EventArgs e) => ViewModel.EndGesture();

    private void OnTrimStartRequested(object? sender, double sourceTime) => ViewModel.SetTrimStart(sourceTime);

    private void OnTrimEndRequested(object? sender, double sourceTime) => ViewModel.SetTrimEnd(sourceTime);

    private void OnScrubRequested(object? sender, double sourceTime) => ViewModel.Scrub(sourceTime);
}
