using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TinyClips.Core.Studio;

/// <summary>A caption as a video shows it, in output time.</summary>
public readonly record struct StudioResolvedCaption(double Start, double End, string Text);

/// <summary>
/// Turns a project's cues into the captions its video has: section 15.2 of the project format.
/// Both the files and the drawn captions come from here, so they cannot disagree.
/// </summary>
public static class StudioCaptionResolver
{
    /// <summary>The shortest caption there is, in seconds. Less than this cannot be read.</summary>
    public const double ShortestPart = 0.1;

    /// <summary>The captions of <paramref name="project"/>, in time order and clear of each other.</summary>
    public static StudioResolvedCaption[] Resolve(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Resolve(project.Captions, StudioTimeMap.FromProject(project));
    }

    /// <summary>The captions <paramref name="captions"/> gives a video with this time map.</summary>
    public static StudioResolvedCaption[] Resolve(StudioCaptions captions, StudioTimeMap map)
    {
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(map);

        var parts = new List<StudioResolvedCaption>();
        foreach (var cue in UsableCues(captions.Cues))
        {
            var elapsed = 0.0;
            foreach (var piece in map.Pieces)
            {
                // A piece at another rate has no sound (section 7), so there is nothing to caption.
                if (piece.Rate == 1 && TryGetPart(cue, piece, elapsed, out var part))
                {
                    parts.Add(part);
                }

                elapsed += piece.OutputDuration;
            }
        }

        var ordered = parts
            .OrderBy(static part => part.Start)
            .ThenBy(static part => part.End)
            .ToArray();

        var result = new List<StudioResolvedCaption>(ordered.Length);
        foreach (var part in ordered)
        {
            if (result.Count > 0 && result[^1].End > part.Start)
            {
                result[^1] = result[^1] with { End = part.Start };
                if (result[^1].End - result[^1].Start < ShortestPart)
                {
                    result.RemoveAt(result.Count - 1);
                }
            }

            result.Add(part);
        }

        return [.. result];
    }

    /// <summary>
    /// Where in <paramref name="captions"/> the caption showing at <paramref name="outputTime"/> is,
    /// or -1 when none is. <paramref name="captions"/> comes from <see cref="Resolve(StudioProject)"/>.
    /// </summary>
    public static int IndexAt(IReadOnlyList<StudioResolvedCaption> captions, double outputTime)
    {
        ArgumentNullException.ThrowIfNull(captions);
        var low = 0;
        var high = captions.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (outputTime < captions[middle].Start)
            {
                high = middle - 1;
            }
            else if (outputTime >= captions[middle].End)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    /// <summary>Every line without the space around it, and no blank lines.</summary>
    public static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0);
        return string.Join('\n', lines);
    }

    private static IEnumerable<UsableCue> UsableCues(StudioCaptionCue[] cues) =>
        cues
            .Select(static cue => new UsableCue(cue.Start, cue.End, CleanText(cue.Text), UsableWords(cue.Words)))
            .Where(static cue => cue.Text.Length > 0 && double.IsFinite(cue.Start) && double.IsFinite(cue.End) && cue.End > cue.Start)
            .OrderBy(static cue => cue.Start)
            .ThenBy(static cue => cue.End);

    private static StudioCaptionWord[] UsableWords(StudioCaptionWord[] words) =>
        words
            .Where(static word => double.IsFinite(word.Start) && double.IsFinite(word.End) && word.End >= word.Start && !string.IsNullOrWhiteSpace(word.Text))
            .OrderBy(static word => word.Start)
            .ThenBy(static word => word.End)
            .ToArray();

    private static bool TryGetPart(UsableCue cue, StudioTimePiece piece, double elapsed, out StudioResolvedCaption part)
    {
        part = default;
        var start = Math.Max(cue.Start, piece.Start);
        var end = Math.Min(cue.End, piece.End);
        if (end <= start)
        {
            return false;
        }

        var text = cue.Text;
        if (cue.Words.Length > 0)
        {
            var kept = cue.Words
                .Where(word => (word.Start + word.End) / 2 >= piece.Start && (word.Start + word.End) / 2 < piece.End)
                .ToArray();
            if (kept.Length == 0)
            {
                return false;
            }

            if (kept.Length < cue.Words.Length)
            {
                text = string.Join(' ', kept.Select(static word => word.Text.Trim()));
                start = Math.Max(piece.Start, kept[0].Start);
                end = Math.Min(piece.End, kept[^1].End);
            }
        }

        if (end - start < ShortestPart)
        {
            return false;
        }

        part = new StudioResolvedCaption(elapsed + (start - piece.Start), elapsed + (end - piece.Start), text);
        return true;
    }

    private sealed record UsableCue(double Start, double End, string Text, StudioCaptionWord[] Words);
}

/// <summary>What <see cref="StudioCaptionFiles.Read"/> found in a file.</summary>
/// <param name="Cues">The cues, in the order of the file, each with an id of its own and no words.</param>
/// <param name="SkippedBlocks">How many blocks of the file could not be read as a cue.</param>
public sealed record StudioCaptionFileContents(StudioCaptionCue[] Cues, int SkippedBlocks);

/// <summary>A kind of caption file.</summary>
public enum StudioCaptionFormat
{
    Srt,
    Vtt,
}

/// <summary>What the times of a caption file count from: section 15.5 of the project format.</summary>
public enum StudioCaptionTiming
{
    /// <summary>The recording, as the project keeps them.</summary>
    Recording,

    /// <summary>The edited video, such as a file made from an exported video.</summary>
    EditedVideo,
}

/// <summary>Writes and reads SRT and WebVTT text: section 15.3 of the project format.</summary>
public static partial class StudioCaptionFiles
{
    /// <summary>A caption file is text. Anything larger than this is not one.</summary>
    public const long LargestFile = 64 * 1024 * 1024;

    /// <summary>The extension of a kind of file, with its dot.</summary>
    public static string GetExtension(StudioCaptionFormat format) => format == StudioCaptionFormat.Vtt ? ".vtt" : ".srt";

    /// <summary>The kind of file an extension names, or null.</summary>
    public static StudioCaptionFormat? GetFormat(string? path) => Path.GetExtension(path)?.ToLowerInvariant() switch
    {
        ".srt" => StudioCaptionFormat.Srt,
        ".vtt" => StudioCaptionFormat.Vtt,
        _ => null,
    };

    /// <summary>The text of a file of that kind.</summary>
    public static string Write(StudioCaptionFormat format, IReadOnlyList<StudioResolvedCaption> captions) =>
        format == StudioCaptionFormat.Vtt ? WriteVtt(captions) : WriteSrt(captions);

    /// <summary>
    /// The cues of a file with the given timing, in source time. A file timed to the edited video
    /// goes through <paramref name="map"/>, and a cue with no time left is counted as skipped.
    /// </summary>
    public static StudioCaptionFileContents Read(string text, StudioCaptionTiming timing, StudioTimeMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var contents = Read(text);
        if (timing == StudioCaptionTiming.Recording)
        {
            return contents;
        }

        var cues = new List<StudioCaptionCue>(contents.Cues.Length);
        foreach (var cue in contents.Cues)
        {
            var start = map.OutputToSource(cue.Start);
            var end = map.OutputToSource(cue.End);
            if (end > start)
            {
                cues.Add(cue with { Start = start, End = end });
            }
        }

        return new StudioCaptionFileContents([.. cues], contents.SkippedBlocks + contents.Cues.Length - cues.Count);
    }

    /// <summary>The text of a caption file: UTF-8 unless a byte order mark says otherwise.</summary>
    /// <exception cref="InvalidDataException">The file is too large to be a caption file.</exception>
    public static string ReadFile(string path)
    {
        if (new FileInfo(path).Length > LargestFile)
        {
            throw new InvalidDataException("This file is too large to be a caption file.");
        }

        return File.ReadAllText(path);
    }

    /// <summary>Writes a file that was approved to replace what is there, all at once.</summary>
    public static void ReplaceFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new DirectoryNotFoundException(path);
        var staged = Path.Combine(folder, $".{Guid.NewGuid():N}.tcexport");
        try
        {
            File.WriteAllText(staged, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    /// <summary>Writes a file that must not be there yet.</summary>
    /// <exception cref="IOException">A file has that name.</exception>
    public static void WriteNewFile(string path, string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    /// <summary>The text of an SRT file. Empty with no captions.</summary>
    public static string WriteSrt(IReadOnlyList<StudioResolvedCaption> captions)
    {
        ArgumentNullException.ThrowIfNull(captions);
        var builder = new StringBuilder();
        for (var i = 0; i < captions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            AppendTiming(builder, captions[i], ',');
            builder.Append(captions[i].Text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The text of a WebVTT file. Only its header with no captions.</summary>
    public static string WriteVtt(IReadOnlyList<StudioResolvedCaption> captions)
    {
        ArgumentNullException.ThrowIfNull(captions);
        var builder = new StringBuilder("WEBVTT\n");
        foreach (var caption in captions)
        {
            builder.Append('\n');
            AppendTiming(builder, caption, '.');
            builder.Append(caption.Text
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The cues of an SRT or WebVTT file. Text that is neither has no cues.</summary>
    public static StudioCaptionFileContents Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var blocks = ReadBlocks(text);
        var isVtt = blocks.Count > 0 && StartsWithKeyword(blocks[0][0], "WEBVTT");
        var cues = new List<StudioCaptionCue>();
        var skipped = 0;
        foreach (var block in isVtt ? blocks.Skip(1) : blocks)
        {
            if (isVtt && (StartsWithKeyword(block[0], "NOTE") || StartsWithKeyword(block[0], "STYLE") || StartsWithKeyword(block[0], "REGION")))
            {
                continue;
            }

            if (TryReadCue(block, isVtt, out var cue))
            {
                cues.Add(cue);
            }
            else
            {
                skipped++;
            }
        }

        return new StudioCaptionFileContents([.. cues], skipped);
    }

    private static void AppendTiming(StringBuilder builder, StudioResolvedCaption caption, char separator)
    {
        AppendTimestamp(builder, caption.Start, separator);
        builder.Append(" --> ");
        AppendTimestamp(builder, caption.End, separator);
        builder.Append('\n');
    }

    private static void AppendTimestamp(StringBuilder builder, double seconds, char separator)
    {
        var ms = (long)Math.Floor((seconds * 1000) + 0.5);
        builder.Append(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}");
    }

    private static List<List<string>> ReadBlocks(string text)
    {
        if (text.StartsWith('\ufeff'))
        {
            text = text[1..];
        }

        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = [];
                blocks.Add(current);
            }

            current.Add(line);
        }

        return blocks;
    }

    private static bool StartsWithKeyword(string line, string keyword) =>
        line.StartsWith(keyword, StringComparison.Ordinal)
        && (line.Length == keyword.Length || line[keyword.Length] is ' ' or '\t');

    private static bool TryReadCue(List<string> block, bool isVtt, out StudioCaptionCue cue)
    {
        cue = new StudioCaptionCue();

        // The timing is the first line, or the second after a number or a name.
        for (var i = 0; i < Math.Min(2, block.Count); i++)
        {
            var match = TimingRegex().Match(block[i]);
            if (!match.Success)
            {
                continue;
            }

            var start = ReadSeconds(match, 1);
            var end = ReadSeconds(match, 5);
            var markup = isVtt ? VttMarkupRegex() : SrtMarkupRegex();
            var body = markup.Replace(string.Join('\n', block.Skip(i + 1)), string.Empty);
            if (isVtt)
            {
                body = body
                    .Replace("&lt;", "<", StringComparison.Ordinal)
                    .Replace("&gt;", ">", StringComparison.Ordinal)
                    .Replace("&nbsp;", "\u00a0", StringComparison.Ordinal)
                    .Replace("&lrm;", "\u200e", StringComparison.Ordinal)
                    .Replace("&rlm;", "\u200f", StringComparison.Ordinal)
                    .Replace("&amp;", "&", StringComparison.Ordinal);
            }

            body = StudioCaptionResolver.CleanText(body);
            if (end <= start || body.Length == 0)
            {
                return false;
            }

            cue = new StudioCaptionCue { Id = Guid.NewGuid().ToString("D"), Start = start, End = end, Text = body };
            return true;
        }

        return false;
    }

    private static double ReadSeconds(Match match, int firstGroup)
    {
        var hours = match.Groups[firstGroup].Success ? long.Parse(match.Groups[firstGroup].ValueSpan, CultureInfo.InvariantCulture) : 0;
        var minutes = long.Parse(match.Groups[firstGroup + 1].ValueSpan, CultureInfo.InvariantCulture);
        var seconds = long.Parse(match.Groups[firstGroup + 2].ValueSpan, CultureInfo.InvariantCulture);
        var fraction = long.Parse(match.Groups[firstGroup + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return ((hours * 3600000) + (minutes * 60000) + (seconds * 1000) + fraction) / 1000.0;
    }

    [GeneratedRegex(@"^\s*(?:([0-9]+):)?([0-9]{1,2}):([0-9]{2})[.,]([0-9]{1,3})\s*-->\s*(?:([0-9]+):)?([0-9]{1,2}):([0-9]{2})[.,]([0-9]{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TimingRegex();

    // An SRT has no way to write a literal tag, so only the tags it has go.
    [GeneratedRegex(@"</?(?:[bBiIuUsS]|[fF][oO][nN][tT])(?:\s[^<>]*)?>|\{\\[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex SrtMarkupRegex();

    // A VTT escapes every literal "<", so anything shaped like a tag is one.
    [GeneratedRegex(@"</?[A-Za-z][^<>]*>|<[0-9][0-9:.]*>", RegexOptions.CultureInvariant)]
    private static partial Regex VttMarkupRegex();
}
