using System.Diagnostics.CodeAnalysis;

namespace TinyClips.Core.Studio;

/// <summary>
/// What an edit to a speed change did: whether the project changed, and where the speed change is
/// in <see cref="StudioEdits.Speed"/> afterwards. The index is null when there is no such speed
/// change, which is also the case once it has been deleted.
/// </summary>
public readonly record struct StudioSpeedEditResult(bool Changed, int? Index);

// Speed changes: stretches of the recording the video plays faster or slower. They are stored in
// source time, like cuts, so one moves nothing else in the project; what it changes is how long
// the video is. The list is kept in time order without overlaps, so a speed change's index
// identifies it between two edits. Two may touch.
public sealed partial class StudioEditorModel
{
    /// <summary>The shortest speed change the editor makes, in seconds of the recording.</summary>
    public const double MinimumSpeedDuration = 0.1;

    /// <summary>
    /// How much of the recording a speed change covers when it is added, in seconds, where there
    /// is room for it.
    /// </summary>
    public const double NewSpeedDuration = 2;

    /// <summary>The rate a speed change has when it is added: twice as fast.</summary>
    public const double NewSpeedRate = 2;

    /// <summary>
    /// The rates the editor offers, slowest first. A project file may hold any rate from
    /// <see cref="StudioTimeMap.SlowestRate"/> to <see cref="StudioTimeMap.FastestRate"/>.
    /// </summary>
    public static IReadOnlyList<double> SpeedRates { get; } = [0.25, 0.5, 1.5, 2, 4, 8];

    /// <summary>
    /// The speed change that contains <paramref name="sourceTime"/>, or null. It contains its
    /// start and not its end, as in the time map.
    /// </summary>
    public int? GetSpeedIndexAt(double sourceTime)
    {
        var speed = Project.Edits.Speed;
        for (var i = 0; i < speed.Length; i++)
        {
            if (speed[i].Start <= sourceTime && sourceTime < speed[i].End)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// How fast a playing preview goes at <paramref name="sourceTime"/>: how many seconds of the
    /// recording pass in one second, as in the video. 1 outside every speed change, and where
    /// the video keeps nothing.
    /// </summary>
    public double GetPlaybackRate(double sourceTime) => TimeMap.GetRate(sourceTime);

    /// <summary>
    /// Adds a speed change that starts at <paramref name="sourceTime"/>, plays at
    /// <see cref="NewSpeedRate"/>, and covers <see cref="NewSpeedDuration"/> of the recording, or
    /// up to the next speed change or the end of the recording when that comes sooner.
    /// </summary>
    /// <returns>
    /// The new speed change's index. Unchanged with the index of the one that is already there,
    /// and unchanged with no index when there is no room for <see cref="MinimumSpeedDuration"/> or
    /// the video would become too short.
    /// </returns>
    public StudioSpeedEditResult AddSpeed(double sourceTime)
    {
        if (!double.IsFinite(sourceTime))
        {
            return new StudioSpeedEditResult(false, null);
        }

        var start = Clamp(sourceTime, 0, SourceDuration);
        if (GetSpeedIndexAt(start) is { } existing)
        {
            return new StudioSpeedEditResult(false, existing);
        }

        if (!TryGetNewSpeedPlace(start, out var index, out var end))
        {
            return new StudioSpeedEditResult(false, null);
        }

        return SetSpeed(WithNewSpeed(index, start, end))
            ? new StudioSpeedEditResult(true, index)
            : new StudioSpeedEditResult(false, null);
    }

    /// <summary>
    /// Whether <see cref="AddSpeed"/> at this time has a speed change to answer with: a new one,
    /// or the one that is already there.
    /// </summary>
    public bool CanAddSpeed(double sourceTime)
    {
        if (!double.IsFinite(sourceTime))
        {
            return false;
        }

        var start = Clamp(sourceTime, 0, SourceDuration);
        if (GetSpeedIndexAt(start) is not null)
        {
            return true;
        }

        return TryGetNewSpeedPlace(start, out var index, out var end)
            && LeavesEnoughVideo(Project.Edits with { Speed = WithNewSpeed(index, start, end) });
    }

    /// <summary>
    /// Deletes a speed change, so its stretch plays at the recording's own speed again. Not made
    /// when that would leave too little video, which only a slower stretch in a video of a few
    /// frames can.
    /// </summary>
    public StudioSpeedEditResult RemoveSpeed(int index)
    {
        if (!TryGetSpeed(index, out _))
        {
            return new StudioSpeedEditResult(false, null);
        }

        var speed = Project.Edits.Speed;
        StudioSpeedRange[] updated = [.. speed[..index], .. speed[(index + 1)..]];
        return SetSpeed(updated) ? new StudioSpeedEditResult(true, null) : new StudioSpeedEditResult(false, index);
    }

    /// <summary>
    /// Moves a speed change's start. It stays at or after the end of the one before it, and at
    /// least <see cref="MinimumSpeedDuration"/> before its own end.
    /// </summary>
    public StudioSpeedEditResult SetSpeedStart(int index, double sourceTime)
    {
        if (!TryGetSpeed(index, out var entry))
        {
            return new StudioSpeedEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioSpeedEditResult(false, index);
        }

        // Speed changes never overlap, so where both limits cannot be kept the one before decides.
        var earliest = index > 0 ? Project.Edits.Speed[index - 1].End : 0;
        var latest = entry.End - MinimumSpeedDuration;
        return ReplaceSpeed(index, entry with { Start = Math.Max(earliest, Math.Min(sourceTime, latest)) });
    }

    /// <summary>
    /// Moves a speed change's end. It stays at least <see cref="MinimumSpeedDuration"/> after its
    /// own start, and at or before the start of the next one and the end of the recording.
    /// </summary>
    public StudioSpeedEditResult SetSpeedEnd(int index, double sourceTime)
    {
        if (!TryGetSpeed(index, out var entry))
        {
            return new StudioSpeedEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioSpeedEditResult(false, index);
        }

        var speed = Project.Edits.Speed;
        var earliest = entry.Start + MinimumSpeedDuration;
        var latest = index + 1 < speed.Length ? speed[index + 1].Start : SourceDuration;
        return ReplaceSpeed(index, entry with { End = Math.Min(Math.Max(sourceTime, earliest), latest) });
    }

    /// <summary>
    /// Moves a whole speed change so it starts at <paramref name="sourceTime"/>, keeping its
    /// length and its rate. It stays between the one before it and the one after it, or the ends
    /// of the recording.
    /// </summary>
    public StudioSpeedEditResult MoveSpeed(int index, double sourceTime)
    {
        if (!TryGetSpeed(index, out var entry))
        {
            return new StudioSpeedEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioSpeedEditResult(false, index);
        }

        var speed = Project.Edits.Speed;
        var length = entry.End - entry.Start;
        var earliest = index > 0 ? speed[index - 1].End : 0;
        var latestEnd = index + 1 < speed.Length ? speed[index + 1].Start : SourceDuration;

        double start;
        double end;
        if (sourceTime <= earliest)
        {
            start = earliest;
            end = Math.Min(earliest + length, latestEnd);
        }
        else if (sourceTime + length >= latestEnd)
        {
            end = latestEnd;
            start = Math.Max(earliest, latestEnd - length);
        }
        else
        {
            start = sourceTime;
            end = sourceTime + length;
        }

        return end > start
            ? ReplaceSpeed(index, entry with { Start = start, End = end })
            : new StudioSpeedEditResult(false, index);
    }

    /// <summary>
    /// Sets how fast a speed change plays: how many seconds of the recording pass in one second
    /// of video. A rate outside what the time map plays counts as the nearer limit. A rate of 1
    /// is no speed change and is not taken; deleting it is how a stretch goes back to the
    /// recording's own speed.
    /// </summary>
    public StudioSpeedEditResult SetSpeedRate(int index, double rate)
    {
        if (!TryGetSpeed(index, out var entry))
        {
            return new StudioSpeedEditResult(false, null);
        }

        if (!double.IsFinite(rate) || rate <= 0)
        {
            return new StudioSpeedEditResult(false, index);
        }

        var limited = Clamp(rate, StudioTimeMap.SlowestRate, StudioTimeMap.FastestRate);
        return limited == 1
            ? new StudioSpeedEditResult(false, index)
            : ReplaceSpeed(index, entry with { Rate = limited });
    }

    /// <summary>
    /// The speed change after the selected one, for stepping through them. With nothing selected,
    /// the one at <paramref name="sourceTime"/> or the first one after it. Null when there is none.
    /// </summary>
    public int? GetSpeedIndexAfter(int? selectedIndex, double sourceTime)
    {
        var speed = Project.Edits.Speed;
        if (selectedIndex is { } selected && selected >= 0 && selected < speed.Length)
        {
            return selected + 1 < speed.Length ? selected + 1 : null;
        }

        var index = Array.FindIndex(speed, entry => entry.End > sourceTime);
        return index >= 0 ? index : null;
    }

    /// <summary>
    /// The speed change before the selected one. With nothing selected, the one at
    /// <paramref name="sourceTime"/> or the last one before it. Null when there is none.
    /// </summary>
    public int? GetSpeedIndexBefore(int? selectedIndex, double sourceTime)
    {
        var speed = Project.Edits.Speed;
        if (selectedIndex is { } selected && selected >= 0 && selected < speed.Length)
        {
            return selected > 0 ? selected - 1 : null;
        }

        var index = Array.FindLastIndex(speed, entry => entry.Start <= sourceTime);
        return index >= 0 ? index : null;
    }

    /// <summary>
    /// Where the speed change at <paramref name="index"/> of <paramref name="before"/> is in
    /// <paramref name="after"/>, for an edit that did not say, such as undo. When the two lists
    /// differ in that one speed change at most, it is that one, changed: the same place.
    /// Otherwise it is the one that shares the most time with it, and among equals the one
    /// nearest to where it was. Null when there was no such speed change, or none shares any time
    /// with it.
    /// </summary>
    public static int? FindSpeedFollowing(IReadOnlyList<StudioSpeedRange> before, int index, IReadOnlyList<StudioSpeedRange> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (index < 0 || index >= before.Count)
        {
            return null;
        }

        if (before.Count == after.Count)
        {
            var othersAreTheSame = true;
            for (var i = 0; i < before.Count && othersAreTheSame; i++)
            {
                othersAreTheSame = i == index
                    || (before[i].Start == after[i].Start && before[i].End == after[i].End && before[i].Rate == after[i].Rate);
            }

            if (othersAreTheSame)
            {
                return index;
            }
        }

        var entry = before[index];
        int? best = null;
        var bestShared = 0.0;
        for (var i = 0; i < after.Count; i++)
        {
            var shared = Math.Min(entry.End, after[i].End) - Math.Max(entry.Start, after[i].Start);
            if (!(shared > 0))
            {
                continue;
            }

            if (best is not { } current
                || shared > bestShared
                || (shared == bestShared && Math.Abs(i - index) < Math.Abs(current - index)))
            {
                best = i;
                bestShared = shared;
            }
        }

        return best;
    }

    /// <summary>
    /// The speed changes of a stored project as the editor keeps them: the ones the time map
    /// counts, in time order and clear of each other as it reads them (section 7 of the project
    /// format), and inside the recording. So opening a project and saving it again does not
    /// change how it plays.
    /// </summary>
    internal static StudioSpeedRange[] NormalizeStoredSpeed(IEnumerable<StudioSpeedRange?>? speed, double sourceDuration) =>
    [
        .. StudioTimeMap.NormalizeSpeed(speed)
            .Select(entry => entry with { Start = Clamp(entry.Start, 0, sourceDuration), End = Clamp(entry.End, 0, sourceDuration) })
            .Where(static entry => entry.End > entry.Start),
    ];

    // Replaces the speed changes unless that would leave too little video. A faster stretch makes
    // the video shorter, and so does taking a slower one away. True when the project changed.
    private bool SetSpeed(StudioSpeedRange[] speed)
    {
        var edits = Project.Edits with { Speed = speed };
        if (!LeavesEnoughVideo(edits))
        {
            return false;
        }

        var before = Project;
        Mutate(project => project with { Edits = edits });
        return !ReferenceEquals(before, Project);
    }

    // Its neighbors keep a speed change in its place in the list, so its index stays.
    private StudioSpeedEditResult ReplaceSpeed(int index, StudioSpeedRange entry)
    {
        var updated = (StudioSpeedRange[])Project.Edits.Speed.Clone();
        updated[index] = entry;
        return new StudioSpeedEditResult(SetSpeed(updated), index);
    }

    private StudioSpeedRange[] WithNewSpeed(int index, double start, double end)
    {
        var speed = Project.Edits.Speed;
        return [.. speed[..index], new StudioSpeedRange { Start = start, End = end, Rate = NewSpeedRate }, .. speed[index..]];
    }

    private bool TryGetSpeed(int index, [NotNullWhen(true)] out StudioSpeedRange? entry)
    {
        var speed = Project.Edits.Speed;
        entry = index >= 0 && index < speed.Length ? speed[index] : null;
        return entry is not null;
    }

    // Where a speed change starting at a time that none contains goes in the list, and where it
    // ends: after NewSpeedDuration, or at the next one or the end of the recording when that comes
    // sooner. False when that leaves less than the shortest speed change.
    private bool TryGetNewSpeedPlace(double start, out int index, out double end)
    {
        var speed = Project.Edits.Speed;
        index = Array.FindIndex(speed, entry => entry.Start > start);
        if (index < 0)
        {
            index = speed.Length;
        }

        var nextStart = index < speed.Length ? speed[index].Start : SourceDuration;
        end = Math.Min(start + NewSpeedDuration, nextStart);
        return end - start >= MinimumSpeedDuration;
    }
}
