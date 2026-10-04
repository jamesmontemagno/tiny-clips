using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// A slider that says when a pointer drag starts and ends, so the editor can make the whole drag
/// one undo step, and that tells screen readers its value as the text shown next to it.
/// </summary>
/// <remarks>
/// <para>
/// A slider can be dragged in two ways. A press on the thumb is a drag of the thumb, which keeps
/// the pointer until it is let go. A press anywhere else jumps the value to that point and then
/// follows the pointer: the slider does that in a handler on a part of its template, which keeps
/// the pointer, moves the value and marks the press as handled before the slider itself hears of
/// it.
/// </para>
/// <para>
/// So a gesture starts with the first change of the value that is made while one of those parts
/// keeps a pointer, just before the change is announced, and ends when the pointer is let go. The
/// keyboard and screen readers hold no pointer, and each of their changes stands alone.
/// </para>
/// <para>
/// The template parts are used as plain elements, which is all that asking them about the pointer
/// needs. It also avoids a cast to a type such as Thumb, which can fail on a part read back from a
/// template when the app is compiled ahead of time. The app is not now; 1.8.0 and 1.8.1 were.
/// </para>
/// </remarks>
public sealed partial class StudioSlider : Slider
{
    // The two thumbs, and the part that takes a press on the track.
    private static readonly string[] PointerPartNames = ["HorizontalThumb", "VerticalThumb", "SliderContainer"];

    private readonly List<UIElement> _pointerParts = [];
    private string _valueText = string.Empty;
    private bool _isGestureActive;

    public StudioSlider()
    {
        // The slider's template handles these, so they are asked for whether handled or not.
        var pointerEnded = new PointerEventHandler(OnPointerEnded);
        AddHandler(PointerReleasedEvent, pointerEnded, true);
        AddHandler(PointerCanceledEvent, pointerEnded, true);
        AddHandler(PointerCaptureLostEvent, pointerEnded, true);
    }

    /// <summary>Raised when a pointer starts to move the slider, before the first change is announced.</summary>
    public event EventHandler? GestureStarted;

    /// <summary>Raised when the pointer lets go of the slider.</summary>
    public event EventHandler? GestureCompleted;

    /// <summary>The value as the user reads it, such as "6%". Screen readers say this.</summary>
    public string ValueText
    {
        get => _valueText;
        set
        {
            var newValue = value ?? string.Empty;
            if (newValue == _valueText)
            {
                return;
            }

            var oldValue = _valueText;
            _valueText = newValue;
            if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged)
                && FrameworkElementAutomationPeer.FromElement(this) is { } peer)
            {
                peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldValue, newValue);
            }
        }
    }

    /// <summary>True while a pointer is dragging a thumb, or is held down after a press on the track.</summary>
    private bool IsPointerHeld => HoldsPointer(this) || _pointerParts.Exists(HoldsPointer);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _pointerParts.Clear();
        foreach (var name in PointerPartNames)
        {
            if (GetTemplateChild(name) is UIElement part)
            {
                _pointerParts.Add(part);
            }
        }
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        if (IsPointerHeld)
        {
            BeginGesture();
        }

        base.OnValueChanged(oldValue, newValue);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioSliderAutomationPeer(this);

    private static bool HoldsPointer(UIElement element) => element.PointerCaptures is { Count: > 0 };

    private void OnPointerEnded(object sender, PointerRoutedEventArgs e) => EndGesture();

    private void BeginGesture()
    {
        if (!_isGestureActive)
        {
            _isGestureActive = true;
            GestureStarted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EndGesture()
    {
        if (_isGestureActive)
        {
            _isGestureActive = false;
            GestureCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Adds the slider's text value to what a slider already offers, and hands out a range value
/// through which a screen reader can set every value of the range.
/// </summary>
public sealed partial class StudioSliderAutomationPeer(StudioSlider owner) : SliderAutomationPeer(owner), IValueProvider
{
    private StudioSliderRangeValue? _rangeValue;

    private StudioSlider Slider => (StudioSlider)Owner;

    // Explicit, because the slider's own range pattern already has a number called Value.
    bool IValueProvider.IsReadOnly => true;

    string IValueProvider.Value => Slider.ValueText;

    void IValueProvider.SetValue(string value) =>
        throw new InvalidOperationException("The slider's text is read-only. Set its number through the range value.");

    protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Value => this,
        PatternInterface.RangeValue => _rangeValue ??= new StudioSliderRangeValue(Slider),
        _ => base.GetPatternCore(patternInterface),
    };
}

/// <summary>
/// The slider's number, as screen readers read and set it.
/// </summary>
/// <remarks>
/// <para>
/// A number set through UI Automation arrives in single precision: 0.2 as 0.2000000030. The
/// slider's own peer refuses a number outside the range, and that one is above a maximum of 0.2.
/// Left to it, the highest value of a range such as 0 to 0.2, 0.4 or 0.6 could not be set, nor
/// the lowest of one that starts at 0.08. Here the number that was meant is worked out first.
/// </para>
/// <para>
/// This is an object of its own because the peer cannot answer for the range value itself: a
/// peer derived from the slider's peer is asked for it on the interface the slider's peer
/// already implements, and that is where the call goes.
/// </para>
/// </remarks>
public sealed partial class StudioSliderRangeValue(StudioSlider slider) : IRangeValueProvider
{
    // How far a value that was added up from steps may be past the end of the range.
    private const double RangeTolerance = 1e-9;

    public bool IsReadOnly => !slider.IsEnabled;

    public double Minimum => slider.Minimum;

    public double Maximum => slider.Maximum;

    public double Value => slider.Value;

    public double SmallChange => slider.SmallChange;

    public double LargeChange => slider.LargeChange;

    public void SetValue(double value)
    {
        if (!slider.IsEnabled)
        {
            throw new ElementNotEnabledException();
        }

        var meant = StudioEditorText.ResolveAutomationValue(value, slider.Value, slider.SmallChange);
        if (meant < slider.Minimum - RangeTolerance || meant > slider.Maximum + RangeTolerance)
        {
            throw new ArgumentException("The value is outside the slider's range.", nameof(value));
        }

        slider.Value = Math.Min(Math.Max(meant, slider.Minimum), slider.Maximum);
    }
}
