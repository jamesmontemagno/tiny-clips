using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace TinyClips.App;

/// <summary>
/// A single-line trim control modeled on the macOS app: a dimmed track with an accent-colored
/// selected region between two draggable handles plus a movable playhead. All values are
/// normalized fractions in the range [0, 1]; the hosting window maps them to seconds or frames.
/// </summary>
/// <remarks>
/// The control raises <see cref="StartFractionChanged"/>, <see cref="EndFractionChanged"/> and
/// <see cref="SeekRequested"/> only in response to direct user input. Assigning the matching
/// properties (e.g. from the host while clamping) re-lays out without re-raising events, so there
/// is no feedback loop. User input supports both pointer gestures and keyboard commands.
/// </remarks>
public sealed partial class TrimBar : UserControl
{
    private enum DragMode
    {
        None,
        Start,
        End,
        Range,
        Seek,
    }

    private const double HandleWidth = 14.0;
    private const double TrackHeight = 36.0;
    private const double HandleGrab = 12.0;
    private const double KeyboardRangeStep = 0.1;

    private static readonly InputSystemCursor SizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputSystemCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputSystemCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    private DragMode _drag = DragMode.None;
    private double _start;
    private double _end = 1.0;
    private double _play;
    private string _automationName = string.Empty;

    // Anchors captured at the start of a range drag so the selection moves rigidly with the cursor.
    private double _rangeGrabFraction;
    private double _rangeStartAtGrab;
    private double _rangeEndAtGrab;

    public TrimBar()
    {
        InitializeComponent();

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerExited += OnPointerExited;
        KeyDown += OnKeyDown;
        SizeChanged += (_, _) => LayoutParts();
        UpdateAutomationName();
    }

    /// <summary>Raised when the user changes the start handle. Carries the new start fraction.</summary>
    public event EventHandler<double>? StartFractionChanged;

    /// <summary>Raised when the user changes the end handle. Carries the new end fraction.</summary>
    public event EventHandler<double>? EndFractionChanged;

    /// <summary>Raised when the user requests a playhead position. Carries the requested fraction.</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>
    /// Raised when the user moves the selected region as a whole. Carries the new start and end
    /// fractions (the selection width is preserved).
    /// </summary>
    public event EventHandler<(double Start, double End)>? RangeChanged;

    /// <summary>
    /// Gets or sets the fraction changed by each arrow-key press. Hosts set this to their
    /// smallest meaningful unit, such as one frame for GIFs.
    /// </summary>
    public double KeyboardStep { get; set; } = 0.01;

    public double StartFraction
    {
        get => _start;
        set
        {
            _start = Clamp01(value);
            LayoutParts();
            UpdateAutomationName();
        }
    }

    public double EndFraction
    {
        get => _end;
        set
        {
            _end = Clamp01(value);
            LayoutParts();
            UpdateAutomationName();
        }
    }

    public double PlayheadFraction
    {
        get => _play;
        set
        {
            _play = Clamp01(value);
            LayoutParts();
            UpdateAutomationName();
        }
    }

    private double Usable => Math.Max(1.0, ActualWidth - HandleWidth);

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private void LayoutParts()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var half = HandleWidth / 2.0;
        var top = (h - TrackHeight) / 2.0;
        var usable = Math.Max(1.0, w - HandleWidth);
        double X(double f) => half + f * usable;

        var sx = X(_start);
        var ex = X(_end);
        var px = X(_play);

        TrackBg.Width = w;
        TrackBg.Height = TrackHeight;
        Canvas.SetLeft(TrackBg, 0);
        Canvas.SetTop(TrackBg, top);

        ActiveRegion.Width = Math.Max(0, ex - sx);
        ActiveRegion.Height = TrackHeight;
        Canvas.SetLeft(ActiveRegion, sx);
        Canvas.SetTop(ActiveRegion, top);

        Playhead.Height = TrackHeight + 8;
        Canvas.SetLeft(Playhead, px - 1);
        Canvas.SetTop(Playhead, top - 4);

        StartHandle.Width = HandleWidth;
        StartHandle.Height = TrackHeight;
        Canvas.SetLeft(StartHandle, sx - half);
        Canvas.SetTop(StartHandle, top);

        EndHandle.Width = HandleWidth;
        EndHandle.Height = TrackHeight;
        Canvas.SetLeft(EndHandle, ex - half);
        Canvas.SetTop(EndHandle, top);
    }

    private double FractionFromX(double x) => Clamp01((x - HandleWidth / 2.0) / Usable);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);

        var x = e.GetCurrentPoint(this).Position.X;
        var half = HandleWidth / 2.0;
        var usable = Usable;
        var sx = half + _start * usable;
        var ex = half + _end * usable;
        var dStart = Math.Abs(x - sx);
        var dEnd = Math.Abs(x - ex);
        var fraction = FractionFromX(x);

        if (dStart <= HandleGrab && dStart <= dEnd)
        {
            _drag = DragMode.Start;
            ApplyDrag(fraction);
        }
        else if (dEnd <= HandleGrab)
        {
            _drag = DragMode.End;
            ApplyDrag(fraction);
        }
        else if (x > sx && x < ex)
        {
            // Press inside the selected region: drag the whole selection, preserving its width.
            _drag = DragMode.Range;
            _rangeGrabFraction = fraction;
            _rangeStartAtGrab = _start;
            _rangeEndAtGrab = _end;
        }
        else
        {
            _drag = DragMode.Seek;
            ApplyDrag(fraction);
        }

        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(this).Position.X;
        if (_drag == DragMode.None)
        {
            UpdateHoverCursor(x);
            return;
        }

        ApplyDrag(FractionFromX(x));
        e.Handled = true;
    }

    private void ApplyDrag(double fraction)
    {
        switch (_drag)
        {
            case DragMode.Start:
                StartFractionChanged?.Invoke(this, fraction);
                break;
            case DragMode.End:
                EndFractionChanged?.Invoke(this, fraction);
                break;
            case DragMode.Range:
                var width = _rangeEndAtGrab - _rangeStartAtGrab;
                var delta = fraction - _rangeGrabFraction;
                var newStart = Math.Clamp(_rangeStartAtGrab + delta, 0.0, 1.0 - width);
                var newEnd = newStart + width;
                RangeChanged?.Invoke(this, (newStart, newEnd));
                break;
            case DragMode.Seek:
                SeekRequested?.Invoke(this, fraction);
                break;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _drag = DragMode.None;
        ReleasePointerCapture(e.Pointer);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _drag = DragMode.None;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            ProtectedCursor = ArrowCursor;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);
        var previousAutomationName = _automationName;
        var step = Math.Clamp(KeyboardStep, double.Epsilon, 1);

        switch (e.Key)
        {
            case VirtualKey.Left:
                ApplyKeyboardArrow(-step, control, shift);
                break;
            case VirtualKey.Right:
                ApplyKeyboardArrow(step, control, shift);
                break;
            case VirtualKey.PageUp:
                MoveRange(Math.Max(KeyboardRangeStep, step));
                break;
            case VirtualKey.PageDown:
                MoveRange(-Math.Max(KeyboardRangeStep, step));
                break;
            case VirtualKey.Home:
                SeekRequested?.Invoke(this, 0);
                break;
            case VirtualKey.End:
                SeekRequested?.Invoke(this, 1);
                break;
            default:
                return;
        }

        UpdateAutomationName(previousAutomationName);
        e.Handled = true;
    }

    private void ApplyKeyboardArrow(double delta, bool control, bool shift)
    {
        if (control)
        {
            StartFractionChanged?.Invoke(this, Clamp01(_start + delta));
            return;
        }

        if (shift)
        {
            EndFractionChanged?.Invoke(this, Clamp01(_end + delta));
            return;
        }

        SeekRequested?.Invoke(this, Clamp01(_play + delta));
    }

    private void MoveRange(double delta)
    {
        var width = _end - _start;
        var start = Math.Clamp(_start + delta, 0, 1 - width);
        RangeChanged?.Invoke(this, (start, start + width));
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void UpdateAutomationName(string? previousName = null)
    {
        var name = $"Trim range. Start {FormatFraction(_start)}, end {FormatFraction(_end)}, current {FormatFraction(_play)}.";
        var oldName = previousName ?? _automationName;
        _automationName = name;
        AutomationProperties.SetName(this, name);

        if (previousName is not null && !string.Equals(oldName, name, StringComparison.Ordinal))
        {
            var peer = FrameworkElementAutomationPeer.FromElement(this)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, oldName, name);
        }
    }

    private static string FormatFraction(double fraction) =>
        $"{Math.Round(Clamp01(fraction) * 100):0}%";

    private void UpdateHoverCursor(double x)
    {
        var half = HandleWidth / 2.0;
        var usable = Usable;
        var sx = half + _start * usable;
        var ex = half + _end * usable;
        var nearHandle = Math.Min(Math.Abs(x - sx), Math.Abs(x - ex)) <= HandleGrab;

        if (nearHandle)
        {
            ProtectedCursor = SizeCursor;
        }
        else if (x > sx && x < ex)
        {
            ProtectedCursor = MoveCursor;
        }
        else
        {
            ProtectedCursor = ArrowCursor;
        }
    }
}
