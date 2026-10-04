using System.Globalization;

namespace TinyClips.Core.Studio.Editing;

/// <summary>
/// The text the Studio editor shows and reads out, and the number rules behind it. Kept apart from
/// the window so the wording and the rounding are tested, and match the Mac editor.
/// </summary>
public static class StudioEditorText
{
    /// <summary>What a project with no name is called.</summary>
    public const string UntitledName = "Untitled recording";

    /// <summary>Said when a zoom has been added at the playhead.</summary>
    public const string ZoomAddedMessage = "Zoom added.";

    /// <summary>Said when a zoom was asked for where one already is. That zoom is selected instead.</summary>
    public const string ZoomAlreadyThereMessage = "There is already a zoom here.";

    /// <summary>Said when a zoom was asked for where less than the shortest zoom fits.</summary>
    public const string NoRoomForZoomMessage = "There is no room for a zoom here.";

    /// <summary>Said when the selected zoom has been deleted.</summary>
    public const string ZoomDeletedMessage = "Zoom deleted.";

    /// <summary>Said when the suggested zooms have been removed.</summary>
    public const string ZoomSuggestionsRemovedMessage = "Suggested zooms removed.";

    /// <summary>Said when zooms were asked for and the clicks give none.</summary>
    public const string NoZoomSuggestionsMessage = "No zooms to suggest for this recording.";

    /// <summary>Why zooms cannot be suggested for a recording without clicks, such as one of a window.</summary>
    public const string NoClicksExplanation = "This recording has no clicks to suggest zooms from.";

    /// <summary>Why a zoom cannot follow the pointer in a recording without pointer positions.</summary>
    public const string NoPointerExplanation = "This recording has no pointer positions to follow.";

    // How far a number that went through single precision may be from what was meant, as a part of
    // its size. One unit in the last place is 2^-23 of it at most, and the value a screen reader
    // read, the step it read and the sum it sent back each lose up to half of one.
    private const double SinglePrecisionTolerance = 4.0 / (1 << 23);

    /// <summary>The project name without surrounding white space, or <see cref="UntitledName"/>.</summary>
    public static string GetClipName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? UntitledName : trimmed;
    }

    /// <summary>The transport read-out, such as <c>0:02.5 / 0:10.0</c>. Both times are output time.</summary>
    public static string GetTimeText(double outputTime, double outputDuration) =>
        $"{StudioEditorModel.FormatTime(outputTime)} / {StudioEditorModel.FormatTime(outputDuration)}";

    /// <summary>For example <c>Exports at 1920 × 1080</c>.</summary>
    public static string GetExportSizeText(StudioSize size) =>
        string.Create(CultureInfo.InvariantCulture, $"Exports at {WholeNumber(size.Width)} × {WholeNumber(size.Height)}");

    /// <summary>A fraction as a whole percentage, such as <c>6%</c>. Halves round away from zero.</summary>
    public static string GetPercentText(double fraction) =>
        string.Create(CultureInfo.InvariantCulture, $"{WholePercent(fraction)}%");

    /// <summary>Like <see cref="GetPercentText"/>, with a plus sign on values above zero: <c>+3%</c>.</summary>
    public static string GetSignedPercentText(double fraction)
    {
        var percent = WholePercent(fraction);
        return string.Create(CultureInfo.InvariantCulture, $"{(percent > 0 ? "+" : string.Empty)}{percent}%");
    }

    /// <summary>
    /// The value a slider stores: the nearest multiple of <paramref name="step"/>, kept inside the
    /// range. A step of zero or less only applies the range.
    /// </summary>
    public static double SnapToStep(double value, double step, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
        {
            return minimum;
        }

        var snapped = step > 0 ? Math.Round(value / step, MidpointRounding.AwayFromZero) * step : value;
        return Math.Min(Math.Max(snapped, minimum), maximum);
    }

    /// <summary>
    /// How far one keyboard or screen reader step moves a trim handle: about a hundred steps across
    /// the recording, never finer than one frame and never coarser than one second.
    /// </summary>
    public static double GetTrimStep(double frameDuration, double sourceDuration) =>
        Math.Max(frameDuration, Math.Min(1, sourceDuration / 100));

    /// <summary>
    /// The value a screen reader meant when it set a number through UI Automation.
    /// </summary>
    /// <remarks>
    /// WinUI passes such a number on in single precision, so 3.2 arrives as 3.2000000477 and a
    /// time that was on a frame boundary may arrive just before it. A screen reader asks for the
    /// current value plus or minus a number of steps. A request that is a whole number of steps
    /// away, as closely as single precision can tell, is therefore taken to mean exactly that, and
    /// gives the value the arrow keys would give. Any other request is kept to a thousandth, which
    /// for a time is a millisecond.
    /// </remarks>
    /// <param name="requested">The number that arrived.</param>
    /// <param name="current">The value the control has now.</param>
    /// <param name="step">One step of the control.</param>
    public static double ResolveAutomationValue(double requested, double current, double step)
    {
        if (!double.IsFinite(requested))
        {
            return current;
        }

        if (double.IsFinite(current) && double.IsFinite(step) && step > 0)
        {
            var steps = Math.Round((requested - current) / step, MidpointRounding.AwayFromZero);
            var stepped = current + steps * step;
            var tolerance = Math.Max(Math.Abs(requested), Math.Abs(stepped)) * SinglePrecisionTolerance + 1e-9;
            if (Math.Abs(requested - stepped) <= tolerance)
            {
                return stepped;
            }
        }

        return Math.Round(requested, 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// What the preview is showing, for screen readers: the layout, and for the bubble layout the
    /// corner the camera is anchored to.
    /// </summary>
    public static string GetPreviewDescription(StudioEditorModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var layout = StudioEditorModel.GetLayoutName(model.EffectiveLayout);
        if (model.EffectiveLayout != StudioLayout.Bubble)
        {
            return layout;
        }

        var corner = StudioEditorModel.GetAnchorName(model.CurrentScene.Bubble.Anchor).ToLowerInvariant();
        return $"{layout}, camera {corner}";
    }

    /// <summary>
    /// A zoom for screen readers, such as <c>Zoom 2×, 12.0 to 16.5 seconds</c>. The times are
    /// source time, as the trim handles read. "Follows the pointer" and "suggested" are added
    /// where they apply.
    /// </summary>
    public static string GetZoomDescription(StudioZoom zoom)
    {
        ArgumentNullException.ThrowIfNull(zoom);

        var text = $"Zoom {GetZoomScaleText(zoom.Scale)}, {GetZoomRangeText(zoom)}";
        if (zoom.Focus.Mode == StudioZoomFocusMode.Cursor)
        {
            text += ", follows the pointer";
        }

        return zoom.Origin == StudioZoomOrigin.Auto ? text + ", suggested" : text;
    }

    /// <summary>
    /// How much a zoom magnifies, such as <c>2×</c> or <c>1.25×</c>: the scale that is drawn, which
    /// is the stored one kept within 1 to 5, with up to two decimals.
    /// </summary>
    public static string GetZoomScaleText(double scale)
    {
        var drawn = double.IsFinite(scale) ? Math.Min(5, Math.Max(1, scale)) : 1;
        return string.Create(CultureInfo.InvariantCulture, $"{drawn:0.##}×");
    }

    /// <summary>When a zoom starts and ends, in source time: <c>12.0 to 16.5 seconds</c>.</summary>
    public static string GetZoomRangeText(StudioZoom zoom)
    {
        ArgumentNullException.ThrowIfNull(zoom);
        var start = double.IsFinite(zoom.Start) ? zoom.Start : 0;
        var end = double.IsFinite(zoom.End) ? zoom.End : 0;
        return string.Create(CultureInfo.InvariantCulture, $"{start:0.0} to {end:0.0} seconds");
    }

    /// <summary>Which zoom is selected, counting from one: <c>Zoom 2 of 5</c>.</summary>
    public static string GetZoomPositionText(int index, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"Zoom {index + 1} of {count}");

    /// <summary>What to say after zooms were suggested, given how many suggestions there are now.</summary>
    public static string GetZoomSuggestionsText(int count) => count switch
    {
        <= 0 => NoZoomSuggestionsMessage,
        1 => "1 zoom suggested.",
        _ => string.Create(CultureInfo.InvariantCulture, $"{count} zooms suggested."),
    };

    private static long WholePercent(double fraction) =>
        double.IsFinite(fraction) ? (long)Math.Round(fraction * 100, MidpointRounding.AwayFromZero) : 0;

    private static long WholeNumber(double value) => double.IsFinite(value) ? (long)value : 0;
}
