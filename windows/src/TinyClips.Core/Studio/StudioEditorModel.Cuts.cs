using System.Diagnostics.CodeAnalysis;

namespace TinyClips.Core.Studio;

/// <summary>
/// What an edit to a cut did: whether the project changed, and where the cut is in
/// <see cref="StudioEdits.Cuts"/> afterwards. The index is null when there is no such cut, which
/// is also the case once it has been deleted.
/// </summary>
public readonly record struct StudioCutEditResult(bool Changed, int? Index);

// Cuts: stretches of the recording the video leaves out. They are stored in source time, so a cut
// moves nothing else. The list is kept in time order without overlaps, so a cut's index identifies
// it between two edits. Two cuts may touch; the time map plays them as one.
public sealed partial class StudioEditorModel
{
    /// <summary>The shortest cut the editor makes, in seconds.</summary>
    public const double MinimumCutDuration = 0.1;

    /// <summary>How long a cut is when it is added, in seconds, where there is room for it.</summary>
    public const double NewCutDuration = 1;

    /// <summary>
    /// The first instant the video shows: the trim start, or the end of a cut that begins there.
    /// The trim start when cuts leave nothing.
    /// </summary>
    public double PlaybackStart => TimeMap.Segments is { Count: > 0 } segments ? segments[0].Start : TrimStart;

    /// <summary>
    /// Where the video ends: the trim end, or the start of a cut that runs up to it. The trim start
    /// when cuts leave nothing.
    /// </summary>
    public double PlaybackEnd => TimeMap.Segments is { Count: > 0 } segments ? segments[^1].End : TrimStart;

    /// <summary>
    /// The cut that contains <paramref name="sourceTime"/>, or null. A cut contains its start and
    /// not its end, as in the time map: the frame at its end is the first one kept again.
    /// </summary>
    public int? GetCutIndexAt(double sourceTime)
    {
        var cuts = Project.Edits.Cuts;
        for (var i = 0; i < cuts.Length; i++)
        {
            if (cuts[i].Start <= sourceTime && sourceTime < cuts[i].End)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Where a playing preview goes on from when it has reached a stretch the video leaves out:
    /// the next instant that is kept. Null where <paramref name="sourceTime"/> is kept itself, and
    /// outside what the video shows, where playback starts or stops and does not jump.
    /// </summary>
    public double? GetCutSkipTarget(double sourceTime)
    {
        var segments = TimeMap.Segments;
        for (var i = 0; i + 1 < segments.Count; i++)
        {
            if (sourceTime >= segments[i].End && sourceTime < segments[i + 1].Start)
            {
                return segments[i + 1].Start;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds a cut that starts at <paramref name="sourceTime"/> and lasts
    /// <see cref="NewCutDuration"/>, or until the next cut or the end of the recording when that
    /// comes sooner.
    /// </summary>
    /// <returns>
    /// The new cut's index. Unchanged with the index of the cut that is already there, and
    /// unchanged with no index when there is no room for <see cref="MinimumCutDuration"/> or the
    /// cut would leave no video.
    /// </returns>
    public StudioCutEditResult AddCut(double sourceTime)
    {
        if (!double.IsFinite(sourceTime))
        {
            return new StudioCutEditResult(false, null);
        }

        var start = Clamp(sourceTime, 0, SourceDuration);
        if (GetCutIndexAt(start) is { } existing)
        {
            return new StudioCutEditResult(false, existing);
        }

        if (!TryGetNewCutPlace(start, out var index, out var end))
        {
            return new StudioCutEditResult(false, null);
        }

        var cuts = Project.Edits.Cuts;
        StudioTimeRange[] updated = [.. cuts[..index], new StudioTimeRange { Start = start, End = end }, .. cuts[index..]];
        return SetCuts(updated) ? new StudioCutEditResult(true, index) : new StudioCutEditResult(false, null);
    }

    /// <summary>
    /// Whether <see cref="AddCut"/> at this time has a cut to answer with: a new one, or the one
    /// that is already there.
    /// </summary>
    public bool CanAddCut(double sourceTime)
    {
        if (!double.IsFinite(sourceTime))
        {
            return false;
        }

        var start = Clamp(sourceTime, 0, SourceDuration);
        if (GetCutIndexAt(start) is not null)
        {
            return true;
        }

        if (!TryGetNewCutPlace(start, out var index, out var end))
        {
            return false;
        }

        var cuts = Project.Edits.Cuts;
        return LeavesEnoughVideo(Project.Edits with
        {
            Cuts = [.. cuts[..index], new StudioTimeRange { Start = start, End = end }, .. cuts[index..]],
        });
    }

    /// <summary>Deletes a cut, which puts its stretch back into the video.</summary>
    public StudioCutEditResult RemoveCut(int index)
    {
        if (!TryGetCut(index, out _))
        {
            return new StudioCutEditResult(false, null);
        }

        var cuts = Project.Edits.Cuts;
        StudioTimeRange[] updated = [.. cuts[..index], .. cuts[(index + 1)..]];
        Mutate(project => project with { Edits = project.Edits with { Cuts = updated } });
        return new StudioCutEditResult(true, null);
    }

    /// <summary>
    /// Moves a cut's start. It stays at or after the end of the cut before it, and at least
    /// <see cref="MinimumCutDuration"/> before its own end.
    /// </summary>
    public StudioCutEditResult SetCutStart(int index, double sourceTime)
    {
        if (!TryGetCut(index, out var cut))
        {
            return new StudioCutEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioCutEditResult(false, index);
        }

        // Cuts never overlap, so where both limits cannot be kept the cut before decides.
        var earliest = index > 0 ? Project.Edits.Cuts[index - 1].End : 0;
        var latest = cut.End - MinimumCutDuration;
        return ReplaceCut(index, Math.Max(earliest, Math.Min(sourceTime, latest)), cut.End);
    }

    /// <summary>
    /// Moves a cut's end. It stays at least <see cref="MinimumCutDuration"/> after its own start,
    /// and at or before the start of the next cut and the end of the recording.
    /// </summary>
    public StudioCutEditResult SetCutEnd(int index, double sourceTime)
    {
        if (!TryGetCut(index, out var cut))
        {
            return new StudioCutEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioCutEditResult(false, index);
        }

        var cuts = Project.Edits.Cuts;
        var earliest = cut.Start + MinimumCutDuration;
        var latest = index + 1 < cuts.Length ? cuts[index + 1].Start : SourceDuration;
        return ReplaceCut(index, cut.Start, Math.Min(Math.Max(sourceTime, earliest), latest));
    }

    /// <summary>
    /// Moves a whole cut so it starts at <paramref name="sourceTime"/>, keeping its length. It
    /// stays between the cut before it and the cut after it, or the ends of the recording.
    /// </summary>
    public StudioCutEditResult MoveCut(int index, double sourceTime)
    {
        if (!TryGetCut(index, out var cut))
        {
            return new StudioCutEditResult(false, null);
        }

        if (!double.IsFinite(sourceTime))
        {
            return new StudioCutEditResult(false, index);
        }

        var cuts = Project.Edits.Cuts;
        var length = cut.End - cut.Start;
        var earliest = index > 0 ? cuts[index - 1].End : 0;
        var latestEnd = index + 1 < cuts.Length ? cuts[index + 1].Start : SourceDuration;

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

        return end > start ? ReplaceCut(index, start, end) : new StudioCutEditResult(false, index);
    }

    /// <summary>
    /// The cut after the selected one, for stepping through the cuts. With nothing selected, the
    /// cut at <paramref name="sourceTime"/> or the first one after it. Null when there is none.
    /// </summary>
    public int? GetCutIndexAfter(int? selectedIndex, double sourceTime)
    {
        var cuts = Project.Edits.Cuts;
        if (selectedIndex is { } selected && selected >= 0 && selected < cuts.Length)
        {
            return selected + 1 < cuts.Length ? selected + 1 : null;
        }

        var index = Array.FindIndex(cuts, cut => cut.End > sourceTime);
        return index >= 0 ? index : null;
    }

    /// <summary>
    /// The cut before the selected one. With nothing selected, the cut at
    /// <paramref name="sourceTime"/> or the last one before it. Null when there is none.
    /// </summary>
    public int? GetCutIndexBefore(int? selectedIndex, double sourceTime)
    {
        var cuts = Project.Edits.Cuts;
        if (selectedIndex is { } selected && selected >= 0 && selected < cuts.Length)
        {
            return selected > 0 ? selected - 1 : null;
        }

        var index = Array.FindLastIndex(cuts, cut => cut.Start <= sourceTime);
        return index >= 0 ? index : null;
    }

    /// <summary>
    /// Where the cut at <paramref name="index"/> of <paramref name="before"/> is in
    /// <paramref name="after"/>, for an edit that did not say, such as undo. When the two lists
    /// differ in that one cut at most, it is that cut, changed: the same place. Otherwise it is
    /// the cut that shares the most time with it, and among equals the one nearest to where it
    /// was. Null when there was no such cut, or none shares any time with it.
    /// </summary>
    public static int? FindCutFollowing(IReadOnlyList<StudioTimeRange> before, int index, IReadOnlyList<StudioTimeRange> after)
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
                othersAreTheSame = i == index || (before[i].Start == after[i].Start && before[i].End == after[i].End);
            }

            if (othersAreTheSame)
            {
                return index;
            }
        }

        var cut = before[index];
        int? best = null;
        var bestShared = 0.0;
        for (var i = 0; i < after.Count; i++)
        {
            var shared = Math.Min(cut.End, after[i].End) - Math.Max(cut.Start, after[i].Start);
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
    /// The cuts of a stored project as the editor keeps them: inside the recording, in time order,
    /// and without overlaps, which are joined. Ranges that remove nothing are dropped, and with
    /// them any whose start or end is not a number, which compares as neither before nor after.
    /// </summary>
    internal static StudioTimeRange[] NormalizeStoredCuts(IEnumerable<StudioTimeRange?>? cuts, double sourceDuration)
    {
        var ordered = (cuts ?? [])
            .OfType<StudioTimeRange>()
            .Select(cut => cut with { Start = Clamp(cut.Start, 0, sourceDuration), End = Clamp(cut.End, 0, sourceDuration) })
            .Where(static cut => cut.End > cut.Start)
            .OrderBy(static cut => cut.Start)
            .ToList();

        for (var i = 0; i + 1 < ordered.Count;)
        {
            if (ordered[i + 1].Start < ordered[i].End)
            {
                ordered[i] = ordered[i] with { End = Math.Max(ordered[i].End, ordered[i + 1].End) };
                ordered.RemoveAt(i + 1);
            }
            else
            {
                i++;
            }
        }

        return [.. ordered];
    }

    // At least MinimumDuration of video has to stay, or the whole recording when it is shorter.
    private bool LeavesEnoughVideo(StudioEdits edits) =>
        new StudioTimeMap(SourceDuration, edits).OutputDuration >= Math.Min(MinimumDuration, SourceDuration) - 1e-9;

    // Replaces the cuts unless that would leave too little video. True when the project changed.
    private bool SetCuts(StudioTimeRange[] cuts)
    {
        var edits = Project.Edits with { Cuts = cuts };
        if (!LeavesEnoughVideo(edits))
        {
            return false;
        }

        var before = Project;
        Mutate(project => project with { Edits = edits });
        return !ReferenceEquals(before, Project);
    }

    // Its neighbors keep a cut in its place in the list, so its index stays.
    private StudioCutEditResult ReplaceCut(int index, double start, double end)
    {
        var cuts = Project.Edits.Cuts;
        var updated = (StudioTimeRange[])cuts.Clone();
        updated[index] = cuts[index] with { Start = start, End = end };
        return new StudioCutEditResult(SetCuts(updated), index);
    }

    private bool TryGetCut(int index, [NotNullWhen(true)] out StudioTimeRange? cut)
    {
        var cuts = Project.Edits.Cuts;
        cut = index >= 0 && index < cuts.Length ? cuts[index] : null;
        return cut is not null;
    }

    // Where a cut starting at a time that no cut contains goes in the list, and where it ends:
    // after NewCutDuration, or at the next cut or the end of the recording when that comes sooner.
    // False when that leaves less than the shortest cut.
    private bool TryGetNewCutPlace(double start, out int index, out double end)
    {
        var cuts = Project.Edits.Cuts;
        index = Array.FindIndex(cuts, cut => cut.Start > start);
        if (index < 0)
        {
            index = cuts.Length;
        }

        var nextStart = index < cuts.Length ? cuts[index].Start : SourceDuration;
        end = Math.Min(start + NewCutDuration, nextStart);
        return end - start >= MinimumCutDuration;
    }
}
