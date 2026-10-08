using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TinyClips.Core.Studio;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>Where the handles of a block on the zoom, cut or speed lane are.</summary>
internal enum StudioLaneHandlePlacement
{
    /// <summary>A narrow block that is not selected has none.</summary>
    None,

    /// <summary>Inside the ends of a block with room for them.</summary>
    Inside,

    /// <summary>Outside the ends of a narrow block that is selected.</summary>
    Outside,
}

/// <summary>One handle of a block. The pointer over it is the one for resizing from side to side.</summary>
public sealed partial class StudioLaneHandle : Grid
{
    public StudioLaneHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}

/// <summary>
/// The two handles of a block on the zoom, cut or speed lane. Where they are follows
/// <see cref="StudioEditorModel.LaneBlockHasInsideHandles"/> and
/// <see cref="StudioEditorModel.GetLaneHandleOutset"/>, which is also what the lane asks about a
/// press, so a handle is drawn where a press takes hold of an end. The handles are for a
/// pointer: the Start and End rows of the inspector do the same with the keyboard and for a
/// screen reader, which is not given these.
/// </summary>
public sealed partial class StudioLaneBlockHandles : UserControl
{
    public static readonly DependencyProperty GripBrushProperty = DependencyProperty.Register(
        nameof(GripBrush), typeof(Brush), typeof(StudioLaneBlockHandles), new PropertyMetadata(null, OnLookChanged));

    public static readonly DependencyProperty SelectedGripBrushProperty = DependencyProperty.Register(
        nameof(SelectedGripBrush), typeof(Brush), typeof(StudioLaneBlockHandles), new PropertyMetadata(null, OnLookChanged));

    public static readonly DependencyProperty TabBrushProperty = DependencyProperty.Register(
        nameof(TabBrush), typeof(Brush), typeof(StudioLaneBlockHandles), new PropertyMetadata(null, OnLookChanged));

    public static readonly DependencyProperty TabGripBrushProperty = DependencyProperty.Register(
        nameof(TabGripBrush), typeof(Brush), typeof(StudioLaneBlockHandles), new PropertyMetadata(null, OnLookChanged));

    public static readonly DependencyProperty PlateBrushProperty = DependencyProperty.Register(
        nameof(PlateBrush), typeof(Brush), typeof(StudioLaneBlockHandles), new PropertyMetadata(null, OnLookChanged));

    public static readonly DependencyProperty EndRadiusProperty = DependencyProperty.Register(
        nameof(EndRadius), typeof(double), typeof(StudioLaneBlockHandles), new PropertyMetadata(4.0, OnLookChanged));

    // A tab outside a block stops this short of the block, so the two are told apart.
    private const double TabGap = 1;

    private StudioLaneHandlePlacement _placement;
    private bool _isSelected;
    private bool _isPointerOver;

    public StudioLaneBlockHandles()
    {
        InitializeComponent();
        StartHandle.Width = StudioEditorModel.LaneHandleWidth;
        EndHandle.Width = StudioEditorModel.LaneHandleWidth;
        StartTab.Width = StudioEditorModel.LaneHandleWidth - TabGap;
        EndTab.Width = StudioEditorModel.LaneHandleWidth - TabGap;

        // A state gone to before the handles are in the window is not always shown.
        Loaded += (_, _) => ShowLook();
    }

    /// <summary>The color of a grip mark on a block that is not selected: the color of what is written on the block.</summary>
    public Brush? GripBrush
    {
        get => (Brush?)GetValue(GripBrushProperty);
        set => SetValue(GripBrushProperty, value);
    }

    /// <summary>The color of a grip mark on the selected block.</summary>
    public Brush? SelectedGripBrush
    {
        get => (Brush?)GetValue(SelectedGripBrushProperty);
        set => SetValue(SelectedGripBrushProperty, value);
    }

    /// <summary>What a handle outside the block is filled with: the selected block's own color.</summary>
    public Brush? TabBrush
    {
        get => (Brush?)GetValue(TabBrushProperty);
        set => SetValue(TabBrushProperty, value);
    }

    /// <summary>The color of a grip mark on a handle outside the block.</summary>
    public Brush? TabGripBrush
    {
        get => (Brush?)GetValue(TabGripBrushProperty);
        set => SetValue(TabGripBrushProperty, value);
    }

    /// <summary>What is under a handle inside the block, for a block that is not filled. Null for one that is.</summary>
    public Brush? PlateBrush
    {
        get => (Brush?)GetValue(PlateBrushProperty);
        set => SetValue(PlateBrushProperty, value);
    }

    /// <summary>How round the block's own ends are, which a handle's outer side follows.</summary>
    public double EndRadius
    {
        get => (double)GetValue(EndRadiusProperty);
        set => SetValue(EndRadiusProperty, value);
    }

    /// <summary>Where the handles are. For the block's own text, and for the check tool.</summary>
    internal StudioLaneHandlePlacement Placement => _placement;

    /// <summary>Whether the handles are drawn at their strongest, as on the selected block. For the check tool.</summary>
    internal bool IsStrong => _placement == StudioLaneHandlePlacement.Outside || (_placement == StudioLaneHandlePlacement.Inside && _isSelected);

    /// <summary>Whether the handles are drawn stronger than at rest because the pointer is over the block. For the check tool.</summary>
    internal bool IsRaised => _placement == StudioLaneHandlePlacement.Inside && !_isSelected && _isPointerOver;

    /// <summary>Shows the handles of a block of a width that is selected or not, with the pointer over it or not.</summary>
    internal void Show(double blockWidth, bool isSelected, bool isPointerOver)
    {
        _placement = StudioEditorModel.LaneBlockHasInsideHandles(blockWidth) ? StudioLaneHandlePlacement.Inside
            : StudioEditorModel.GetLaneHandleOutset(blockWidth, isSelected) > 0 ? StudioLaneHandlePlacement.Outside
            : StudioLaneHandlePlacement.None;
        _isSelected = isSelected;
        _isPointerOver = isPointerOver;
        ShowLook();
    }

    /// <summary>
    /// A handle's rectangle as it is laid out, in the coordinates of another element, or null
    /// while there is none. For the check tool, which sends no pointer and so cannot find a
    /// handle by moving over it.
    /// </summary>
    internal Rect? GetHandleBounds(bool isStart, UIElement relativeTo)
    {
        var handle = isStart ? StartHandle : EndHandle;
        return _placement == StudioLaneHandlePlacement.None || handle.ActualWidth <= 0
            ? null
            : handle.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, handle.ActualWidth, handle.ActualHeight));
    }

    private static void OnLookChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((StudioLaneBlockHandles)sender).ShowLook();

    private void ShowLook()
    {
        var isShown = _placement != StudioLaneHandlePlacement.None;
        var isOutside = _placement == StudioLaneHandlePlacement.Outside;
        var width = StudioEditorModel.LaneHandleWidth;
        StartHandle.Visibility = isShown ? Visibility.Visible : Visibility.Collapsed;
        EndHandle.Visibility = isShown ? Visibility.Visible : Visibility.Collapsed;

        // A handle outside the block is laid out past the block's end.
        StartHandle.Margin = new Thickness(isOutside ? -width : 0, 0, 0, 0);
        EndHandle.Margin = new Thickness(0, 0, isOutside ? -width : 0, 0);

        // Round on the side that is the block's own end, or the tab's outer side.
        var radius = Math.Max(0, EndRadius);
        var (startCorners, endCorners) = (new CornerRadius(radius, 0, 0, radius), new CornerRadius(0, radius, radius, 0));
        var grip = isOutside ? TabGripBrush : _isSelected ? SelectedGripBrush : GripBrush;
        Show(StartPlate, StartTint, StartTab, StartGrip, startCorners, isOutside, grip, isStart: true);
        Show(EndPlate, EndTint, EndTab, EndGrip, endCorners, isOutside, grip, isStart: false);

        var state = IsStrong ? "Selected" : IsRaised ? "PointerOver" : "AtRest";
        VisualStateManager.GoToState(this, state, false);
    }

    private void Show(Border plate, Border tint, Border tab, Rectangle gripMark, CornerRadius corners, bool isOutside, Brush? grip, bool isStart)
    {
        plate.Background = PlateBrush;
        plate.CornerRadius = corners;
        plate.Visibility = isOutside || PlateBrush is null ? Visibility.Collapsed : Visibility.Visible;
        tint.Background = grip;
        tint.CornerRadius = corners;
        tint.Visibility = isOutside ? Visibility.Collapsed : Visibility.Visible;
        tab.Background = TabBrush;
        tab.CornerRadius = corners;
        tab.Visibility = isOutside ? Visibility.Visible : Visibility.Collapsed;
        gripMark.Fill = grip;

        // On a tab the mark is in the middle of the tab, which is narrower than the handle by the gap.
        gripMark.Margin = !isOutside ? default : isStart ? new Thickness(0, 0, TabGap, 0) : new Thickness(TabGap, 0, 0, 0);
    }

    // A grip mark is half as high as the block, and never less than four.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = Math.Max(4, e.NewSize.Height / 2);
        StartGrip.Height = height;
        EndGrip.Height = height;
    }
}
