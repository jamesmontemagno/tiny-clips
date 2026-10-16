using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Studio;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The whole recording as a bar, in source time. The two handles are where the video starts and
/// ends, the tinted parts between them are what gets exported, with a gap for every cut and for
/// nothing else, and the line is the playhead. Pressing or dragging on the bar moves the playhead.
/// </summary>
/// <remarks>
/// <para>
/// The bar keeps no state of its own. It asks for changes through its events, and whoever owns it
/// calls <see cref="Update"/> with what the editor ended up with.
/// </para>
/// <para>
/// The existing <c>TrimBar</c> of the video trimmer is not used here: it is one element to screen
/// readers, has no adjustable handles with a value in seconds, and does not say when a drag starts
/// and ends, which is what makes a drag one undo step.
/// </para>
/// </remarks>
public sealed partial class StudioTrimBar : UserControl
{
    // The handles sit outside the kept range, so the range itself spans the bar minus both. The
    // lanes above the bar leave the same room at their ends.
    private const double HandleWidth = StudioTimelineMetrics.EdgeInset;
    private const double TrimLargeStepCount = 10;
    private const double DisabledOpacity = 0.4;

    private readonly List<Border> _keptParts = [];
    private readonly List<(double Start, double End)> _keptRuns = [];
    private IReadOnlyList<StudioTimeSegment> _keptSegments = [];
    private double _duration = 0.0001;
    private double _trimStart;
    private double _trimEnd;
    private double _playhead;
    private StudioTrimThumb? _dragThumb;
    private uint _dragPointerId;
    private double _dragStartX;
    private double _dragStartTime;
    private bool _isScrubbing;
    private uint _scrubPointerId;

    public StudioTrimBar()
    {
        InitializeComponent();
        IsEnabledChanged += OnIsEnabledChanged;
    }

    /// <summary>Raised when the pointer takes hold of a handle. Everything until it lets go is one change.</summary>
    public event EventHandler? GestureStarted;

    /// <summary>Raised when the pointer lets go of a handle.</summary>
    public event EventHandler? GestureCompleted;

    /// <summary>Raised with the source time, in seconds, the start handle was moved to.</summary>
    public event EventHandler<double>? TrimStartRequested;

    /// <summary>Raised with the source time, in seconds, the end handle was moved to.</summary>
    public event EventHandler<double>? TrimEndRequested;

    /// <summary>Raised with the source time, in seconds, the playhead was moved to.</summary>
    public event EventHandler<double>? ScrubRequested;

    /// <summary>
    /// How many tinted parts the bar draws. For the check tool: stretches that touch are one
    /// part, and the parts are not among what a screen reader is given.
    /// </summary>
    internal int KeptPartCount => _keptParts.Count;

    private double UsableWidth => Math.Max(1, Root.ActualWidth - HandleWidth * 2);

    /// <summary>Shows the trim and the playhead of a recording. Times are in seconds of source time.</summary>
    /// <param name="duration">The length of the recording.</param>
    /// <param name="trimStart">Where the video starts.</param>
    /// <param name="trimEnd">Where the video ends.</param>
    /// <param name="keptSegments">
    /// The stretches between the two that the video keeps, in time order: all of it, or what the
    /// cuts leave. Two that touch leave nothing out between them, as where the video goes on at
    /// another speed, and are drawn as one.
    /// </param>
    /// <param name="playhead">Where the playhead is.</param>
    /// <param name="frameDuration">The length of one frame, which is one step of the playhead.</param>
    /// <param name="trimStep">One keyboard or screen reader step of a handle.</param>
    /// <param name="trimStartText">The start as text, such as "0.0 seconds".</param>
    /// <param name="trimEndText">The end as text.</param>
    /// <param name="playheadText">The playhead as text, such as "0:02.5 of 0:10.0".</param>
    public void Update(
        double duration,
        double trimStart,
        double trimEnd,
        IReadOnlyList<StudioTimeSegment> keptSegments,
        double playhead,
        double frameDuration,
        double trimStep,
        string trimStartText,
        string trimEndText,
        string playheadText)
    {
        var length = double.IsFinite(duration) ? Math.Max(duration, 0) : 0;
        _duration = Math.Max(length, 0.0001);
        _trimStart = trimStart;
        _trimEnd = trimEnd;
        _keptSegments = keptSegments;
        _playhead = playhead;

        var trimLargeStep = Math.Min(trimStep * TrimLargeStepCount, _duration);
        StartThumb.Update(0, length, trimStart, trimStep, trimLargeStep, trimStartText);
        EndThumb.Update(0, length, trimEnd, trimStep, trimLargeStep, trimEndText);
        PlayheadThumb.Update(0, length, playhead, frameDuration, Math.Max(frameDuration, Math.Min(1, _duration)), playheadText);
        PlaceParts();
    }

    /// <summary>Moves only the playhead, which changes many times a second while playing.</summary>
    public void UpdatePlayhead(double playhead, string playheadText)
    {
        _playhead = playhead;
        PlayheadThumb.Update(
            PlayheadThumb.Minimum,
            PlayheadThumb.Maximum,
            playhead,
            PlayheadThumb.SmallChange,
            PlayheadThumb.LargeChange,
            playheadText);
        PlacePlayhead();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioTrimBarAutomationPeer(this);

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => PlaceParts();

    // The handles keep their accent color, so while the editor is disabled they are dimmed instead.
    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Parts.Opacity = IsEnabled ? 1 : DisabledOpacity;

    private void PlaceParts()
    {
        var height = Root.ActualHeight;
        var usable = UsableWidth;
        var startX = Fraction(_trimStart) * usable;
        var endX = Fraction(_trimEnd) * usable;

        StartThumb.Height = height;
        Canvas.SetLeft(StartThumb, startX);

        PlaceKeptParts(height, usable);

        EndThumb.Height = height;
        Canvas.SetLeft(EndThumb, endX + HandleWidth);

        PlayheadThumb.Height = height;
        PlacePlayhead();
    }

    /// <summary>
    /// One tinted part for every stretch the video keeps without a break. What is cut out between
    /// two of them stays the bar's own color.
    /// </summary>
    private void PlaceKeptParts(double height, double usable)
    {
        // Stretches that touch are one part. Each part has round corners, so two parts drawn
        // side by side would show a notch where nothing is left out: only a cut leaves a gap.
        _keptRuns.Clear();
        foreach (var segment in _keptSegments)
        {
            if (_keptRuns.Count > 0 && segment.Start <= _keptRuns[^1].End)
            {
                _keptRuns[^1] = (_keptRuns[^1].Start, Math.Max(_keptRuns[^1].End, segment.End));
            }
            else
            {
                _keptRuns.Add((segment.Start, segment.End));
            }
        }

        while (_keptParts.Count > _keptRuns.Count)
        {
            KeptRanges.Children.RemoveAt(_keptParts.Count - 1);
            _keptParts.RemoveAt(_keptParts.Count - 1);
        }

        while (_keptParts.Count < _keptRuns.Count)
        {
            var part = new Border { Style = (Style)Resources["StudioKeptRangeStyle"] };
            _keptParts.Add(part);
            KeptRanges.Children.Add(part);
        }

        for (var index = 0; index < _keptParts.Count; index++)
        {
            var left = Fraction(_keptRuns[index].Start) * usable;
            var right = Fraction(_keptRuns[index].End) * usable;
            _keptParts[index].Height = height;
            _keptParts[index].Width = Math.Max(0, right - left);
            Canvas.SetLeft(_keptParts[index], left + HandleWidth);
        }
    }

    private void PlacePlayhead() =>
        Canvas.SetLeft(PlayheadThumb, HandleWidth + Fraction(_playhead) * UsableWidth - PlayheadThumb.Width / 2);

    private double Fraction(double time) => Math.Min(Math.Max(time / _duration, 0), 1);

    // Scrubbing on the track

    private void OnTrackPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (_isScrubbing || !IsPrimaryPress(e, point) || !Track.CapturePointer(e.Pointer))
        {
            return;
        }

        _isScrubbing = true;
        _scrubPointerId = e.Pointer.PointerId;
        ScrubTo(point.Position.X);
        e.Handled = true;
    }

    private void OnTrackPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isScrubbing && e.Pointer.PointerId == _scrubPointerId)
        {
            ScrubTo(e.GetCurrentPoint(Root).Position.X);
            e.Handled = true;
        }
    }

    private void OnTrackPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_isScrubbing && e.Pointer.PointerId == _scrubPointerId)
        {
            _isScrubbing = false;
            Track.ReleasePointerCaptures();
        }
    }

    private void ScrubTo(double x)
    {
        var fraction = Math.Min(Math.Max((x - HandleWidth) / UsableWidth, 0), 1);
        ScrubRequested?.Invoke(this, fraction * _duration);
    }

    // Dragging a handle

    private void OnThumbPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var thumb = (StudioTrimThumb)sender;
        var point = e.GetCurrentPoint(Root);
        if (_dragThumb is not null || !IsPrimaryPress(e, point) || !thumb.CapturePointer(e.Pointer))
        {
            return;
        }

        _dragThumb = thumb;
        _dragPointerId = e.Pointer.PointerId;
        _dragStartX = point.Position.X;
        _dragStartTime = thumb.Value;
        thumb.Focus(FocusState.Pointer);
        GestureStarted?.Invoke(this, EventArgs.Empty);

        // Asking for the time the handle already has changes nothing, and shows its frame.
        RequestTrim(thumb, _dragStartTime);
        e.Handled = true;
    }

    private void OnThumbPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _dragThumb) || e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        // Measured in the bar's space, which stays still while the handle moves.
        var deltaX = e.GetCurrentPoint(Root).Position.X - _dragStartX;
        RequestTrim(_dragThumb, _dragStartTime + deltaX / UsableWidth * _duration);
        e.Handled = true;
    }

    private void OnThumbPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _dragThumb) || e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        var thumb = _dragThumb;
        _dragThumb = null;
        thumb.ReleasePointerCaptures();
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void RequestTrim(StudioTrimThumb thumb, double time)
    {
        if (ReferenceEquals(thumb, StartThumb))
        {
            TrimStartRequested?.Invoke(this, time);
        }
        else
        {
            TrimEndRequested?.Invoke(this, time);
        }
    }

    // The keyboard and screen readers

    private void OnStartValueRequested(object? sender, double time) => TrimStartRequested?.Invoke(this, time);

    private void OnEndValueRequested(object? sender, double time) => TrimEndRequested?.Invoke(this, time);

    private void OnPlayheadValueRequested(object? sender, double time) => ScrubRequested?.Invoke(this, time);

    private static bool IsPrimaryPress(PointerRoutedEventArgs e, PointerPoint point) =>
        e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed;
}

/// <summary>Groups the two handles and the playhead under one name for screen readers.</summary>
public sealed partial class StudioTrimBarAutomationPeer(StudioTrimBar owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => nameof(StudioTrimBar);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
