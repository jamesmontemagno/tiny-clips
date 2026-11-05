using System.Globalization;

namespace TinyClips.Core.Studio;

/// <summary>
/// What the editor says about captions. The same, word for word, on the Mac
/// (<c>StudioCaptionText</c> in <c>StudioCaptionQuality.swift</c>).
/// </summary>
public static class StudioCaptionText
{
    /// <summary>What the panel says when a project has no captions.</summary>
    public const string EmptyHint = "Import an SRT or WebVTT file to add captions.";

    /// <summary>What the panel says about what an export leaves out.</summary>
    public const string ExportNote = "Exports are timed to the edited video. Captions in cut or sped-up parts are left out.";

    /// <summary>What the checks say when they find nothing.</summary>
    public const string NoProblems = "No problems found.";

    /// <summary>Said when a file has no cues in it.</summary>
    public const string NothingToImport = "No captions could be read from that file.";

    /// <summary>Said when captions have been written to a file.</summary>
    public const string ExportedMessage = "Captions exported.";

    /// <summary>How many cues there are: <c>No captions yet</c>, <c>1 caption</c>, <c>12 captions</c>.</summary>
    public static string GetCountText(int count) => count switch
    {
        0 => "No captions yet",
        1 => "1 caption",
        _ => string.Create(CultureInfo.InvariantCulture, $"{count} captions"),
    };

    /// <summary>When a cue starts and ends, in source time: <c>12.0 to 16.5 seconds</c>.</summary>
    public static string GetRangeText(StudioCaptionCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var start = double.IsFinite(cue.Start) ? cue.Start : 0;
        var end = double.IsFinite(cue.End) ? cue.End : 0;
        return string.Create(CultureInfo.InvariantCulture, $"{start:0.0} to {end:0.0} seconds");
    }

    /// <summary>What a cue says, on one line. A cue with nothing to say says so.</summary>
    public static string GetRowText(StudioCaptionCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var text = StudioCaptionResolver.CleanText(cue.Text).Replace('\n', ' ');
        return text.Length > 0 ? text : "(Empty)";
    }

    /// <summary>What came of an import: <c>Imported 12 captions. 1 block could not be read.</c></summary>
    public static string GetImportedMessage(int count, int skipped)
    {
        var imported = count == 1 ? "Imported 1 caption." : string.Create(CultureInfo.InvariantCulture, $"Imported {count} captions.");
        return skipped switch
        {
            0 => imported,
            1 => $"{imported} 1 block could not be read.",
            _ => string.Create(CultureInfo.InvariantCulture, $"{imported} {skipped} blocks could not be read."),
        };
    }

    /// <summary>What came of Auto Fix: <c>Made 3 fixes.</c></summary>
    public static string GetFixedMessage(int applied) => applied switch
    {
        0 => "Nothing to fix.",
        1 => "Made 1 fix.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Made {applied} fixes."),
    };
}
