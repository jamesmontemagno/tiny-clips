using System.Xml.Linq;

namespace TinyClips.App.Tests;

public sealed class GuideNavigationTests
{
    private static IReadOnlyList<XElement> Elements => SettingsMarkup.Elements("GuideWindow.xaml");

    private static XElement Navigation => Assert.Single(Elements, element => element.Name.LocalName == "TabView");

    private static IReadOnlyList<XElement> Tabs => [.. Navigation.Elements()];

    [Fact]
    public void GuideTopics_AreFixedNativeTabs_DefaultingToCapture()
    {
        var navigation = Navigation;
        Assert.Equal("Guide topics", navigation.Attr("AutomationProperties.Name"));
        Assert.Equal("0", navigation.Attr("SelectedIndex"));
        Assert.Equal("False", navigation.Attr("IsAddTabButtonVisible"));
        Assert.Equal("False", navigation.Attr("CanDragTabs"));
        Assert.Equal("False", navigation.Attr("CanReorderTabs"));
        Assert.Equal("SizeToContent", navigation.Attr("TabWidthMode"));
        Assert.Equal("2", navigation.Attr("Grid.Row"));
        Assert.Equal("Grid", navigation.Parent?.Name.LocalName);
        Assert.DoesNotContain(navigation.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal(["Capture", "Shortcuts", "Edit & record", "Library", "Agents"], Tabs.Select(tab => tab.Attr("Header")));
        Assert.All(Tabs, tab =>
        {
            Assert.Equal("TabViewItem", tab.Name.LocalName);
            Assert.Equal("False", tab.Attr("IsClosable"));
            Assert.False(string.IsNullOrWhiteSpace(tab.Attr("AutomationProperties.Name")));
            Assert.False(string.IsNullOrWhiteSpace(tab.Attr("AutomationProperties.AutomationId")));
        });
        Assert.Equal(Tabs.Count, Tabs.Select(tab => tab.Attr("AutomationProperties.AutomationId")).Distinct().Count());
    }

    [Fact]
    public void EachTopic_ScrollsIndependently_WithoutMovingTheNavigation()
    {
        Assert.Equal(Tabs.Count, Elements.Count(element => element.Name.LocalName == "ScrollViewer"));
        Assert.All(Tabs, tab =>
        {
            var scroll = Assert.Single(tab.Elements());
            Assert.Equal("ScrollViewer", scroll.Name.LocalName);
            Assert.Equal("Disabled", scroll.Attr("HorizontalScrollBarVisibility"));
            Assert.Equal("Auto", scroll.Attr("VerticalScrollBarVisibility"));
            Assert.False(string.IsNullOrWhiteSpace(scroll.Attr("AutomationProperties.Name")));
            Assert.Single(scroll.Elements(), element => element.Name.LocalName == "StackPanel");
        });
    }

    [Theory]
    [InlineData("Capture", "Capture modes|Choosing what to capture|Tray quick access")]
    [InlineData("Shortcuts", "Keyboard shortcuts")]
    [InlineData("Edit & record", "Screenshot editor|Recording extras")]
    [InlineData("Library", "Clips Library")]
    [InlineData("Agents", "Agent control")]
    public void Topics_GroupExistingSections_WithoutLosingAny(string header, string headings)
    {
        var tab = Assert.Single(Tabs, element => element.Attr("Header") == header);
        var sectionHeadings = tab.Descendants()
            .Where(element => element.Attr("AutomationProperties.HeadingLevel") == "Level2")
            .Select(element => element.Attr("Text"));

        Assert.Equal(headings.Split('|'), sectionHeadings);
    }

    [Fact]
    public void Shortcuts_KeepAllNamedFields_ForTheCurrentUserBindings()
    {
        var shortcuts = Assert.Single(Tabs, element => element.Attr("Header") == "Shortcuts");
        Assert.Equal(
            ["ScreenshotShortcut", "ScreenshotRegionShortcut", "ScreenshotWindowShortcut", "ScreenshotScreenShortcut",
                "VideoShortcut", "GifShortcut", "OcrShortcut", "StopRecordingShortcut"],
            shortcuts.Descendants().Select(element => element.Attr("Name")).OfType<string>());
    }

    [Fact]
    public void Agents_KeepSelectableSetupCommands_AndTheFullDocumentationLink()
    {
        var agents = Assert.Single(Tabs, element => element.Attr("Header") == "Agents");
        var selectableText = agents.Descendants()
            .Where(element => element.Attr("IsTextSelectionEnabled") == "True")
            .Select(element => element.Attr("Text"));
        Assert.Contains("https://github.com/jamesmontemagno/tiny-clips.git", selectableText);
        Assert.Contains("dnx TinyClips --yes -- pair --name \"My agent\"\ndnx TinyClips --yes -- status", selectableText);
        var link = Assert.Single(agents.Descendants(), element => element.Name.LocalName == "HyperlinkButton");
        Assert.Equal("https://github.com/jamesmontemagno/tiny-clips/blob/main/docs/agent-integration.md", link.Attr("NavigateUri"));
    }
}
