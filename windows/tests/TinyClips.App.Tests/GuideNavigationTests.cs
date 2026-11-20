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
        Assert.Equal(["Capture", "Editor", "Recording", "Studio", "Library", "Agents", "Help"], Tabs.Select(tab => tab.Attr("Header")));
        Assert.Equal(["capture", "editor", "recording", "studio", "library", "agents", "help"], Tabs.Select(tab => tab.Attr("Tag")));
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
    [InlineData("Capture", "Capture overview|Choose the output and target|Take a screenshot — step by step|Capture a scrolling page — step by step")]
    [InlineData("Editor", "Editor overview|Tools and finishing touches|Polish and save — step by step")]
    [InlineData("Recording", "Recording overview|Before and during a take|Record and trim — step by step")]
    [InlineData("Studio", "Studio overview|Composition and timeline|Create an editable video — step by step")]
    [InlineData("Library", "Library overview|Find and organize|Organize and share — step by step")]
    [InlineData("Agents", "Agents overview|Connect an agent — step by step|Permissions and safe use")]
    [InlineData("Help", "Help overview|Live global shortcuts|Fixed keys inside windows|Settings and recovery — step by step|More documentation")]
    public void Topics_HaveAccessibleOverviewAndPracticalHeadings(string header, string headings)
    {
        var tab = Assert.Single(Tabs, element => element.Attr("Header") == header);
        var sectionHeadings = tab.Descendants()
            .Where(element => element.Attr("AutomationProperties.HeadingLevel") == "Level2")
            .Select(element => element.Attr("Text"));

        Assert.Equal(headings.Split('|'), sectionHeadings);
        Assert.All(tab.Descendants().Where(element => element.Attr("AutomationProperties.HeadingLevel") == "Level2"),
            heading => Assert.Equal("{StaticResource GuideHeadingStyle}", heading.Attr("Style")));
        var steps = tab.Descendants()
            .Where(element => element.Name.LocalName == "GuideRow")
            .Select(element => element.Attr("Title"))
            .OfType<string>()
            .Where(title => title.Length > 2 && char.IsAsciiDigit(title[0]) && title[1] == '.')
            .ToArray();
        Assert.True(steps.Length >= 3, $"{header} needs at least three practical numbered steps.");
        Assert.Contains(steps, title => title.StartsWith("1. ", StringComparison.Ordinal));
        Assert.Contains(steps, title => title.StartsWith("2. ", StringComparison.Ordinal));
        Assert.Contains(steps, title => title.StartsWith("3. ", StringComparison.Ordinal));
    }

    [Fact]
    public void Shortcuts_KeepAllNamedFields_ForTheCurrentUserBindings()
    {
        var shortcuts = Assert.Single(Tabs, element => element.Attr("Header") == "Help");
        Assert.Equal(
            ["ScreenshotShortcut", "ScreenshotRegionShortcut", "ScreenshotWindowShortcut", "ScreenshotScreenShortcut",
                "VideoShortcut", "GifShortcut", "OcrShortcut", "StopRecordingShortcut"],
            shortcuts.Descendants().Select(element => element.Attr("Name")).OfType<string>());
        Assert.All(shortcuts.Descendants().Where(element => element.Attr("Name") is not null), field =>
        {
            Assert.Equal("Wrap", field.Attr("TextWrapping"));
            // A fixed automation name would hide the live Text from a TextBlock peer.
            Assert.Null(field.Attr("AutomationProperties.Name"));
            var label = Assert.Single(field.ElementsBeforeSelf());
            Assert.False(string.IsNullOrWhiteSpace(label.Attr("Text")));
            Assert.Null(field.Attr("Text")); // Populated from IHotKeyService, never a stale default.
        });
        Assert.DoesNotContain(Tabs.Where(tab => tab.Attr("Header") != "Help").SelectMany(tab => tab.Descendants()),
            element => element.Attr("Name")?.EndsWith("Shortcut", StringComparison.Ordinal) == true);
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

    [Fact]
    public void EveryTopic_HasAnOriginalScalableDrawing_AndReadableAccessibleSummary()
    {
        Assert.All(Tabs, tab =>
        {
            var drawing = Assert.Single(tab.Descendants(), element =>
                element.Attr("Style") == "{StaticResource GuideDiagramStyle}");
            Assert.Equal("Border", drawing.Name.LocalName);
            Assert.False(string.IsNullOrWhiteSpace(drawing.Attr("AutomationProperties.AutomationId")));
            Assert.False(string.IsNullOrWhiteSpace(drawing.Attr("AutomationProperties.Name")));
            var viewbox = Assert.Single(drawing.Elements());
            Assert.Equal("Viewbox", viewbox.Name.LocalName);
            Assert.Equal("Uniform", viewbox.Attr("Stretch"));
            Assert.Equal("DownOnly", viewbox.Attr("StretchDirection"));
            Assert.Equal("Raw", viewbox.Attr("AutomationProperties.AccessibilityView"));
            Assert.DoesNotContain(drawing.Descendants(), element => element.Name.LocalName is "Image" or "ImageBrush");
            Assert.Contains(drawing.Descendants(), element =>
                element.Name.LocalName is "Rectangle" or "Ellipse" or "Line" or "Polyline" or "FontIcon");
            var summary = Assert.Single(tab.Descendants(), element =>
                element.Attr("Style") == "{StaticResource GuideDrawingSummaryStyle}");
            Assert.Equal("TextBlock", summary.Name.LocalName); // A text peer, not a decorative Border peer.
            Assert.Equal(drawing.Attr("AutomationProperties.AutomationId") + "Summary",
                summary.Attr("AutomationProperties.AutomationId"));
            Assert.StartsWith("Illustration: ", summary.Attr("Text")!);
        });
        var drawingStyles = Elements.Where(element => element.Name.LocalName == "Style" &&
            element.Attr("Key")?.StartsWith("GuideDrawing", StringComparison.Ordinal) == true);
        Assert.All(drawingStyles.SelectMany(style => style.Descendants())
            .Where(element => element.Attr("Property") is "Foreground" or "Background" or "BorderBrush"),
            setter => Assert.StartsWith("{ThemeResource ", setter.Attr("Value")!));
        Assert.All(Tabs.SelectMany(tab => tab.Descendants())
            .Where(element => element.Attr("Style") == "{StaticResource GuideDiagramStyle}")
            .SelectMany(drawing => drawing.Descendants())
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Fill" or "Stroke" or "Foreground" or "Background"),
            attribute => Assert.StartsWith("{ThemeResource ", attribute.Value));
    }

    [Fact]
    public void Handbook_IsReadOnly_AndInteractiveNavigationAndLinksHaveUniqueAutomationIds()
    {
        Assert.Contains(Elements, element => element.Name.LocalName == "MicaBackdrop");
        Assert.Contains(Elements, element => element.Name.LocalName == "TitleBar");
        Assert.Single(Elements, element => element.Attr("AutomationProperties.HeadingLevel") == "Level1");
        Assert.DoesNotContain(Elements, element =>
            element.Name.LocalName is "Button" or "TextBox" or "ComboBox" or "CheckBox" or "ToggleSwitch" or "WebView2");
        Assert.DoesNotContain(Elements.SelectMany(element => element.Attributes()), attribute =>
            attribute.Name.LocalName is "Click" or "Command" or "Mode" ||
            attribute.Value.Contains("Mode=TwoWay", StringComparison.Ordinal));
        Assert.All(Elements.Where(element => element.Name.LocalName is "TabView" or "TabViewItem" or "HyperlinkButton"), element =>
        {
            Assert.False(string.IsNullOrWhiteSpace(element.Attr("AutomationProperties.AutomationId")));
            Assert.False(string.IsNullOrWhiteSpace(element.Attr("AutomationProperties.Name")));
        });
        var ids = SettingsMarkup.AutomationIds("GuideWindow.xaml");
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("capture", "WebP|Scroll (P)|overlap|Recognize Text|clipboard")]
    [InlineData("editor", "curved arrows|Solid|Find sensitive text|Fill or Fit|9:16|Save a copy")]
    [InlineData("recording", "system audio|microphone|GIF captures neither|Teleprompter|pause/resume|Copy frame|Export frame")]
    [InlineData("studio", "early preview|separate|0.25×|SRT|WebVTT|downloaded|.tinyclips|Drafts|Export")]
    [InlineData("library", "Smart Collections|notes|batch|Archive|Delete|Windows Share|Uploadcare|Upload automatically|not remote deletion")]
    [InlineData("agents", ".NET 10 SDK|same OS account|Scopes|approval|Revoke|audit log|tokens")]
    [InlineData("help", "saved bindings|Fixed keys|Privacy & security|Microphone|shortcut conflict|Store-managed|winget")]
    public void TopicCopy_CoversShippingCapabilitiesAndPrivacyBoundaries(string topic, string requiredTerms)
    {
        var tab = Assert.Single(Tabs, element => element.Attr("Tag") == topic);
        var copy = string.Join("\n", tab.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Text" or "Title" or "Subtitle")
            .Select(attribute => attribute.Value));
        foreach (var term in requiredTerms.Split('|'))
        {
            Assert.Contains(term, copy, StringComparison.OrdinalIgnoreCase);
        }
    }
}
