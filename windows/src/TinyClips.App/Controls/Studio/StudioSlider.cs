using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
/// needs. A part read back from a template is not reliably of its own type in the NativeAOT
/// build, where a cast to a type such as Thumb can fail.
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
/// Adds the slider's text value to what a slider already offers. The number stays available
/// through the range pattern, so a screen reader can still raise and lower it.
/// </summary>
public sealed partial class StudioSliderAutomationPeer(StudioSlider owner) : SliderAutomationPeer(owner), IValueProvider
{
    // Explicit, because the slider's own range pattern already has a number called Value.
    bool IValueProvider.IsReadOnly => true;

    string IValueProvider.Value => ((StudioSlider)Owner).ValueText;

    void IValueProvider.SetValue(string value) =>
        throw new InvalidOperationException("The slider's text is read-only. Set its number through the range value.");

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
}
