namespace TinyClips.Core.Automation;

/// <summary>Maps agent-supplied seconds onto video ranges and GIF frames for <c>editor.trim</c> and <c>editor.saveFrame</c>.</summary>
public static class AutomationMediaRange
{
    /// <summary>Validates a trim range and clamps its end to the media duration.</summary>
    public static (double Start, double End) Clamp(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "start and end must be non-negative numbers of seconds.");
        }

        if (end <= start)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "end must be greater than start.");
        }

        if (duration <= 0 || start >= duration)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                $"start must be before the end of the media ({duration:0.00} s).");
        }

        return (start, Math.Min(end, duration));
    }

    /// <summary>Total length of a GIF whose frame delays are in seconds.</summary>
    public static double Duration(IReadOnlyList<double> delays) => delays.Sum();

    /// <summary>The first and last frames shown between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static (int First, int Last) GifFrames(double start, double end, IReadOnlyList<double> delays)
    {
        var first = FrameIndex(start, delays);
        var last = first;
        var nextStart = StartTime(first + 1, delays);
        while (last + 1 < delays.Count && nextStart < end)
        {
            last++;
            nextStart += delays[last];
        }

        return (first, last);
    }

    /// <summary>The frame on screen at <paramref name="time"/> seconds.</summary>
    public static int GifFrameAt(double time, IReadOnlyList<double> delays)
    {
        var duration = Duration(delays);
        if (!double.IsFinite(time) || time < 0 || time >= duration)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                $"time must be within the GIF (0–{duration:0.00} s).");
        }

        return FrameIndex(time, delays);
    }

    public static double StartTime(int frame, IReadOnlyList<double> delays)
    {
        var start = 0.0;
        for (var i = 0; i < frame && i < delays.Count; i++)
        {
            start += delays[i];
        }

        return start;
    }

    private static int FrameIndex(double time, IReadOnlyList<double> delays)
    {
        var index = 0;
        var nextStart = delays.Count > 0 ? delays[0] : 0;
        while (index + 1 < delays.Count && nextStart <= time)
        {
            index++;
            nextStart += delays[index];
        }

        return index;
    }
}
