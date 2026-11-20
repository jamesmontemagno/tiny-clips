using TinyClips.Core.Studio;

namespace TinyClips.App.Views.ClipsLibrary;

public sealed record StudioLibraryRow(string Name, string Detail, string? OpenProjectId, string OpenAutomationName)
{
    public bool CanOpen => OpenProjectId is not null;
    public string OpenAutomationId => $"OpenStudioProject-{OpenProjectId}";
    public string AutomationName => $"{Name}. {Detail}";

    internal static StudioLibraryRow[] FromListing(StudioProjectListing listing)
    {
        var readable = StudioProjectSummary.LibraryProjects(listing.Summaries, "").Select(project =>
        {
            var name = project.LibraryName;
            var date = project.LastUsedAt.LocalDateTime.ToString("g");
            var kind = project.ExportMissing ? "Export missing" : project.IsDraft ? "Draft" : "Project";
            var missing = project.SourceExists ? "" : " · Recording is missing";
            return new StudioLibraryRow(name, $"{kind} · {date}{missing}", project.Id, $"Open {name}, {date}, in Studio");
        });
        var unreadable = listing.Unreadable.OrderByDescending(project => project.CreatedAt).Select(project =>
            new StudioLibraryRow("Unreadable Project",
                $"Studio cannot read this project from {project.CreatedAt.LocalDateTime:g}. It may be damaged or made by a newer version.",
                null, ""));
        return [.. readable, .. unreadable];
    }

    internal static StudioLibraryRow[] Matching(StudioLibraryRow[] rows, string search)
    {
        var query = search.Trim();
        return query.Length == 0 ? rows : rows.Where(row => row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }
}
