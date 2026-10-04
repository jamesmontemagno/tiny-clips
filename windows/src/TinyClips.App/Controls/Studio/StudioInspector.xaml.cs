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
/// The right-hand side of the Studio window: layout, background, screen, camera and extras, and
/// the button that saves the look as the default. The camera section follows the layout.
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(StudioViewModel.BackgroundPresetId))
        {
            SyncSwatches();
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
