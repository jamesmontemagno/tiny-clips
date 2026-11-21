using System.Xml.Linq;
using TinyClips.App.Views.ClipsLibrary;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioLibraryRowTests
{
    [Fact]
    public void SearchReusesOrderedRowsAndOnlyReadableProjectsHaveAnOpenAction()
    {
        var date = DateTimeOffset.UtcNow;
        var draft = new StudioProjectSummary("b", "  ", date, date, true, false, false, 10, SourceExists: false);
        var other = draft with { Id = "a", Name = "My Project", IsDraft = false, ExportMissing = true };
        var bad = new StudioUnreadableProject("c", date.AddDays(-1), 2, false);
        var rows = StudioLibraryRow.FromListing(new StudioProjectListing([draft, other], [bad]));
        Assert.Equal(["My Project", "Untitled Recording", "Unreadable Project"], rows.Select(row => row.Name));
        Assert.Equal("a", rows[0].ProjectId);
        Assert.Contains("Export missing", rows[0].Detail);
        Assert.Contains("Recording is missing", rows[1].Detail);
        Assert.Contains("Untitled Recording", rows[1].OpenAutomationName);
        Assert.False(rows[2].CanOpen);
        Assert.Equal("c", rows[2].ProjectId);
        Assert.Contains("cannot read", rows[2].Detail);

        Assert.Same(rows, StudioLibraryRow.Matching(rows, " "));
        var matches = StudioLibraryRow.Matching(rows, " PROJECT ");
        Assert.Collection(matches, row => Assert.Same(rows[0], row), row => Assert.Same(rows[2], row));
        Assert.Same(rows[1], Assert.Single(StudioLibraryRow.Matching(rows, "recording")));
        Assert.Empty(StudioLibraryRow.Matching(rows, "gone"));
    }

    [Fact]
    public void ProjectListOwnsScrollingAndItsRecycledOpenButtonUsesTheBoundProject()
    {
        using var stream = typeof(StudioLibraryRowTests).Assembly.GetManifestResourceStream("Library.Window.xaml")!;
        var document = XDocument.Load(stream);
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var list = Assert.Single(document.Descendants(ui + "ListView"), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "StudioProjectList");
        Assert.Empty(list.Ancestors(ui + "ScrollViewer"));
        Assert.Empty(list.Elements(ui + "ListView.ItemsPanel")); // WinUI's virtualizing default.
        var buttons = list.Descendants(ui + "Button").ToDictionary(element => (string)element.Attribute("Click")!);
        var button = buttons["OnOpenStudioProject"];
        Assert.Equal("{x:Bind OpenAutomationName}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{x:Bind ProjectId}", (string?)button.Attribute("Tag"));
        Assert.Equal("{x:Bind CanOpen}", (string?)button.Attribute("Visibility"));
        Assert.Equal("{x:Bind ProjectId}", (string?)buttons["OnOpenStudioFolder"].Attribute("Tag"));
        Assert.Equal("{x:Bind DeleteAutomationName}", (string?)buttons["OnDeleteStudioProject"].Attribute("AutomationProperties.Name"));
    }
}
