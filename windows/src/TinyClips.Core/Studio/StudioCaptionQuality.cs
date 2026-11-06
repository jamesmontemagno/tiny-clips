using System.Text;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Studio;

/// <summary>What a check of the captions can find: section 15.6 of the project format, in its order.</summary>
public enum StudioCaptionFindingKind
{
    InvalidTiming,
    Overlap,
    Empty,
    Whitespace,
    LongLine,
    TooManyLines,
    ShortDuration,
    ReadingSpeed,
}

/// <param name="Cue">The cue's place in time order, from 0.</param>
/// <param name="Kind">What was found.</param>
/// <param name="CanFix">Whether <see cref="StudioCaptionQuality.Fix"/> can do something about it.</param>
public readonly record struct StudioCaptionFinding(int Cue, StudioCaptionFindingKind Kind, bool CanFix);

/// <param name="Cues">The cues after the fixes, in time order.</param>
/// <param name="Applied">How many things were changed.</param>
public sealed record StudioCaptionFixResult(StudioCaptionCue[] Cues, int Applied);

/// <summary>
/// Limits for reading captions, and the fixes that are safe to make: section 15.6 of the project
/// format. They come from OutroKit's CaptionQualityService.
/// </summary>
public static class StudioCaptionQuality
{
    public const double ShortestDuration = 0.7;
    public const int LongestLine = 42;
    public const int MostLines = 2;
    public const double FastestRate = 20;

    /// <summary>The most a fix moves a cue's start earlier, or takes from a neighbour.</summary>
    public const double MostMoved = 0.5;

    private const int MostParts = 64;

    /// <summary>The cues in the order the checks and <see cref="StudioCaptionResolver"/> use.</summary>
    public static StudioCaptionCue[] InOrder(IEnumerable<StudioCaptionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return cues.OrderBy(static cue => cue.Start).ThenBy(static cue => cue.End).ToArray();
    }

    /// <summary>What is found in the cues of a recording <paramref name="sourceDuration"/> seconds long.</summary>
    public static StudioCaptionFinding[] Check(IEnumerable<StudioCaptionCue> cues, double sourceDuration)
    {
        var ordered = InOrder(cues);
        var tidiedCues = ordered.Select(static cue => cue with { Text = Tidy(cue.Text) }).ToArray();
        var findings = new List<StudioCaptionFinding>();
        StudioCaptionCue? previous = null;
        for (var index = 0; index < ordered.Length; index++)
        {
            var cue = ordered[index];
            if (!double.IsFinite(cue.Start) || !double.IsFinite(cue.End) || cue.End <= cue.Start)
            {
                findings.Add(new(index, StudioCaptionFindingKind.InvalidTiming, false));
                continue;
            }

            if (previous is not null && cue.Start < previous.End)
            {
                findings.Add(new(index, StudioCaptionFindingKind.Overlap, cue.Start - previous.Start >= ShortestDuration));
            }

            if (tidiedCues[index].Text.Length == 0)
            {
                findings.Add(new(index, StudioCaptionFindingKind.Empty, true));
            }
            else
            {
                CheckText(findings, index, cue, tidiedCues, sourceDuration);
            }

            previous = cue;
        }

        return [.. findings];
    }

    /// <summary>Makes the safe fixes, in the order of the format. The cues given are left as they are.</summary>
    public static StudioCaptionFixResult Fix(IEnumerable<StudioCaptionCue> cues, double sourceDuration)
    {
        var ordered = InOrder(cues);
        var kept = ordered.Where(static cue => Tidy(cue.Text).Length > 0).ToArray();
        var applied = ordered.Length - kept.Length;

        for (var index = 0; index < kept.Length; index++)
        {
            var tidied = Tidy(kept[index].Text);
            if (!string.Equals(tidied, kept[index].Text, StringComparison.Ordinal))
            {
                kept[index] = kept[index] with { Text = tidied };
                applied++;
            }
        }

        for (var index = 1; index < kept.Length; index++)
        {
            if (kept[index].Start < kept[index - 1].End && kept[index].Start - kept[index - 1].Start >= ShortestDuration)
            {
                kept[index - 1] = kept[index - 1] with { End = kept[index].Start };
                applied++;
            }
        }

        var laidOut = new List<StudioCaptionCue>(kept.Length);
        foreach (var cue in kept)
        {
            applied += LayOut(cue, laidOut);
        }

        for (var index = 0; index < laidOut.Count; index++)
        {
            var cue = laidOut[index];
            var length = cue.End - cue.Start;
            if (!(length > 0))
            {
                continue;
            }

            var wanted = Wanted(length, cue.Text);
            if (wanted > length && cue.Start + wanted <= Limit(laidOut, index, sourceDuration))
            {
                laidOut[index] = cue with { End = cue.Start + wanted };
                applied++;
            }
        }

        applied += Borrow(laidOut, sourceDuration);
        applied += JoinHardCues(laidOut);
        return new StudioCaptionFixResult([.. laidOut], applied);
    }

    /// <summary>The findings by count, such as "2 long lines, 1 too short". Empty with none.</summary>
    public static string GetSummary(IEnumerable<StudioCaptionFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var counts = findings.GroupBy(static finding => finding.Kind).ToDictionary(static group => group.Key, static group => group.Count());
        var parts = new List<string>();
        foreach (var kind in Enum.GetValues<StudioCaptionFindingKind>())
        {
            if (counts.TryGetValue(kind, out var count))
            {
                parts.Add($"{count} {GetKindText(kind, count)}");
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>The name of a kind in the project format and the agent contract.</summary>
    public static string GetKindName(StudioCaptionFindingKind kind) => kind switch
    {
        StudioCaptionFindingKind.InvalidTiming => "invalidTiming",
        StudioCaptionFindingKind.Overlap => "overlap",
        StudioCaptionFindingKind.Empty => "empty",
        StudioCaptionFindingKind.Whitespace => "whitespace",
        StudioCaptionFindingKind.LongLine => "longLine",
        StudioCaptionFindingKind.TooManyLines => "tooManyLines",
        StudioCaptionFindingKind.ShortDuration => "shortDuration",
        _ => "readingSpeed",
    };

    /// <summary>Each line with single spaces and nothing around it, and no blank lines.</summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => string.Join(' ', Words(line)))
            .Where(static line => line.Length > 0);
        return string.Join('\n', lines);
    }

    private static void CheckText(
        List<StudioCaptionFinding> findings,
        int index,
        StudioCaptionCue cue,
        IReadOnlyList<StudioCaptionCue> tidiedCues,
        double sourceDuration)
    {
        var tidied = tidiedCues[index].Text;
        if (!string.Equals(tidied, cue.Text, StringComparison.Ordinal))
        {
            findings.Add(new(index, StudioCaptionFindingKind.Whitespace, true));
        }

        var lines = tidied.Split('\n');
        var isLong = lines.Any(static line => Length(line) > LongestLine);
        var isTall = lines.Length > MostLines;
        if (isLong || isTall)
        {
            var canFix = Wrap(tidied) is not null || CanSplit(cue, Split(tidied));
            if (isLong)
            {
                findings.Add(new(index, StudioCaptionFindingKind.LongLine, canFix));
            }

            if (isTall)
            {
                findings.Add(new(index, StudioCaptionFindingKind.TooManyLines, canFix));
            }
        }

        var length = cue.End - cue.Start;
        var canFixTime = false;
        if (IsHard(tidiedCues[index]))
        {
            var more = Enough(tidied) - length;
            var (after, before) = GetRoom(tidiedCues, index, sourceDuration);
            canFixTime = cue.Start + Wanted(length, tidied) <= Limit(tidiedCues, index, sourceDuration)
                || after + before >= more
                || CanJoin(tidiedCues, index);
        }

        if (length < ShortestDuration)
        {
            findings.Add(new(index, StudioCaptionFindingKind.ShortDuration, canFixTime));
        }

        if (Count(tidied) / length > FastestRate)
        {
            findings.Add(new(index, StudioCaptionFindingKind.ReadingSpeed, canFixTime));
        }
    }

    // Lengthens each cue still too hard to read into the time its neighbours can spare: its end
    // later and its start a little earlier. Says how many cues that was.
    private static int Borrow(List<StudioCaptionCue> cues, double sourceDuration)
    {
        var applied = 0;
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (!IsPresent(cue) || !IsHard(cue))
            {
                continue;
            }

            var more = Enough(cue.Text) - (cue.End - cue.Start);
            var (after, before) = GetRoom(cues, index, sourceDuration);
            if (after + before < more)
            {
                continue;
            }

            var later = Math.Min(more, after);
            var earlier = more - later;
            if (later > 0)
            {
                cues[index] = cues[index] with { End = cues[index].End + later };
                if (index + 1 < cues.Count)
                {
                    cues[index + 1] = cues[index + 1] with { Start = Math.Max(cues[index + 1].Start, cues[index].End) };
                }
            }

            if (earlier > 0)
            {
                cues[index] = cues[index] with { Start = cues[index].Start - earlier };
                if (index > 0)
                {
                    cues[index - 1] = cues[index - 1] with { End = Math.Min(cues[index - 1].End, cues[index].Start) };
                }
            }

            applied++;
        }

        return applied;
    }

    // Puts each cue still too hard to read together with a neighbour, when the two read as one.
    // Says how many joins that was.
    private static int JoinHardCues(List<StudioCaptionCue> cues)
    {
        var applied = 0;
        for (var index = 0; index < cues.Count; index++)
        {
            if (!IsPresent(cues[index]) || !IsHard(cues[index]))
            {
                continue;
            }

            foreach (var first in GetJoinOrder(cues, index))
            {
                if (first < 0 || first + 1 >= cues.Count || Join(cues[first], cues[first + 1]) is not { } joined)
                {
                    continue;
                }

                cues[first] = joined;
                cues.RemoveAt(first + 1);
                applied++;
                index = first;
                break;
            }
        }

        return applied;
    }

    // The least time the text reads in, and a millisecond so rounding cannot leave a cue just short.
    private static double Enough(string text) => Math.Max(ShortestDuration, Count(text) / FastestRate) + 0.001;

    // Whether the cue has a time and text, so it can give time to or join a neighbour.
    private static bool IsPresent(StudioCaptionCue cue) =>
        double.IsFinite(cue.Start) && double.IsFinite(cue.End) && cue.End > cue.Start && cue.Text.Length > 0;

    private static bool IsHard(StudioCaptionCue cue)
    {
        var length = cue.End - cue.Start;
        return length < ShortestDuration || Count(cue.Text) / length > FastestRate;
    }

    private static double Spare(StudioCaptionCue cue) =>
        IsPresent(cue) ? Math.Min(MostMoved, Math.Max(0, cue.End - cue.Start - Enough(cue.Text))) : 0;

    // The seconds the cue at index can take after its end and before its start.
    private static (double After, double Before) GetRoom(IReadOnlyList<StudioCaptionCue> cues, int index, double sourceDuration)
    {
        var cue = cues[index];
        double after;
        if (index + 1 == cues.Count)
        {
            after = sourceDuration - cue.End;
        }
        else
        {
            var next = cues[index + 1];
            after = !IsPresent(next) || next.Start < cue.End ? 0 : Math.Min(sourceDuration, next.Start + Spare(next)) - cue.End;
        }

        double before;
        if (index == 0)
        {
            before = Math.Min(MostMoved, cue.Start);
        }
        else
        {
            var previous = cues[index - 1];
            before = !IsPresent(previous) || previous.End > cue.Start
                ? 0
                : Math.Min(MostMoved, cue.Start - (previous.End - Spare(previous)));
        }

        return (Math.Max(0, after), Math.Max(0, before));
    }

    private static StudioCaptionCue? Join(StudioCaptionCue first, StudioCaptionCue second)
    {
        if (!IsPresent(first) || !IsPresent(second))
        {
            return null;
        }

        var gap = second.Start - first.End;
        if (gap < 0 || gap > MostMoved || Wrap(first.Text + " " + second.Text) is not { } text || second.End - first.Start < Enough(text))
        {
            return null;
        }

        var spellsBoth = new[] { first, second }.All(static cue => cue.Words.Length > 0 && StudioCaptionTranscript.Spells(cue.Words, cue.Text));
        return first with { End = second.End, Text = text, Words = spellsBoth ? [.. first.Words, .. second.Words] : [] };
    }

    // Where a cue too hard to read looks for one to join: the one before it first, unless that ends a sentence.
    private static int[] GetJoinOrder(IReadOnlyList<StudioCaptionCue> cues, int index)
    {
        var endsSentence = index > 0 && cues[index - 1].Text is { Length: > 0 } text && text[^1] is '.' or '!' or '?';
        return index > 0 && !endsSentence ? [index - 1, index] : [index, index - 1];
    }

    private static bool CanJoin(IReadOnlyList<StudioCaptionCue> cues, int index) =>
        GetJoinOrder(cues, index).Any(first => first >= 0 && first + 1 < cues.Count && Join(cues[first], cues[first + 1]) is not null);

    // Adds the cue to the list, wrapped or split where it has to be, and says how many fixes that was.
    private static int LayOut(StudioCaptionCue cue, List<StudioCaptionCue> laidOut)
    {
        var lines = cue.Text.Split('\n');
        if (lines.Length <= MostLines && lines.All(static line => Length(line) <= LongestLine))
        {
            laidOut.Add(cue);
            return 0;
        }

        if (Wrap(cue.Text) is { } wrapped)
        {
            laidOut.Add(cue with { Text = wrapped });
            return 1;
        }

        var parts = Split(cue.Text);
        if (!CanSplit(cue, parts))
        {
            laidOut.Add(cue);
            return 0;
        }

        var timings = SplitTiming(cue.Start, cue.End, parts!);
        for (var part = 0; part < parts!.Count; part++)
        {
            laidOut.Add(cue with
            {
                Id = part == 0 ? cue.Id : Guid.NewGuid().ToString("D"),
                Start = timings[part].Start,
                End = timings[part].End,
                Text = parts[part],
                Words = [],
            });
        }

        return 1;
    }

    private static double Limit(IReadOnlyList<StudioCaptionCue> ordered, int index, double sourceDuration) =>
        index + 1 == ordered.Count ? sourceDuration : Math.Min(ordered[index + 1].Start, sourceDuration);

    private static double Wanted(double length, string text)
    {
        var target = length < ShortestDuration ? 1 : length;
        var reading = Count(text) / FastestRate;
        return reading > target ? reading : target;
    }

    private static string? Wrap(string text)
    {
        var words = Words(text);
        if (words.Length == 0 || words.Any(static word => Length(word) > LongestLine))
        {
            return null;
        }

        for (var count = 1; count <= MostLines; count++)
        {
            var lines = Balance(words, count).Select(static group => string.Join(' ', group)).ToArray();
            if (lines.All(static line => Length(line) <= LongestLine))
            {
                return string.Join('\n', lines);
            }
        }

        return null;
    }

    private static List<string>? Split(string text)
    {
        var words = Words(text);
        if (words.Length < 2 || words.Any(static word => Length(word) > LongestLine))
        {
            return null;
        }

        var first = Math.Max(2, (int)Math.Ceiling(Length(string.Join(' ', words)) / (double)(LongestLine * MostLines)));
        for (var count = first; count <= Math.Min(MostParts, words.Length); count++)
        {
            var parts = Balance(words, count).Select(static group => Wrap(string.Join(' ', group))).ToArray();
            if (parts.All(static part => part is not null))
            {
                return parts.Select(static part => part!).ToList();
            }
        }

        return null;
    }

    private static bool CanSplit(StudioCaptionCue cue, List<string>? parts) =>
        parts is not null && SplitTiming(cue.Start, cue.End, parts).All(static timing => timing.End - timing.Start >= ShortestDuration);

    private static List<string[]> Balance(string[] words, int count)
    {
        if (count <= 1)
        {
            return [words];
        }

        if (words.Length <= count)
        {
            return words.Select(static word => new[] { word }).ToList();
        }

        var groups = new List<string[]>(count);
        var cursor = 0;
        for (var group = 1; group < count; group++)
        {
            var target = Length(string.Join(' ', words[cursor..])) / (double)(count - group + 1);
            var best = cursor + 1;
            var bestScore = double.PositiveInfinity;
            var length = -1;
            for (var index = cursor; index < words.Length - (count - group); index++)
            {
                length += Length(words[index]) + 1;
                var score = Math.Abs(length - target) - Bonus(words[index]);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = index + 1;
                }
            }

            groups.Add(words[cursor..best]);
            cursor = best;
        }

        groups.Add(words[cursor..]);
        return groups;
    }

    private static int Bonus(string word) => word[^1] switch
    {
        '.' or '!' or '?' => 6,
        ',' or ';' or ':' => 3,
        _ => 0,
    };

    private static List<(double Start, double End)> SplitTiming(double start, double end, List<string> parts)
    {
        var weights = parts.Select(static part => Math.Max(1, Count(part))).ToArray();
        var total = weights.Sum();
        var length = Math.Max(0, end - start);
        var timings = new List<(double Start, double End)>(parts.Count);
        var cursor = start;
        for (var index = 0; index < parts.Count; index++)
        {
            var following = index == parts.Count - 1 ? end : cursor + (length * (weights[index] / (double)total));
            timings.Add((cursor, following));
            cursor = following;
        }

        return timings;
    }

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // A character is a Unicode code point, on every platform.
    private static int Length(string text) => text.EnumerateRunes().Count();

    private static int Count(string text) => text.EnumerateRunes().Count(static rune => !Rune.IsWhiteSpace(rune));

    private static string GetKindText(StudioCaptionFindingKind kind, int count) => kind switch
    {
        StudioCaptionFindingKind.InvalidTiming => count == 1 ? "invalid time" : "invalid times",
        StudioCaptionFindingKind.Overlap => count == 1 ? "overlap" : "overlaps",
        StudioCaptionFindingKind.Empty => count == 1 ? "empty caption" : "empty captions",
        StudioCaptionFindingKind.Whitespace => "with extra space",
        StudioCaptionFindingKind.LongLine => count == 1 ? "long line" : "long lines",
        StudioCaptionFindingKind.TooManyLines => "with too many lines",
        StudioCaptionFindingKind.ShortDuration => "too short",
        _ => "too fast to read",
    };
}
