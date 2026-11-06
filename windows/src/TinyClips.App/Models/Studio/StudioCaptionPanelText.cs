using System.Globalization;
using TinyClips.Core.Studio;

namespace TinyClips.App.Models.Studio;

/// <summary>
/// What only the Captions panel says. What both editors say, word for word, is in
/// <see cref="StudioCaptionText"/>; this follows the Mac's panel where the two panels agree.
/// </summary>
public static class StudioCaptionPanelText
{
    public const string NoSpeechTitle = "No speech found";
    public const string TryAnotherModel = "Try another model\u2026";
    public const string DeletedMessage = "Caption deleted.";
    public const string SplitMessage = "Caption split at the playhead.";
    public const string MergedMessage = "Captions merged.";
    public const string NotGeneratedMessage = "Captions could not be generated.";

    /// <summary>
    /// Said when generating heard nobody speak. Whisper finds the language by itself, so what is
    /// worth trying is another model: one that hears more than English, or one that hears better.
    /// </summary>
    public static string GetNoSpeechMessage(bool isEnglishOnly, bool hasCaptions)
    {
        var message = isEnglishOnly
            ? "No speech was found in this recording. If people speak a language other than English in it, try a model that is not English-only."
            : "No speech was found in this recording. If people speak in it, try a larger model: it hears quiet speech better.";
        return hasCaptions ? $"{message} The captions are as they were." : message;
    }

    /// <summary>The title of the notification shown when generating ends while Studio is behind.</summary>
    public static string GetNotificationTitle(StudioCaptionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return notice.Kind switch
        {
            StudioCaptionNoticeKind.Done => "Captions ready",
            StudioCaptionNoticeKind.Info => "Captions",
            StudioCaptionNoticeKind.NoSpeech => notice.Title ?? NoSpeechTitle,
            _ => "Captions not generated",
        };
    }

    public static string GetNotificationMessage(StudioCaptionNotice notice, string clipName)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return string.IsNullOrEmpty(clipName) ? notice.Message : $"{clipName}: {notice.Message}";
    }

    /// <summary>The words on the Generate button: an invitation before there are captions.</summary>
    public static string GetGenerateButtonText(bool hasCaptions) => hasCaptions ? "Generate again\u2026" : "Generate captions\u2026";

    /// <summary>What the checks found in one cue.</summary>
    public static string GetProblemText(StudioCaptionFindingKind kind) => kind switch
    {
        StudioCaptionFindingKind.InvalidTiming => "Invalid time",
        StudioCaptionFindingKind.Overlap => "Overlaps the caption before",
        StudioCaptionFindingKind.Empty => "Empty",
        StudioCaptionFindingKind.Whitespace => "Extra spaces",
        StudioCaptionFindingKind.LongLine => "Line too long",
        StudioCaptionFindingKind.TooManyLines => "Too many lines",
        StudioCaptionFindingKind.ShortDuration => "Too short to read",
        _ => "Too fast to read",
    };

    public static string GetProblemsText(IEnumerable<StudioCaptionFindingKind> kinds) =>
        string.Join(", ", kinds.Select(GetProblemText));

    /// <summary>A time as a clock shows it, to a tenth of a second: <c>1:05.3</c>.</summary>
    public static string GetClockText(double seconds)
    {
        // Capped as on the Mac: a hand-edited cue time far past a day would not fit.
        var clamped = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 359_999) : 0;
        var tenths = (int)Math.Round(clamped * 10, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 600}:{tenths % 600 / 10.0:00.0}");
    }

    /// <summary>When a cue starts and ends, as a clock shows it: <c>0:12.0 – 0:16.5</c>.</summary>
    public static string GetClockRangeText(StudioCaptionCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        return $"{GetClockText(cue.Start)} \u2013 {GetClockText(cue.End)}";
    }

    /// <summary>How many cues a search found: <c>3 of 12</c>.</summary>
    public static string GetSearchCountText(int found, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"{found} of {total}");

    public static string GetNoMatchesText(string query) => $"No captions contain \u201C{query}\u201D.";

    public static string GetLookName(StudioCaptionAppearance appearance) => appearance switch
    {
        StudioCaptionAppearance.ContrastPanel => "Contrast panel",
        StudioCaptionAppearance.BoldImpact => "Bold impact",
        StudioCaptionAppearance.CleanShadow => "Clean shadow",
        _ => "Classic outline",
    };
}
