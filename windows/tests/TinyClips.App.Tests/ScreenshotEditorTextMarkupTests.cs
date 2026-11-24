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
