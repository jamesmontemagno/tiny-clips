using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Models.Studio;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The right-hand side of the Studio window: layout, background, screen, camera, zoom and extras,
/// and the button that saves the look as the default. The camera section follows the layout, and
/// the zoom section the selected zoom.
/// </summary>
public sealed partial class StudioInspector : UserControl
{
    private bool _isSyncingSwatches;

    public StudioInspector(StudioViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // The names come from the editor model, so the lists and what screen readers hear agree.
        foreach (var shape in Enum.GetValues<StudioCameraShape>())
        {
            ShapeChoice.Items.Add(StudioEditorModel.GetShapeName(shape));
        }

        foreach (var anchor in Enum.GetValues<StudioAnchor>())
        {
            PositionChoice.Items.Add(StudioEditorModel.GetAnchorName(anchor));
        }

        AddSwatches(SolidSwatches, StudioSwatch.Solid);
        AddSwatches(GradientSwatches, StudioSwatch.Gradient);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public StudioViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncSwatches();
        FocusPad.Update(ViewModel.ZoomPad);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(StudioViewModel.BackgroundPresetId))
        {
            SyncSwatches();
        }

        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(StudioViewModel.ZoomPad))
        {
            FocusPad.Update(ViewModel.ZoomPad);
        }
    }

    private void OnFocusPadRequested(object? sender, StudioFocusPadPoint e) => ViewModel.SetZoomFocusOnPad(e.X, e.Y);

    // A button that its own action switches off, or hides, would leave the keyboard focus to
    // whatever comes next in the window. Each of these moves it to where a person would go on
    // from instead. A button that was pressed without having the focus leaves the focus alone.

    private void OnPreviousZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = FocusStateOf(sender);
        ViewModel.ShowPreviousZoom();
        if (!ViewModel.CanSelectPreviousZoom)
        {
            MoveFocus(focus, NextZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnNextZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = FocusStateOf(sender);
        ViewModel.ShowNextZoom();
        if (!ViewModel.CanSelectNextZoom)
        {
            MoveFocus(focus, PreviousZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnRemoveSuggestionsClick(object sender, RoutedEventArgs e)
    {
        var focus = FocusStateOf(sender);
        ViewModel.RemoveZoomSuggestions();
        if (!ViewModel.HasSuggestedZooms)
        {
            MoveFocus(focus, SuggestZoomsButton, ZoomSectionAddButton);
        }
    }

    private void OnDeleteZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = FocusStateOf(sender);
        ViewModel.RemoveSelectedZoom();
        if (!ViewModel.HasSelectedZoom)
        {
            MoveFocus(focus, NextZoomButton, PreviousZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnResetScreenCropClick(object sender, RoutedEventArgs e)
    {
        var focus = FocusStateOf(sender);
        ViewModel.ResetScreenCrop();
        if (!ViewModel.CanResetScreenCrop && focus != FocusState.Unfocused)
        {
            ScreenCropLeftRow.FocusSlider(focus);
        }
    }

    private void OnResetCameraCropClick(object sender, RoutedEventArgs e)
    {
        var focus = FocusStateOf(sender);
        ViewModel.ResetCameraCrop();
        if (!ViewModel.CanResetCameraCrop && focus != FocusState.Unfocused)
        {
            CameraCropLeftRow.FocusSlider(focus);
        }
    }

    private static FocusState FocusStateOf(object sender) =>
        sender is Control control ? control.FocusState : FocusState.Unfocused;

    /// <summary>Gives the focus to the first of the controls that can take it, the way the pressed button had it.</summary>
    private static void MoveFocus(FocusState state, params Control[] candidates)
    {
        if (state == FocusState.Unfocused)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.IsEnabled && candidate.Focus(state))
            {
                return;
            }
        }
    }

    private void AddSwatches(GridView grid, IReadOnlyList<StudioSwatch> swatches)
    {
        var itemStyle = (Style)Resources["StudioSwatchItemStyle"];
        var fillStyle = (Style)Resources["StudioSwatchFillStyle"];
        foreach (var swatch in swatches)
        {
            var item = new GridViewItem
            {
                Style = itemStyle,
                Tag = swatch,
                Content = new Border { Style = fillStyle, Background = CreateFill(swatch) },
            };
            AutomationProperties.SetName(item, swatch.AccessibleName);
            AutomationProperties.SetAutomationId(item, $"StudioSwatch_{swatch.Id}");
            ToolTipService.SetToolTip(item, swatch.Label);
            grid.Items.Add(item);
        }
    }

    private static Brush CreateFill(StudioSwatch swatch) =>
        swatch.Secondary is { } secondary
            ? new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = swatch.Primary, Offset = 0 },
                    new GradientStop { Color = secondary, Offset = 1 },
                },
            }
            : new SolidColorBrush(swatch.Primary);

    private void OnSwatchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSwatches)
        {
            return;
        }

        if (e.AddedItems.Count > 0 && e.AddedItems[0] is GridViewItem { Tag: StudioSwatch swatch })
        {
            ViewModel.SelectSwatch(swatch);
        }

        // The two lists share one choice, and taking the selection off a swatch chooses nothing.
        SyncSwatches();
    }

    private void SyncSwatches()
    {
        _isSyncingSwatches = true;
        try
        {
            var presetId = ViewModel.BackgroundPresetId;
            Select(SolidSwatches, presetId);
            Select(GradientSwatches, presetId);
        }
        finally
        {
            _isSyncingSwatches = false;
        }
    }

    private static void Select(GridView grid, string? presetId)
    {
        GridViewItem? selected = null;
        foreach (var candidate in grid.Items)
        {
            if (candidate is GridViewItem { Tag: StudioSwatch swatch } item
                && string.Equals(swatch.Id, presetId, StringComparison.Ordinal))
            {
                selected = item;
                break;
            }
        }

        if (!ReferenceEquals(grid.SelectedItem, selected))
        {
            grid.SelectedItem = selected;
        }
    }
}
