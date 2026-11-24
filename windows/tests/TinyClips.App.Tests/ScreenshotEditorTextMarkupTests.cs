using System.Xml.Linq;

namespace TinyClips.App.Tests;

public sealed class ScreenshotEditorTextMarkupTests
{
    private const string Prefix = "ScreenshotEditorMarkup.";
    private const string AutomationId = "AutomationProperties.AutomationId";

    [Fact]
    public void InlineTextStyle_IsASegmentedChoiceWithCustom()
    {
        var elements = Read("EditorInspector.xaml").Descendants().ToList();
        var choice = Assert.Single(elements, element => Attribute(element, AutomationId) == "EditorTextStyleChoice");

        Assert.Equal("StudioSegmented", choice.Name.LocalName);
        Assert.Equal(
            ["Plain", "Light", "Dark", "Accent"],
            choice.Elements().Select(item => Attribute(item, "Content")));
    }

    [Fact]
    public void StrokeAndFillColors_HaveDirectOpacitySliders()
    {
        var elements = Read("EditorInspector.xaml").Descendants().ToList();
        var badgeOpacity = Assert.Single(
            elements,
            element => Attribute(element, AutomationId) == "EditorBadgeOpacitySlider");
        var outlineOpacity = Assert.Single(
            elements,
            element => Attribute(element, AutomationId) == "EditorOutlineOpacitySlider");
        var strokeOpacity = Assert.Single(
            elements,
            element => Attribute(element, AutomationId) == "EditorStrokeOpacitySlider");

        Assert.Equal("Slider", badgeOpacity.Name.LocalName);
        Assert.Equal("Slider", outlineOpacity.Name.LocalName);
        Assert.Equal("Slider", strokeOpacity.Name.LocalName);
        Assert.Equal("Stroke opacity", Attribute(strokeOpacity, "AutomationProperties.Name"));
        Assert.Equal("Stroke opacity", Attribute(outlineOpacity, "AutomationProperties.Name"));
        Assert.Equal(
            "EditorNumberSizeSlider",
            Attribute(
                badgeOpacity.Parent!.Elements().TakeWhile(element => element != badgeOpacity).Last(),
                AutomationId));
        Assert.Equal(
            "EditorStrokeSlider",
            Attribute(
                strokeOpacity.Parent!.Elements().TakeWhile(element => element != strokeOpacity).Last(),
                AutomationId));
        Assert.Equal(
            "EditorOutlineSlider",
            Attribute(
                outlineOpacity.Parent!.Elements().TakeWhile(element => element != outlineOpacity).Last(),
                AutomationId));
    }

    [Fact]
    public void TextDialog_IsOrganizedAroundEntryStylePreviewAndOptionalBoxDetails()
    {
        var elements = Read("TextEntryDialog.xaml").Descendants().ToList();
        var entry = IndexOf(elements, "TextDialogEntry");
        var style = IndexOf(elements, "TextDialogStyleChoice");
        var preview = IndexOf(elements, "TextDialogPreview");
        var box = IndexOf(elements, "TextDialogBoxExpander");

        Assert.True(entry < style && style < preview && preview < box);
        Assert.Equal("StudioSegmented", elements[style].Name.LocalName);
        Assert.Equal(
            ["Plain", "Light", "Dark", "Accent"],
            elements[style].Elements().Select(item => Attribute(item, "Content")));
        Assert.NotNull(elements.SingleOrDefault(
            element => Attribute(element, AutomationId) == "TextDialogBackgroundOpacity"));
    }

    [Fact]
    public void TextDialog_ContentShrinksWithItsAvailableWidth()
    {
        var document = Read("TextEntryDialog.xaml");
        var contentPanel = Assert.Single(document.Root!.Elements(), element => element.Name.LocalName == "StackPanel");
        var textColor = document.Descendants().Single(
            element => Attribute(element, AutomationId) == "TextDialogColor");
        var boxExpander = document.Descendants().Single(
            element => Attribute(element, AutomationId) == "TextDialogBoxExpander");

        Assert.Null(Attribute(contentPanel, "MinWidth"));
        Assert.Equal("0", Attribute(textColor, "MinWidth"));
        Assert.Equal("Stretch", Attribute(boxExpander, "HorizontalContentAlignment"));
    }

    [Fact]
    public void ScreenshotEditor_EditCommandsStartDisabled()
    {
        var window = XDocument.Load(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "ScreenshotEditorWindow.xaml"));
        var buttons = window.Descendants().ToList();

        foreach (var id in new[] { "EditorUndoButton", "EditorDeleteButton", "EditorResetButton" })
        {
            var button = Assert.Single(buttons, element => Attribute(element, AutomationId) == id);
            Assert.Equal("False", Attribute(button, "IsEnabled"));
        }
    }

    private static XDocument Read(string name)
    {
        using var stream = typeof(ScreenshotEditorTextMarkupTests).Assembly
            .GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"{name} is not embedded in the test assembly.");
        return XDocument.Load(stream);
    }

    private static int IndexOf(IReadOnlyList<XElement> elements, string id) =>
        elements.Select((element, index) => (element, index))
            .Single(item => Attribute(item.element, AutomationId) == id).index;

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;
}
