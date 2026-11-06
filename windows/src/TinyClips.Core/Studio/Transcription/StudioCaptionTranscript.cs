namespace TinyClips.Core.Studio.Transcription;

/// <summary>A piece of what Whisper heard, with its times in hundredths of a second.</summary>
internal readonly record struct StudioTranscriptToken(string Text, long Start, long End);

/// <summary>
/// Turns what Whisper says about a stretch of speech into a cue. Pure, so that what Whisper's
/// tokens look like, as read from a real run, can be tested without a model.
/// </summary>
internal static class StudioCaptionTranscript
{
    // What whisper.cpp says instead of text for a stretch without speech.
    private const string BlankAudioMarker = "[BLANK_AUDIO]";

    /// <summary>The longest a cue is made: two full lines (section 15.6 of the project format).</summary>
    public const int LongestCue = StudioCaptionQuality.LongestLine * StudioCaptionQuality.MostLines;

    /// <summary>The cue for a segment, or null for one that holds no speech.</summary>
    /// <param name="sourceDuration">The recording's length. Whisper rounds up past the end of it.</param>
    public static StudioCaptionCue? ToCue(string text, TimeSpan start, TimeSpan end, IReadOnlyList<StudioTranscriptToken> tokens, double sourceDuration)
    {
        var cleaned = StudioCaptionResolver.CleanText(text);
        var cueStart = start.TotalSeconds;
        var cueEnd = Math.Min(end.TotalSeconds, sourceDuration);
        if (cleaned.Length == 0 || cleaned == BlankAudioMarker || cueEnd <= cueStart)
        {
            return null;
        }

        return new StudioCaptionCue
        {
            Id = Guid.NewGuid().ToString("D"),
            Start = cueStart,
            End = cueEnd,
            Text = cleaned,
            Words = ToWords(cleaned, tokens, sourceDuration),
        };
    }

    /// <summary>
    /// The words of a segment. A token that starts with a space starts a word, and any other joins
    /// the one before it: punctuation, and the rest of a word in several pieces. Markers such as
    /// <c>[_BEG_]</c> and <c>[_TT_352]</c> are not words. When the words do not spell out the text,
    /// as when a character was split across tokens, there are none: a cue without words is only
    /// clipped by a cut, which is right, and a cue with wrong words would say something else.
    /// </summary>
    public static StudioCaptionWord[] ToWords(string text, IReadOnlyList<StudioTranscriptToken> tokens, double sourceDuration)
    {
        var words = new List<StudioCaptionWord>();
        foreach (var token in tokens)
        {
            if (token.Text.Length == 0 || token.Text.StartsWith("[_", StringComparison.Ordinal))
            {
                continue;
            }

            var start = Math.Min(token.Start / 100.0, sourceDuration);
            var end = Math.Min(token.End / 100.0, sourceDuration);
            if (words.Count == 0 || char.IsWhiteSpace(token.Text[0]))
            {
                words.Add(new StudioCaptionWord { Start = start, End = end, Text = token.Text.Trim() });
            }
            else
            {
                words[^1] = words[^1] with { End = Math.Max(words[^1].End, end), Text = words[^1].Text + token.Text.TrimEnd() };
            }
        }

        var spelled = string.Concat(words.Select(static word => word.Text));
        return string.Equals(spelled, WithoutWhiteSpace(text), StringComparison.Ordinal) ? [.. words] : [];
    }

    /// <summary>Whether <paramref name="words"/> spell out <paramref name="text"/>, leaving out the space between and around them.</summary>
    public static bool Spells(IReadOnlyList<StudioCaptionWord> words, string text) =>
        string.Equals(
            WithoutWhiteSpace(string.Concat(words.Select(static word => word.Text))),
            WithoutWhiteSpace(text),
            StringComparison.Ordinal);

    /// <summary>
    /// Where each word is in <paramref name="text"/>, from its first letter to just after its last,
    /// or null when the words do not spell it out from its start.
    /// </summary>
    public static (int Start, int End)[]? GetPlaces(IReadOnlyList<StudioCaptionWord> words, string text)
    {
        var index = 0;
        var places = new (int Start, int End)[words.Count];
        for (var place = 0; place < words.Count; place++)
        {
            int? start = null;
            foreach (var character in words[place].Text)
            {
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                if (index >= text.Length || text[index] != character)
                {
                    return null;
                }

                start ??= index;
                index++;
            }

            places[place] = (start ?? index, index);
        }

        return places;
    }

    private static string WithoutWhiteSpace(string text) =>
        string.Concat(text.Where(static character => !char.IsWhiteSpace(character)));
}
