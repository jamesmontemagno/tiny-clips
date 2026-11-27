using System.Globalization;

namespace TinyClips.Core.Studio.Editing;

/// <summary>
/// One clip of the primary strip. The strip is in project time: <see cref="ProjectStart"/> and
/// <see cref="ProjectEnd"/> are where the clip plays on the timeline, after its own cuts and speed
/// changes. A join's effect belongs to the clip that comes in, so the first clip has none.
/// </summary>
public sealed record StudioClipStripItem(
    string Id,
    int Index,
    int Count,
    string Name,
    double ProjectStart,
    double ProjectEnd,
    StudioClipTransition TransitionIn,
    bool IsSelected)
{
    /// <summary>How long the clip plays on the timeline.</summary>
    public double Duration => Math.Max(0, ProjectEnd - ProjectStart);

    public string DurationText => StudioEditorModel.FormatTime(Duration);

    /// <summary>What a screen reader reads for the clip, such as <c>Clip 2 of 3: Intro, 4.5 seconds</c>.</summary>
    public string AccessibleName =>
        string.Create(CultureInfo.InvariantCulture, $"Clip {Index + 1} of {Count}: {Name}, {StudioEditorModel.GetSecondsText(Duration)}");
}

/// <summary>What the primary clip strip shows, and the words for it.</summary>
public static class StudioClipStrip
{
    /// <summary>The widest overlap a cross-dissolve makes, as the format puts it.</summary>
    public const string CrossDissolveHint = "Cross-Dissolve (up to 0.5 s overlap)";

    /// <summary>
    /// The clips of a timeline in order. A single recording has one item that spans it, so the
    /// strip reads the same before and after a second clip is added.
    /// </summary>
    /// <param name="manifest">The whole manifest of the open editor.</param>
    /// <param name="map">The timeline's map, or null for a single recording.</param>
    /// <param name="selectedClipId">The clip the inspectors edit.</param>
    /// <param name="singleName">The name to show for a single recording.</param>
    /// <param name="singleDuration">The length of a single recording's output.</param>
    public static IReadOnlyList<StudioClipStripItem> Build(
        StudioProject manifest,
        StudioTimelineMap? map,
        string? selectedClipId,
        string singleName,
        double singleDuration)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (map is null || manifest.Timeline?.Clips is not { Length: > 0 } clips || clips.Length != map.Clips.Count)
        {
            return [new StudioClipStripItem(manifest.Id, 0, 1, singleName, 0, singleDuration, StudioClipTransition.Cut, true)];
        }

        var items = new StudioClipStripItem[clips.Length];
        for (var index = 0; index < clips.Length; index++)
        {
            var clip = clips[index];
            var placed = map.Clips[index];
            items[index] = new StudioClipStripItem(
                clip.Id,
                index,
                clips.Length,
                GetClipName(clip.Name, index),
                placed.ProjectStart,
                placed.ProjectEnd,
                index == 0 ? StudioClipTransition.Cut : clip.TransitionIn,
                string.Equals(clip.Id, selectedClipId, StringComparison.Ordinal));
        }

        return items;
    }

    /// <summary>The clip's own name, or <c>Clip 3</c> where it has none.</summary>
    public static string GetClipName(string? name, int index)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? string.Create(CultureInfo.InvariantCulture, $"Clip {index + 1}")
            : trimmed;
    }

    public static string GetTransitionName(StudioClipTransition transition) => transition switch
    {
        StudioClipTransition.FadeThroughBlack => "Fade Through Black",
        StudioClipTransition.FadeThroughWhite => "Fade Through White",
        StudioClipTransition.CrossDissolve => "Cross-Dissolve",
        _ => "Cut",
    };

    /// <summary>The Segoe Fluent Icons picture of a join.</summary>
    public static string GetTransitionGlyph(StudioClipTransition transition) => transition switch
    {
        StudioClipTransition.FadeThroughBlack => "\uE706",
        StudioClipTransition.FadeThroughWhite => "\uE706",
        StudioClipTransition.CrossDissolve => "\uE8B9",
        _ => "\uE76C",
    };

    /// <summary>What a screen reader reads for the join between two clips, such as <c>Transition from Intro to Demo</c>.</summary>
    public static string GetJoinAccessibleName(string previousName, string nextName) =>
        $"Transition from {previousName} to {nextName}";

    public static string GetClipAddedMessage(int count) =>
        count == 1 ? "Clip added to the timeline." : string.Create(CultureInfo.InvariantCulture, $"{count} clips added to the timeline.");

    public static string GetClipMovedMessage(string name, int position, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{name} moved to position {position} of {count}.");

    public static string GetClipRemovedMessage(string name) => $"{name} removed from the timeline.";

    public static string GetClipSplitMessage(string name) => $"{name} split at the playhead.";

    public static string GetTransitionSetMessage(string nextName, StudioClipTransition transition) =>
        $"{GetTransitionName(transition)} before {nextName}.";
}
