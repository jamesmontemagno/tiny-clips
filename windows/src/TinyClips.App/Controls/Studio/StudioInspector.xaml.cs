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
/// The right-hand side of the Studio window: scene, layout, background, screen, camera, zoom, cut
/// and extras, and the button that saves the look as the default. The scene, layout and camera
/// sections follow the scene the playhead is in, and the zoom and cut sections the selected zoom
/// or cut.
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

    private void OnPreviousSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.StepToPreviousScene();
        if (!ViewModel.CanShowPreviousScene)
        {
            StudioFocus.Move(focus, NextSceneButton, SceneSectionSplitButton);
        }
    }

    private void OnNextSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.StepToNextScene();
        if (!ViewModel.CanShowNextScene)
        {
            StudioFocus.Move(focus, PreviousSceneButton, SceneSectionSplitButton);
        }
    }

    // A paused playhead goes on to where the new scene has been entered, and no scene can be
    // split again that close to its end. The new scene is never the first, so Previous works.
    private void OnSplitSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.SplitSceneAtPlayhead();
        if (!ViewModel.CanSplitSceneAtPlayhead)
        {
            StudioFocus.Move(focus, PreviousSceneButton, NextSceneButton, SelectedLayoutChoice);
        }
    }

    // Delete scene stays while there is another scene to delete. With one scene left it is gone,
    // and what there is to do next is to split that scene, or to choose its layout.
    private void OnDeleteSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveCurrentScene();
        if (!ViewModel.IsDeleteSceneVisible)
        {
            StudioFocus.Move(focus, SceneSectionSplitButton, SelectedLayoutChoice);
        }
    }

    private void OnPreviousZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowPreviousZoom();
        if (!ViewModel.CanSelectPreviousZoom)
        {
            StudioFocus.Move(focus, NextZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnNextZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowNextZoom();
        if (!ViewModel.CanSelectNextZoom)
        {
            StudioFocus.Move(focus, PreviousZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnRemoveSuggestionsClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveZoomSuggestions();
        if (!ViewModel.HasSuggestedZooms)
        {
            StudioFocus.Move(focus, SuggestZoomsButton, ZoomSectionAddButton);
        }
    }

    private void OnDeleteZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveSelectedZoom();
        if (!ViewModel.HasSelectedZoom)
        {
            StudioFocus.Move(focus, NextZoomButton, PreviousZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnPreviousCutClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowPreviousCut();
        if (!ViewModel.CanSelectPreviousCut)
        {
            StudioFocus.Move(focus, NextCutButton, CutSectionAddButton);
        }
    }

    private void OnNextCutClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowNextCut();
        if (!ViewModel.CanSelectNextCut)
        {
            StudioFocus.Move(focus, PreviousCutButton, CutSectionAddButton);
        }
    }

    private void OnDeleteCutClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveSelectedCut();
        if (!ViewModel.HasSelectedCut)
        {
            StudioFocus.Move(focus, NextCutButton, PreviousCutButton, CutSectionAddButton);
        }
    }

    private void OnResetScreenCropClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ResetScreenCrop();
        if (!ViewModel.CanResetScreenCrop && focus != FocusState.Unfocused)
        {
            ScreenCropLeftRow.FocusSlider(focus);
        }
    }

    private void OnResetCameraCropClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ResetCameraCrop();
        if (!ViewModel.CanResetCameraCrop && focus != FocusState.Unfocused)
        {
            CameraCropLeftRow.FocusSlider(focus);
        }
    }

    /// <summary>The one of the four layout choices that is chosen, which is where the Tab key stops among them.</summary>
    private RadioButton SelectedLayoutChoice => ViewModel.LayoutIndex switch
    {
        (int)StudioLayout.Screen => LayoutScreenChoice,
        (int)StudioLayout.SideBySide => LayoutSideBySideChoice,
        (int)StudioLayout.Camera => LayoutCameraChoice,
        _ => LayoutBubbleChoice,
    };

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
