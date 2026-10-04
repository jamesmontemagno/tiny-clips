namespace TinyClips.App.Controls.Studio;

/// <summary>
/// How the Studio timeline lays time out along a bar. The trim bar and the zoom lane above it both
/// use it, so a zoom's edges sit over the same times as the trim handles and the playhead.
/// </summary>
internal static class StudioTimelineMetrics
{
    /// <summary>
    /// The room at each end of a bar that is not part of the recording. It is the width of a trim
    /// handle, which sits there when nothing is trimmed.
    /// </summary>
    public const double EdgeInset = 12;

    // A recording without a length still needs something to divide by.
    private const double ShortestDuration = 0.0001;

    /// <summary>The width between the two insets, which is the whole recording.</summary>
    public static double GetUsableWidth(double barWidth) => Math.Max(1, barWidth - (EdgeInset * 2));

    /// <summary>Where a source time is along a bar, kept inside the recording.</summary>
    public static double GetX(double sourceTime, double duration, double barWidth)
    {
        var fraction = Math.Min(Math.Max(sourceTime / Math.Max(duration, ShortestDuration), 0), 1);
        return EdgeInset + (fraction * GetUsableWidth(barWidth));
    }

    /// <summary>The source time at a place along a bar, kept inside the recording.</summary>
    public static double GetTime(double x, double duration, double barWidth)
    {
        var fraction = Math.Min(Math.Max((x - EdgeInset) / GetUsableWidth(barWidth), 0), 1);
        return fraction * Math.Max(duration, ShortestDuration);
    }
}
