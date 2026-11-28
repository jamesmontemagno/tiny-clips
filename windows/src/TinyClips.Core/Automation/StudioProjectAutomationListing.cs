using System.Globalization;
using System.Text.Json.Nodes;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Automation;

/// <summary>Builds the Studio project-list response, including projects with unreadable metadata.</summary>
public static class StudioProjectAutomationListing
{
    public static JsonObject Create(StudioProjectListing listing, int limit)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);

        var projects = listing.Summaries
            .Select(summary => (
                summary.Id,
                Activity: summary.LastUsedAt,
                Value: Readable(summary)))
            .Concat(listing.Unreadable.Select(project => (
                project.Id,
                Activity: project.CreatedAt,
                Value: new JsonObject
                {
                    ["projectId"] = project.Id,
                    ["name"] = "Unreadable project",
                    ["createdAt"] = FormatTimestamp(project.CreatedAt),
                    ["isReadable"] = false,
                })))
            .OrderByDescending(project => project.Activity)
            .ThenBy(project => project.Id, StringComparer.Ordinal)
            .Take(limit);

        var result = new JsonArray();
        foreach (var project in projects)
        {
            result.Add(project.Value);
        }

        return new JsonObject { ["projects"] = result };
    }

    private static JsonObject Readable(StudioProjectSummary summary)
    {
        var value = new JsonObject
        {
            ["projectId"] = summary.Id,
            ["name"] = summary.Name,
            ["createdAt"] = FormatTimestamp(summary.CreatedAt),
            ["lastOpenedAt"] = FormatTimestamp(summary.LastOpenedAt),
            ["isDraft"] = summary.IsDraft,
            ["isFlat"] = summary.IsFlat,
            ["keepSources"] = summary.KeepSources,
            ["sourceExists"] = summary.SourceExists,
            ["isReadable"] = true,
        };
        if (summary.TakeOf is { } owner)
        {
            value["takeOf"] = new JsonObject { ["projectId"] = owner.ProjectId, ["name"] = owner.Name };
        }

        return value;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
