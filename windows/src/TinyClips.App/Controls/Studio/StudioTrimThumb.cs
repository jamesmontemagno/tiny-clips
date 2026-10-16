using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Studio.Editing;
using Windows.System;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One adjustable part of the Studio trim bar: the start handle, the end handle, or the playhead.
/// It can take keyboard focus, moves with the arrow keys, and is a slider to screen readers, with
/// its value both as a number of seconds and as the text the editor uses for it.
/// </summary>
/// <remarks>
/// The thumb does not change its own value. It asks for one through <see cref="ValueRequested"/>,
/// and the trim bar gives it the value the editor ended up with.
/// </remarks>
public sealed partial class StudioTrimThumb : ContentControl
{
    private string _valueText = string.Empty;

    public StudioTrimThumb()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>Raised with the value, in seconds, that the keyboard or a screen reader asked for.</summary>
    public event EventHandler<double>? ValueRequested;

    public double Minimum { get; private set; }

    public double Maximum { get; private set; }

    /// <summary>Where the thumb is, in seconds of source time.</summary>
    public double Value { get; private set; }

    /// <summary>How far an arrow key, or one screen reader step, moves the thumb.</summary>
    public double SmallChange { get; private set; }

    /// <summary>How far Page Up and Page Down move the thumb.</summary>
    public double LargeChange { get; private set; }

    /// <summary>The value as the editor words it, such as "12.5 seconds".</summary>
    public string ValueText => _valueText;

    /// <summary>Sets everything the thumb reports, and tells screen readers what changed.</summary>
    public void Update(double minimum, double maximum, double value, double smallChange, double largeChange, string valueText)
    {
        var oldValue = Value;
        var oldText = _valueText;
        Minimum = minimum;
        Maximum = Math.Max(minimum, maximum);
        Value = Math.Min(Math.Max(value, Minimum), Maximum);
        SmallChange = smallChange;
        LargeChange = largeChange;
        _valueText = valueText ?? string.Empty;

        if ((oldValue == Value && oldText == _valueText)
            || !AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged)
            || FrameworkElementAutomationPeer.FromElement(this) is not { } peer)
        {
            return;
        }

        if (oldValue != Value)
        {
            peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, Value);
        }

        if (oldText != _valueText)
        {
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldText, _valueText);
        }
    }

    /// <summary>Asks for a value, kept inside the range. Does nothing while the thumb is disabled.</summary>
    public void RequestValue(double value)
    {
        if (IsEnabled && double.IsFinite(value))
        {
            ValueRequested?.Invoke(this, Math.Min(Math.Max(value, Minimum), Maximum));
        }
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyStatus.IsMenuKeyDown)
        {
            return;
        }

        // In a right-to-left layout the bar runs the other way, and so do the two horizontal keys.
        var isRightToLeft = FlowDirection == FlowDirection.RightToLeft;
        double? target = e.Key switch
        {
            VirtualKey.Left => Value + (isRightToLeft ? SmallChange : -SmallChange),
            VirtualKey.Right => Value + (isRightToLeft ? -SmallChange : SmallChange),
            VirtualKey.Down => Value - SmallChange,
            VirtualKey.Up => Value + SmallChange,
            VirtualKey.PageDown => Value - LargeChange,
            VirtualKey.PageUp => Value + LargeChange,
            VirtualKey.Home => Minimum,
            VirtualKey.End => Maximum,
            _ => null,
        };

        if (target is { } value)
        {
            RequestValue(value);
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioTrimThumbAutomationPeer(this);
}

/// <summary>
/// Makes a <see cref="StudioTrimThumb"/> a slider to screen readers. The range pattern carries the
/// position in seconds and lets it be raised and lowered; the value pattern carries the wording.
/// </summary>
public sealed partial class StudioTrimThumbAutomationPeer(StudioTrimThumb owner)
    : FrameworkElementAutomationPeer(owner), IRangeValueProvider, IValueProvider
{
    private StudioTrimThumb Thumb => (StudioTrimThumb)Owner;

    // Both patterns have a Value and an IsReadOnly, of different meaning, so each is explicit.
    bool IRangeValueProvider.IsReadOnly => !Thumb.IsEnabled;

    double IRangeValueProvider.Minimum => Thumb.Minimum;

    double IRangeValueProvider.Maximum => Thumb.Maximum;

    double IRangeValueProvider.Value => Thumb.Value;

    double IRangeValueProvider.SmallChange => Thumb.SmallChange;

    double IRangeValueProvider.LargeChange => Thumb.LargeChange;

    bool IValueProvider.IsReadOnly => true;

    string IValueProvider.Value => Thumb.ValueText;

    void IRangeValueProvider.SetValue(double value)
    {
        if (!Thumb.IsEnabled)
        {
            throw new ElementNotEnabledException();
        }

        // The number arrives in single precision. See ResolveAutomationValue for what is made of it.
        Thumb.RequestValue(StudioEditorText.ResolveAutomationValue(value, Thumb.Value, Thumb.SmallChange));
    }

    void IValueProvider.SetValue(string value) =>
        throw new InvalidOperationException("The text is read-only. Set the position through the range value.");

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override string GetClassNameCore() => nameof(StudioTrimThumb);

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.RangeValue or PatternInterface.Value
            ? this
            : base.GetPatternCore(patternInterface);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
