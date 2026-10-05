using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.Studio;
using Windows.System;

namespace TinyClips.App.Controls.Studio;

/// <summary>The part of a block a press took hold of.</summary>
internal enum StudioLanePart
{
    Body,
    Start,
    End,
}

/// <summary>A press on a lane, which becomes a drag once the pointer has moved.</summary>
/// <param name="index">The scene, zoom, cut or speed change that was taken hold of, or null for a press that only moves the playhead.</param>
/// <param name="part">The part of the block that was pressed.</param>
/// <param name="start">When what was pressed started as the press began.</param>
/// <param name="end">When it ended as the press began.</param>
/// <param name="x">Where along the lane the press began.</param>
internal sealed class StudioLanePress(int? index, StudioLanePart part, double start, double end, double x)
{
    public int? Index { get; set; } = index;

    public StudioLanePart Part { get; } = part;

    public double Start { get; } = start;

    public double End { get; } = end;

    public double X { get; } = x;

    public bool HasMoved { get; set; }

    /// <summary>A press that took hold of nothing: it moves the playhead, and goes on moving it while it is dragged.</summary>
    public static StudioLanePress OnTheLane(double x) => new(null, StudioLanePart.Body, 0, 0, x);
}

/// <summary>
/// One lane of the Studio timeline: blocks along the recording, in source time, lined up with the
/// trim bar under the lanes, and a line where the playhead is. <see cref="StudioSceneLane"/>,
/// <see cref="StudioZoomLane"/>, <see cref="StudioCutLane"/> and <see cref="StudioSpeedLane"/>
/// say what the blocks stand for and what a press, a drag and the keys do to them. What the four
/// have in common is here.
/// </summary>
/// <remarks>
/// <para>
/// A lane is one stop for the Tab key. With the focus on it, Left and Right go to the block before
/// and after the marked one, and Home and End to the first and the last. Delete is left to the
/// window. To screen readers the lane is a list and each block an item of it, and the marked
/// block is the selected item: the selected zoom, cut or speed change, or the scene the playhead
/// is in.
/// </para>
/// <para>
/// What a pointer does is in <see cref="PressAt"/>, <see cref="DragTo"/> and
/// <see cref="EndPress"/>, which take a place along the lane. The pointer handlers only capture
/// the pointer and call them.
/// </para>
/// </remarks>
public abstract partial class StudioLane : UserControl
{
    // A block stops this short of the top and the bottom of the lane.
    private const double BlockInset = 2;

    // A press that moves less than this is a press, and changes nothing but the playhead.
    private const double DragThreshold = 3;
    private const double DisabledOpacity = 0.4;

    private readonly List<StudioLaneBlock> _blocks = [];
    private object? _items;
    private double _duration;
    private StudioLanePress? _press;
    private uint _pointerId;
    private bool _isPointerDown;

    /// <param name="automationId">The lane's automation id. The text of an empty lane gets the same with "EmptyText" after it.</param>
    /// <param name="name">What the list is called to a screen reader.</param>
    /// <param name="emptyText">What the lane says while it has no blocks, or an empty text for a lane that always has one.</param>
    protected StudioLane(StudioViewModel viewModel, string automationId, string name, string emptyText, double height)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Height = height;
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetName(this, name);
        EmptyText.Text = emptyText;
        AutomationProperties.SetAutomationId(EmptyText, automationId + "EmptyText");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsEnabledChanged += OnIsEnabledChanged;
    }

    /// <summary>Whether a block is marked.</summary>
    internal bool HasSelection => SelectedIndex is not null;

    /// <summary>The marked block, or null.</summary>
    internal StudioLaneBlock? SelectedBlock =>
        SelectedIndex is { } index && index >= 0 && index < _blocks.Count ? _blocks[index] : null;

    /// <summary>Whether one block is always marked, as the scene the playhead is in is.</summary>
    internal virtual bool IsSelectionRequired => false;

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

    protected StudioViewModel ViewModel { get; }

    /// <summary>The length of the recording the blocks were last laid out for.</summary>
    protected double Duration => _duration;

    // What the lane's blocks stand for

    /// <summary>
    /// The view model's list of what the blocks stand for. It is the same object for as long as
    /// none of them changes, so the lane can tell by reference that there is nothing to lay out.
    /// </summary>
    protected abstract object Items { get; }

    protected abstract int ItemCount { get; }

    /// <summary>The marked item's place in the list: the selected zoom, cut or speed change, or the scene the playhead is in.</summary>
    protected abstract int? SelectedIndex { get; }

    /// <summary>The view model's property that says another item is the marked one.</summary>
    protected abstract string SelectedIndexPropertyName { get; }

    /// <summary>When an item starts and ends, in source time.</summary>
    protected abstract (double Start, double End) GetRange(int index);

    protected abstract StudioLaneBlock CreateBlock();

    /// <summary>Shows an item in one of the lane's own blocks, which is drawn at a width.</summary>
    protected abstract void ShowItem(StudioLaneBlock block, int index, double width, bool isSelected);

    /// <summary>
    /// What a press at a place along the lane takes hold of. It also selects what a press there
    /// selects. The playhead is moved by the lane.
    /// </summary>
    private protected abstract StudioLanePress TakeHold(double x);

    /// <summary>
    /// Moves what a press took hold of by a number of seconds from where it was when the press
    /// began. Returns the item's place in the list afterwards, or null when it has none.
    /// </summary>
    private protected abstract int? Drag(StudioLanePress press, double seconds);

    /// <summary>Goes to the item before the marked one, as the Left key does.</summary>
    protected abstract bool ShowPrevious();

    /// <summary>Goes to the item after the marked one, as the Right key does.</summary>
    protected abstract bool ShowNext();

    // What a screen reader does

    /// <summary>Selects an item, or none where the lane can do without.</summary>
    internal abstract void Select(int? index);

    /// <summary>Selects an item and moves the playhead to where it shows, as the arrow keys do.</summary>
    internal abstract bool SelectAndShow(int index);

    // What a pointer does

    /// <summary>
    /// A pointer went down at a place along the lane. The playhead goes to the time that was
    /// pressed, whatever else the press takes hold of.
    /// </summary>
    internal void PressAt(double x)
    {
        if (_press is not null || !ViewModel.IsEditable)
        {
            return;
        }

        _press = TakeHold(x);
        ViewModel.Scrub(GetTime(x));
    }

    /// <summary>
    /// The pointer that went down moved to a place along the lane. A press that took hold of
    /// nothing moves the playhead. Otherwise, once it has moved far enough to be a drag, it moves
    /// what it took hold of; everything until <see cref="EndPress"/> is one undo step.
    /// </summary>
    internal void DragTo(double x)
    {
        if (_press is not { } press)
        {
            return;
        }

        if (press.Index is null)
        {
            ViewModel.Scrub(GetTime(x));
            return;
        }

        if (!press.HasMoved)
        {
            if (Math.Abs(x - press.X) < DragThreshold)
            {
                return;
            }

            press.HasMoved = true;
            ViewModel.BeginGesture();
        }

        // Measured from where the item was when the press began, so that the steps of a drag do
        // not add their rounding up.
        var seconds = (x - press.X) / StudioTimelineMetrics.GetUsableWidth(Root.ActualWidth) * _duration;

        // The items are kept in time order, so an edit can leave one at another place in the list.
        if (Drag(press, seconds) is { } now)
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
            ViewModel.EndGesture();
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
                _ = isRightToLeft ? ShowNext() : ShowPrevious();
                return true;
            case VirtualKey.Right:
                _ = isRightToLeft ? ShowPrevious() : ShowNext();
                return true;
            case VirtualKey.Home:
                SelectAndShow(0);
                return true;
            case VirtualKey.End:
                SelectAndShow(ItemCount - 1);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The block drawn at a place along the lane. Where two overlap, the later one, which is drawn on top.</summary>
    protected StudioLaneBlock? FindBlockAt(double x)
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

    /// <summary>Where a source time is along the lane.</summary>
    protected double GetX(double sourceTime) => StudioTimelineMetrics.GetX(sourceTime, _duration, Root.ActualWidth);

    /// <summary>The source time at a place along the lane.</summary>
    protected double GetTime(double x) => StudioTimelineMetrics.GetTime(x, _duration, Root.ActualWidth);

    /// <summary>
    /// Where a block is drawn for an item that starts and ends at two places along the lane. A
    /// block that has to be wider than its item stays centered on it, and inside the lane.
    /// </summary>
    protected virtual (double Left, double Width) PlaceBlock(double startX, double endX, double laneWidth)
    {
        var drawn = Math.Max(MinimumBlockWidth, endX - startX);
        var left = Math.Min(Math.Max(((startX + endX) / 2) - (drawn / 2), 0), Math.Max(0, laneWidth - drawn));
        return (left, drawn);
    }

    /// <summary>A short item in a long recording still has to be wide enough to see and to press.</summary>
    protected virtual double MinimumBlockWidth => 10;

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && !e.KeyStatus.IsMenuKeyDown && HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioLaneAutomationPeer(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Whatever changed while the lane was not listening.
        _items = null;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ReleasePointer();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            Refresh();
        }
        else if (e.PropertyName == SelectedIndexPropertyName)
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

    // The blocks keep their own colors, so while the editor is disabled they are dimmed instead.
    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Blocks.Opacity = IsEnabled ? 1 : DisabledOpacity;

    /// <summary>
    /// Shows the project as it is now. The blocks are laid out again only when an item or the
    /// length of the recording has changed: the list of items is the same object until one does.
    /// </summary>
    private void Refresh()
    {
        var items = Items;
        var duration = ViewModel.SourceDuration;
        if (!ReferenceEquals(items, _items) || duration != _duration)
        {
            _items = items;
            _duration = duration;
            var count = ItemCount;
            while (_blocks.Count > count)
            {
                Blocks.Children.RemoveAt(_blocks.Count - 1);
                _blocks.RemoveAt(_blocks.Count - 1);
            }

            while (_blocks.Count < count)
            {
                var block = CreateBlock();
                _blocks.Add(block);
                Blocks.Children.Add(block);
            }

            // A list without items says why, also to a screen reader that lands on the list.
            var isEmpty = count == 0 && EmptyText.Text.Length > 0;
            EmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(this, isEmpty ? EmptyText.Text : string.Empty);
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
        if (_items is null || !(width > 0))
        {
            return;
        }

        BlockPlacements++;
        var height = Math.Max(0, Root.ActualHeight - (BlockInset * 2));
        var selected = SelectedIndex;
        var count = Math.Min(_blocks.Count, ItemCount);
        for (var index = 0; index < count; index++)
        {
            var (start, end) = GetRange(index);
            var (left, drawn) = PlaceBlock(GetX(start), GetX(end), width);
            var block = _blocks[index];
            block.Width = drawn;
            block.Height = height;
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, BlockInset);
            ShowItem(block, index, drawn, index == selected);
        }
    }

    private void ShowSelection()
    {
        var selected = SelectedIndex;
        for (var index = 0; index < _blocks.Count; index++)
        {
            _blocks[index].SetSelected(index == selected);
        }
    }

    private void PlacePlayhead()
    {
        PlayheadLine.Height = Root.ActualHeight;
        Canvas.SetLeft(PlayheadLine, StudioTimelineMetrics.GetX(ViewModel.Playhead, _duration, Root.ActualWidth) - (PlayheadLine.Width / 2));
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

        // The arrow keys and Delete then act on what was pressed.
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
}

/// <summary>
/// Makes a lane a list to screen readers, with one item for each block and at most one of them
/// selected.
/// </summary>
public sealed partial class StudioLaneAutomationPeer(StudioLane owner)
    : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    private StudioLane Lane => (StudioLane)Owner;

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => Lane.IsSelectionRequired;

    public IRawElementProviderSimple[] GetSelection() =>
        Lane.SelectedBlock is { } block ? [ProviderFromPeer(CreatePeerForElement(block))] : [];

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    // The lane's own class: StudioSceneLane, StudioZoomLane, StudioCutLane or StudioSpeedLane.
    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
