using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioFixtureTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void SharedLayoutAndTimeMapFixturesMatchWindowsImplementation()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "StudioFixtures");
        var layoutFiles = Directory.Exists(Path.Combine(fixtureRoot, "layout"))
            ? Directory.EnumerateFiles(Path.Combine(fixtureRoot, "layout"), "*.json").Order().ToArray()
            : [];
        var timeMapFiles = Directory.Exists(Path.Combine(fixtureRoot, "timemap"))
            ? Directory.EnumerateFiles(Path.Combine(fixtureRoot, "timemap"), "*.json").Order().ToArray()
            : [];
        var canvasFiles = Directory.Exists(Path.Combine(fixtureRoot, "canvas"))
            ? Directory.EnumerateFiles(Path.Combine(fixtureRoot, "canvas"), "*.json").Order().ToArray()
            : [];

        Assert.True(layoutFiles.Length > 0, $"No Studio layout fixtures were copied to {Path.Combine(fixtureRoot, "layout")}.");
        Assert.True(timeMapFiles.Length > 0, $"No Studio timemap fixtures were copied to {Path.Combine(fixtureRoot, "timemap")}.");
        Assert.True(canvasFiles.Length > 0, $"No Studio canvas fixtures were copied to {Path.Combine(fixtureRoot, "canvas")}.");

        foreach (var file in layoutFiles)
        {
            VerifyLayoutFixture(file);
        }

        foreach (var file in timeMapFiles)
        {
            VerifyTimeMapFixture(file);
        }

        foreach (var file in canvasFiles)
        {
            VerifyCanvasFixture(file);
        }
    }

    private static void VerifyLayoutFixture(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var project = StudioProjectJson.ReadProject(root.GetProperty("project").GetRawText());

        var natural = StudioCanvasMath.NaturalSize(project);
        CompareNumber(file, -1, "naturalCanvas.width", root.GetProperty("naturalCanvas").GetProperty("width").GetDouble(), natural.Width);
        CompareNumber(file, -1, "naturalCanvas.height", root.GetProperty("naturalCanvas").GetProperty("height").GetDouble(), natural.Height);

        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        for (var i = 0; i < cases.Length; i++)
        {
            var testCase = cases[i];
            var canvas = testCase.GetProperty("canvas");
            var frame = StudioLayoutResolver.Resolve(
                project,
                testCase.GetProperty("time").GetDouble(),
                canvas.GetProperty("width").GetDouble(),
                canvas.GetProperty("height").GetDouble());
            CompareFrame(file, i, testCase.GetProperty("expected"), frame);
        }
    }

    private static void VerifyTimeMapFixture(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var edits = root.TryGetProperty("edits", out var editsElement)
            ? JsonSerializer.Deserialize<StudioEdits>(editsElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new StudioEdits()
            : new StudioEdits();
        var map = new StudioTimeMap(root.GetProperty("sourceDuration").GetDouble(), edits);
        var expected = root.GetProperty("expected");

        CompareNumber(file, -1, "expected.outputDuration", expected.GetProperty("outputDuration").GetDouble(), map.OutputDuration);

        var segments = expected.GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal(segments.Length, map.Segments.Count);
        for (var i = 0; i < segments.Length; i++)
        {
            CompareNumber(file, i, "segments.start", segments[i].GetProperty("start").GetDouble(), map.Segments[i].Start);
            CompareNumber(file, i, "segments.end", segments[i].GetProperty("end").GetDouble(), map.Segments[i].End);
        }

        foreach (var sample in expected.GetProperty("sourceToOutput").EnumerateArray().Select((value, index) => (value, index)))
        {
            CompareNumber(file, sample.index, "sourceToOutput.output", sample.value.GetProperty("output").GetDouble(), map.SourceToOutput(sample.value.GetProperty("source").GetDouble()));
        }

        foreach (var sample in expected.GetProperty("outputToSource").EnumerateArray().Select((value, index) => (value, index)))
        {
            CompareNumber(file, sample.index, "outputToSource.source", sample.value.GetProperty("source").GetDouble(), map.OutputToSource(sample.value.GetProperty("output").GetDouble()));
        }
    }

    private static void VerifyCanvasFixture(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        for (var i = 0; i < cases.Length; i++)
        {
            var testCase = cases[i];
            var natural = testCase.GetProperty("natural");
            var actual = StudioCanvasMath.ExportSize(
                new StudioSize(natural.GetProperty("width").GetDouble(), natural.GetProperty("height").GetDouble()),
                testCase.GetProperty("limit").GetDouble());
            var expected = testCase.GetProperty("expected");

            CompareNumber(file, i, "expected.width", expected.GetProperty("width").GetDouble(), actual.Width);
            CompareNumber(file, i, "expected.height", expected.GetProperty("height").GetDouble(), actual.Height);
        }
    }

    private static void CompareFrame(string file, int caseIndex, JsonElement expected, StudioResolvedFrame actual)
    {
        CompareNumber(file, caseIndex, "sceneIndex", expected.GetProperty("sceneIndex").GetInt32(), actual.SceneIndex);
        CompareString(file, caseIndex, "layout", expected.GetProperty("layout").GetString(), LayoutString(actual.Layout));
        CompareScreen(file, caseIndex, expected.GetProperty("screen"), actual.Screen);
        CompareCamera(file, caseIndex, expected.GetProperty("camera"), actual.Camera);
    }

    private static void CompareScreen(string file, int caseIndex, JsonElement expected, StudioResolvedScreen? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual is null, $"{file} case {caseIndex} field screen expected null.");
            return;
        }

        Assert.True(actual is not null, $"{file} case {caseIndex} field screen expected non-null.");
        CompareRect(file, caseIndex, "screen.rect", expected.GetProperty("rect"), actual.Rect);
        CompareRect(file, caseIndex, "screen.source", expected.GetProperty("source"), actual.Source);
        CompareNumber(file, caseIndex, "screen.cornerRadius", expected.GetProperty("cornerRadius").GetDouble(), actual.CornerRadius);
        CompareShadow(file, caseIndex, "screen.shadow", expected.GetProperty("shadow"), actual.Shadow);
    }

    private static void CompareCamera(string file, int caseIndex, JsonElement expected, StudioResolvedCamera? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual is null, $"{file} case {caseIndex} field camera expected null.");
            return;
        }

        Assert.True(actual is not null, $"{file} case {caseIndex} field camera expected non-null.");
        CompareRect(file, caseIndex, "camera.rect", expected.GetProperty("rect"), actual.Rect);
        CompareRect(file, caseIndex, "camera.source", expected.GetProperty("source"), actual.Source);
        CompareString(file, caseIndex, "camera.shape", expected.GetProperty("shape").GetString(), ShapeString(actual.Shape));
        CompareNumber(file, caseIndex, "camera.cornerRadius", expected.GetProperty("cornerRadius").GetDouble(), actual.CornerRadius);
        CompareBool(file, caseIndex, "camera.mirror", expected.GetProperty("mirror").GetBoolean(), actual.Mirror);
        CompareNumber(file, caseIndex, "camera.borderWidth", expected.GetProperty("borderWidth").GetDouble(), actual.BorderWidth);
        CompareShadow(file, caseIndex, "camera.shadow", expected.GetProperty("shadow"), actual.Shadow);
        CompareNumber(file, caseIndex, "camera.sourceTime", expected.GetProperty("sourceTime").GetDouble(), actual.SourceTime);
        CompareBool(file, caseIndex, "camera.visible", expected.GetProperty("visible").GetBoolean(), actual.Visible);
    }

    private static void CompareRect(string file, int caseIndex, string field, JsonElement expected, StudioRect actual)
    {
        CompareNumber(file, caseIndex, $"{field}.x", expected.GetProperty("x").GetDouble(), actual.X);
        CompareNumber(file, caseIndex, $"{field}.y", expected.GetProperty("y").GetDouble(), actual.Y);
        CompareNumber(file, caseIndex, $"{field}.width", expected.GetProperty("width").GetDouble(), actual.Width);
        CompareNumber(file, caseIndex, $"{field}.height", expected.GetProperty("height").GetDouble(), actual.Height);
    }

    private static void CompareShadow(string file, int caseIndex, string field, JsonElement expected, StudioResolvedShadow actual)
    {
        CompareNumber(file, caseIndex, $"{field}.blur", expected.GetProperty("blur").GetDouble(), actual.Blur);
        CompareNumber(file, caseIndex, $"{field}.offsetY", expected.GetProperty("offsetY").GetDouble(), actual.OffsetY);
        CompareNumber(file, caseIndex, $"{field}.opacity", expected.GetProperty("opacity").GetDouble(), actual.Opacity);
    }

    private static void CompareNumber(string file, int caseIndex, string field, double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, $"{file} case {caseIndex} field {field}: expected {expected}, actual {actual}.");

    private static void CompareString(string file, int caseIndex, string field, string? expected, string actual) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"{file} case {caseIndex} field {field}: expected {expected}, actual {actual}.");

    private static void CompareBool(string file, int caseIndex, string field, bool expected, bool actual) =>
        Assert.True(expected == actual, $"{file} case {caseIndex} field {field}: expected {expected}, actual {actual}.");

    private static string LayoutString(StudioLayout layout) =>
        layout switch
        {
            StudioLayout.Screen => "screen",
            StudioLayout.SideBySide => "sideBySide",
            StudioLayout.Camera => "camera",
            _ => "bubble",
        };

    private static string ShapeString(StudioCameraShape shape) =>
        shape switch
        {
            StudioCameraShape.RoundedRectangle => "roundedRectangle",
            StudioCameraShape.Squircle => "squircle",
            StudioCameraShape.Rectangle => "rectangle",
            _ => "circle",
        };
}
