using TinyClips.Core.Studio;

namespace TinyClips.App.Views.ClipsLibrary;

public sealed record StudioLibraryRow(
    string Name, string Detail, string ProjectId, string DatedName, bool CanOpen, string? DeleteNote = null)
{
    public string OpenAutomationName => $"Open {DatedName} in Studio";
    public string FolderAutomationName => $"Open folder for {DatedName}";
    public string DeleteAutomationName => $"Delete {DatedName} from Studio";
    public string OpenAutomationId => $"OpenStudioProject-{ProjectId}";
    public string AutomationName => $"{Name}. {Detail}";

    internal static StudioLibraryRow[] FromListing(StudioProjectListing listing)
    {
        var readable = StudioProjectSummary.LibraryProjects(listing.Summaries, "").Select(project =>
        {
            var name = project.LibraryName;
            var date = project.LastUsedAt.LocalDateTime.ToString("g");
            var kind = StudioTakeLinkage.LibraryKind(project);
            var missing = project.SourceExists ? "" : " · Recording is missing";
            return new StudioLibraryRow(name, $"{kind} · {date}{missing}", project.Id, $"{name}, {date}", true,
                StudioTakeLinkage.DeleteNote(project));
        });
        var unreadable = listing.Unreadable.OrderByDescending(project => project.CreatedAt).Select(project =>
            new StudioLibraryRow("Unreadable Project",
                $"Studio cannot read this project from {project.CreatedAt.LocalDateTime:g}. It may be damaged or made by a newer version.",
                project.Id, $"Unreadable Project, {project.CreatedAt.LocalDateTime:g}", false));
        return [.. readable, .. unreadable];
    }

    internal static StudioLibraryRow[] Matching(StudioLibraryRow[] rows, string search)
    {
        var query = search.Trim();
        return query.Length == 0 ? rows : rows.Where(row => row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }
}
