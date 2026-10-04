using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using Windows.System;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The zooms as blocks along the recording, in source time, lined up with the trim bar under it.
/// Pressing a block selects its zoom, dragging it moves the zoom, and dragging one of its ends
/// changes when the zoom starts or stops. Pressing or dragging on the empty lane moves the
/// playhead.
/// </summary>
/// <remarks>
/// <para>
/// The lane is one stop for the Tab key. With the focus on it, Left and Right select the zoom
/// before and after the selected one, and Home and End the first and the last. Delete is left to
/// the window, which removes the selected zoom. To screen readers the lane is a list and each
/// block an item of it.
/// </para>
/// <para>
/// What a pointer does is in <see cref="PressAt"/>, <see cref="DragTo"/> and
/// <see cref="EndPress"/>, which take a place along the lane. The pointer handlers only capture
/// the pointer and call them.
/// </para>
/// </remarks>
public sealed partial class StudioZoomLane : UserControl
{
    // A block stops this short of the top and the bottom of the lane.
    private const double BlockInset = 2;

    // A short zoom in a long recording still has to be wide enough to press.
    private const double MinimumBlockWidth = 10;

    // This much of each end of a block moves that end, on a block wide enough to have a middle left.
    private const double EndGripWidth = 6;
    private const double EndGripMinimumBlockWidth = 24;

    // A press that moves less than this is a press, and changes no zoom.
    private const double DragThreshold = 3;
    private const double DisabledOpacity = 0.4;

    private readonly StudioViewModel _viewModel;
    private readonly List<StudioZoomBlock> _blocks = [];
    private IReadOnlyList<StudioZoom>? _zooms;
    private double _duration;
    private Press? _press;
    private uint _pointerId;
    private bool _isPointerDown;

    public StudioZoomLane(StudioViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsEnabledChanged += OnIsEnabledChanged;
    }

    /// <summary>The part of a block a press landed on.</summary>
    private enum ZoomPart
    {
        Body,
        Start,
        End,
    }

    /// <summary>Whether a zoom is selected.</summary>
    internal bool HasSelection => _viewModel.HasSelectedZoom;

    /// <summary>The block of the selected zoom, or null.</summary>
    internal StudioZoomBlock? SelectedBlock =>
        _viewModel.SelectedZoomIndex is { } index && index < _blocks.Count ? _blocks[index] : null;

    /// <summary>
    /// How often the blocks have been laid out. For the check tool: a playhead that moves must
    /// not lay them out again.
    /// </summary>
    internal int BlockPlacements { get; private set; }

    /// <summary>
    /// Where the middle of the playhead line is along the lane. For the check tool: the line has
    /// to follow the playhead, and it is not among what a screen reader is given.
    /// </summary>
    internal double PlayheadLineX => Canvas.GetLeft(PlayheadLine) + (PlayheadLine.Width / 2);

    // What a pointer does

    /// <summary>
    /// A pointer went down at a place along the lane. On a block it selects the zoom, and on the
    /// empty lane it selects nothing; either way the playhead goes to the time that was pressed.
    /// </summary>
    internal void PressAt(double x)
    {
        if (_press is not null || !_viewModel.IsEditable)
        {
            return;
        }

        if (FindBlockAt(x) is { } block && _zooms is { } zooms && block.Index < zooms.Count)
        {
            var zoom = zooms[block.Index];
            _press = new Press(block.Index, GetPart(x - Canvas.GetLeft(block), block.Width), zoom.Start, zoom.End, x);
            _viewModel.SelectZoom(block.Index);
        }
        else
        {
            _press = new Press(null, ZoomPart.Body, 0, 0, x);
            _viewModel.SelectZoom(null);
        }

        _viewModel.Scrub(GetTime(x));
    }

    /// <summary>
    /// The pointer that went down moved to a place along the lane. From the empty lane it moves
    /// the playhead. From a block, once it has moved far enough to be a drag, it moves the zoom
    /// or the end it took hold of; everything until <see cref="EndPress"/> is one undo step.
    /// </summary>
    internal void DragTo(double x)
    {
        if (_press is not { } press)
        {
            return;
        }

        if (press.Index is not { } index)
        {
            _viewModel.Scrub(GetTime(x));
            return;
        }

        if (!press.HasMoved)
        {
            if (Math.Abs(x - press.X) < DragThreshold)
            {
                return;
            }

            press.HasMoved = true;
            _viewModel.BeginGesture();
        }

        // Measured from where the zoom was when the press began, so that the steps of a drag do
        // not add their rounding up.
        var seconds = (x - press.X) / StudioTimelineMetrics.GetUsableWidth(Root.ActualWidth) * _duration;
        var result = press.Part switch
        {
            ZoomPart.Start => _viewModel.DragZoomStart(index, press.Start + seconds),
            ZoomPart.End => _viewModel.DragZoomEnd(index, press.End + seconds),
            _ => _viewModel.MoveZoom(index, press.Start + seconds),
        };

        // The zooms are kept in time order, so an edit can leave the zoom at another place.
        if (result.Index is { } now)
        {
            press.Index = now;
        }
    }

    /// <summary>The pointer was let go, or taken away. Ends the undo step a drag began.</summary>
    internal void EndPress()
    {
        if (_press is not { } press)
        {
            return;
        }

        _press = null;
        if (press.HasMoved)
        {
            _viewModel.EndGesture();
        }
    }

    // What the keyboard does

    /// <summary>
    /// What a key does while the lane has the keyboard focus. False for a key that is not the
    /// lane's, which is then left for the window.
    /// </summary>
    internal bool HandleKey(VirtualKey key)
    {
        // In a right-to-left layout the lane runs the other way, and so do the two arrow keys.
        var isRightToLeft = FlowDirection == FlowDirection.RightToLeft;
        switch (key)
        {
            case VirtualKey.Left:
                _ = isRightToLeft ? _viewModel.SelectNextZoom() : _viewModel.SelectPreviousZoom();
                return true;
            case VirtualKey.Right:
                _ = isRightToLeft ? _viewModel.SelectPreviousZoom() : _viewModel.SelectNextZoom();
                return true;
            case VirtualKey.Home:
                _viewModel.SelectAndShowZoom(0);
                return true;
            case VirtualKey.End:
                _viewModel.SelectAndShowZoom(_viewModel.Zooms.Count - 1);
                return true;
            default:
                return false;
        }
    }

    // What a screen reader does

    /// <summary>Selects a zoom, or none. The playhead stays.</summary>
    internal void Select(int? index) => _viewModel.SelectZoom(index);

    /// <summary>Selects a zoom and moves the playhead to where it has moved in, as the arrow keys do.</summary>
    internal void SelectAndShow(int index) => _viewModel.SelectAndShowZoom(index);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && !e.KeyStatus.IsMenuKeyDown && HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioZoomLaneAutomationPeer(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Whatever changed while the lane was not listening.
        _zooms = null;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ReleasePointer();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            Refresh();
        }
        else if (e.PropertyName == nameof(StudioViewModel.SelectedZoomIndex))
        {
            ShowSelection();
        }
        else if (e.PropertyName == nameof(StudioViewModel.PlayheadText))
        {
            // Raised with the playhead, after it. Only the line moves: the blocks stay as they are.
            PlacePlayhead();
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PlaceBlocks();
        PlacePlayhead();
    }

    // The blocks keep their accent color, so while the editor is disabled they are dimmed instead.
    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Blocks.Opacity = IsEnabled ? 1 : DisabledOpacity;

    /// <summary>
    /// Shows the project as it is now. The blocks are laid out again only when a zoom or the
    /// length of the recording has changed: the list of zooms is the same object until one does.
    /// </summary>
    private void Refresh()
    {
        var zooms = _viewModel.Zooms;
        var duration = _viewModel.SourceDuration;
        if (!ReferenceEquals(zooms, _zooms) || duration != _duration)
        {
            _zooms = zooms;
            _duration = duration;
            while (_blocks.Count > zooms.Count)
            {
                Blocks.Children.RemoveAt(_blocks.Count - 1);
                _blocks.RemoveAt(_blocks.Count - 1);
            }

            while (_blocks.Count < zooms.Count)
            {
                var block = new StudioZoomBlock(this);
                _blocks.Add(block);
                Blocks.Children.Add(block);
            }

            // A list without items says why, also to a screen reader that lands on the list.
            EmptyText.Visibility = zooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(this, zooms.Count == 0 ? EmptyText.Text : string.Empty);
            PlaceBlocks();
        }
        else
        {
            ShowSelection();
        }

        PlacePlayhead();
    }

    private void PlaceBlocks()
    {
        var width = Root.ActualWidth;
        if (_zooms is not { } zooms || !(width > 0))
        {
            return;
        }

        BlockPlacements++;
        var height = Math.Max(0, Root.ActualHeight - (BlockInset * 2));
        var selected = _viewModel.SelectedZoomIndex;
        for (var index = 0; index < _blocks.Count && index < zooms.Count; index++)
        {
            var zoom = zooms[index];
            var start = StudioTimelineMetrics.GetX(zoom.Start, _duration, width);
            var end = StudioTimelineMetrics.GetX(zoom.End, _duration, width);

            // A block that had to be made wider stays centered on its zoom, and inside the lane.
            var drawn = Math.Max(MinimumBlockWidth, end - start);
            var left = Math.Min(Math.Max(((start + end) / 2) - (drawn / 2), 0), Math.Max(0, width - drawn));
            var block = _blocks[index];
            block.Width = drawn;
            block.Height = height;
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, BlockInset);
            block.Update(index, zoom, drawn, index == selected);
        }
    }

    private void ShowSelection()
    {
        var selected = _viewModel.SelectedZoomIndex;
        for (var index = 0; index < _blocks.Count; index++)
        {
            _blocks[index].SetSelected(index == selected);
        }
    }

    private void PlacePlayhead()
    {
        PlayheadLine.Height = Root.ActualHeight;
        Canvas.SetLeft(PlayheadLine, StudioTimelineMetrics.GetX(_viewModel.Playhead, _duration, Root.ActualWidth) - (PlayheadLine.Width / 2));
    }

    private double GetTime(double x) => StudioTimelineMetrics.GetTime(x, _duration, Root.ActualWidth);

    /// <summary>The block drawn at a place along the lane. Where two overlap, the later zoom's, which is drawn on top.</summary>
    private StudioZoomBlock? FindBlockAt(double x)
    {
        for (var index = _blocks.Count - 1; index >= 0; index--)
        {
            var block = _blocks[index];
            var left = Canvas.GetLeft(block);
            if (x >= left && x < left + block.Width)
            {
                return block;
            }
        }

        return null;
    }

    private static ZoomPart GetPart(double xInBlock, double blockWidth)
    {
        if (blockWidth < EndGripMinimumBlockWidth)
        {
            return ZoomPart.Body;
        }

        if (xInBlock <= EndGripWidth)
        {
            return ZoomPart.Start;
        }

        return xInBlock >= blockWidth - EndGripWidth ? ZoomPart.End : ZoomPart.Body;
    }

    // The pointer

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (_isPointerDown || !IsPrimaryPress(e, point) || !Root.CapturePointer(e.Pointer))
        {
            return;
        }

        _isPointerDown = true;
        _pointerId = e.Pointer.PointerId;

        // The arrow keys and Delete then act on the zoom that was pressed.
        Focus(FocusState.Pointer);
        PressAt(point.Position.X);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isPointerDown && e.Pointer.PointerId == _pointerId)
        {
            DragTo(e.GetCurrentPoint(Root).Position.X);
            e.Handled = true;
        }
    }

    // Letting go, losing the pointer and a cancelled press all end here.
    private void OnPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_isPointerDown && e.Pointer.PointerId == _pointerId)
        {
            ReleasePointer();
        }
    }

    private void ReleasePointer()
    {
        if (_isPointerDown)
        {
            _isPointerDown = false;
            Root.ReleasePointerCaptures();
        }

        EndPress();
    }

    private static bool IsPrimaryPress(PointerRoutedEventArgs e, PointerPoint point) =>
        e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed;

    /// <summary>A press on the lane, which becomes a drag once the pointer has moved.</summary>
    /// <param name="index">Where the pressed zoom is in the list, or null for a press on the empty lane.</param>
    /// <param name="part">The part of the block that was pressed.</param>
    /// <param name="start">The zoom's start when the press began.</param>
    /// <param name="end">The zoom's end when the press began.</param>
    /// <param name="x">Where along the lane the press began.</param>
    private sealed class Press(int? index, ZoomPart part, double start, double end, double x)
    {
        public int? Index { get; set; } = index;

        public ZoomPart Part { get; } = part;

        public double Start { get; } = start;

        public double End { get; } = end;

        public double X { get; } = x;

        public bool HasMoved { get; set; }
    }
}

/// <summary>
/// Makes the zoom lane a list to screen readers, with one item for each zoom and at most one of
/// them selected.
/// </summary>
public sealed partial class StudioZoomLaneAutomationPeer(StudioZoomLane owner)
    : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    private StudioZoomLane Lane => (StudioZoomLane)Owner;

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => false;

    public IRawElementProviderSimple[] GetSelection() =>
        Lane.SelectedBlock is { } block ? [ProviderFromPeer(CreatePeerForElement(block))] : [];

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => nameof(StudioZoomLane);

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
