using System.Text.Json;

namespace TinyClips.Core.Studio;

/// <summary>The project a recorded take draft was imported into.</summary>
public sealed record StudioTakeReference(string ProjectId, string Name)
{
    public string LibraryName => StudioTakeLinkage.LibraryName(Name);
}

/// <summary>
/// Read-only linkage between a Studio project and the take drafts recorded for it with
/// Record another segment. The destination's media source names the take as takeProjectId;
/// the take's own manifest is never written.
/// </summary>
public static class StudioTakeLinkage
{
    public const string TakeProjectIdKey = "takeProjectId";

    /// <summary>The take draft a source was recorded as, or null when it is not a valid project id.</summary>
    public static string? TakeProjectId(StudioMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ExtensionData.TryGetValue(TakeProjectIdKey, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } id
            && IsProjectId(id)
            ? id
            : null;
    }

    /// <summary>The take draft the source of a clip was recorded as, or null.</summary>
    public static string? TakeProjectIdForClip(StudioProject project, string clipId)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sourceId = project.Timeline?.Clips.FirstOrDefault(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal))?.SourceId;
        var source = sourceId is null ? null
            : project.MediaSources?.FirstOrDefault(candidate => string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        return source is null ? null : TakeProjectId(source);
    }

    /// <summary>The distinct take ids a project's media registry names, excluding the project itself.</summary>
    public static IReadOnlyList<string> TakeProjectIds(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return (project.MediaSources ?? [])
            .Select(TakeProjectId)
            .OfType<string>()
            .Where(id => !string.Equals(id, project.Id, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Fills <see cref="StudioProjectSummary.TakeOf"/> and <see cref="StudioProjectSummary.TakeCount"/>
    /// from the readable projects of one listing. When several projects name the same take, the one
    /// used most recently wins, ties going to the smaller id.
    /// </summary>
    public static IReadOnlyList<StudioProjectSummary> Link(
        IReadOnlyList<(StudioProjectSummary Summary, StudioProject Project)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var existing = entries.Select(entry => entry.Summary.Id).ToHashSet(StringComparer.Ordinal);
        var owners = new Dictionary<string, StudioProjectSummary>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (summary, project) in entries)
        {
            var takes = TakeProjectIds(project with { Id = summary.Id }).Where(existing.Contains).ToArray();
            counts[summary.Id] = takes.Length;
            foreach (var take in takes)
            {
                if (!owners.TryGetValue(take, out var owner) || Prefer(summary, owner))
                    owners[take] = summary;
            }
        }

        return entries.Select(entry => entry.Summary with
        {
            TakeOf = owners.TryGetValue(entry.Summary.Id, out var owner)
                ? new StudioTakeReference(owner.Id, owner.Name) : null,
            TakeCount = counts.GetValueOrDefault(entry.Summary.Id),
        }).ToArray();
    }

    /// <summary>The kind a Library row shows for a project.</summary>
    public static string LibraryKind(StudioProjectSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.ExportMissing) return "Export missing";
        if (!summary.IsDraft) return "Project";
        return summary.TakeOf is { } takeOf ? $"Take for \u201C{takeOf.LibraryName}\u201D" : "Draft";
    }

    /// <summary>The note a drafts list shows for a take draft, or null for any other project.</summary>
    public static string? DraftNote(StudioProjectSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.TakeOf is { } takeOf
            ? $"Recorded for \u201C{takeOf.LibraryName}\u201D, which keeps its own copy."
            : null;
    }

    /// <summary>
    /// Added to a delete confirmation so that deleting either side of a take is understood, or
    /// null when the project is neither a take nor has takes. Nothing else is deleted.
    /// </summary>
    public static string? DeleteNote(StudioProjectSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var notes = new List<string>();
        if (summary.TakeOf is { } takeOf)
            notes.Add($"\u201C{takeOf.LibraryName}\u201D keeps its own copy of this take and is not changed.");
        if (summary.TakeCount == 1)
            notes.Add("Its recorded take stays in the Library as a separate draft.");
        else if (summary.TakeCount > 1)
            notes.Add($"Its {summary.TakeCount} recorded takes stay in the Library as separate drafts.");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>Said in the Studio window once a recorded take has been added to the timeline.</summary>
    public static string SavedAsDraftNotice(string? takeName) =>
        $"Take also saved as draft \u201C{LibraryName(takeName)}\u201D in the Library.";

    /// <summary>What the Library calls a project, as <see cref="StudioProjectSummary.LibraryName"/> does.</summary>
    public static string LibraryName(string? name) => string.IsNullOrWhiteSpace(name) ? "Untitled Recording" : name.Trim();

    private static bool Prefer(StudioProjectSummary candidate, StudioProjectSummary current) =>
        candidate.LastUsedAt != current.LastUsedAt
            ? candidate.LastUsedAt > current.LastUsedAt
            : string.CompareOrdinal(candidate.Id, current.Id) < 0;

    private static bool IsProjectId(string value) =>
        value.Length == 36
        && Guid.TryParseExact(value, "D", out _)
        && value.All(c => c is '-' or (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
