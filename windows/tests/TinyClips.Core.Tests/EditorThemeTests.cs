using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Editing;
using TinyClips.Core.Editing.Themes;
using Windows.UI;

namespace TinyClips.Core.Tests;

public sealed class EditorThemeFormatTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EditorThemeFixtures", name));

    [Theory]
    [InlineData("default-theme.json")]
    [InlineData("brand-theme.json")]
    public void FixturesRoundTripExactly(string name)
    {
        var expected = JsonNode.Parse(Fixture(name));

        var theme = EditorTheme.Parse(Fixture(name));

        Assert.True(JsonNode.DeepEquals(expected, theme.ToJson()), theme.ToJson().ToJsonString());
    }

    [Fact]
    public void MissingFieldsExpandToTheDefaultTheme()
    {
        var theme = EditorTheme.Parse("""{ "id": "default", "name": "Default" }""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Fixture("default-theme.json")), theme.ToJson()));
        Assert.Equal(theme, EditorTheme.CreateDefault("default", "Default"));
    }

    [Fact]
    public void EveryInvalidFixtureIsRejectedNamingItsField()
    {
        var cases = JsonNode.Parse(Fixture("invalid-themes.json"))!["cases"]!.AsArray();
        Assert.NotEmpty(cases);

        foreach (var item in cases)
        {
            var field = item!["field"]!.GetValue<string>();
            var error = Assert.Throws<EditorThemeFormatException>(() => EditorTheme.Parse(item["theme"]));
            Assert.Equal(field, error.Field);
            Assert.Contains(field, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ColorsAndNamesAreNormalized()
    {
        var theme = EditorTheme.Parse("""{ "id": "a", "name": "  Brand  ", "line": { "color": "#a0b0c0" }, "number": { "badgeColor": "#80a0b0c0" } }""");

        Assert.Equal("Brand", theme.Name);
        Assert.Equal("#FFA0B0C0", theme.Line.Color);
        Assert.Equal("#80A0B0C0", theme.Number.BadgeColor);
    }

    [Fact]
    public void UnknownFieldsAreIgnored_AndAMissingIdStaysBlank()
    {
        var theme = EditorTheme.Parse("""{ "name": "A", "future": 1, "rectangle": { "corners": 4 } }""");

        Assert.Equal(string.Empty, theme.Id);
        Assert.Equal(EditorTheme.CreateDefault(string.Empty, "A"), theme);
    }

    [Fact]
    public void TheOldGroupedFieldsAreNotRead()
    {
        var theme = EditorTheme.Parse("""
            { "name": "A", "shape": { "strokeColor": "#FF010203" }, "line": { "arrowStyle": "curvedLeft" } }
            """);

        Assert.Equal(EditorTheme.CreateDefault(string.Empty, "A"), theme);
    }

    [Theory]
    [InlineData("rectangle", """{ "strokeColor": "#FF010203", "strokeWidth": 9, "fillColor": "#FF040506", "gradientEndColor": "#FF070809", "gradientDirection": 10, "shadowBlur": 3, "shadowColor": "#FF0A0B0C" }""")]
    [InlineData("ellipse", """{ "strokeColor": "#FF010203", "strokeWidth": 9, "fillColor": "#FF040506", "gradientEndColor": "#FF070809", "gradientDirection": 10, "shadowBlur": 3, "shadowColor": "#FF0A0B0C" }""")]
    [InlineData("arrow", """{ "color": "#FF010203", "width": 9, "style": "curvedLeft" }""")]
    [InlineData("line", """{ "color": "#FF010203", "width": 9 }""")]
    [InlineData("pen", """{ "color": "#FF010203", "width": 9 }""")]
    public void EachSectionRoundTripsWithoutTouchingTheOthers(string section, string values)
    {
        var defaults = EditorTheme.CreateDefault("a", "A").ToJson();
        var expected = defaults.DeepClone().AsObject();
        expected[section] = JsonNode.Parse(values);

        var theme = EditorTheme.Parse(expected);
        var json = theme.ToJson();

        Assert.True(JsonNode.DeepEquals(expected, json), json.ToJsonString());
        foreach (var other in new[] { "rectangle", "ellipse", "arrow", "line", "pen", "text", "number", "canvas" })
        {
            if (other != section)
            {
                Assert.True(JsonNode.DeepEquals(defaults[other], json[other]), other);
            }
        }
    }

    [Fact]
    public void NotJsonIsAFormatError()
    {
        Assert.Throws<EditorThemeFormatException>(() => EditorTheme.Parse("{ nope"));
        Assert.Throws<EditorThemeFormatException>(() => EditorTheme.Parse("[]"));
    }
}

public sealed class EditorThemeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsEditorThemeTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, EditorThemeStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void WriteFile(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, contents);
    }

    [Fact]
    public void ThemesAndTheActiveThemeSurviveAReload()
    {
        var store = new EditorThemeStore(FilePath);
        var saved = store.Save(EditorTheme.CreateDefault(string.Empty, "Brand"));
        Assert.True(Guid.TryParse(saved.Id, out _));
        Assert.True(store.SetActive(saved.Id));

        var reloaded = new EditorThemeStore(FilePath);

        Assert.Equal(saved, Assert.Single(reloaded.Themes));
        Assert.Equal(saved.Id, reloaded.ActiveThemeId);
        Assert.Equal(saved, reloaded.ActiveTheme);
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(FilePath))!["version"]!.GetValue<int>());
    }

    [Fact]
    public void SaveReplacesTheSameId_AndAllowsSharedNames()
    {
        var store = new EditorThemeStore(FilePath);
        var first = store.Save(EditorTheme.CreateDefault("a", "Same"));
        store.Save(EditorTheme.CreateDefault("b", "Same"));

        store.Save(first with { Line = first.Line with { Width = 9 } });

        Assert.Equal(2, store.Themes.Count);
        Assert.Equal(9, store.Get("a")!.Line.Width);
    }

    [Fact]
    public void SaveRejectsAnInvalidTheme()
    {
        var store = new EditorThemeStore(FilePath);
        var theme = EditorTheme.CreateDefault("a", "A");

        var error = Assert.Throws<EditorThemeFormatException>(() => store.Save(theme with { Line = theme.Line with { Width = 101 } }));

        Assert.Equal("line.width", error.Field);
        Assert.Empty(store.Themes);
    }

    [Fact]
    public void SetActiveAndDeleteReportUnknownIds_AndDeleteClearsActive()
    {
        var store = new EditorThemeStore(FilePath);
        var theme = store.Save(EditorTheme.CreateDefault("a", "A"));
        Assert.False(store.SetActive("nope"));
        Assert.False(store.Delete("nope"));

        store.SetActive(theme.Id);
        Assert.True(store.Delete(theme.Id));

        Assert.Null(store.ActiveThemeId);
        Assert.Null(new EditorThemeStore(FilePath).ActiveThemeId);
    }

    [Fact]
    public void ChangedIsRaisedForEachChange()
    {
        var store = new EditorThemeStore(FilePath);
        var count = 0;
        store.Changed += (_, _) => count++;

        store.Save(EditorTheme.CreateDefault("a", "A"));
        store.SetActive("a");
        store.SetActive("a");
        store.Delete("a");

        Assert.Equal(3, count);
    }

    [Fact]
    public void ImportKeepsWhatIsAlreadySaved()
    {
        var store = new EditorThemeStore(FilePath);
        var original = store.Save(EditorTheme.CreateDefault("a", "Brand"));
        var export = store.Export("a")!;

        var imported = store.Import(export);
        var again = store.Import(export);

        Assert.NotEqual("a", imported.Id);
        Assert.Equal("Brand (2)", imported.Name);
        Assert.Equal("Brand (3)", again.Name);
        Assert.NotEqual(imported.Id, again.Id);
        Assert.Equal(original, store.Get("a"));
        Assert.Equal(3, store.Themes.Count);
    }

    [Fact]
    public void ImportKeepsAFreeId_AndAFullNameStaysValid()
    {
        var store = new EditorThemeStore(FilePath);
        var longName = new string('n', EditorTheme.MaximumNameLength);
        store.Save(EditorTheme.CreateDefault("a", longName));

        var imported = store.Import(EditorTheme.CreateDefault("b", longName).ToJson().ToJsonString());

        Assert.Equal("b", imported.Id);
        Assert.Equal(EditorTheme.MaximumNameLength, imported.Name.Length);
        Assert.EndsWith(" (2)", imported.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportIsTheThemeJson_AndNullForAnUnknownId()
    {
        var store = new EditorThemeStore(FilePath);
        var theme = store.Save(EditorTheme.CreateDefault("a", "A"));

        Assert.Equal(theme, EditorTheme.Parse(store.Export("a")!));
        Assert.Null(store.Export("nope"));
    }

    [Fact]
    public void TheLimitIsEnforced_ButAFullStoreCanStillReplace()
    {
        var store = new EditorThemeStore(FilePath);
        for (var i = 0; i < EditorThemeStore.MaximumThemes; i++)
        {
            store.Save(EditorTheme.CreateDefault($"t{i}", $"Theme {i}"));
        }

        Assert.Throws<EditorThemeLimitException>(() => store.Save(EditorTheme.CreateDefault("more", "More")));
        Assert.Throws<EditorThemeLimitException>(() => store.Import(EditorTheme.CreateDefault("more", "More").ToJson().ToJsonString()));
        store.Save(EditorTheme.CreateDefault("t0", "Renamed"));

        Assert.Equal(EditorThemeStore.MaximumThemes, store.Themes.Count);
        Assert.Equal("Renamed", store.Get("t0")!.Name);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "version": 2, "activeThemeId": null, "themes": [] }""")]
    [InlineData("""{ "version": 1 }""")]
    public void AnUnreadableFileIsBackedUpAndTheStoreStartsOver(string contents)
    {
        WriteFile(contents);

        var store = new EditorThemeStore(FilePath);

        Assert.Empty(store.Themes);
        Assert.Equal(contents, File.ReadAllText(store.BackupFilePath));

        store.Save(EditorTheme.CreateDefault("a", "A"));
        Assert.Single(new EditorThemeStore(FilePath).Themes);
    }

    [Fact]
    public void AnInvalidThemeIsSkippedAndBackedUp_AndAnActiveIdItHeldIsCleared()
    {
        var contents = """
            { "version": 1, "activeThemeId": "bad", "themes": [
              { "id": "good", "name": "Good" },
              { "id": "bad", "name": "Bad", "line": { "width": 500 } }
            ] }
            """;
        WriteFile(contents);

        var store = new EditorThemeStore(FilePath);

        Assert.Equal("good", Assert.Single(store.Themes).Id);
        Assert.Null(store.ActiveThemeId);
        Assert.Equal(contents, File.ReadAllText(store.BackupFilePath));
    }

    [Fact]
    public void AGoodFileLeavesNoBackup()
    {
        new EditorThemeStore(FilePath).Save(EditorTheme.CreateDefault("a", "A"));

        var store = new EditorThemeStore(FilePath);

        Assert.False(File.Exists(store.BackupFilePath));
    }

    [Fact]
    public void ConcurrentSavesAreAllKept()
    {
        var store = new EditorThemeStore(FilePath);

        Parallel.For(0, 40, i => store.Save(EditorTheme.CreateDefault($"t{i}", $"Theme {i}")));

        Assert.Equal(40, store.Themes.Count);
        Assert.Equal(40, new EditorThemeStore(FilePath).Themes.Count);
    }

    [Fact]
    public void DefaultFilePathSitsWithTheAppsOtherData()
    {
        Assert.Equal(
            Path.Combine("local", "TinyClips", "editor-themes.json"),
            EditorThemeStore.DefaultFilePath("local"));
    }
}

public sealed class EditorThemeStylingTests
{
    private const double Width = 1723;

    private static EditorTheme Brand() => EditorTheme.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EditorThemeFixtures", "brand-theme.json")));

    [Theory]
    [InlineData(450, 4)]
    [InlineData(900, 4)]
    [InlineData(1800, 8)]
    public void StrokeUnitsNeverShrinkBelowOnePixelPerUnit(double imageWidth, double pixels) =>
        Assert.Equal(pixels, EditorThemeUnits.StrokePixels(4, imageWidth), 6);

    [Theory]
    [InlineData(400, 8)]
    [InlineData(800, 16)]
    [InlineData(1600, 32)]
    public void TextUnitsFollowTheImageWidth(double imageWidth, double pixels) =>
        Assert.Equal(pixels, EditorThemeUnits.TextPixels(16, imageWidth), 6);

    [Fact]
    public void ApplySkipsRedactionsAndEmoji()
    {
        foreach (var tool in new[] { EditTool.Redact, EditTool.Emoji })
        {
            var annotation = new ScreenshotAnnotation
            {
                Tool = tool,
                Color = Color.FromArgb(255, 1, 2, 3),
                Thickness = 7,
                FontSize = 9,
                Text = "x",
                SizeScale = 2,
                Redaction = RedactionLevel.Heavy,
                RedactStyle = RedactionStyle.Solid,
            };
            var before = annotation.CaptureSnapshot();

            EditorThemeStyling.Apply(annotation, Brand(), Width);

            Assert.Equal(before.Color, annotation.Color);
            Assert.Equal(before.Thickness, annotation.Thickness);
            Assert.Equal(before.FontSize, annotation.FontSize);
            Assert.Equal(before.SizeScale, annotation.SizeScale);
            Assert.Equal(before.Redaction, annotation.Redaction);
            Assert.Equal(before.RedactStyle, annotation.RedactStyle);
        }
    }

    [Fact]
    public void ApplyRestylesShapesWithEffects()
    {
        var shape = new ScreenshotAnnotation { Tool = EditTool.Rectangle };

        EditorThemeStyling.Apply(shape, Brand(), 1800);

        Assert.Equal(EditorTheme.ColorOf("#FF102030"), shape.Color);
        Assert.Equal(11, shape.Thickness, 6);
        Assert.Equal(EditorTheme.ColorOf("#80405060"), shape.FillColor);
        Assert.True(shape.ShapeEffects.IsGradientFill);
        Assert.Equal(EditorTheme.ColorOf("#FF708090"), shape.ShapeEffects.GradientEndColor);
        Assert.Equal(120, shape.ShapeEffects.GradientDirectionDegrees);
        Assert.True(shape.ShapeEffects.HasShadow);
        Assert.Equal(36, shape.ShapeEffects.ShadowBlur, 6);
    }

    [Fact]
    public void NoGradientAndNoBlurTurnTheEffectsOff()
    {
        var effects = EditorThemeStyling.ShapeEffectsOf(EditorTheme.CreateDefault("a", "A").Rectangle, Width);

        Assert.False(effects.IsGradientFill);
        Assert.False(effects.HasShadow);
    }

    [Fact]
    public void RectanglesAndEllipsesEachUseTheirOwnSection()
    {
        var rectangle = new ScreenshotAnnotation { Tool = EditTool.Rectangle };
        var ellipse = new ScreenshotAnnotation { Tool = EditTool.Ellipse };

        EditorThemeStyling.Apply(rectangle, Brand(), 1800);
        EditorThemeStyling.Apply(ellipse, Brand(), 1800);

        Assert.Equal(EditorTheme.ColorOf("#FF102030"), rectangle.Color);
        Assert.Equal(EditorTheme.ColorOf("#FF203040"), ellipse.Color);
        Assert.Equal(11, rectangle.Thickness, 6);
        Assert.Equal(7, ellipse.Thickness, 6);
        Assert.Equal(EditorTheme.ColorOf("#80405060"), rectangle.FillColor);
        Assert.Equal(EditorTheme.ColorOf("#90506070"), ellipse.FillColor);
        Assert.True(rectangle.ShapeEffects.IsGradientFill);
        Assert.False(ellipse.ShapeEffects.IsGradientFill);
        Assert.Equal(200, ellipse.ShapeEffects.GradientDirectionDegrees);
        Assert.Equal(36, rectangle.ShapeEffects.ShadowBlur, 6);
        Assert.Equal(18, ellipse.ShapeEffects.ShadowBlur, 6);
        Assert.Equal(EditorTheme.ColorOf("#55110000"), ellipse.ShapeEffects.ShadowColor);
    }

    [Fact]
    public void ArrowsLinesAndPensEachUseTheirOwnSection()
    {
        var arrow = new ScreenshotAnnotation { Tool = EditTool.Arrow };
        var line = new ScreenshotAnnotation { Tool = EditTool.Line };
        var pen = new ScreenshotAnnotation { Tool = EditTool.Pen };

        EditorThemeStyling.Apply(arrow, Brand(), 1800);
        EditorThemeStyling.Apply(line, Brand(), 1800);
        EditorThemeStyling.Apply(pen, Brand(), 1800);

        Assert.Equal(EditorTheme.ColorOf("#FFA0B0C0"), arrow.Color);
        Assert.Equal(EditorTheme.ColorOf("#FFB0C0D0"), line.Color);
        Assert.Equal(EditorTheme.ColorOf("#FFC0D0E0"), pen.Color);
        Assert.Equal(14, arrow.Thickness, 6);
        Assert.Equal(12, line.Thickness, 6);
        Assert.Equal(5, pen.Thickness, 6);
    }

    [Fact]
    public void DefaultsComeFromEachToolsOwnSection()
    {
        var defaults = EditorThemeStyling.DefaultsOf(Brand(), 1800);

        Assert.Equal(EditorTheme.ColorOf("#FF102030"), defaults.Rectangle.StrokeColor);
        Assert.Equal(EditorTheme.ColorOf("#FF203040"), defaults.Ellipse.StrokeColor);
        Assert.Equal(EditorTheme.ColorOf("#80405060"), defaults.Rectangle.Fill);
        Assert.Equal(EditorTheme.ColorOf("#90506070"), defaults.Ellipse.Fill);
        Assert.True(defaults.Rectangle.Effects.IsGradientFill);
        Assert.False(defaults.Ellipse.Effects.IsGradientFill);
        Assert.Equal(new EditorThemeStrokeDefaults(EditorTheme.ColorOf("#FFA0B0C0"), 14), defaults.Arrow);
        Assert.Equal(new EditorThemeStrokeDefaults(EditorTheme.ColorOf("#FFB0C0D0"), 12), defaults.Line);
        Assert.Equal(new EditorThemeStrokeDefaults(EditorTheme.ColorOf("#FFC0D0E0"), 5), defaults.Pen);
        Assert.Equal(ArrowStyle.Curved2, defaults.ArrowStyle);
    }

    [Fact]
    public void ArrowStyleOnlyGoesOnArrows()
    {
        var arrow = new ScreenshotAnnotation { Tool = EditTool.Arrow };
        var line = new ScreenshotAnnotation { Tool = EditTool.Line };

        EditorThemeStyling.Apply(arrow, Brand(), Width);
        EditorThemeStyling.Apply(line, Brand(), Width);

        Assert.Equal(ArrowStyle.Curved2, arrow.ArrowStyle);
        Assert.Equal(ArrowStyle.Straight, line.ArrowStyle);
        Assert.Equal(ArrowStyle.Curved1, EditorThemeStyling.ArrowStyleOf("curvedLeft"));
    }

    [Fact]
    public void ApplyRestylesTextAndKeepsStrikethrough()
    {
        var text = new ScreenshotAnnotation { Tool = EditTool.Text, Strikethrough = true };

        EditorThemeStyling.Apply(text, Brand(), 1600);

        Assert.Equal("Georgia", text.FontFamily);
        Assert.Equal(44, text.FontSize, 6);
        Assert.True(text.Bold && text.Italic && text.Underline && text.Strikethrough);
        Assert.Equal(TextBoxPreset.Custom, text.TextBoxStyle.Preset);
        Assert.Equal(20, text.TextBoxStyle.Padding, 6);
    }

    [Fact]
    public void ApplySetsANumbersSizeScale()
    {
        var number = new ScreenshotAnnotation { Tool = EditTool.Counter };

        EditorThemeStyling.Apply(number, Brand(), Width);

        Assert.Equal(1.5, number.SizeScale);
        Assert.Equal(EditorTheme.ColorOf("#FF778899"), number.Color);
        Assert.Equal(EditorTheme.ColorOf("#FF001122"), number.TextColor);
    }

    [Fact]
    public void TheCanvasMapsToEditorValues_AndCanKeepAnImageBackground()
    {
        var state = EditorThemeStyling.CanvasStateOf(Brand());

        Assert.Equal(ExportBackgroundStyle.Gradient, state.Background);
        Assert.Equal(ExportFramePreset.LandscapeSixteenByNine, state.Frame);
        Assert.Equal(ExportHorizontalAlignment.Left, state.HorizontalAlignment);
        Assert.Equal(ExportVerticalAlignment.Bottom, state.VerticalAlignment);
        Assert.Equal(48, state.Padding);
        Assert.Equal(ExportBackgroundStyle.Image, EditorThemeStyling.CanvasStateOf(Brand(), keepsImageBackground: true).Background);
    }
}

public sealed class EditorThemeCaptureTests
{
    private const double Width = 1723;

    private static EditorTheme Brand() => EditorTheme.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EditorThemeFixtures", "brand-theme.json")));

    private static ScreenshotAnnotation Themed(EditTool tool, EditorTheme theme, double width = Width)
    {
        var annotation = new ScreenshotAnnotation { Tool = tool };
        EditorThemeStyling.Apply(annotation, theme, width);
        return annotation;
    }

    private static EditorTheme Capture(EditorTheme theme, IEnumerable<ScreenshotAnnotation> annotations, double width = Width)
    {
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        return EditorThemeCapture.Capture(theme, annotations, EditorThemeStyling.DefaultsOf(theme, width), canvas, canvas, width);
    }

    [Theory]
    [InlineData(641)]
    [InlineData(1723)]
    [InlineData(3841)]
    public void RightAfterApplyingNothingIsModified(double width)
    {
        foreach (var theme in new[] { Brand(), EditorTheme.CreateDefault("a", "A") })
        {
            ScreenshotAnnotation[] annotations =
            [
                Themed(EditTool.Rectangle, theme, width),
                Themed(EditTool.Ellipse, theme, width),
                Themed(EditTool.Arrow, theme, width),
                Themed(EditTool.Line, theme, width),
                Themed(EditTool.Pen, theme, width),
                Themed(EditTool.Text, theme, width),
                Themed(EditTool.Counter, theme, width),
            ];

            var captured = Capture(theme, annotations, width);

            Assert.False(EditorThemeCapture.IsModified(theme, captured));
            Assert.Equal(theme, captured);
        }
    }

    [Fact]
    public void TheNewestDifferingAnnotationWins()
    {
        var theme = Brand();
        var older = Themed(EditTool.Rectangle, theme);
        older.Color = Color.FromArgb(255, 1, 1, 1);
        var newer = Themed(EditTool.Rectangle, theme);
        newer.Color = Color.FromArgb(255, 2, 2, 2);
        var newest = Themed(EditTool.Rectangle, theme);

        var captured = Capture(theme, [older, newer, newest]);

        Assert.Equal("#FF020202", captured.Rectangle.StrokeColor);
        Assert.True(EditorThemeCapture.IsModified(theme, captured));
    }

    [Fact]
    public void EditingARectangleOnlyChangesRectangle()
    {
        var theme = Brand();
        var rectangle = Themed(EditTool.Rectangle, theme);
        rectangle.Color = Color.FromArgb(255, 1, 1, 1);
        rectangle.FillColor = Color.FromArgb(255, 2, 2, 2);

        var captured = Capture(theme, [rectangle, Themed(EditTool.Ellipse, theme)]);

        Assert.Equal("#FF010101", captured.Rectangle.StrokeColor);
        Assert.Equal("#FF020202", captured.Rectangle.FillColor);
        Assert.Equal(theme with { Rectangle = captured.Rectangle }, captured);
    }

    [Fact]
    public void EditingAnEllipseOnlyChangesEllipse()
    {
        var theme = Brand();
        var ellipse = Themed(EditTool.Ellipse, theme);
        ellipse.ShapeEffects = ellipse.ShapeEffects with { HasShadow = false };

        var captured = Capture(theme, [Themed(EditTool.Rectangle, theme), ellipse]);

        Assert.Equal(0, captured.Ellipse.ShadowBlur);
        Assert.Equal(theme with { Ellipse = captured.Ellipse }, captured);
    }

    [Theory]
    [InlineData(EditTool.Arrow)]
    [InlineData(EditTool.Line)]
    [InlineData(EditTool.Pen)]
    public void EditingOneStrokeToolOnlyChangesItsSection(EditTool tool)
    {
        var theme = Brand();
        var edited = Themed(tool, theme);
        edited.Color = Color.FromArgb(255, 1, 1, 1);
        edited.Thickness = EditorThemeUnits.StrokePixels(9, Width);
        ScreenshotAnnotation[] annotations =
        [
            Themed(EditTool.Arrow, theme),
            Themed(EditTool.Line, theme),
            Themed(EditTool.Pen, theme),
            edited,
        ];

        var captured = Capture(theme, annotations);

        var expected = tool switch
        {
            EditTool.Arrow => theme with { Arrow = new EditorThemeArrow("#FF010101", 9, theme.Arrow.Style) },
            EditTool.Line => theme with { Line = new EditorThemeStroke("#FF010101", 9) },
            _ => theme with { Pen = new EditorThemeStroke("#FF010101", 9) },
        };
        Assert.Equal(expected, captured);
    }

    [Fact]
    public void AnArrowsStyleIsCapturedIntoArrowOnly()
    {
        var theme = Brand();
        var arrow = Themed(EditTool.Arrow, theme);
        arrow.ArrowStyle = ArrowStyle.Straight;
        var number = Themed(EditTool.Counter, theme);
        number.SizeScale = 2;

        var captured = Capture(theme, [arrow, number, Themed(EditTool.Text, theme)]);

        Assert.Equal("straight", captured.Arrow.Style);
        Assert.Equal(2, captured.Number.Size);
        Assert.Equal(theme with { Arrow = captured.Arrow, Number = captured.Number }, captured);
    }

    [Fact]
    public void AToolsDefaultsAreCapturedIntoOnlyItsSection()
    {
        var theme = Brand();
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var unchanged = EditorThemeStyling.DefaultsOf(theme, Width);
        var defaults = unchanged with
        {
            Ellipse = unchanged.Ellipse with { Fill = Color.FromArgb(255, 1, 1, 1) },
            Pen = new EditorThemeStrokeDefaults(Color.FromArgb(255, 2, 2, 2), unchanged.Pen.Width),
        };

        var captured = EditorThemeCapture.Capture(theme, [], defaults, canvas, canvas, Width);

        Assert.Equal("#FF010101", captured.Ellipse.FillColor);
        Assert.Equal("#FF020202", captured.Pen.Color);
        Assert.Equal(theme with { Ellipse = captured.Ellipse, Pen = captured.Pen }, captured);
    }

    [Fact]
    public void ALookSettledOnOneToolIsStillAnEditOnAnother()
    {
        // Each pair below has the same fields, so a shared settled set would hide the edit.
        var brand = Brand();
        var theme = brand with { Rectangle = brand.Rectangle with { GradientDirection = brand.Ellipse.GradientDirection } };
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var defaults = EditorThemeStyling.DefaultsOf(theme, Width);
        var rectangle = Themed(EditTool.Rectangle, theme);
        rectangle.Color = Color.FromArgb(255, 1, 1, 1);
        rectangle.Thickness = EditorThemeUnits.StrokePixels(theme.Ellipse.StrokeWidth, Width);
        rectangle.FillColor = EditorTheme.ColorOf(theme.Ellipse.FillColor);
        rectangle.ShapeEffects = EditorThemeStyling.ShapeEffectsOf(theme.Ellipse, Width);
        var line = Themed(EditTool.Line, theme);
        line.Color = Color.FromArgb(255, 2, 2, 2);
        line.Thickness = EditorThemeUnits.StrokePixels(theme.Pen.Width, Width);
        var settled = EditorThemeCapture.Settle(theme, [rectangle, line], Width);
        var baseline = new EditorThemeBaseline(theme, canvas, Width, settled);

        var ellipse = Themed(EditTool.Ellipse, theme);
        ellipse.Color = rectangle.Color;
        var pen = Themed(EditTool.Pen, theme);
        pen.Color = line.Color;

        var captured = EditorThemeCapture.Capture(baseline, [rectangle, line, ellipse, pen], defaults, canvas);

        Assert.Equal("#FF010101", captured.Ellipse.StrokeColor);
        Assert.Equal("#FF020202", captured.Pen.Color);
        Assert.Equal(theme.Rectangle, captured.Rectangle);
        Assert.Equal(theme.Line, captured.Line);
    }

    [Fact]
    public void DefaultsAreUsedWhenNoAnnotationDiffers()
    {
        var theme = Brand();
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var defaults = WithLineWidth(EditorThemeStyling.DefaultsOf(theme, Width), 9);

        var captured = EditorThemeCapture.Capture(theme, [Themed(EditTool.Line, theme)], defaults, canvas, canvas, Width);

        Assert.Equal(9, captured.Line.Width);
    }

    private static EditorThemeDefaults WithLineWidth(EditorThemeDefaults defaults, double units) =>
        defaults with { Line = defaults.Line with { Width = EditorThemeUnits.StrokePixels(units, Width) } };

    [Fact]
    public void AnAnnotationBeatsTheDefaults()
    {
        var theme = Brand();
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var defaults = WithLineWidth(EditorThemeStyling.DefaultsOf(theme, Width), 9);
        var line = Themed(EditTool.Line, theme);
        line.Thickness = EditorThemeUnits.StrokePixels(12, Width);

        var captured = EditorThemeCapture.Capture(theme, [line], defaults, canvas, canvas, Width);

        Assert.Equal(12, captured.Line.Width);
    }

    [Fact]
    public void RedactionsAndEmojiNeverCount()
    {
        var theme = Brand();
        var captured = Capture(theme,
        [
            new ScreenshotAnnotation { Tool = EditTool.Redact, Color = Color.FromArgb(255, 9, 9, 9), Thickness = 50 },
            new ScreenshotAnnotation { Tool = EditTool.Emoji, Color = Color.FromArgb(255, 9, 9, 9), Thickness = 50 },
        ]);

        Assert.False(EditorThemeCapture.IsModified(theme, captured));
    }

    [Fact]
    public void ACanvasThatDidNotApplyOnOpenIsNotModified()
    {
        var theme = Brand();
        var showing = new EditorThemeCanvasState(ExportBackgroundStyle.Transparent, default, default, 45, 0, 0, 0,
            ExportFramePreset.Original, ExportHorizontalAlignment.Center, ExportVerticalAlignment.Center);

        var captured = EditorThemeCapture.Capture(theme, [], EditorThemeStyling.DefaultsOf(theme, Width), showing, showing, Width);

        Assert.False(EditorThemeCapture.IsModified(theme, captured));

        var edited = EditorThemeCapture.Capture(theme, [], EditorThemeStyling.DefaultsOf(theme, Width), showing with { Padding = 12 }, showing, Width);

        Assert.Equal(12, edited.Canvas.Padding);
        Assert.Equal(theme.Canvas.Background, edited.Canvas.Background);
        Assert.True(EditorThemeCapture.IsModified(theme, edited));
    }

    [Fact]
    public void AnImageBackgroundIsNotSaved()
    {
        var theme = Brand();
        var then = EditorThemeStyling.CanvasStateOf(theme);
        var now = then with { Background = ExportBackgroundStyle.Image };

        var captured = EditorThemeCapture.Capture(theme, [], EditorThemeStyling.DefaultsOf(theme, Width), now, then, Width);

        Assert.Equal("gradient", captured.Canvas.Background);
    }

    [Fact]
    public void ACropDoesNotReadAsModifiedBecauseTheAppliedWidthIsKept()
    {
        var theme = Brand();
        var annotation = Themed(EditTool.Rectangle, theme, 3000);
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var baseline = new EditorThemeBaseline(theme, canvas, 3000);

        // The bitmap is now 500 px wide, and the baseline still says 3000.
        var captured = EditorThemeCapture.Capture(baseline, [annotation], EditorThemeStyling.DefaultsOf(theme, 3000), canvas);

        Assert.False(EditorThemeCapture.IsModified(theme, captured));
    }

    [Fact]
    public void SizesRoundToTwoDecimals()
    {
        var theme = Brand();
        var almost = theme with { Arrow = theme.Arrow with { Width = 7.004 } };
        var different = theme with { Pen = theme.Pen with { Width = 2.51 } };

        Assert.False(EditorThemeCapture.IsModified(theme, almost));
        Assert.True(EditorThemeCapture.IsModified(theme, different));
    }

    [Fact]
    public void TurningAGradientOrShadowOffIsCaptured()
    {
        var theme = Brand();
        var shape = Themed(EditTool.Rectangle, theme);
        shape.ShapeEffects = shape.ShapeEffects with { IsGradientFill = false, HasShadow = false };

        var captured = Capture(theme, [shape]);

        Assert.Null(captured.Rectangle.GradientEndColor);
        Assert.Equal(0, captured.Rectangle.ShadowBlur);
    }

    [Fact]
    public void WhatIsCapturedIsAlwaysAValidTheme()
    {
        var theme = EditorTheme.CreateDefault("a", "A");
        var text = Themed(EditTool.Text, theme);
        text.FontSize = 5000;
        var shape = Themed(EditTool.Rectangle, theme);
        shape.Thickness = 5000;

        var captured = Capture(theme, [text, shape]);

        Assert.Equal(captured, EditorTheme.Parse(captured.ToJson()));
    }

    [Fact]
    public void SettledLooksDoNotReadAsModified_ButANewEditDoes()
    {
        var theme = Brand();
        var older = Themed(EditTool.Rectangle, theme);
        older.Color = Color.FromArgb(255, 1, 1, 1);
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var settled = EditorThemeCapture.Settle(theme, [older], Width);
        var baseline = new EditorThemeBaseline(theme, canvas, Width, settled);
        var defaults = EditorThemeStyling.DefaultsOf(theme, Width);

        Assert.False(EditorThemeCapture.IsModified(theme, EditorThemeCapture.Capture(baseline, [older], defaults, canvas)));

        older.Color = Color.FromArgb(255, 2, 2, 2);
        Assert.True(EditorThemeCapture.IsModified(theme, EditorThemeCapture.Capture(baseline, [older], defaults, canvas)));
    }
}

public sealed class EditorThemeParserTests
{
    private static EditorTheme Brand() => EditorTheme.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EditorThemeFixtures", "brand-theme.json")));

    private static ScreenshotAnnotation ParseOne(string fields, EditorTheme? theme) =>
        AutomationAnnotationParser.Parse(
            JsonNode.Parse($$"""[{ "bounds": { "x": 10, "y": 10, "width": 100, "height": 50 }, {{fields}} }]"""), 1800, 900, theme)[0];

    [Fact]
    public void MissingValuesComeFromTheTheme()
    {
        var shape = ParseOne("""  "type": "rectangle" """, Brand());

        Assert.Equal(EditorTheme.ColorOf("#FF102030"), shape.Color);
        Assert.Equal(11, shape.Thickness, 6);
        Assert.Equal(EditorTheme.ColorOf("#80405060"), shape.FillColor);
        Assert.True(shape.ShapeEffects.IsGradientFill);
        Assert.Equal(36, shape.ShapeEffects.ShadowBlur, 6);
    }

    [Fact]
    public void ExplicitValuesWin()
    {
        var shape = ParseOne(
            """ "type": "rectangle", "color": "#FF010203", "lineWidth": 3, "fillColor": "#FF040506", "fillEndColor": "#FF070809", "shadowBlur": 0 """,
            Brand());

        Assert.Equal(Color.FromArgb(255, 1, 2, 3), shape.Color);
        Assert.Equal(3, shape.Thickness);
        Assert.Equal(Color.FromArgb(255, 4, 5, 6), shape.FillColor);
        Assert.Equal(Color.FromArgb(255, 7, 8, 9), shape.ShapeEffects.GradientEndColor);
        Assert.Equal(120, shape.ShapeEffects.GradientDirectionDegrees);
        Assert.False(shape.ShapeEffects.HasShadow);
    }

    [Fact]
    public void EachTypeTakesItsOwnSection()
    {
        var ellipse = ParseOne(""" "type": "ellipse" """, Brand());
        var arrow = ParseOne(""" "type": "arrow", "points": [{ "x": 1, "y": 1 }, { "x": 9, "y": 9 }] """, Brand());
        var line = ParseOne(""" "type": "line", "points": [{ "x": 1, "y": 1 }, { "x": 9, "y": 9 }] """, Brand());
        var pen = ParseOne(""" "type": "pen", "points": [{ "x": 1, "y": 1 }, { "x": 9, "y": 9 }], "lineWidth": 3 """, Brand());

        Assert.Equal(EditorTheme.ColorOf("#FF203040"), ellipse.Color);
        Assert.False(ellipse.ShapeEffects.IsGradientFill);
        Assert.Equal(EditorTheme.ColorOf("#FFA0B0C0"), arrow.Color);
        Assert.Equal(ArrowStyle.Curved2, arrow.ArrowStyle);
        Assert.Equal(14, arrow.Thickness, 6);
        Assert.Equal(EditorTheme.ColorOf("#FFB0C0D0"), line.Color);
        Assert.Equal(12, line.Thickness, 6);
        Assert.Equal(EditorTheme.ColorOf("#FFC0D0E0"), pen.Color);
        Assert.Equal(3, pen.Thickness);
    }

    [Fact]
    public void TextAndNumbersTakeTheirSections()
    {
        var text = ParseOne(""" "type": "text", "text": "Hi", "fontSize": 30 """, Brand());
        var number = ParseOne(""" "type": "number", "textColor": "#FF010203" """, Brand());

        Assert.Equal("Georgia", text.FontFamily);
        Assert.Equal(30, text.FontSize);
        Assert.Equal(EditorTheme.ColorOf("#FFD0E0F0"), text.Color);
        Assert.Equal(1.5, number.SizeScale);
        Assert.Equal(Color.FromArgb(255, 1, 2, 3), number.TextColor);
        Assert.Equal(EditorTheme.ColorOf("#FF778899"), number.Color);
    }

    [Fact]
    public void RedactionsIgnoreTheTheme()
    {
        var withTheme = ParseOne(""" "type": "redact" """, Brand());
        var without = ParseOne(""" "type": "redact" """, null);

        Assert.Equal(without.CaptureSnapshot().Color, withTheme.Color);
        Assert.Equal(without.Thickness, withTheme.Thickness);
    }

    [Fact]
    public void WithoutAThemeNothingChanges()
    {
        var shape = ParseOne(""" "type": "rectangle" """, null);

        Assert.Equal(AutomationAnnotationParser.DefaultColor, shape.Color);
        Assert.Equal(AutomationAnnotationParser.DefaultLineWidth, shape.Thickness);
        Assert.False(shape.ShapeEffects.IsGradientFill);
    }
}
