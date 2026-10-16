namespace TinyClips.Tools.StudioPreviewCheck.Media;

/// <summary>
/// Which frame of a file a slot of a timeline shows, and the other way round, from the times
/// the file's frames start at. The rule is the export's (<c>StudioVideoSource.GetFrame</c> at
/// the middle of each output frame): a slot shows the last frame that began at or before its
/// middle, and the first frame while none has begun. Times are in 100 ns units. Pure arithmetic.
/// </summary>
internal static class SlotMath
{
    private const double TicksPerSecond = 10_000_000;

    /// <summary>The middle of a slot.</summary>
    public static long MiddleOf(int slot, double fps) => (long)Math.Round((slot + 0.5) * TicksPerSecond / fps, MidpointRounding.AwayFromZero);

    /// <summary>The frame showing at a time: the last of <paramref name="times"/> at or before it, and the first while none is.</summary>
    public static int FrameAt(long[] times, long ticks)
    {
        var low = 0;
        var high = times.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (times[middle] <= ticks)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>The frame slot <paramref name="slot"/> shows. <paramref name="offset"/> is where the file's zero sits on the timeline.</summary>
    public static int FrameOfSlot(long[] times, int slot, double fps, long offset = 0) => FrameAt(times, MiddleOf(slot, fps) - offset);

    /// <summary>The first slot whose middle is at or after a time: the first slot that shows a frame that begins then.</summary>
    public static int SlotWhoseMiddleIsAtOrAfter(long ticks, double fps)
    {
        var slot = (int)Math.Floor((ticks * fps / TicksPerSecond) - 0.5);
        while (MiddleOf(slot, fps) < ticks)
        {
            slot++;
        }

        while (MiddleOf(slot - 1, fps) >= ticks)
        {
            slot--;
        }

        return slot;
    }

    /// <summary>
    /// The slots, of <paramref name="slots"/> in all, that show a frame: the first and the last.
    /// Null for a frame that no slot shows, because the frame after it begins before the next
    /// middle, or because it lies outside the timeline.
    /// </summary>
    public static (int First, int Last)? SlotsOf(long[] times, int frame, double fps, int slots, long offset = 0)
    {
        if (frame < 0 || frame >= times.Length || slots <= 0)
        {
            return null;
        }

        // From the first slot whose middle the frame has begun by, to the slot before the first
        // one whose middle the next frame has begun by. The first frame also shows before it begins.
        var first = frame == 0 ? 0 : SlotWhoseMiddleIsAtOrAfter(times[frame] + offset, fps);
        var last = frame == times.Length - 1 ? slots - 1 : SlotWhoseMiddleIsAtOrAfter(times[frame + 1] + offset, fps) - 1;
        first = Math.Max(0, first);
        last = Math.Min(slots - 1, last);
        return first > last ? null : (first, last);
    }
}
