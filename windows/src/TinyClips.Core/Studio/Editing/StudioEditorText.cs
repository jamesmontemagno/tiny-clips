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

        // The scale that is drawn, which is the stored one kept within 1 to 5.
        var scale = double.IsFinite(zoom.Scale) ? Math.Min(5, Math.Max(1, zoom.Scale)) : 1;
        var start = double.IsFinite(zoom.Start) ? zoom.Start : 0;
        var end = double.IsFinite(zoom.End) ? zoom.End : 0;
        var text = string.Create(CultureInfo.InvariantCulture, $"Zoom {scale:0.##}×, {start:0.0} to {end:0.0} seconds");
        if (zoom.Focus.Mode == StudioZoomFocusMode.Cursor)
        {
            text += ", follows the pointer";
        }

        return zoom.Origin == StudioZoomOrigin.Auto ? text + ", suggested" : text;
    }

    private static long WholePercent(double fraction) =>
        double.IsFinite(fraction) ? (long)Math.Round(fraction * 100, MidpointRounding.AwayFromZero) : 0;

    private static long WholeNumber(double value) => double.IsFinite(value) ? (long)value : 0;
}
