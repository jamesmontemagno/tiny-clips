using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// A named slider with its value written next to the name. The value moves in whole steps without
/// a tick mark for each one, and one pointer drag is reported as one gesture.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is meant for a two-way binding. Only the user moving the slider writes to
/// it: a value that the slider adjusts by itself, when its range is set, is never passed on.
/// </remarks>
public sealed partial class StudioSliderRow : UserControl
{
    // Larger steps for Page Up and Page Down, as a multiple of one step.
    private const double LargeStepCount = 10;
    private const double Tolerance = 1e-9;

    // A number written in XAML reaches a double property through single precision, so 0.01 arrives
    // as 0.00999999977648258. Left like that, every value the slider stores would carry the same
    // error into the project file. The ranges and steps here have at most three decimals.
    private const int RangeDecimals = 6;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StudioSliderRow), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(StudioSliderRow), new PropertyMetadata(string.Empty, OnValueTextChanged));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(StudioSliderRow), new PropertyMetadata(0d, OnRangeChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(StudioSliderRow), new PropertyMetadata(0d, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(StudioSliderRow), new PropertyMetadata(1d, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(StudioSliderRow), new PropertyMetadata(0.01, OnRangeChanged));

    public static readonly DependencyProperty SliderAutomationIdProperty = DependencyProperty.Register(
        nameof(SliderAutomationId), typeof(string), typeof(StudioSliderRow), new PropertyMetadata(string.Empty, OnSliderAutomationIdChanged));

    private bool _isSyncing;

    public StudioSliderRow()
    {
        InitializeComponent();
        SyncSlider();
    }

    /// <summary>Raised when a pointer drag on the slider starts.</summary>
    public event EventHandler? GestureStarted;

    /// <summary>Raised when a pointer drag on the slider ends.</summary>
    public event EventHandler? GestureCompleted;

    /// <summary>The name shown above the slider, and its accessible name.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The value as text, such as "6%", shown next to the name and read by screen readers.</summary>
    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The value moves in multiples of this.</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>The automation id of the slider inside the row.</summary>
    public string SliderAutomationId
    {
        get => (string)GetValue(SliderAutomationIdProperty);
        set => SetValue(SliderAutomationIdProperty, value);
    }

    /// <summary>Puts the keyboard focus on the slider. The row itself is not a tab stop.</summary>
    public bool FocusSlider(FocusState state) => ValueSlider.Focus(state);

    private static void OnTitleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var row = (StudioSliderRow)sender;
        row.TitleLabel.Text = row.Title;
        AutomationProperties.SetName(row.ValueSlider, row.Title);
    }

    private static void OnValueTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var row = (StudioSliderRow)sender;
        row.ValueLabel.Text = row.ValueText;
        row.ValueSlider.ValueText = row.ValueText;
    }

    private static void OnRangeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((StudioSliderRow)sender).SyncSlider();

    private static void OnSliderAutomationIdChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var row = (StudioSliderRow)sender;
        AutomationProperties.SetAutomationId(row.ValueSlider, row.SliderAutomationId);
    }

    private double CleanMinimum => Math.Round(Minimum, RangeDecimals);

    private double CleanMaximum => Math.Round(Maximum, RangeDecimals);

    private double CleanStep => Step > 0 ? Math.Round(Step, RangeDecimals) : 0.01;

    /// <summary>
    /// Copies the range, the step and the value to the slider. The slider pulls its value inside a
    /// new range by itself; that is not the user's doing, so it is not reported.
    /// </summary>
    private void SyncSlider()
    {
        _isSyncing = true;
        try
        {
            ValueSlider.Minimum = CleanMinimum;
            ValueSlider.Maximum = Math.Max(CleanMinimum, CleanMaximum);
            ValueSlider.StepFrequency = CleanStep;
            ValueSlider.SmallChange = CleanStep;
            ValueSlider.LargeChange = CleanStep * LargeStepCount;
            if (Math.Abs(ValueSlider.Value - Value) > Tolerance)
            {
                ValueSlider.Value = Value;
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSyncing)
        {
            return;
        }

        var snapped = StudioEditorText.SnapToStep(e.NewValue, CleanStep, CleanMinimum, CleanMaximum);
        if (Math.Abs(snapped - Value) > Tolerance)
        {
            Value = snapped;
        }
    }

    private void OnSliderGestureStarted(object? sender, EventArgs e) => GestureStarted?.Invoke(this, EventArgs.Empty);

    private void OnSliderGestureCompleted(object? sender, EventArgs e) => GestureCompleted?.Invoke(this, EventArgs.Empty);
}
