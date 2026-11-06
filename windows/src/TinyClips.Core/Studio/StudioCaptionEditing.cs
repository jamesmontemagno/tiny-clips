using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Studio;

/// <summary>One end of a caption.</summary>
public enum StudioCaptionEdge
{
    Start,
    End,
}

/// <summary>What an edit of the captions made.</summary>
/// <param name="Cues">Every cue, in time order.</param>
/// <param name="Index">
/// Where the changed cue is in <paramref name="Cues"/>. After a split, the second part, which starts
/// at the split.
/// </param>
public sealed record StudioCaptionEditResult(StudioCaptionCue[] Cues, int Index);

/// <summary>
/// The Captions panel's edits to the cues, all in source time. Each gives back every cue in time
/// order and where the cue it changed is now, or null when the edit cannot be made or would change
/// nothing. No part is made shorter than <see cref="StudioCaptionResolver.ShortestPart"/>. A cue
/// keeps its words only while each word's middle is inside it, as the resolver reads them. The
/// same as <c>StudioCaptionEditing</c> on the Mac.
/// </summary>
public static class StudioCaptionEditing
{
    /// <summary>The places of the cues in time order: by start, then by end, keeping the order of a tie.</summary>
    public static int[] GetIndexesInTimeOrder(IReadOnlyList<StudioCaptionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return Enumerable.Range(0, cues.Count)
            .OrderBy(index => cues[index].Start)
            .ThenBy(index => cues[index].End)
            .ToArray();
    }

    /// <summary>
    /// Moves one end of the cue at <paramref name="index"/> to <paramref name="time"/>, or as near
    /// to it as it can go: the start stays at or after 0 and a shortest part before the end, and
    /// the end a shortest part after the start and at or before the end of the recording.
    /// </summary>
    public static StudioCaptionEditResult? Retime(
        IReadOnlyList<StudioCaptionCue> cues,
        int index,
        StudioCaptionEdge edge,
        double time,
        double sourceDuration)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (index < 0 || index >= cues.Count || !double.IsFinite(time))
        {
            return null;
        }

        const double shortest = StudioCaptionResolver.ShortestPart;
        var cue = cues[index];
        StudioCaptionCue moved;
        if (edge == StudioCaptionEdge.Start)
        {
            if (!double.IsFinite(cue.End) || cue.End < shortest)
            {
                return null;
            }

            moved = cue with { Start = Math.Min(Math.Max(0, time), cue.End - shortest) };
        }
        else
        {
            if (!double.IsFinite(cue.Start))
            {
                return null;
            }

            var earliest = cue.Start + shortest;
            var latest = double.IsFinite(sourceDuration) && sourceDuration > 0
                ? Math.Max(earliest, sourceDuration)
                : double.PositiveInfinity;
            moved = cue with { End = Math.Min(Math.Max(earliest, time), latest) };
        }

        if (moved.Start == cue.Start && moved.End == cue.End)
        {
            return null;
        }

        if (!WordsFit(moved.Words, moved.Start, moved.End))
        {
            moved = moved with { Words = [] };
        }

        var changed = cues.ToArray();
        changed[index] = moved;
        return InTimeOrder(changed, index);
    }

    /// <summary>
    /// Whether <paramref name="cue"/> can be split at <paramref name="time"/>: each part a shortest
    /// part long or more, with at least one word of the text in each.
    /// </summary>
    public static bool CanSplit(StudioCaptionCue cue, double time)
    {
        ArgumentNullException.ThrowIfNull(cue);
        return IsInside(cue, time) && GetSplitText(cue, time) is not null;
    }

    /// <summary>
    /// Where Split cuts <paramref name="cue"/>: at <paramref name="playhead"/> when it can be split
    /// there, and otherwise in its middle, between the words nearest it. Null when it cannot be
    /// split at all.
    /// </summary>
    public static double? GetSplitTime(StudioCaptionCue cue, double playhead)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (CanSplit(cue, playhead))
        {
            return playhead;
        }

        if (!IsTimed(cue))
        {
            return null;
        }

        var middle = (cue.Start + cue.End) / 2;
        if (GetMiddleWordGap(cue, middle) is { } gap && CanSplit(cue, gap))
        {
            return gap;
        }

        return CanSplit(cue, middle) ? middle : null;
    }

    /// <summary>
    /// Splits the cue at <paramref name="index"/> in two at <paramref name="time"/>. The words said
    /// before it go in the first part and the rest in the second, by the cue's word times when it
    /// has them, and otherwise by how far through the cue the time is. The first part keeps the
    /// cue's id, and the second has a new one.
    /// </summary>
    public static StudioCaptionEditResult? Split(IReadOnlyList<StudioCaptionCue> cues, int index, double time)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (index < 0 || index >= cues.Count || !IsInside(cues[index], time) || GetSplitText(cues[index], time) is not { } parts)
        {
            return null;
        }

        var cue = cues[index];
        var first = cue with
        {
            End = time,
            Text = parts.FirstText,
            Words = WordsFit(parts.FirstWords, cue.Start, time) ? parts.FirstWords : [],
        };
        var second = cue with
        {
            Id = Guid.NewGuid().ToString("D"),
            Start = time,
            Text = parts.SecondText,
            Words = WordsFit(parts.SecondWords, time, cue.End) ? parts.SecondWords : [],
        };

        var changed = cues.ToList();
        changed[index] = first;
        changed.Insert(index + 1, second);
        return InTimeOrder(changed, index + 1);
    }

    /// <summary>Whether the cue at <paramref name="index"/> has one after it in time order to be put together with.</summary>
    public static bool CanMergeWithNext(IReadOnlyList<StudioCaptionCue> cues, int index)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return GetNextInTime(cues, index) is { } next && IsTimed(cues[index]) && IsTimed(cues[next]);
    }

    /// <summary>
    /// Puts the cue at <paramref name="index"/> together with the one after it in time order: from
    /// the earlier start to the later end, saying the one and then the other on a line. It keeps
    /// the first's id. Its words are both cues' words when each had words that spell out its text.
    /// </summary>
    public static StudioCaptionEditResult? MergeWithNext(IReadOnlyList<StudioCaptionCue> cues, int index)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (!CanMergeWithNext(cues, index) || GetNextInTime(cues, index) is not { } nextIndex)
        {
            return null;
        }

        var first = cues[index];
        var next = cues[nextIndex];
        var text = string.Join(' ', new[] { first.Text, next.Text }
            .Select(static part => StudioCaptionQuality.Tidy(part).Replace('\n', ' '))
            .Where(static part => part.Length > 0));
        var spellsBoth = HasSpelledWords(first) && HasSpelledWords(next);
        var merged = first with
        {
            Start = Math.Min(first.Start, next.Start),
            End = Math.Max(first.End, next.End),
            Text = text,
            Words = spellsBoth ? [.. first.Words, .. next.Words] : [],
        };

        var changed = cues.ToList();
        changed[index] = merged;
        changed.RemoveAt(nextIndex);
        return InTimeOrder(changed, nextIndex < index ? index - 1 : index);
    }

    /// <summary>
    /// Where in <paramref name="ordered"/>, which is in time order, the cue showing at
    /// <paramref name="time"/> is, or null when none is. A cue ends where the next one starts, as
    /// in section 15.2 of the project format.
    /// </summary>
    public static int? GetPositionAt(IReadOnlyList<StudioCaptionCue> ordered, double time)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (!double.IsFinite(time))
        {
            return null;
        }

        var low = 0;
        var high = ordered.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (ordered[middle].Start <= time)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low > 0 && ordered[low - 1].End > time ? low - 1 : null;
    }

    /// <summary>
    /// The place, among <paramref name="count"/> rows, of the next one that
    /// <paramref name="hasProblem"/>: after <paramref name="current"/>, or the first when there is
    /// no current row, going round to the first after the last. Null when none has a problem.
    /// </summary>
    public static int? GetNextProblem(int count, int? current, Func<int, bool> hasProblem)
    {
        ArgumentNullException.ThrowIfNull(hasProblem);
        if (count <= 0)
        {
            return null;
        }

        var start = current is { } place ? Math.Clamp(place + 1, 0, count) : 0;
        for (var row = start; row < count; row++)
        {
            if (hasProblem(row))
            {
                return row;
            }
        }

        for (var row = 0; row < start; row++)
        {
            if (hasProblem(row))
            {
                return row;
            }
        }

        return null;
    }

    private sealed record SplitText(string FirstText, string SecondText, StudioCaptionWord[] FirstWords, StudioCaptionWord[] SecondWords);

    private static bool IsTimed(StudioCaptionCue cue) => double.IsFinite(cue.Start) && double.IsFinite(cue.End);

    private static bool IsInside(StudioCaptionCue cue, double time) =>
        double.IsFinite(time)
        && IsTimed(cue)
        && time - cue.Start >= StudioCaptionResolver.ShortestPart
        && cue.End - time >= StudioCaptionResolver.ShortestPart;

    private static bool HasSpelledWords(StudioCaptionCue cue) =>
        cue.Words.Length > 0 && StudioCaptionTranscript.Spells(cue.Words, cue.Text);

    private static int? GetNextInTime(IReadOnlyList<StudioCaptionCue> cues, int index)
    {
        var order = GetIndexesInTimeOrder(cues);
        var position = Array.IndexOf(order, index);
        return position >= 0 && position + 1 < order.Length ? order[position + 1] : null;
    }

    // The time between two words that is nearest the middle, when the words spell out the text.
    private static double? GetMiddleWordGap(StudioCaptionCue cue, double middle)
    {
        var words = cue.Words;
        if (words.Length < 2 || !StudioCaptionTranscript.Spells(words, StudioCaptionResolver.CleanText(cue.Text)))
        {
            return null;
        }

        double? best = null;
        for (var count = 1; count < words.Length; count++)
        {
            var gap = (words[count - 1].End + words[count].Start) / 2;
            if (best is not { } nearest || Math.Abs(gap - middle) < Math.Abs(nearest - middle))
            {
                best = gap;
            }
        }

        return best;
    }

    private static SplitText? GetSplitText(StudioCaptionCue cue, double time)
    {
        var text = StudioCaptionResolver.CleanText(cue.Text);
        var words = cue.Words;

        // Split where the words say, out of the text itself, so its spacing and its lines stay.
        if (words.Length >= 2
            && StudioCaptionTranscript.Spells(words, text)
            && StudioCaptionTranscript.GetPlaces(words, text) is { } places)
        {
            var said = Array.FindIndex(words, word => (word.Start + word.End) / 2 >= time);
            var count = Math.Clamp(said < 0 ? words.Length : said, 1, words.Length - 1);
            var firstText = StudioCaptionResolver.CleanText(text[..places[count - 1].End]);
            var secondText = StudioCaptionResolver.CleanText(text[places[count].Start..]);
            return firstText.Length == 0 || secondText.Length == 0
                ? null
                : new SplitText(firstText, secondText, words[..count], words[count..]);
        }

        // Without word times, the text is split where its letters are shared most like the time.
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        var wanted = (time - cue.Start) / (cue.End - cue.Start);
        var lengths = tokens.Select(static token => token.EnumerateRunes().Count()).ToArray();
        var total = (double)lengths.Sum();
        var best = 1;
        var bestDistance = double.PositiveInfinity;
        var before = 0;
        for (var count = 1; count < tokens.Length; count++)
        {
            before += lengths[count - 1];
            var distance = Math.Abs((before / total) - wanted);
            if (distance < bestDistance)
            {
                best = count;
                bestDistance = distance;
            }
        }

        return new SplitText(string.Join(' ', tokens[..best]), string.Join(' ', tokens[best..]), [], []);
    }

    private static bool WordsFit(IEnumerable<StudioCaptionWord> words, double start, double end) =>
        words.All(word =>
        {
            var middle = (word.Start + word.End) / 2;
            return middle >= start && middle <= end;
        });

    private static StudioCaptionEditResult InTimeOrder(IReadOnlyList<StudioCaptionCue> cues, int index)
    {
        var order = GetIndexesInTimeOrder(cues);
        var position = Array.IndexOf(order, index);
        return new StudioCaptionEditResult(order.Select(place => cues[place]).ToArray(), Math.Max(position, 0));
    }
}
