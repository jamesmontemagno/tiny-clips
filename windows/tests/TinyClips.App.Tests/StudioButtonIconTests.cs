using System.Reflection;
using System.Xml.Linq;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Tests;

/// <summary>
/// The glyphs on the Studio editor's buttons, read from the markup of the window and of its
/// controls: each button that edits shows a glyph of <see cref="StudioGlyphs"/> before its words,
/// and says its words as its name. Which glyph each name of <see cref="StudioGlyphs"/> is, and
/// that the four of them that add or show what a panel edits are the rail's, is held by the Core
/// tests (<c>StudioInspectorPanelTests</c>).
/// </summary>
/// <remarks>
/// A button cannot be made here, because a XAML control needs the running app. So this says what
/// the markup asks for, and not what is drawn or what a screen reader is told: the window check
/// reads the names from the window itself.
/// </remarks>
public sealed class StudioButtonIconTests
{
    private const string MarkupPrefix = "StudioMarkup.";
    private const string GlyphsNamespace = "using:TinyClips.Core.Studio.Editing";

    private static readonly string[] MarkupFiles =
    [
        .. typeof(StudioButtonIconTests).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(MarkupPrefix, StringComparison.Ordinal))
            .Select(name => name[MarkupPrefix.Length..])
            .Order(StringComparer.Ordinal),
    ];

    // The file, the button's automation id, its glyph, the words it shows, and the name it says.
    private static readonly (string File, string Id, string Glyph, string Words, string Name)[] Table =
    [
        // The timeline row. Three of its buttons are short for what their names say.
        ("StudioTimeline.xaml", "StudioSplitSceneButton", nameof(StudioGlyphs.SplitScene), "Split", "Split scene"),
        ("StudioTimeline.xaml", "StudioAddZoomButton", nameof(StudioGlyphs.AddZoom), "Add zoom", "Add zoom"),
        ("StudioTimeline.xaml", "StudioAddCutButton", nameof(StudioGlyphs.AddCut), "Cut", "Add cut"),
        ("StudioTimeline.xaml", "StudioAddSpeedButton", nameof(StudioGlyphs.AddSpeed), "Speed", "Add speed change"),
        ("StudioTimeline.xaml", "StudioStartHereButton", nameof(StudioGlyphs.TrimStart), "Start here", "Start here"),
        ("StudioTimeline.xaml", "StudioEndHereButton", nameof(StudioGlyphs.TrimEnd), "End here", "End here"),

        // The inspector's panels.
        ("StudioInspector.xaml", "StudioSceneSectionSplitButton", nameof(StudioGlyphs.SplitScene), "Split scene", "Split scene"),
        ("StudioInspector.xaml", "StudioDeleteSceneButton", nameof(StudioGlyphs.Delete), "Delete scene", "Delete scene"),
        ("StudioInspector.xaml", "StudioScreenCropResetButton", nameof(StudioGlyphs.ResetCrop), "Reset crop", "Reset crop"),
        ("StudioInspector.xaml", "StudioShowSceneButton", nameof(StudioGlyphs.ShowScene), "Show scene", "Show scene"),
        ("StudioInspector.xaml", "StudioCameraCropResetButton", nameof(StudioGlyphs.ResetCrop), "Reset crop", "Reset crop"),
        ("StudioInspector.xaml", "StudioZoomSectionAddButton", nameof(StudioGlyphs.AddZoom), "Add zoom", "Add zoom"),
        ("StudioInspector.xaml", "StudioSuggestZoomsButton", nameof(StudioGlyphs.SuggestZooms), "Suggest zooms", "Suggest zooms"),
        ("StudioInspector.xaml", "StudioRemoveSuggestionsButton", nameof(StudioGlyphs.RemoveSuggestions), "Remove suggestions", "Remove suggestions"),
        ("StudioInspector.xaml", "StudioDeleteZoomButton", nameof(StudioGlyphs.Delete), "Delete zoom", "Delete zoom"),
        ("StudioInspector.xaml", "StudioCutSectionAddButton", nameof(StudioGlyphs.AddCut), "Add cut", "Add cut"),
        ("StudioInspector.xaml", "StudioDeleteCutButton", nameof(StudioGlyphs.Delete), "Delete cut", "Delete cut"),
        ("StudioInspector.xaml", "StudioSpeedSectionAddButton", nameof(StudioGlyphs.AddSpeed), "Add speed change", "Add speed change"),
        ("StudioInspector.xaml", "StudioDeleteSpeedButton", nameof(StudioGlyphs.Delete), "Delete speed change", "Delete speed change"),
        ("StudioInspector.xaml", "StudioSaveDefaultLookButton", nameof(StudioGlyphs.SaveDefaultLook), "Save as default look", "Save as default look"),
    ];

    public static TheoryData<string, string, string, string, string> Buttons
    {
        get
        {
            var data = new TheoryData<string, string, string, string, string>();
            foreach (var row in Table)
            {
                data.Add(row.File, row.Id, row.Glyph, row.Words, row.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Buttons))]
    public void AButtonThatEdits_ShowsItsGlyphBeforeItsWords_AndSaysItsWordsAsItsName(string file, string id, string glyph, string words, string name)
    {
        var button = Assert.Single(Elements(file), element => element.Name.LocalName == "Button" && Attr(element, "AutomationProperties.AutomationId") == id);

        // The name is written on the button: what it holds is no longer text it could take a name from.
        Assert.Equal(name, Attr(button, "AutomationProperties.Name"));
        Assert.Null(Attr(button, "Content"));

        var label = Assert.Single(button.Elements());
        Assert.Equal("StudioButtonLabel", label.Name.LocalName);
        Assert.Equal(words, Attr(label, "Text"));
        Assert.Equal($"{{x:Bind editing:StudioGlyphs.{glyph}}}", Attr(label, "Glyph"));
        Assert.Equal(GlyphsNamespace, label.GetNamespaceOfPrefix("editing")?.NamespaceName);
        Assert.NotNull(typeof(StudioGlyphs).GetProperty(glyph, BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void EveryLabelInTheMarkup_IsOfOneOfTheseButtons()
    {
        // A button that gains a glyph gains a row above, which is what holds its name and its glyph.
        var known = Table.Select(row => (row.File, row.Id)).ToHashSet();
        var labels = MarkupFiles
            .SelectMany(file => Elements(file)
                .Where(element => element.Name.LocalName == "StudioButtonLabel")
                .Select(label => (File: file, Id: label.Parent is { } parent ? Attr(parent, "AutomationProperties.AutomationId") ?? string.Empty : string.Empty)))
            .ToArray();

        Assert.Equal(known.Count, labels.Length);
        Assert.All(labels, label => Assert.Contains(label, known));
    }

    [Fact]
    public void TheSaveScreenRecordingButton_ShowsItsGlyphBeforeItsTwoLabels_AndAScreenReaderIsNotShownIt()
    {
        var button = Assert.Single(Elements("StudioWindow.xaml"), element => Attr(element, "AutomationProperties.AutomationId") == "StudioSaveScreenRecordingButton");
        Assert.Equal("{x:Bind ViewModel.SaveScreenRecordingName}", Attr(button, "AutomationProperties.Name"));

        var row = Assert.Single(button.Elements());
        Assert.Equal("StackPanel", row.Name.LocalName);
        Assert.Equal("Horizontal", Attr(row, "Orientation"));
        var parts = row.Elements().ToArray();
        Assert.Equal(new[] { "FontIcon", "Grid" }, parts.Select(part => part.Name.LocalName));
        Assert.Equal($"{{x:Bind editing:StudioGlyphs.{nameof(StudioGlyphs.SaveRecording)}}}", Attr(parts[0], "Glyph"));
        Assert.Equal(GlyphsNamespace, parts[0].GetNamespaceOfPrefix("editing")?.NamespaceName);
        Assert.Equal("Raw", Attr(parts[0], "AutomationProperties.AccessibilityView"));
        Assert.All(parts[1].Elements(), text => Assert.Equal("Raw", Attr(text, "AutomationProperties.AccessibilityView")));
    }

    [Fact]
    public void NoGlyphThatHasANameIsWrittenOutInTheMarkup()
    {
        // A glyph of StudioGlyphs, or of the rail, that the markup spelled out could be changed
        // there alone. The marks of a zoom's and of a cut's block are set in code for that reason.
        var named = StudioGlyphs.All.ToHashSet();
        var written = MarkupFiles
            .SelectMany(file => Elements(file)
                .Select(element => (File: file, Glyph: Attr(element, "Glyph")))
                .Where(found => found.Glyph is not null && named.Contains(found.Glyph)))
            .Select(found => $"{found.File} writes out U+{(int)found.Glyph![0]:X4}")
            .ToArray();

        Assert.Empty(written);
        Assert.Contains("StudioZoomBlock.xaml", MarkupFiles);
        Assert.Contains("StudioCutBlock.xaml", MarkupFiles);
        Assert.Contains("StudioClosePromptDialog.xaml", MarkupFiles);
    }

    private static IReadOnlyList<XElement> Elements(string file)
    {
        using var stream = typeof(StudioButtonIconTests).Assembly.GetManifestResourceStream(MarkupPrefix + file)
            ?? throw new InvalidOperationException($"{file} is not in the test assembly. The project file puts it there.");
        using var reader = new StreamReader(stream);
        return [.. XDocument.Parse(reader.ReadToEnd()).Descendants()];
    }

    private static string? Attr(XElement element, string attribute) =>
        element.Attributes().FirstOrDefault(candidate => candidate.Name.LocalName == attribute)?.Value;
}
