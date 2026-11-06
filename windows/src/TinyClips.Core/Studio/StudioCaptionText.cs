using System.Globalization;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Studio;

/// <summary>
/// What the editor says about captions. The same, word for word, on the Mac
/// (<c>StudioCaptionText</c> in <c>StudioCaptionQuality.swift</c>).
/// </summary>
public static class StudioCaptionText
{
    /// <summary>What the panel says when a project has no captions.</summary>
    public const string EmptyHint = "Import an SRT or WebVTT file to add captions.";

    /// <summary>The same, where captions can also be generated.</summary>
    public const string GenerateHint = "Generate captions from the recording's sound, or import an SRT or WebVTT file.";

    /// <summary>What the panel says about what an export leaves out.</summary>
    public const string ExportNote = "Exports are timed to the edited video. Captions in cut or sped-up parts are left out.";

    /// <summary>What the checks say when they find nothing.</summary>
    public const string NoProblems = "No problems found.";

    /// <summary>Said about drawing captions into the picture.</summary>
    public const string DrawnNote = "Drawn captions are part of the picture, so viewers cannot turn them off. Export a file as well for players that can.";

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

    /// <summary>
    /// What came of Auto Fix: <c>Made 3 fixes.</c>, and how many of the problems are
    /// <paramref name="left"/>: <c>Made 3 fixes. 1 problem needs a manual edit.</c>
    /// </summary>
    public static string GetFixedMessage(int applied, int left = 0)
    {
        var made = applied switch
        {
            0 => left > 0 ? "Nothing could be fixed automatically." : "Nothing to fix.",
            1 => "Made 1 fix.",
            _ => string.Create(CultureInfo.InvariantCulture, $"Made {applied} fixes."),
        };
        if (left <= 0)
        {
            return made;
        }

        return left == 1
            ? $"{made} 1 problem needs a manual edit."
            : string.Create(CultureInfo.InvariantCulture, $"{made} {left} problems need a manual edit.");
    }

    /// <summary>Said with the checks when Auto Fix cannot fix every problem they found, or null when it can.</summary>
    public static string? GetManualFixNote(int fixable, int total)
    {
        if (total <= Math.Max(0, fixable))
        {
            return null;
        }

        if (fixable <= 0)
        {
            return total == 1
                ? "Auto Fix can't fix this one. Edit the caption's text or timing."
                : "Auto Fix can't fix these. Edit the captions' text or timing.";
        }

        var others = total - fixable == 1 ? "The other needs" : "The others need";
        return string.Create(CultureInfo.InvariantCulture, $"Auto Fix can fix {fixable} of {total}. {others} a manual edit.");
    }

    // Generating.

    /// <summary>Said about where the sound goes.</summary>
    public const string PrivacyNote = "Your sound stays on this computer. Only the model is downloaded.";

    /// <summary>Said before generating over captions that are there.</summary>
    public const string ReplaceNote = "This replaces the current captions. Undo brings them back.";

    /// <summary>Said when generating starts.</summary>
    public const string GeneratingMessage = "Generating captions.";

    /// <summary>Said from when stopping is asked for until it has stopped, which can take a while.</summary>
    public const string StoppingMessage = "Stopping captions\u2026";

    /// <summary>Said when generating was stopped.</summary>
    public const string CancelledMessage = "Generating captions stopped. The captions are as they were.";

    /// <summary>What came of generating: <c>Generated 12 captions.</c></summary>
    public static string GetGeneratedMessage(int count) => count == 1
        ? "Generated 1 caption."
        : string.Create(CultureInfo.InvariantCulture, $"Generated {count} captions.");

    /// <summary>How far generating is: <c>Recognizing speech: 40%</c>.</summary>
    public static string GetProgressText(StudioCaptionProgress progress)
    {
        var percent = (int)Math.Round(Math.Clamp(progress.Fraction, 0, 1) * 100);
        var stage = progress.Stage switch
        {
            StudioCaptionStage.DownloadingModel => "Downloading the model",
            StudioCaptionStage.ReadingSound => "Reading the sound",
            _ => "Recognizing speech",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{stage}: {percent}%");
    }

    /// <summary>What a model is good for, and whether choosing it means a download.</summary>
    public static string GetModelNote(StudioCaptionModel model, bool isInstalled) =>
        isInstalled
            ? $"{model.Guidance}. Already on this computer."
            : $"{model.Guidance}. Downloads {model.SizeText} once, from Hugging Face.";

    /// <summary>The words on the button that starts it.</summary>
    public static string GetGenerateActionText(StudioCaptionModel model, bool isInstalled) =>
        isInstalled ? "Generate" : $"Download {model.SizeText} and generate";

    /// <summary>How much room the downloaded models take, for Settings.</summary>
    public static string GetInstalledModelsText(int count, long bytes)
    {
        if (count == 0)
        {
            return "No models downloaded.";
        }

        var size = bytes >= 1024L * 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0} MB");
        return count == 1 ? $"1 model downloaded, using {size}." : $"{count} models downloaded, using {size}.";
    }
}
