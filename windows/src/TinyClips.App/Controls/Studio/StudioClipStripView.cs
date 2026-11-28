using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The primary clip strip of the Studio window: the clips of the project in the order they play,
/// the joins between them, the menus that add a clip and act on the selected one, and, once there
/// are two clips or more, a slider for the playhead on the project's timeline. What is added is
/// asked of the window, which owns the pickers and the recorder.
/// </summary>
/// <remarks>
/// The lanes, the trim bar and the inspectors under the strip are in the source time of the
/// selected clip. The strip and its slider are in the time of the whole project.
/// </remarks>
public sealed partial class StudioClipStripView : UserControl
{
    private readonly StudioViewModel _viewModel;
    private readonly StudioClipTrack _track;
    private readonly DropDownButton _addButton;
    private readonly DropDownButton _clipButton;
    private readonly ProgressBar _progress;
    private readonly Slider _playhead;
    private readonly TextBlock _heading;
    private MenuFlyoutItem? _recordItem;
    private MenuFlyoutItem? _recordAtPlayheadItem;
    private bool _isUpdatingPlayhead;

    internal StudioClipStripView(StudioViewModel viewModel)
    {
        _viewModel = viewModel;
        _track = new StudioClipTrack(viewModel);
        _track.AddRequested += (_, request) => AddRequested?.Invoke(this, request);
        IsTabStop = false;

        _heading = new TextBlock
        {
            Text = "Clips",
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        AutomationProperties.SetHeadingLevel(_heading, AutomationHeadingLevel.Level2);

        _addButton = new DropDownButton
        {
            Content = "Add clip",
            Flyout = BuildAddMenu(),
        };
        AutomationProperties.SetName(_addButton, "Add clip");
        AutomationProperties.SetAutomationId(_addButton, "StudioAddClipButton");
        AutomationProperties.SetHelpText(_addButton, "Add a video to the end of the timeline, or at the playhead.");
        ToolTipService.SetToolTip(_addButton, "Add a video, or record another segment");

        var clipMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        clipMenu.Opening += (_, _) =>
        {
            if (_track.SelectedItem is { } item)
            {
                _track.FillClipMenu(clipMenu, item);
            }
        };
        _clipButton = new DropDownButton
        {
            Content = "Clip",
            Flyout = clipMenu,
        };
        AutomationProperties.SetName(_clipButton, "Clip");
        AutomationProperties.SetAutomationId(_clipButton, "StudioClipMenuButton");
        AutomationProperties.SetHelpText(_clipButton, "Split, move or remove the selected clip.");
        ToolTipService.SetToolTip(_clipButton, "Split, move or remove the selected clip");

        _progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_progress, "Adding clips");
        AutomationProperties.SetAutomationId(_progress, "StudioClipAddProgress");

        _playhead = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            SmallChange = 0.1,
            LargeChange = 1,
            Visibility = Visibility.Collapsed,
            ThumbToolTipValueConverter = SecondsConverter.Instance,
        };
        AutomationProperties.SetName(_playhead, "Project playhead");
        AutomationProperties.SetAutomationId(_playhead, "StudioProjectPlayheadSlider");
        AutomationProperties.SetHelpText(_playhead, "Move the playhead along the whole timeline, across clips.");
        _playhead.ValueChanged += OnPlayheadChanged;

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_addButton, 1);
        Grid.SetColumn(_clipButton, 2);
        header.Children.Add(_heading);
        header.Children.Add(_addButton);
        header.Children.Add(_clipButton);

        Content = new StackPanel
        {
            Spacing = 6,
            Children = { header, _progress, _track, _playhead },
        };

        Loaded += (_, _) =>
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Refresh();
        };
        Unloaded += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <summary>The window is asked to bring videos into the timeline: from a picker, the Library, a take or a drop.</summary>
    internal event EventHandler<StudioClipAddRequest>? AddRequested;

    /// <summary>Whether the window can record another segment now. It is asked each time the menu opens.</summary>
    internal Func<bool>? CanRecordSegment { get; set; }

    internal StudioClipTrack Track => _track;

    /// <summary>Puts the keyboard's focus on the selected clip.</summary>
    internal bool FocusSelectedClip() => _track.FocusSelected();

    private MenuFlyout BuildAddMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) => UpdateAddMenu();
        AddItems(menu.Items, atPlayhead: false);
        menu.Items.Add(new MenuFlyoutSeparator());
        var insert = new MenuFlyoutSubItem { Text = "Insert at playhead" };
        AutomationProperties.SetAutomationId(insert, "StudioClipInsertMenuItem");
        AddItems(insert.Items, atPlayhead: true);
        menu.Items.Add(insert);
        return menu;
    }

    private void AddItems(IList<MenuFlyoutItemBase> items, bool atPlayhead)
    {
        var prefix = atPlayhead ? "StudioClipInsert" : "StudioClipAdd";
        items.Add(CreateItem("Video file\u2026", prefix + "File", StudioClipAddSource.Files, atPlayhead));
        items.Add(CreateItem("From Clips Library\u2026", prefix + "Library", StudioClipAddSource.Library, atPlayhead));
        var record = CreateItem("Record another segment\u2026", prefix + "Record", StudioClipAddSource.Recording, atPlayhead);
        if (atPlayhead)
        {
            _recordAtPlayheadItem = record;
        }
        else
        {
            _recordItem = record;
        }

        items.Add(record);
    }

    private MenuFlyoutItem CreateItem(string text, string automationId, StudioClipAddSource source, bool atPlayhead)
    {
        var item = new MenuFlyoutItem { Text = text };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) =>
        {
            // The playhead is read when the item is chosen, not when the menu opened.
            var placement = atPlayhead
                ? StudioClipPlacement.AtProjectTime(_viewModel.ProjectPlayhead)
                : StudioClipPlacement.End;
            AddRequested?.Invoke(this, new StudioClipAddRequest(source, placement));
        };
        return item;
    }

    private void UpdateAddMenu()
    {
        var canRecord = CanRecordSegment?.Invoke() ?? false;
        if (_recordItem is not null)
        {
            _recordItem.IsEnabled = canRecord;
        }

        if (_recordAtPlayheadItem is not null)
        {
            _recordAtPlayheadItem.IsEnabled = canRecord;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null or "":
            case nameof(StudioViewModel.ClipStrip):
            case nameof(StudioViewModel.SelectedClip):
            case nameof(StudioViewModel.IsAddingClips):
                Refresh();
                break;
            case nameof(StudioViewModel.ProjectPlayhead):
            case nameof(StudioViewModel.PlayheadText):
                UpdatePlayhead();
                break;
        }
    }

    private void Refresh()
    {
        _track.Update();
        var canAdd = _viewModel.CanAddClips && !_viewModel.IsAddingClips;
        _addButton.IsEnabled = canAdd;
        _clipButton.IsEnabled = _viewModel.IsReady && _viewModel.CanEditClips;
        _progress.Visibility = _viewModel.IsAddingClips ? Visibility.Visible : Visibility.Collapsed;
        _playhead.Visibility = _viewModel.ClipStrip.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        if (_playhead.Visibility != Visibility.Visible)
        {
            return;
        }

        _isUpdatingPlayhead = true;
        try
        {
            _playhead.Maximum = Math.Max(_viewModel.ProjectDuration, _playhead.StepFrequency);
            _playhead.Value = Math.Clamp(_viewModel.ProjectPlayhead, 0, _playhead.Maximum);
        }
        finally
        {
            _isUpdatingPlayhead = false;
        }
    }

    private void OnPlayheadChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isUpdatingPlayhead)
        {
            _viewModel.ScrubProject(e.NewValue);
        }
    }

    private sealed class SecondsConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public static readonly SecondsConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language) =>
            StudioEditorModel.FormatTime(System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}
