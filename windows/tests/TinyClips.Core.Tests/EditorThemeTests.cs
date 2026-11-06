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
        var theme = EditorTheme.Parse("""{ "name": "A", "future": 1, "shape": { "corners": 4 } }""");

        Assert.Equal(string.Empty, theme.Id);
        Assert.Equal(EditorTheme.CreateDefault(string.Empty, "A"), theme);
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
        var effects = EditorThemeStyling.ShapeEffectsOf(EditorTheme.CreateDefault("a", "A").Shape, Width);

        Assert.False(effects.IsGradientFill);
        Assert.False(effects.HasShadow);
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
        var newer = Themed(EditTool.Ellipse, theme);
        newer.Color = Color.FromArgb(255, 2, 2, 2);
        var newest = Themed(EditTool.Rectangle, theme);

        var captured = Capture(theme, [older, newer, newest]);

        Assert.Equal("#FF020202", captured.Shape.StrokeColor);
        Assert.True(EditorThemeCapture.IsModified(theme, captured));
    }

    [Fact]
    public void GroupsAreCapturedSeparately()
    {
        var theme = Brand();
        var line = Themed(EditTool.Arrow, theme);
        line.ArrowStyle = ArrowStyle.Straight;
        var number = Themed(EditTool.Counter, theme);
        number.SizeScale = 2;

        var captured = Capture(theme, [line, number, Themed(EditTool.Text, theme)]);

        Assert.Equal("straight", captured.Line.ArrowStyle);
        Assert.Equal(2, captured.Number.Size);
        Assert.Equal(theme.Shape, captured.Shape);
        Assert.Equal(theme.Text, captured.Text);
    }

    [Fact]
    public void DefaultsAreUsedWhenNoAnnotationDiffers()
    {
        var theme = Brand();
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var defaults = EditorThemeStyling.DefaultsOf(theme, Width) with { LineWidth = EditorThemeUnits.StrokePixels(9, Width) };

        var captured = EditorThemeCapture.Capture(theme, [Themed(EditTool.Line, theme)], defaults, canvas, canvas, Width);

        Assert.Equal(9, captured.Line.Width);
    }

    [Fact]
    public void AnAnnotationBeatsTheDefaults()
    {
        var theme = Brand();
        var canvas = EditorThemeStyling.CanvasStateOf(theme);
        var defaults = EditorThemeStyling.DefaultsOf(theme, Width) with { LineWidth = EditorThemeUnits.StrokePixels(9, Width) };
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
        var almost = theme with { Line = theme.Line with { Width = 7.004 } };
        var different = theme with { Line = theme.Line with { Width = 7.01 } };

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

        Assert.Null(captured.Shape.GradientEndColor);
        Assert.Equal(0, captured.Shape.ShadowBlur);
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
    public void TextAndNumbersTakeTheirGroups()
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
