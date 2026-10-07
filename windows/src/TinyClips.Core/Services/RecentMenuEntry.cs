using TinyClips.Core.Studio;

namespace TinyClips.Core.Services;

/// <summary>
/// One line of the tray menu's recent captures: a capture that was saved as a file, or a Studio
/// project that holds a recording no file has yet.
/// </summary>
public sealed record RecentMenuEntry
{
    private RecentMenuEntry(RecentCapture? capture, StudioProjectSummary? studioDraft)
    {
        Capture = capture;
        StudioDraft = studioDraft;
    }

    /// <summary>The capture that was saved as a file, or null where the line is a Studio project.</summary>
    public RecentCapture? Capture { get; }

    /// <summary>The Studio project, or null where the line is a saved capture.</summary>
    public StudioProjectSummary? StudioDraft { get; }

    /// <summary>Tells one line from another: a capture by its path, a project by its id.</summary>
    public string Id => Capture is not null ? $"capture:{Capture.Path}" : $"studio:{StudioDraft!.Id}";

    /// <summary>When it was saved, or when the project was last worked on.</summary>
    public DateTimeOffset Date => Capture?.CapturedAt ?? StudioDraft!.LastUsedAt;

    public static RecentMenuEntry ForCapture(RecentCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return new RecentMenuEntry(capture, null);
    }

    public static RecentMenuEntry ForStudioDraft(StudioProjectSummary draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new RecentMenuEntry(null, draft);
    }

    /// <summary>
    /// The menu's lines, the newest first. At the same instant a saved capture comes before a
    /// project, and otherwise each kind keeps the order it came in.
    /// </summary>
    /// <param name="limit">How many lines the menu has at most, of both kinds together. Zero or less gives none.</param>
    public static IReadOnlyList<RecentMenuEntry> Merged(
        IEnumerable<RecentCapture> captures,
        IEnumerable<StudioProjectSummary> drafts,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(drafts);

        // The sort keeps the order of what compares equal, and the captures are first in.
        return captures.Select(ForCapture)
            .Concat(drafts.Select(ForStudioDraft))
            .OrderByDescending(static entry => entry.Date)
            .Take(Math.Max(0, limit))
            .ToArray();
    }

    /// <summary>
    /// What the tray menu lists as recent captures: the saved captures and, while Studio is
    /// switched on, the projects that hold the only copy of a recording
    /// (<see cref="StudioProjectSummary.MenuDrafts"/>), mixed by date within one limit. With
    /// Studio switched off no project is listed, as nothing else of Studio is shown then.
    /// </summary>
    /// <param name="captures">The saved captures, the newest first, as the recent captures service gives them.</param>
    /// <param name="projects">Every project of the store, in any order.</param>
    public static IReadOnlyList<RecentMenuEntry> ForMenu(
        IEnumerable<RecentCapture> captures,
        IEnumerable<StudioProjectSummary> projects,
        bool studioEnabled,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var drafts = studioEnabled
            ? StudioProjectSummary.MenuDrafts(projects)
                .OrderByDescending(static draft => draft.LastUsedAt)
                .ThenBy(static draft => draft.Id, StringComparer.Ordinal)
            : Enumerable.Empty<StudioProjectSummary>();
        return Merged(captures, drafts, limit);
    }
}
